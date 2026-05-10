using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Detects VRChat PhysBone components that may conflict with scale animations.
    /// </summary>
    public static class PhysBoneDetector
    {
        private static System.Type s_physBoneType;
        private static bool s_physBoneTypeResolved;

        private static System.Type ResolvePhysBoneType()
        {
            if (s_physBoneTypeResolved) return s_physBoneType;
            s_physBoneType = ReflectionUtil.FindType(
                t => t.Name == "VRCPhysBone" && (t.FullName?.StartsWith("VRC.") ?? false));
            s_physBoneTypeResolved = true; // latch even if missing — VRCSDK may not be installed
            return s_physBoneType;
        }

        /// <summary>
        /// Checks if a bone or any of its ancestors has a PhysBone component.
        /// Returns the name of the conflicting PhysBone GameObject, or null if safe.
        /// </summary>
        public static string CheckConflict(Transform bone)
        {
            if (bone == null) return null;

            var physBoneType = ResolvePhysBoneType();
            if (physBoneType == null) return null;

            // Check the bone itself and walk up the hierarchy
            var current = bone;
            while (current != null)
            {
                foreach (var comp in current.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    if (physBoneType.IsAssignableFrom(comp.GetType()))
                    {
                        // Check if our target bone is in the ignore list
                        var so = new UnityEditor.SerializedObject(comp);
                        var ignoreTransforms = so.FindProperty("ignoreTransforms");
                        if (ignoreTransforms != null && ignoreTransforms.isArray)
                        {
                            for (int i = 0; i < ignoreTransforms.arraySize; i++)
                            {
                                var ignored = ignoreTransforms.GetArrayElementAtIndex(i)
                                    .objectReferenceValue as Transform;
                                if (ignored == bone)
                                {
                                    so.Dispose();
                                    return null; // Bone is in ignore list - safe
                                }
                            }
                        }
                        so.Dispose();
                        return current.name;
                    }
                }
                current = current.parent;
            }

            return null;
        }
    }
}
