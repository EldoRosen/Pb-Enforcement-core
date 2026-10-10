using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;

namespace Advanced_PB_Limiter.Utils
{
    public static class GetSize
    {
        private static HashSet<string> errorToAvoid = new ();
        private static FastResourceLock _lock = new ();
        
        public static void OfAssembly(IMyGridProgram? instance, out long size, out long time)
        {
            time = 0;
            size = 0;

            _lock.SpinAcquireExclusive();
            long startTracker = Stopwatch.GetTimestamp();
            try
            {
                if (instance == null) return;
                Type programType = instance.GetType();
            
                // Measure fields for the current type
                MeasureFields(programType, instance, ref size);

                // Measure fields for nested types
                foreach (Type nestedType in programType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    MeasureFields(nestedType, instance, ref size);
                }
            }
            finally
            {
                time = Stopwatch.GetTimestamp() - startTracker;
                _lock.ReleaseExclusive();
            }
        }
        
        private static void MeasureFields(Type type, IMyGridProgram instance, ref long size)
        {
            // Check fields
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (errorToAvoid.Contains(field.Name)) continue;
                
                try
                {
                    // Use the instance if it's an instance field, or null for static fields
                    object? value = field.IsStatic ? field.GetValue(null) : field.GetValue(instance);
                    if (value != null && value.GetType() != field.FieldType) continue;
                    
                    OfObject(value, out long _size);
                    size += _size;
                }
                catch (Exception)
                {
                    // Log or handle the exception as needed
                    errorToAvoid.Add(field.Name);
                }
            }

            // Check properties
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (errorToAvoid.Contains(property.Name)) continue;
                
                try
                {
                    // Use the instance if it's an instance property, or null for static properties
                    object? value = property.GetGetMethod(true)?.IsStatic == true ? property.GetValue(null) : property.GetValue(instance);

                    OfObject(value, out long _size);
                    size += _size;
                }
                catch (Exception)
                {
                    // Log or handle the exception as needed
                    errorToAvoid.Add(property.Name);
                }
            }
        }
        
        private static void OfObject(object? obj, out long size)
        {
            size = 0;
            if (obj == null) return;
            
            Type type = obj.GetType();
            if (type.IsValueType)
            {
                size = Marshal.SizeOf(obj);
                return;
            }
            
            size += IntPtr.Size;
            
            if (type == typeof(string))
            {
                size += Marshal.SizeOf(typeof(IntPtr)) + Encoding.UTF8.GetByteCount((string)obj);
            }
            else if (type.IsArray)
            {
                Array array = (Array)obj;
                foreach (object? item in array)
                {
                    OfObject(item, out long _size);
                    size += _size;
                }
            }
            else if (typeof(ICollection).IsAssignableFrom(type))
            {
                // Handle collections like List<T>
                ICollection collection = (ICollection)obj;
                if (!type.IsGenericType)
                {
                    size += Marshal.SizeOf(type);
                }

                foreach (object? item in collection)
                {
                    OfObject(item, out long _size);
                    size += _size;
                }
            }
            else if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                IEnumerator enumerator = ((IEnumerable)obj).GetEnumerator();
                try
                {
                    while (enumerator.MoveNext())
                    {
                        OfObject(enumerator.Current, out long _size);
                        size += _size;
                    }
                }
                finally
                {
                    if (enumerator is IDisposable disposable)
                        disposable.Dispose();
                }
            }
            else if (typeof(IDictionary).IsAssignableFrom(type))
            {
                // Handle dictionaries like Dictionary<TKey, TValue>
                IDictionary dictionary = (IDictionary)obj;
                foreach (DictionaryEntry entry in dictionary)
                {
                    OfObject(entry.Key, out long keySize);
                    OfObject(entry.Value, out long valueSize);
                    size += keySize + valueSize;
                }
            }
        }
    }
}