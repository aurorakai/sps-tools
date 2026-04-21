using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Detects VRChat PhysBone components that may conflict with scale animations.
    /// </summary>
    public static class PhysBoneDetector
    {
        /// <summary>
        /// Checks if a bone or any of its ancestors has a PhysBone component.
        /// Returns the name of the conflicting PhysBone GameObject, or null if safe.
        /// </summary>
        public static string CheckConflict(Transform bone)
        {
            if (bone == null) return null;

            // Check the bone itself and walk up the hierarchy
            var current = bone;
            while (current != null)
            {
                foreach (var comp in current.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var typeName = comp.GetType().Name;
                    if (typeName.Contains("PhysBone") && !typeName.Contains("Collider"))
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
