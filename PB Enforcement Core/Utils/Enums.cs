using System;
using System.Collections.Generic;

namespace Advanced_PB_Limiter.Utils
{
    public static class Enums
    {
        public enum Punishment
        {
            TurnOff,
            Damage,
            Destroy
        }
    }
    
    public static class EnumHelper
    {
        public static IEnumerable<string> GetEnumDescriptions(Type enumType)
        {
            return Enum.GetNames(enumType);
        }
    }
}