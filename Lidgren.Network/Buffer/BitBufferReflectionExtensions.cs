using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Lidgren.Network
{
    [Obsolete("This class will soon be replaced with Source Generators.")]
    public static class BitBufferReflectionExtensions
    {
        // TODO: optimize reflection (generic compiled expressions?)
        // TODO: use source generator or create a better reflectionbased serializer (look at LiteNetLib?)
        // TODO: "Bit" type to easily represent/pack 1-bit bool

        public const BindingFlags DefaultBindingFlags =
            BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public;

        private static MethodInfo EnumReadMethod { get; }
        private static MethodInfo EnumWriteMethod { get; }

        private static Dictionary<Type, MethodInfo> ReadMethods { get; } = new Dictionary<Type, MethodInfo>();
        private static Dictionary<Type, MethodInfo> WriteMethods { get; } = new Dictionary<Type, MethodInfo>();

        static BitBufferReflectionExtensions()
        {
            var inMethods = typeof(BitBufferReadExtensions).GetMethods(BindingFlags.Static | BindingFlags.Public);
            foreach (MethodInfo method in inMethods)
            {
                var parameters = method.GetParameters();
                var readType = method.ReturnType;

                if (parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(IBitBuffer) &&
                    method.Name.StartsWith("Read", StringComparison.InvariantCulture))
                {
                    if (readType.IsEnum && readType.IsGenericMethodParameter)
                    {
                        EnumReadMethod = method;
                    }
                    else if (method.Name.AsSpan(4).SequenceEqual(readType.Name))
                    {
                        ReadMethods[readType] = method;
                    }
                }
            }
            if (EnumReadMethod == null)
                throw new InvalidOperationException();

            var outMethods = typeof(BitBufferWriteExtensions).GetMethods(BindingFlags.Static | BindingFlags.Public);
            foreach (MethodInfo method in outMethods)
            {
                if (method.Name.Equals("Write", StringComparison.InvariantCulture))
                {
                    var parameters = method.GetParameters();

                    if (parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(IBitBuffer))
                    {
                        var writeType = parameters[1].ParameterType;
                        if (writeType.IsEnum && writeType.IsGenericMethodParameter)
                            EnumWriteMethod = method;
                        else
                            WriteMethods[writeType] = method;
                    }
                }
            }
            if (EnumWriteMethod == null)
                throw new InvalidOperationException();
        }

        /// <summary>
        /// Reads all fields with the specified binding of the object in alphabetical order using reflection.
        /// </summary>
        public static void ReadAllFields(
            this IBitBuffer buffer, object target, BindingFlags flags = DefaultBindingFlags)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            Type type = target.GetType();
            FieldInfo[] fields = type.GetFields(flags);
            SortMembers(fields);

            var readParams = new[] { buffer };
            foreach (FieldInfo fi in fields)
            {
                // find read method
                MethodInfo? readMethod;

                if (fi.FieldType.IsEnum)
                    readMethod = EnumReadMethod;
                else if (!ReadMethods.TryGetValue(fi.FieldType, out readMethod))
                    throw new LidgrenException("Failed to find read method for type " + fi.FieldType);

                // read and set value
                var value = readMethod.Invoke(null, readParams);
                fi.SetValue(target, value);
            }
        }

        /// <summary>
        /// Reads all properties with the specified binding of the object in alphabetical order using reflection.
        /// </summary>
        public static void ReadAllProperties(
            this IBitBuffer buffer, object target, BindingFlags flags = DefaultBindingFlags)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            Type type = target.GetType();
            PropertyInfo[] properties = type.GetProperties(flags);
            SortMembers(properties);

            var readParams = new[] { buffer };
            foreach (PropertyInfo fi in properties)
            {
                // find read method
                MethodInfo? readMethod;

                if (fi.PropertyType.IsEnum)
                    readMethod = EnumReadMethod;
                else if (!ReadMethods.TryGetValue(fi.PropertyType, out readMethod))
                    throw new LidgrenException("Failed to find read method for type " + fi.PropertyType);

                // read and set value
                var value = readMethod.Invoke(null, readParams);
                fi.SetMethod?.Invoke(target, new[] { value });
            }
        }

        /// <summary>
        /// Writes all fields with specified binding in alphabetical order using reflection.
        /// </summary>
        public static void WriteAllFields(
            this IBitBuffer buffer, object? source, BindingFlags flags = DefaultBindingFlags)
        {
            if (source == null)
                return;

            Type type = source.GetType();
            FieldInfo[] fields = type.GetFields(flags);
            SortMembers(fields);

            foreach (FieldInfo field in fields)
            {
                // find the appropriate Write method

                MethodInfo? writeMethod;
                if (field.FieldType.IsEnum)
                    writeMethod = EnumWriteMethod;
                else if (!WriteMethods.TryGetValue(field.FieldType, out writeMethod))
                    throw new LidgrenException("Failed to find write method for type " + field.FieldType);

                // get and write value
                var value = field.GetValue(source);
                writeMethod.Invoke(null, new[] { buffer, value });
            }
        }

        /// <summary>
        /// Writes all properties with specified binding in alphabetical order using reflection.
        /// </summary>
        public static void WriteAllProperties(
            this IBitBuffer buffer, object? source, BindingFlags flags = DefaultBindingFlags)
        {
            if (source == null)
                return;

            Type type = source.GetType();
            PropertyInfo[] properties = type.GetProperties(flags);
            SortMembers(properties);

            foreach (PropertyInfo prop in properties)
            {
                var getMethod = prop.GetMethod;
                if (getMethod == null)
                    continue;

                // find the appropriate Write method
                MethodInfo? writeMethod;
                if (prop.PropertyType.IsEnum)
                    writeMethod = EnumWriteMethod;
                else if (!WriteMethods.TryGetValue(prop.PropertyType, out writeMethod))
                    throw new LidgrenException("Failed to find write method for type " + prop.PropertyType);

                // get and write value
                var value = getMethod.Invoke(source, null);
                writeMethod.Invoke(null, new[] { buffer, value });
            }
        }

        public static void SortMembers(
            MemberInfo[] members, StringComparison comparisonType = StringComparison.InvariantCulture)
        {
            Array.Sort(members, (x, y) => string.Compare(x.Name, y.Name, comparisonType));
        }
    }
}
