using System;
using System.Linq.Expressions;

namespace Lidgren.Network
{
    public static class EnumConverter
    {
        [CLSCompliant(false)]
        public static ulong ToUInt64<TEnum>(TEnum value)
            where TEnum : Enum
        {
            return Helper<TEnum>.ConvertFrom(value);
        }

        public static long ToInt64<TEnum>(TEnum value)
            where TEnum : Enum
        {
            return (long)ToUInt64(value);
        }

        [CLSCompliant(false)]
        public static TEnum ToEnum<TEnum>(ulong value)
            where TEnum : Enum
        {
            return Helper<TEnum>.ConvertTo(value);
        }

        public static TEnum ToEnum<TEnum>(long value)
            where TEnum : Enum
        {
            return ToEnum<TEnum>((ulong)value);
        }

        private static class Helper<TEnum>
        {
            public static Func<TEnum, ulong> ConvertFrom { get; } = GenerateFromConverter();
            public static Func<ulong, TEnum> ConvertTo { get; } = GenerateToConverter();

            private static Func<TEnum, ulong> GenerateFromConverter()
            {
                var parameter = Expression.Parameter(typeof(TEnum));
                var conversion = Expression.Convert(parameter, typeof(ulong));
                var method = Expression.Lambda<Func<TEnum, ulong>>(conversion, parameter);
                return method.Compile();
            }

            private static Func<ulong, TEnum> GenerateToConverter()
            {
                var parameter = Expression.Parameter(typeof(ulong));
                var conversion = Expression.Convert(parameter, typeof(TEnum));
                var method = Expression.Lambda<Func<ulong, TEnum>>(conversion, parameter);
                return method.Compile();
            }
        }
    }
}
