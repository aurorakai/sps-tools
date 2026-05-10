using System;
using System.Reflection;

namespace AuroraKai.SPSTools
{
    internal static class ReflectionUtil
    {
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

        private static Type FindTypeInAssembly(Assembly asm, Predicate<Type> match)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { return null; }

            foreach (var type in types)
            {
                if (type != null && match(type)) return type;
            }
            return null;
        }
    }
}
