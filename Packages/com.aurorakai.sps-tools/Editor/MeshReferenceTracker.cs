using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Utility for storing and resolving Mesh references on BaseEffectConfig instances.
    ///
    /// Unity ScriptableObject Mesh fields become "fake null" (destroyed but not C# null)
    /// after domain reloads, scene changes, or asset deletion. This tracker stores the
    /// asset path alongside the reference so it can be re-resolved from disk when needed.
    /// </summary>
    public static class MeshReferenceTracker
    {
        /// <summary>
        /// Stores a Mesh reference and its asset path on the config.
        /// </summary>
        /// <param name="config">The config to update.</param>
        /// <param name="field">"original" or "generated".</param>
        /// <param name="mesh">The mesh to store (may be null to clear).</param>
        public static void StoreMesh(BaseEffectConfig config, string field, Mesh mesh)
        {
            string path = (mesh != null) ? AssetDatabase.GetAssetPath(mesh) : "";

            if (field == "original")
            {
                config.originalMesh = mesh;
                config.originalMeshPath = path;
            }
            else if (field == "generated")
            {
                config.generatedMesh = mesh;
                config.generatedMeshPath = path;
            }
            else
            {
                Debug.LogWarning($"[MeshReferenceTracker] Unknown field '{field}'. Use \"original\" or \"generated\".");
            }
        }

        /// <summary>
        /// Returns the Mesh for the given field. If the stored reference is fake-null
        /// (destroyed after a domain reload), attempts to reload it from the stored path.
        /// Returns null if the mesh cannot be resolved.
        /// </summary>
        /// <param name="config">The config to read from.</param>
        /// <param name="field">"original" or "generated".</param>
        public static Mesh ResolveMesh(BaseEffectConfig config, string field)
        {
            Mesh mesh;
            string path;

            if (field == "original")
            {
                mesh = config.originalMesh;
                path = config.originalMeshPath;
            }
            else if (field == "generated")
            {
                mesh = config.generatedMesh;
                path = config.generatedMeshPath;
            }
            else
            {
                Debug.LogWarning($"[MeshReferenceTracker] Unknown field '{field}'. Use \"original\" or \"generated\".");
                return null;
            }

            // If the C# reference is alive and backed by a real asset, use it directly.
            if (mesh != null)
                return mesh;

            // Reference is null (or fake-null). Try to reload from the stored path.
            if (!string.IsNullOrEmpty(path))
            {
                var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (reloaded != null)
                {
                    // Heal the reference so future calls are fast.
                    if (field == "original")
                        config.originalMesh = reloaded;
                    else
                        config.generatedMesh = reloaded;

                    return reloaded;
                }
            }

            return null;
        }

        // =================================================================
        // TrackedMesh overloads (for additional meshes on a config)
        // =================================================================

        /// <summary>
        /// Stores a Mesh reference and its asset path on a TrackedMesh entry.
        /// </summary>
        /// <param name="entry">The TrackedMesh to update.</param>
        /// <param name="field">"original" or "generated".</param>
        /// <param name="mesh">The mesh to store (may be null to clear).</param>
        public static void StoreMesh(TrackedMesh entry, string field, Mesh mesh)
        {
            if (entry == null) return;
            string path = (mesh != null) ? AssetDatabase.GetAssetPath(mesh) : "";

            if (field == "original")
            {
                entry.originalMesh = mesh;
                entry.originalMeshPath = path;
            }
            else if (field == "generated")
            {
                entry.generatedMesh = mesh;
                entry.generatedMeshPath = path;
            }
            else
            {
                Debug.LogWarning($"[MeshReferenceTracker] Unknown field '{field}'. Use \"original\" or \"generated\".");
            }
        }

        /// <summary>
        /// Returns the Mesh for the given field on a TrackedMesh entry.
        /// Re-resolves from disk if the stored reference is fake-null after a domain reload.
        /// </summary>
        public static Mesh ResolveMesh(TrackedMesh entry, string field)
        {
            if (entry == null) return null;

            Mesh mesh;
            string path;
            if (field == "original")
            {
                mesh = entry.originalMesh;
                path = entry.originalMeshPath;
            }
            else if (field == "generated")
            {
                mesh = entry.generatedMesh;
                path = entry.generatedMeshPath;
            }
            else
            {
                Debug.LogWarning($"[MeshReferenceTracker] Unknown field '{field}'. Use \"original\" or \"generated\".");
                return null;
            }

            if (mesh != null) return mesh;

            if (!string.IsNullOrEmpty(path))
            {
                var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (reloaded != null)
                {
                    if (field == "original") entry.originalMesh = reloaded;
                    else entry.generatedMesh = reloaded;
                    return reloaded;
                }
            }

            return null;
        }
    }
}
