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
            if (field == "original")
                Store(mesh, out config.originalMesh, out config.originalMeshPath, out config.originalMeshGuid);
            else if (field == "generated")
                Store(mesh, out config.generatedMesh, out config.generatedMeshPath, out config.generatedMeshGuid);
            else
                WarnUnknownField(field);
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
            if (field == "original")
                return Resolve(ref config.originalMesh, ref config.originalMeshPath, config.originalMeshGuid);
            if (field == "generated")
                return Resolve(ref config.generatedMesh, ref config.generatedMeshPath, config.generatedMeshGuid);
            WarnUnknownField(field);
            return null;
        }

        /// <summary>Forgets the original and generated mesh of the config's primary renderer.</summary>
        public static void Clear(BaseEffectConfig config)
        {
            StoreMesh(config, "original", null);
            StoreMesh(config, "generated", null);
        }

        /// <summary>
        /// Forgets the original and generated meshes of the config's primary
        /// renderer and of every additional mesh.
        /// </summary>
        public static void ClearAll(BaseEffectConfig config)
        {
            Clear(config);
            if (config.additionalMeshes == null) return;
            foreach (var entry in config.additionalMeshes)
                Clear(entry);
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
            if (field == "original")
                Store(mesh, out entry.originalMesh, out entry.originalMeshPath, out entry.originalMeshGuid);
            else if (field == "generated")
                Store(mesh, out entry.generatedMesh, out entry.generatedMeshPath, out entry.generatedMeshGuid);
            else
                WarnUnknownField(field);
        }

        /// <summary>
        /// Returns the Mesh for the given field on a TrackedMesh entry.
        /// Re-resolves from disk if the stored reference is fake-null after a domain reload.
        /// </summary>
        public static Mesh ResolveMesh(TrackedMesh entry, string field)
        {
            if (entry == null) return null;
            if (field == "original")
                return Resolve(ref entry.originalMesh, ref entry.originalMeshPath, entry.originalMeshGuid);
            if (field == "generated")
                return Resolve(ref entry.generatedMesh, ref entry.generatedMeshPath, entry.generatedMeshGuid);
            WarnUnknownField(field);
            return null;
        }

        /// <summary>Forgets the entry's original and generated mesh.</summary>
        public static void Clear(TrackedMesh entry)
        {
            StoreMesh(entry, "original", null);
            StoreMesh(entry, "generated", null);
        }

        // =================================================================
        // Stored-reference primitives, shared with MeshStackAsset
        // =================================================================

        /// <summary>
        /// Stores <paramref name="mesh"/> with its asset path and GUID (both
        /// empty for null or a mesh that isn't an asset).
        /// </summary>
        internal static void Store(Mesh mesh, out Mesh field, out string path, out string guid)
        {
            field = mesh;
            path = mesh != null ? AssetDatabase.GetAssetPath(mesh) : "";
            guid = !string.IsNullOrEmpty(path) ? AssetDatabase.AssetPathToGUID(path) : "";
        }

        /// <summary>
        /// Returns <paramref name="mesh"/> while it's alive, otherwise loads the
        /// stored asset (see <see cref="ResolveStoredPath"/>). Null if it can't be found.
        /// </summary>
        internal static Mesh LoadStored(Mesh mesh, string path, string guid, out string resolvedPath)
        {
            resolvedPath = path;
            if (mesh != null) return mesh;
            resolvedPath = ResolveStoredPath(path, guid);
            return string.IsNullOrEmpty(resolvedPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Mesh>(resolvedPath);
        }

        // Re-resolves a fake-null reference and heals the stored path to where
        // the asset was found.
        private static Mesh Resolve(ref Mesh mesh, ref string path, string guid)
        {
            // If the C# reference is alive and backed by a real asset, use it directly.
            if (mesh != null) return mesh;

            var reloaded = LoadStored(null, path, guid, out string resolvedPath);
            if (reloaded == null) return null;
            mesh = reloaded;
            path = resolvedPath;
            return reloaded;
        }

        private static string ResolveStoredPath(string path, string storedGuid)
        {
            if (string.IsNullOrEmpty(storedGuid))
                return path;

            string guidPath = AssetDatabase.GUIDToAssetPath(storedGuid);
            if (!string.IsNullOrEmpty(guidPath))
                return guidPath;

            if (!string.IsNullOrEmpty(path) &&
                AssetDatabase.AssetPathToGUID(path) == storedGuid)
                return path;

            return null;
        }

        private static void WarnUnknownField(string field) =>
            Debug.LogWarning($"[MeshReferenceTracker] Unknown field '{field}'. Use \"original\" or \"generated\".");
    }
}
