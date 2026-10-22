using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.ModAPI;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Estimates how much memory a script holds.
    ///
    /// Rewritten in 3.1. The old scanner called the script's property getters and enumerated its collections on a background
    /// thread, which ran player code outside the game's instruction limit: a getter or generator that never ended hung the
    /// scan, and a collection containing itself crashed the server with a stack overflow. This one only reads fields, never
    /// calls a method of the script, walks with its own stack (no recursion), visits each object once, and stops at a size,
    /// object count and time budget.
    ///
    /// It follows the script's own types, arrays, strings, StringBuilders and the standard collections (List, Dictionary,
    /// HashSet, Queue, ...). It does not follow game objects (blocks, the grid terminal system), which the script only points to.
    /// The result is an estimate, close enough to catch scripts that hoard memory.
    /// </summary>
    public static class GetSize
    {
        private const int MaxObjects = 250_000;
        private const double MaxScanMilliseconds = 100;
        private const long PointerSize = 8;
        private const long ObjectHeader = 16;
        private const long ArrayHeader = 24;

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();
        private static readonly ConcurrentDictionary<Type, long> ValueSizeCache = new();

        /// <summary>Estimated bytes held by the script. complete is false when a budget stopped the scan early (the size is then a lower bound).</summary>
        public static long OfProgram(IMyGridProgram? instance, out long time, out bool complete)
        {
            long start = Stopwatch.GetTimestamp();
            complete = true;
            long size = 0;

            try
            {
                if (instance == null) return 0;

                Type programType = instance.GetType();
                Assembly scriptAssembly = programType.Assembly;
                long deadline = start + (long)(MaxScanMilliseconds / 1000.0 * Stopwatch.Frequency);

                HashSet<object> visited = new(ReferenceComparer.Instance);
                Stack<object> pending = new();
                pending.Push(instance);

                // Static fields are not read: reading one can run the type's static constructor, which is script code.

                int objects = 0;
                while (pending.Count > 0)
                {
                    object current = pending.Pop();
                    if (!visited.Add(current)) continue;

                    if (++objects > MaxObjects || ((objects & 1023) == 0 && Stopwatch.GetTimestamp() > deadline))
                    {
                        complete = false;
                        break;
                    }

                    size += SizeOfObject(current, scriptAssembly, pending);
                }
            }
            catch (Exception)
            {
                complete = false; // a field changed shape mid-scan, or similar: keep what was counted
            }
            finally
            {
                time = Stopwatch.GetTimestamp() - start;
            }

            return size;
        }

        private static long SizeOfObject(object obj, Assembly scriptAssembly, Stack<object> pending)
        {
            Type type = obj.GetType();

            if (obj is string text) return ObjectHeader + 4 + 2L * text.Length;

            if (obj is Array array)
            {
                Type? elementType = type.GetElementType();
                long length = array.LongLength;
                if (elementType == null) return ArrayHeader;

                if (elementType.IsValueType)
                {
                    long elementSize = ValueSize(elementType);
                    long total = ArrayHeader + elementSize * length;
                    if (!elementType.IsPrimitive && !elementType.IsEnum && ContainsReferences(elementType) && length <= 100_000 && array.Rank == 1)
                    {
                        // An array of structs that hold references (a Dictionary's entries): follow those references.
                        for (int i = 0; i < length && pending.Count < MaxObjects; i++)
                            PushStructReferences(array.GetValue(i), pending);
                    }
                    return total;
                }

                if (array.Rank == 1)
                {
                    object?[]? items = obj as object?[];
                    if (items != null)
                        for (int i = 0; i < items.Length && pending.Count < MaxObjects; i++) // a huge array must not grow the work list without end
                        {
                            object? item = items[i];
                            if (item != null && ShouldFollow(item.GetType(), scriptAssembly)) pending.Push(item);
                        }
                }
                return ArrayHeader + PointerSize * length;
            }

            long size = ObjectHeader;
            if (!ShouldFollow(type, scriptAssembly)) return PointerSize;

            foreach (FieldInfo field in GetInstanceFields(type))
            {
                Type fieldType = field.FieldType;
                if (fieldType.IsValueType)
                {
                    size += ValueSize(fieldType);
                    if (!fieldType.IsPrimitive && !fieldType.IsEnum && ContainsReferences(fieldType))
                        PushStructReferences(SafeGetValue(field, obj), pending);
                    continue;
                }

                size += PointerSize;
                object? value = SafeGetValue(field, obj);
                if (value != null && (value is string || value is Array || ShouldFollow(value.GetType(), scriptAssembly)))
                    pending.Push(value);
            }

            return size;
        }

        /// <summary>Script types, and the standard library containers a script keeps its data in.</summary>
        private static bool ShouldFollow(Type type, Assembly scriptAssembly)
        {
            if (type.Assembly == scriptAssembly) return true;
            if (type == typeof(string) || type.IsArray) return true;
            if (typeof(Delegate).IsAssignableFrom(type)) return false;

            string? ns = type.Namespace;
            if (ns == null) return false;
            if (ns.StartsWith("System.Collections", StringComparison.Ordinal)) return true;
            if (type == typeof(System.Text.StringBuilder)) return true;
            if (ns == "System" && type.IsGenericType && type.Name.StartsWith("Tuple", StringComparison.Ordinal)) return true;
            return false;
        }

        private static void PushStructReferences(object? boxed, Stack<object> pending)
        {
            if (boxed == null) return;
            foreach (FieldInfo field in GetInstanceFields(boxed.GetType()))
            {
                if (field.FieldType.IsValueType) continue;
                object? value = SafeGetValue(field, boxed);
                if (value != null) pending.Push(value);
            }
        }

        private static bool ContainsReferences(Type valueType)
        {
            foreach (FieldInfo field in GetInstanceFields(valueType))
                if (!field.FieldType.IsValueType) return true;
            return false;
        }

        private static long ValueSize(Type type)
        {
            return ValueSizeCache.GetOrAdd(type, t =>
            {
                if (t.IsEnum) t = Enum.GetUnderlyingType(t);
                if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) return 1;
                if (t == typeof(char) || t == typeof(short) || t == typeof(ushort)) return 2;
                if (t == typeof(int) || t == typeof(uint) || t == typeof(float)) return 4;
                if (t == typeof(long) || t == typeof(ulong) || t == typeof(double) || t == typeof(IntPtr) || t == typeof(UIntPtr)) return 8;
                if (t == typeof(decimal)) return 16;
                if (t.IsPrimitive) return 8;

                long total = 0;
                foreach (FieldInfo field in GetInstanceFields(t))
                    total += field.FieldType.IsValueType ? (field.FieldType == t ? 8 : ValueSize(field.FieldType)) : PointerSize;
                return Math.Max(1, total);
            });
        }

        private static FieldInfo[] GetInstanceFields(Type type)
        {
            return FieldCache.GetOrAdd(type, t =>
            {
                List<FieldInfo> fields = new();
                for (Type? current = t; current != null && current != typeof(object); current = current.BaseType)
                {
                    // The game's MyGridProgram base holds the grid terminal system, Me, Runtime and so on: game objects, not script data.
                    if (current == typeof(Sandbox.ModAPI.Ingame.MyGridProgram)) break;
                    foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        fields.Add(field);
                }
                return fields.ToArray();
            });
        }

        private static object? SafeGetValue(FieldInfo field, object? target)
        {
            try { return field.GetValue(target); }
            catch (Exception) { return null; }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
