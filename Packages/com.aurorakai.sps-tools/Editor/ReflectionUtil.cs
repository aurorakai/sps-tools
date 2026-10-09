using System;
using System.Collections.Generic;
using System.Reflection;

namespace AuroraKai.SPSTools
{
    internal static class ReflectionUtil
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Field lookups by (type, name), including misses. Types can't change
        // within a domain, and the GUI reads VRCFury fields every repaint.
        private static readonly Dictionary<(Type, string), FieldInfo> s_fields =
            new Dictionary<(Type, string), FieldInfo>();

        public static Type FindType(Predicate<Type> match)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = FindTypeInAssembly(asm, match);
                if (t != null) return t;
            }
            return null;
        }

        public static Type FindType(Predicate<Assembly> assemblyMatch, Predicate<Type> match)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assemblyMatch(asm)) continue;
                var t = FindTypeInAssembly(asm, match);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// The assembly's types, skipping any that fail to load. Throws for
        /// assemblies that can't be inspected at all.
        /// </summary>
        public static Type[] GetLoadableTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types; }
        }

        private static Type FindTypeInAssembly(Assembly asm, Predicate<Type> match)
        {
            Type[] types;
            try { types = GetLoadableTypes(asm); }
            catch { return null; }

            foreach (var type in types)
            {
                if (type != null && match(type)) return type;
            }
            return null;
        }

        /// <summary>
        /// Reads an instance field by name, public or not, declared on the
        /// object's type or any base type. Returns null if there's no such field.
        /// </summary>
        public static object GetFieldValueRecursive(object obj, string fieldName)
        {
            if (obj == null) return null;
            return FindField(obj.GetType(), fieldName)?.GetValue(obj);
        }

        /// <summary>
        /// Writes an instance field found the same way as
        /// <see cref="GetFieldValueRecursive"/>. Returns false if there's no such field.
        /// </summary>
        public static bool SetFieldIfExists(object obj, string fieldName, object value)
        {
            if (obj == null) return false;
            var field = FindField(obj.GetType(), fieldName);
            if (field == null) return false;
            field.SetValue(obj, value);
            return true;
        }

        private static FieldInfo FindField(Type type, string fieldName)
        {
            var key = (type, fieldName);
            if (s_fields.TryGetValue(key, out var cached)) return cached;

            FieldInfo field = null;
            for (var t = type; t != null && field == null; t = t.BaseType)
                field = t.GetField(fieldName, InstanceFields);

            s_fields[key] = field;
            return field;
        }
    }
}
