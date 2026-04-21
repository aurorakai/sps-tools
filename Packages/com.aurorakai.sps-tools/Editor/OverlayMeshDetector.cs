using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Scans an avatar for SkinnedMeshRenderers whose vertices fall inside the
    /// drawn path's tube - i.e. candidate overlay meshes (clothing, addons)
    /// that should receive matching blendshapes alongside the primary mesh.
    /// </summary>
    public static class OverlayMeshDetector
    {
        public struct Candidate
        {
            public SkinnedMeshRenderer renderer;
            public float coveragePercent;       // % of THIS mesh's verts in range (informational)
            public int totalVerts;
            public int vertsInRange;
            public float densityVsPrimary;      // ratio of (this verts in range) / (primary verts in range), 0..1+
        }

        // Absolute vert count thresholds - the only thing that matters for "will this
        // mesh actually get a useful blendshape" is how many of its verts sit in the
        // affected region, not what fraction of the whole mesh that represents.
        public const int StrongMatchMinVerts = 20;
        public const int SuggestedMatchMinVerts = 5;

        /// <summary>
        /// Returns every candidate (skipping primary) sorted by coverage desc.
        /// Candidates with <0.1% coverage are dropped to keep the list manageable.
        /// </summary>
        public static List<Candidate> FindCandidateOverlays(
            GameObject avatarRoot,
            List<PathWaypoint> path,
            SkinnedMeshRenderer primary,
            string primaryPath = null)
        {
            var results = new List<Candidate>();
            if (avatarRoot == null || path == null || path.Count == 0) return results;

            var tube = CatmullRomSpline.BuildTube(path, Mathf.Max(path.Count * 8, 20));
            var avatarTransform = avatarRoot.transform;
            var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            // Reuse one Mesh across candidates to avoid GC churn during BakeMesh
            var bakedMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };

            // First, count the primary's verts in range - used as the density baseline
            int primaryInRange = primary != null && primary.sharedMesh != null
                ? CountVertsInRange(primary, bakedMesh, avatarTransform, tube)
                : 0;

            try
            {
                foreach (var r in renderers)
                {
                    if (r == null || r.sharedMesh == null) continue;
                    if (r == primary) continue;
                    if (!string.IsNullOrEmpty(primaryPath))
                    {
                        string rPath = BaseEffectConfig.GetRelativePath(avatarTransform, r.transform);
                        if (rPath == primaryPath) continue;
                    }

                    int inRange = CountVertsInRange(r, bakedMesh, avatarTransform, tube);
                    if (inRange == 0) continue;  // mesh has zero presence in the affected region

                    int total = r.sharedMesh.vertexCount;
                    float coverage = total > 0 ? 100f * inRange / total : 0f;
                    float density = primaryInRange > 0 ? (float)inRange / primaryInRange : 0f;

                    results.Add(new Candidate
                    {
                        renderer = r,
                        coveragePercent = coverage,
                        totalVerts = total,
                        vertsInRange = inRange,
                        densityVsPrimary = density
                    });
                }
            }
            finally
            {
                Object.DestroyImmediate(bakedMesh);
            }

            // Sort by absolute vert count in range - that's what determines effect quality
            results.Sort((a, b) => b.vertsInRange.CompareTo(a.vertsInRange));
            return results;
        }

        private static int CountVertsInRange(
            SkinnedMeshRenderer r, Mesh bakedMesh, Transform avatarTransform,
            CatmullRomSpline.SplineTube tube)
        {
            var mesh = r.sharedMesh;
            var worldVerts = BlendshapeGenerator.GetSkinnedWorldRefVerts(
                r, mesh.vertices, bakedMesh);
            var inTube = BlendshapeGenerator.ComputeVerticesInTube(
                mesh, worldVerts, avatarTransform, tube);
            int inRange = 0;
            for (int v = 0; v < inTube.Length; v++)
                if (inTube[v]) inRange++;
            return inRange;
        }
    }
}
