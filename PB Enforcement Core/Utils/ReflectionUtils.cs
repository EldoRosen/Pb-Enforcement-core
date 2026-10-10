using System;
using System.Reflection;

namespace Advanced_PB_Limiter.Utils
{
    public static class ReflectionUtils
    {
        /// <summary>
        /// Gracefully stolen from the original PB Limiter, Credits to SirHamsterAlot and Equinox
        /// </summary>
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        private static MethodInfo Method(IReflect type, string name, BindingFlags flags)
        {
            return type.GetMethod(name, flags) ?? throw new Exception($"Couldn't find method {name} on {type}");
        }

        public static MethodInfo InstanceMethod(Type t, string name)
        {
            return Method(t, name, InstanceFlags);
        }

        public static MethodInfo StaticMethod(Type t, string name)
        {
            return Method(t, name, StaticFlags);
        }
    }
}