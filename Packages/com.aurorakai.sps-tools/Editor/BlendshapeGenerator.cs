using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Result of blendshape generation - modified mesh and the names of generated blendshapes.
    /// </summary>
    public class BlendshapeResult
    {
        public Mesh modifiedMesh;           // copy with blendshapes added (original tracked by caller)
        public List<string> blendshapeNames;
    }

    /// <summary>
    /// Generates blendshapes programmatically by displacing vertices along smoothed normals.
    /// Uses area-weighted normal averaging for smooth results on low-poly meshes.
    /// </summary>
    public static class BlendshapeGenerator
    {
        /// <summary>
        /// Rough average edge length (meters) of the mesh triangles overlapping the
        /// path's affected region. Used by the UI to preview displacement-vs-topology
        /// ratio - when displacement >> avg edge, tri faceting is likely visible.
        /// Uses the same spline tube and surface-distance region test as blendshape
        /// generation so the preview matches what will actually deform.
        /// Returns 0 if unable to compute.
        /// </summary>
        public static float ComputeAverageEdgeLengthInRegion(
            SkinnedMeshRenderer renderer,
            List<PathWaypoint> path,
            Transform avatarRoot)
        {
            if (renderer == null || renderer.sharedMesh == null) return 0f;
            if (path == null || path.Count == 0) return 0f;

            var mesh = renderer.sharedMesh;
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            if (verts.Length == 0 || tris.Length == 0) return 0f;

            var at = avatarRoot != null ? avatarRoot : renderer.transform;
            int segments = Mathf.Max(path.Count * 8, 20);
            var tube = CatmullRomSpline.BuildTube(path, segments);
            var worldVerts = GetSkinnedWorldRefVerts(renderer, verts);
            var inRegion = ComputeVerticesInTube(mesh, worldVerts, at, tube);

            double sum = 0;
            int count = 0;
            for (int i = 0; i < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                if (!inRegion[i0] && !inRegion[i1] && !inRegion[i2]) continue;
                sum += Vector3.Distance(worldVerts[i0], worldVerts[i1]);
                sum += Vector3.Distance(worldVerts[i1], worldVerts[i2]);
                sum += Vector3.Distance(worldVerts[i2], worldVerts[i0]);
                count += 3;
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        /// <summary>
        /// Returns which vertices lie inside the deforming spline tube, using the
        /// same baked/skinned positions, per-sample radius/aspect, and surface-
        /// distance flood fill as the blendshape generator.
        /// </summary>
        internal static bool[] ComputeVerticesInTube(
            Mesh mesh,
            Vector3[] worldRefVerts,
            Transform avatarRoot,
            CatmullRomSpline.SplineTube tube)
        {
            var vertices = mesh != null ? mesh.vertices : null;
            int vertCount = vertices != null ? vertices.Length : 0;
            var inTube = new bool[vertCount];
            if (vertCount == 0 || tube.count == 0)
                return inTube;
            if (worldRefVerts == null || worldRefVerts.Length != vertCount)
            {
                // Silent length mismatches produced "0 verts affected" in the UI
                // with no hint why. The most common cause is mesh read/write being
                // disabled (mesh.vertices returns zero-length) or a renderer whose
                // mesh was swapped mid-bake.
                Debug.LogWarning(
                    $"[SPS] Vertex count mismatch for mesh '{(mesh != null ? mesh.name : "<null>")}' " +
                    $"(mesh={vertCount}, world={(worldRefVerts == null ? 0 : worldRefVerts.Length)}). " +
                    "Check that Read/Write is enabled on the mesh and that the renderer " +
                    "hasn't swapped its sharedMesh since detection ran.");
                return inTube;
            }

            var adjacency = BuildAdjacency(mesh);
            var euclideanDist = new float[vertCount];
            var tubeRadius = new float[vertCount];
            var tubeAspect = new float[vertCount];
            float maxRadius = 0f;

            for (int v = 0; v < vertCount; v++)
            {
                Vector3 localVert = avatarRoot != null
                    ? avatarRoot.InverseTransformPoint(worldRefVerts[v])
                    : worldRefVerts[v];

                euclideanDist[v] = tube.DistanceToTube(localVert,
                    out tubeRadius[v], out float aspect, out _);
                tubeAspect[v] = Mathf.Max(0.1f, aspect);
                if (tubeRadius[v] > maxRadius) maxRadius = tubeRadius[v];
            }

            if (maxRadius <= 0f) return inTube;

            var surfaceDistance = ComputeSurfaceDistances(
                vertices, adjacency, euclideanDist, maxRadius);

            for (int v = 0; v < vertCount; v++)
                inTube[v] = surfaceDistance[v] < (tubeRadius[v] / tubeAspect[v]);

            return inTube;
        }

        /// <summary>
        /// Multi-renderer overload for Bulge: generates matching per-position shapes
        /// on every renderer. The primary runs the full pipeline (smoothing, normalization,
        /// normal recalc). Overlays inherit the primary's vertex deltas via nearest-neighbor
        /// transfer in world space, then run their own normal recalc on their topology -
        /// this keeps overlapping verts in lockstep instead of drifting apart from separate
        /// smoothing passes.
        /// </summary>
        public static List<BlendshapeResult> GenerateBulgeBlendshapes(
            List<SkinnedMeshRenderer> renderers,
            Transform avatarRoot,
            List<PathWaypoint> path,
            int positionCount,
            float displacement,
            string outputFolder,
            string namingPattern = "",
            int smoothingPasses = 3,
            bool subdivide = false,
            int subdivisionPasses = 1,
            bool recalculateNormals = true,
            float normalFalloffSoftness = 1f,
            int normalSmoothingPasses = 0,
            int normalBoundaryRings = 1)
        {
            var results = new List<BlendshapeResult>();
            if (renderers == null || renderers.Count == 0) return results;

            // Primary: full pipeline
            var primaryResult = GenerateBulgeBlendshapes(
                renderers[0], avatarRoot, path, positionCount, displacement,
                outputFolder, namingPattern, smoothingPasses, subdivide,
                subdivisionPasses, recalculateNormals, "",
                normalFalloffSoftness, normalSmoothingPasses, normalBoundaryRings);
            results.Add(primaryResult);

            if (renderers.Count <= 1) return results;

            // Overlays: transfer from primary's baked frames
            var primaryDeltasByName = ReadBlendshapeDeltas(
                primaryResult.modifiedMesh, primaryResult.blendshapeNames);

            var primaryWorldVerts = GetSkinnedWorldRefVerts(
                renderers[0], primaryResult.modifiedMesh.vertices);
            var searchablePrimaryIndices = CollectSearchableIndices(
                primaryDeltasByName, primaryResult.modifiedMesh);

            for (int i = 1; i < renderers.Count; i++)
            {
                string suffix = "_" + SanitizeAssetName(renderers[i].gameObject.name);
                var overlay = GenerateOverlayByTransfer(
                    renderers[i], renderers[0], avatarRoot, path,
                    primaryDeltasByName, primaryWorldVerts, searchablePrimaryIndices,
                    outputFolder, "SPSBulge_GeneratedMesh" + suffix,
                    subdivide, subdivisionPasses, recalculateNormals);
                results.Add(overlay);
            }
            return results;
        }

        public static BlendshapeResult GenerateBulgeBlendshapes(
            SkinnedMeshRenderer renderer,
            Transform avatarRoot,
            List<PathWaypoint> path,
            int positionCount,
            float displacement,
            string outputFolder,
            string namingPattern = "",
            int smoothingPasses = 3,
            bool subdivide = false,
            int subdivisionPasses = 1,
            bool recalculateNormals = true,
            string meshAssetSuffix = "",
            float normalFalloffSoftness = 1f,
            int normalSmoothingPasses = 0,
            int normalBoundaryRings = 1)
        {
            var mesh = CopyMesh(renderer);

            try
            {
                if (subdivide && subdivisionPasses > 0)
                {
                    var preSubdivWorldRefs = GetSkinnedWorldRefVerts(renderer, mesh.vertices);
                    mesh = MeshSubdivider.SubdivideInRegion(
                        mesh, path, renderer.transform, avatarRoot,
                        worldRefVerts: preSubdivWorldRefs,
                        passes: subdivisionPasses);
                    // BakeMesh on the renderer needs to see the subdivided mesh so its
                    // baked vertex count matches what we project against. Without this,
                    // GetSkinnedWorldRefVerts silently falls into the bind-pose fallback,
                    // which is wrong for any non-bind-pose avatar or bone-parented overlay.
                    Undo.RecordObject(renderer, "Assign subdivided mesh");
                    renderer.sharedMesh = mesh;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }

                EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", "Computing surface distances...", 0.1f);

                var vertices = mesh.vertices;
                var smoothNormals = ComputeSmoothedNormals(mesh);

                // Pre-sample the tube once for all positions
                int segments = Mathf.Max(path.Count * 8, 20);
                var tube = CatmullRomSpline.BuildTube(path, segments);

                var names = new List<string>();
                float mag = displacement;

                var adjacency = BuildAdjacency(mesh);

                // Position t-values along the path (0 to 1)
                var positionTs = new float[positionCount];
                for (int pos = 0; pos < positionCount; pos++)
                {
                    positionTs[pos] = positionCount > 1
                        ? (float)pos / (positionCount - 1)
                        : 0.5f;
                }

                // Bell curve sigma: spacing between positions * overlap factor
                // With 1.0 spacing, sigma gives ~95% coverage across the gap
                float spacing = positionCount > 1 ? 1f / (positionCount - 1) : 1f;
                float sigma = spacing * 0.6f; // overlap: each bell extends well into neighbors

                // Pre-compute vertex data: project each vertex onto the spline once
                int vertCount = vertices.Length;
                var vertPathT = new float[vertCount];
                var vertEuclidDist = new float[vertCount];
                var vertTubeRadius = new float[vertCount];
                var vertTubeAspect = new float[vertCount];

                // Use baked positions for path projection so overlay meshes parented
                // to bones (not the avatar root) project to the correct world location.
                Vector3[] worldRefVerts = GetSkinnedWorldRefVerts(renderer, vertices);

                float maxRadius = 0f;
                for (int v = 0; v < vertCount; v++)
                {
                    Vector3 localVert = avatarRoot.InverseTransformPoint(worldRefVerts[v]);

                    float dist = tube.DistanceToTube(localVert,
                        out float radius, out float aspect, out float pathT);

                    vertPathT[v] = pathT;
                    vertEuclidDist[v] = dist;
                    vertTubeRadius[v] = radius;
                    vertTubeAspect[v] = Mathf.Max(0.1f, aspect);
                    if (radius > maxRadius) maxRadius = radius;
                }

                // Compute surface distances from centerline (follows mesh contour)
                var vertSurfDist = ComputeSurfaceDistances(
                    vertices, adjacency, vertEuclidDist, maxRadius);

                // Determine which vertices are inside the tube using surface distance.
                // Use the aspect-expanded radius so vertices within the elliptical
                // across-path extent aren't falsely excluded.
                var vertInTube = new bool[vertCount];
                for (int v = 0; v < vertCount; v++)
                    vertInTube[v] = vertSurfDist[v] < (vertTubeRadius[v] / vertTubeAspect[v]);

                EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", "Computing vertex projections...", 0.3f);

                // Phase 1: Compute and smooth deltas for all positions
                var allDeltas = new List<Vector3[]>();
                for (int pos = 0; pos < positionCount; pos++)
                {
                    EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", $"Position {pos+1}/{positionCount}...", 0.3f + 0.5f * pos / positionCount);

                    float centerT = positionTs[pos];
                    var deltas = new Vector3[vertCount];
                    bool hasAnyWeight = false;

                    for (int v = 0; v < vertCount; v++)
                    {
                        if (!vertInTube[v]) continue;

                        // Aspect stretches the ellipse: >1 elongates along path,
                        // <1 spreads across path. Preserves area (roughly).
                        float aspect = vertTubeAspect[v];
                        float effectiveSigma = sigma * aspect;
                        float effectiveRadius = vertTubeRadius[v] / aspect;

                        float dt = vertPathT[v] - centerT;
                        float alongWeight = Mathf.Exp(-(dt * dt) / (2f * effectiveSigma * effectiveSigma));

                        float perpW = 1f - (vertSurfDist[v] / effectiveRadius);
                        perpW = SmoothStep(Mathf.Clamp01(perpW));

                        float w = alongWeight * perpW;

                        if (w > 0.001f)
                        {
                            deltas[v] = smoothNormals[v] * w * mag;
                            hasAnyWeight = true;
                        }
                    }

                    if (hasAnyWeight)
                    {
                        SmoothDeltas(deltas, adjacency, smoothingPasses);
                        allDeltas.Add(deltas);
                    }
                    else
                    {
                        allDeltas.Add(null);
                    }
                }

                EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", "Normalizing...", 0.85f);

                // Phase 2: Find a single normalization scale across ALL positions.
                // This ensures consistent displacement profiles - edge and center
                // positions get the same proportional boost, preserving their shapes.
                float globalMaxMag = 0f;
                foreach (var deltas in allDeltas)
                {
                    if (deltas == null) continue;
                    for (int v = 0; v < deltas.Length; v++)
                    {
                        float m = deltas[v].magnitude;
                        if (m > globalMaxMag) globalMaxMag = m;
                    }
                }

                float globalScale = (globalMaxMag > mag * 0.1f && !Mathf.Approximately(globalMaxMag, mag))
                    ? mag / globalMaxMag
                    : 1f;

                // Phase 3: Apply uniform scale and add blendshape frames
                var meshNormals = mesh.normals;
                var meshTriangles = mesh.triangles;

                for (int pos = 0; pos < allDeltas.Count; pos++)
                {
                    var deltas = allDeltas[pos];
                    if (deltas == null) continue;

                    if (!Mathf.Approximately(globalScale, 1f))
                    {
                        for (int v = 0; v < deltas.Length; v++)
                            deltas[v] *= globalScale;
                    }

                    int idx = names.Count + 1;
                    bool hasPattern = !string.IsNullOrEmpty(namingPattern) && namingPattern.Contains("{0}");
                    string name = hasPattern
                        ? string.Format(namingPattern, idx)
                        : $"SPSBulge_Pos{idx}";

                    Vector3[] normalDeltas = recalculateNormals
                        ? ComputeNormalDeltas(vertices, deltas, meshTriangles, meshNormals, adjacency, name,
                            normalFalloffSoftness, normalSmoothingPasses, normalBoundaryRings)
                        : null;
                    mesh.AddBlendShapeFrame(name, 100f, deltas, normalDeltas, null);
                    names.Add(name);
                }

                EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", "Saving mesh...", 0.95f);

                string meshPath = $"{outputFolder}/SPSBulge_GeneratedMesh{meshAssetSuffix}.asset";
                SaveMesh(mesh, meshPath);

                Undo.RecordObject(renderer, "Assign generated mesh");
                renderer.sharedMesh = mesh;
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);

                return new BlendshapeResult
                {
                    modifiedMesh = mesh,
                    blendshapeNames = names
                };
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Computes the vertex displacement deltas for a SINGLE Bulge position
        /// on the given mesh. Parallels the per-position math in
        /// <see cref="GenerateBulgeBlendshapes(SkinnedMeshRenderer, Transform, List{PathWaypoint}, int, float, string, string, int, bool, int, bool, string, float, int, int)"/>'s
        /// Phase 1 loop, but extracted so the Normal Map Baker can reproduce
        /// the same deformation on an in-memory subdivided mesh without going
        /// through the full generator pipeline. No blendshape frames added;
        /// no mesh assignment; purely returns the deltas.
        ///
        /// Uses <c>rendererTransform.TransformPoint</c> for coordinate
        /// conversion (not <c>BakeMesh</c>) — at editor time on non-posed
        /// avatars this is equivalent, and it works on in-memory subdivided
        /// copies where BakeMesh can't help (vert count mismatch).
        ///
        /// After smoothing, the returned deltas are scaled so their peak
        /// magnitude equals <paramref name="displacement"/>, giving a
        /// per-position-consistent output regardless of where the chosen
        /// position sits along the path.
        /// </summary>
        internal static Vector3[] ComputeBulgeDisplacementForPosition(
            Mesh mesh,
            Transform rendererTransform,
            Transform avatarRoot,
            List<PathWaypoint> path,
            int positionCount,
            int targetPosition,
            float displacement,
            int smoothingPasses)
        {
            if (mesh == null || path == null || path.Count == 0 || positionCount <= 0)
                return null;

            var vertices = mesh.vertices;
            int vertCount = vertices.Length;
            if (vertCount == 0) return new Vector3[0];

            var smoothNormals = ComputeSmoothedNormals(mesh);
            var adjacency = BuildAdjacency(mesh);

            int segments = Mathf.Max(path.Count * 8, 20);
            var tube = CatmullRomSpline.BuildTube(path, segments);

            // Project each vert onto the tube centerline.
            var vertPathT = new float[vertCount];
            var vertEuclidDist = new float[vertCount];
            var vertTubeRadius = new float[vertCount];
            var vertTubeAspect = new float[vertCount];
            float maxRadius = 0f;

            for (int v = 0; v < vertCount; v++)
            {
                Vector3 worldVert = rendererTransform != null
                    ? rendererTransform.TransformPoint(vertices[v])
                    : vertices[v];
                Vector3 localVert = avatarRoot != null
                    ? avatarRoot.InverseTransformPoint(worldVert)
                    : worldVert;

                float dist = tube.DistanceToTube(localVert,
                    out float radius, out float aspect, out float pathT);

                vertPathT[v] = pathT;
                vertEuclidDist[v] = dist;
                vertTubeRadius[v] = radius;
                vertTubeAspect[v] = Mathf.Max(0.1f, aspect);
                if (radius > maxRadius) maxRadius = radius;
            }

            var vertSurfDist = ComputeSurfaceDistances(
                vertices, adjacency, vertEuclidDist, maxRadius);

            var vertInTube = new bool[vertCount];
            for (int v = 0; v < vertCount; v++)
                vertInTube[v] = vertSurfDist[v] < (vertTubeRadius[v] / vertTubeAspect[v]);

            // Target position's t-value + gaussian sigma along the path.
            float centerT = positionCount > 1
                ? (float)targetPosition / (positionCount - 1)
                : 0.5f;
            float spacing = positionCount > 1 ? 1f / (positionCount - 1) : 1f;
            float sigma = spacing * 0.6f;

            // Compute per-vertex weighted delta for this one position.
            var deltas = new Vector3[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                if (!vertInTube[v]) continue;

                float aspect = vertTubeAspect[v];
                float effectiveSigma = sigma * aspect;
                float effectiveRadius = vertTubeRadius[v] / aspect;

                float dt = vertPathT[v] - centerT;
                float alongWeight = Mathf.Exp(-(dt * dt) / (2f * effectiveSigma * effectiveSigma));

                float perpW = 1f - (vertSurfDist[v] / effectiveRadius);
                perpW = SmoothStep(Mathf.Clamp01(perpW));

                float w = alongWeight * perpW;
                if (w > 0.001f)
                    deltas[v] = smoothNormals[v] * w * displacement;
            }

            SmoothDeltas(deltas, adjacency, smoothingPasses);

            // Per-position normalization: bring the peak magnitude back up
            // to `displacement` (smoothing attenuates peaks). Only rescale
            // when the measured peak deviates meaningfully from the target.
            float peakMag = 0f;
            for (int v = 0; v < vertCount; v++)
            {
                float m = deltas[v].magnitude;
                if (m > peakMag) peakMag = m;
            }
            if (peakMag > displacement * 0.1f
                && !Mathf.Approximately(peakMag, displacement))
            {
                float scale = displacement / peakMag;
                for (int v = 0; v < vertCount; v++)
                    deltas[v] *= scale;
            }

            return deltas;
        }

        // --- Internal helpers ---

        private static Mesh CopyMesh(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
                throw new System.ArgumentNullException(nameof(renderer),
                    "[SPS Effects] BlendshapeGenerator: renderer is null.");
            if (renderer.sharedMesh == null)
                throw new System.InvalidOperationException(
                    "[SPS Effects] BlendshapeGenerator: renderer.sharedMesh is null. " +
                    "Assign a mesh to the SkinnedMeshRenderer before generating blendshapes.");

            return Object.Instantiate(renderer.sharedMesh);
        }

        /// <summary>
        /// Hermite smoothstep: 3t² - 2t³. Much gentler than quadratic w*w.
        /// </summary>
        private static float SmoothStep(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Computes area-weighted smoothed normals per vertex.
        /// On low-poly meshes, raw normals are faceted and cause spikey displacement.
        /// This averages normals across all faces sharing each vertex position,
        /// weighted by triangle area, producing smooth outward directions.
        /// </summary>
        private static Vector3[] ComputeSmoothedNormals(Mesh mesh)
            => ComputeSmoothedNormals(mesh.vertices, mesh.triangles, mesh.normals);

        /// <summary>
        /// Area-weighted smoothed normals from raw arrays.
        /// Used for both original and deformed vertex sets.
        /// </summary>
        private static Vector3[] ComputeSmoothedNormals(
            Vector3[] vertices, int[] triangles, Vector3[] fallbackNormals)
        {
            int vertCount = vertices.Length;

            // Accumulate area-weighted face normals per vertex
            var accumulated = new Vector3[vertCount];

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];

                Vector3 v0 = vertices[i0];
                Vector3 v1 = vertices[i1];
                Vector3 v2 = vertices[i2];

                // Cross product gives area-weighted normal (magnitude = 2x triangle area)
                Vector3 faceNormal = Vector3.Cross(v1 - v0, v2 - v0);

                accumulated[i0] += faceNormal;
                accumulated[i1] += faceNormal;
                accumulated[i2] += faceNormal;
            }

            // Also merge normals for vertices that share the same position
            // (common on low-poly meshes with hard edges / split UVs)
            var positionBuckets = new Dictionary<long, List<int>>();
            for (int v = 0; v < vertCount; v++)
            {
                // Quantize position to merge nearby vertices
                long key = QuantizePosition(vertices[v]);
                if (!positionBuckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>();
                    positionBuckets[key] = bucket;
                }
                bucket.Add(v);
            }

            // Average normals for co-located vertices
            foreach (var bucket in positionBuckets.Values)
            {
                if (bucket.Count <= 1) continue;

                Vector3 merged = Vector3.zero;
                foreach (int v in bucket)
                    merged += accumulated[v];

                foreach (int v in bucket)
                    accumulated[v] = merged;
            }

            // Normalize
            var result = new Vector3[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                // Threshold is scale-independent - face-normal cross products on
                // dense avatar meshes have sqrMagnitude around 1e-8, not 1e-4
                result[v] = accumulated[v].sqrMagnitude > 1e-20f
                    ? accumulated[v].normalized
                    : fallbackNormals[v]; // fallback to original normal
            }

            return result;
        }

        /// <summary>
        /// Computes normal deltas for a single blendshape. Only processes vertices
        /// that moved (and their 1-ring neighbors, whose normals depend on moved
        /// face normals). Applies a soft falloff based on neighborhood movement so
        /// the edge of the affected region blends smoothly into unchanged shading.
        /// </summary>
        private static Vector3[] ComputeNormalDeltas(
            Vector3[] originalVertices,
            Vector3[] vertexDeltas,
            int[] triangles,
            Vector3[] originalNormals,
            List<int>[] adjacency,
            string blendshapeName = "",
            float falloffSoftness = 1f,
            int smoothingPasses = 0,
            int boundaryRings = 1)
        {
            int vertCount = originalVertices.Length;
            var normalDeltas = new Vector3[vertCount];

            // Find moved verts and peak delta magnitude (for falloff normalization)
            var moved = new bool[vertCount];
            float peakSqrMag = 0f;
            int movedCount = 0;
            for (int v = 0; v < vertCount; v++)
            {
                float m = vertexDeltas[v].sqrMagnitude;
                if (m > 0.0000001f)
                {
                    moved[v] = true;
                    movedCount++;
                    if (m > peakSqrMag) peakSqrMag = m;
                }
            }
            if (peakSqrMag < 0.0000001f)
            {
                Debug.Log($"[SPS Normals] {blendshapeName}: NO MOVED VERTS - skipping normal delta computation.");
                return normalDeltas;
            }
            float peakMag = Mathf.Sqrt(peakSqrMag);

            // Affected = moved + N-ring neighbors. Defaults to 1 ring (preserves old behavior).
            // Larger N widens the boundary-blend band so the normal transition spans more tris —
            // needed on low-poly meshes where a 1-tri-wide transition reads as a visible crease.
            // Only verts with ringDist > 0 use the ring-based falloff below; moved verts
            // (ringDist == 0) keep the original per-vert scaling.
            var affected = new bool[vertCount];
            int[] ringDist = null;
            if (boundaryRings <= 1)
            {
                for (int v = 0; v < vertCount; v++)
                {
                    if (!moved[v]) continue;
                    affected[v] = true;
                    foreach (int n in adjacency[v])
                        affected[n] = true;
                }
            }
            else
            {
                ringDist = new int[vertCount];
                for (int i = 0; i < vertCount; i++) ringDist[i] = -1;
                var bfs = new Queue<int>();
                for (int v = 0; v < vertCount; v++)
                {
                    if (moved[v])
                    {
                        ringDist[v] = 0;
                        affected[v] = true;
                        bfs.Enqueue(v);
                    }
                }
                while (bfs.Count > 0)
                {
                    int v = bfs.Dequeue();
                    if (ringDist[v] >= boundaryRings) continue;
                    foreach (int n in adjacency[v])
                    {
                        if (ringDist[n] != -1) continue;
                        ringDist[n] = ringDist[v] + 1;
                        affected[n] = true;
                        bfs.Enqueue(n);
                    }
                }
            }

            // Accumulate face normals for triangles touching affected verts, in
            // BOTH deformed and undeformed configurations. The undeformed pass
            // gives us a baseline normal computed with the SAME area-weighted
            // method, so any reconstruction bias (vs Unity's stored per-vert
            // normals — which may be authored in Blender, carry custom split
            // normals, etc.) cancels out when we subtract. A vert with zero
            // movement now always produces zero delta, regardless of how the
            // source mesh's normals were authored.
            var accumulated = new Vector3[vertCount];
            var accumulatedBase = new Vector3[vertCount];
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];

                if (!affected[i0] && !affected[i1] && !affected[i2]) continue;

                Vector3 b0 = originalVertices[i0];
                Vector3 b1 = originalVertices[i1];
                Vector3 b2 = originalVertices[i2];
                Vector3 baseNormal = Vector3.Cross(b1 - b0, b2 - b0);

                Vector3 d0 = b0 + vertexDeltas[i0];
                Vector3 d1 = b1 + vertexDeltas[i1];
                Vector3 d2 = b2 + vertexDeltas[i2];
                Vector3 faceNormal = Vector3.Cross(d1 - d0, d2 - d0);

                if (affected[i0]) { accumulated[i0] += faceNormal; accumulatedBase[i0] += baseNormal; }
                if (affected[i1]) { accumulated[i1] += faceNormal; accumulatedBase[i1] += baseNormal; }
                if (affected[i2]) { accumulated[i2] += faceNormal; accumulatedBase[i2] += baseNormal; }
            }

            // Merge co-located affected vertices (so UV seams / hard edges stay consistent).
            // Apply the SAME merge to the baseline accumulations so the subtract is symmetric.
            var buckets = new Dictionary<long, List<int>>();
            for (int v = 0; v < vertCount; v++)
            {
                if (!affected[v]) continue;
                long key = QuantizePosition(originalVertices[v]);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>();
                    buckets[key] = bucket;
                }
                bucket.Add(v);
            }
            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count <= 1) continue;
                Vector3 merged = Vector3.zero;
                Vector3 mergedBase = Vector3.zero;
                foreach (int v in bucket) { merged += accumulated[v]; mergedBase += accumulatedBase[v]; }
                foreach (int v in bucket) { accumulated[v] = merged; accumulatedBase[v] = mergedBase; }
            }

            // Compute final delta with soft falloff based on 1-ring max movement
            // (boundary verts that barely moved get a smaller normal delta - no hard seam)
            int affectedCount = 0;
            int nonzeroDeltaCount = 0;
            float maxDeltaMag = 0f;
            float sumDeltaMag = 0f;
            for (int v = 0; v < vertCount; v++)
            {
                if (FeatureFlags.DebugUiEnabled && affected[v]) affectedCount++;
                if (!affected[v]) continue;
                // Threshold is scale-independent - face-normal cross products on
                // dense avatar meshes have sqrMagnitude around 1e-8, not 1e-4
                if (accumulated[v].sqrMagnitude < 1e-20f) continue;

                Vector3 deformedNormal = accumulated[v].normalized;
                // Symmetric baseline: the recomputed-from-undeformed normal. Falls back
                // to Unity's stored normal only if the baseline accumulation was degenerate
                // (e.g. a degenerate tri fan), which effectively never happens in practice.
                Vector3 baselineNormal = accumulatedBase[v].sqrMagnitude > 1e-20f
                    ? accumulatedBase[v].normalized
                    : originalNormals[v];

                float neighborhoodMag;
                if (ringDist == null || ringDist[v] <= 0)
                {
                    // Moved verts (or legacy 1-ring path): own + 1-ring max, as before.
                    neighborhoodMag = vertexDeltas[v].magnitude;
                    foreach (int n in adjacency[v])
                    {
                        float m = vertexDeltas[n].magnitude;
                        if (m > neighborhoodMag) neighborhoodMag = m;
                    }
                }
                else
                {
                    // Outside moved region in the expanded boundary band: decay from
                    // ring 1 to ring N+1 so that ring 1 itself gets a sub-peak falloff
                    // (produces an actual gradient starting at the boundary rather than
                    // a flat full-strength band of ring-1 verts).
                    float boundaryT = 1f - ringDist[v] / (float)(boundaryRings + 1);
                    neighborhoodMag = peakMag * boundaryT;
                }

                // Smoothstep falloff: 0 at boundary, 1 at peak movement.
                // Softness exponent = 1/softness: softness>1 → exponent<1 → curve pushed
                // toward 1 → wider blend at the boundary (hides tri edges).
                float t = Mathf.Clamp01(neighborhoodMag / peakMag);
                float falloff = t * t * (3f - 2f * t);
                if (!Mathf.Approximately(falloffSoftness, 1f) && falloffSoftness > 0f)
                    falloff = Mathf.Pow(falloff, 1f / falloffSoftness);

                normalDeltas[v] = (deformedNormal - baselineNormal) * falloff;

                if (FeatureFlags.DebugUiEnabled)
                {
                    float deltaMag = normalDeltas[v].magnitude;
                    if (deltaMag > 0.00001f)
                    {
                        nonzeroDeltaCount++;
                        sumDeltaMag += deltaMag;
                        if (deltaMag > maxDeltaMag) maxDeltaMag = deltaMag;
                    }
                }
            }

            // Optional 1-ring Laplacian blur on the normal deltas. Averages each affected
            // vert with ALL its neighbors (affected or not). Unaffected neighbors contribute
            // their implicit zero delta, which diffuses the boundary outward and eliminates
            // the step from last-affected-vert to first-unaffected-vert. Interior verts are
            // unchanged because all their neighbors are affected (same as before); only
            // boundary verts see their delta pulled toward zero.
            if (smoothingPasses > 0)
            {
                var scratch = new Vector3[vertCount];
                for (int pass = 0; pass < smoothingPasses; pass++)
                {
                    for (int v = 0; v < vertCount; v++)
                    {
                        if (!affected[v]) { scratch[v] = normalDeltas[v]; continue; }
                        Vector3 sum = normalDeltas[v];
                        int count = 1;
                        foreach (int n in adjacency[v])
                        {
                            sum += normalDeltas[n]; // zero for unaffected, correct weighting
                            count++;
                        }
                        scratch[v] = Vector3.Lerp(normalDeltas[v], sum / count, 0.5f);
                    }
                    (normalDeltas, scratch) = (scratch, normalDeltas);
                }
            }

            if (FeatureFlags.DebugUiEnabled)
            {
                float avgDeltaMag = nonzeroDeltaCount > 0 ? sumDeltaMag / nonzeroDeltaCount : 0f;
                Debug.Log(
                    $"[SPS Normals] {blendshapeName}: " +
                    $"moved={movedCount}, affected={affectedCount}, peakVertDelta={peakMag:F5}m | " +
                    $"non-zero normal deltas={nonzeroDeltaCount}, " +
                    $"max={maxDeltaMag:F5}, avg={avgDeltaMag:F5} | " +
                    $"softness={falloffSoftness:F2}, smoothingPasses={smoothingPasses}, boundaryRings={boundaryRings}");
            }

            return normalDeltas;
        }

        /// <summary>
        /// Quantizes a position to a grid for merging co-located vertices.
        /// Uses ~0.0001 unit precision.
        /// </summary>
        private static long QuantizePosition(Vector3 pos)
        {
            long x = (long)(pos.x * 10000f);
            long y = (long)(pos.y * 10000f);
            long z = (long)(pos.z * 10000f);
            return x * 73856093L ^ y * 19349663L ^ z * 83492791L;
        }

        /// <summary>
        /// Builds a vertex adjacency list from the mesh triangles.
        /// Also links co-located vertices (same position, different index)
        /// so displacement smoothing works across UV seams and hard edges.
        /// </summary>
        internal static List<int>[] BuildAdjacency(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            int vertCount = vertices.Length;

            // HashSet during build so the co-located-bucket merge (which can
            // touch dozens of verts that share a position under UV seams)
            // doesn't go quadratic via List.Contains on each Add.
            var sets = new HashSet<int>[vertCount];
            for (int v = 0; v < vertCount; v++)
                sets[v] = new HashSet<int>();

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                sets[a].Add(b); sets[b].Add(a);
                sets[b].Add(c); sets[c].Add(b);
                sets[c].Add(a); sets[a].Add(c);
            }

            // Link co-located vertices (same position, different index)
            var buckets = new Dictionary<long, List<int>>();
            for (int v = 0; v < vertCount; v++)
            {
                long key = QuantizePosition(vertices[v]);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                list.Add(v);
            }

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count <= 1) continue;
                for (int i = 0; i < bucket.Count; i++)
                    for (int j = i + 1; j < bucket.Count; j++)
                    {
                        sets[bucket[i]].Add(bucket[j]);
                        sets[bucket[j]].Add(bucket[i]);
                    }
            }

            // Flatten to List<int>[] - callers iterate but don't mutate.
            var adj = new List<int>[vertCount];
            for (int v = 0; v < vertCount; v++)
                adj[v] = new List<int>(sets[v]);
            return adj;
        }

        /// <summary>
        /// Laplacian smoothing of displacement deltas.
        /// Averages each vertex's delta with its neighbors over multiple passes.
        /// Eliminates spikes on low-poly meshes while preserving the overall shape.
        ///
        /// The pass count adapts: sparse areas (few affected verts per neighbor)
        /// get more smoothing automatically because the averaging spreads further.
        /// </summary>
        private static void SmoothDeltas(Vector3[] deltas, List<int>[] adjacency, int passes)
        {
            int vertCount = deltas.Length;
            var temp = new Vector3[vertCount];

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(deltas, temp, vertCount);

                for (int v = 0; v < vertCount; v++)
                {
                    // Skip unaffected vertices
                    if (temp[v].sqrMagnitude < 0.0000001f) continue;

                    Vector3 sum = temp[v];
                    int count = 1;

                    foreach (int n in adjacency[v])
                    {
                        sum += temp[n];
                        count++;
                    }

                    deltas[v] = sum / count;
                }
            }
        }

        /// <summary>
        /// Rescales all deltas so the maximum magnitude equals targetMag.
        /// Ensures every blendshape produces the same peak displacement
        /// regardless of how smoothing affected different positions.
        /// </summary>
        private static void NormalizeDeltas(Vector3[] deltas, float targetMag)
        {
            float maxMag = 0f;
            for (int i = 0; i < deltas.Length; i++)
            {
                float m = deltas[i].magnitude;
                if (m > maxMag) maxMag = m;
            }

            // Only normalize if the peak is at least 10% of target.
            // If smoothing crushed it below that, the data is too degraded
            // to rescale safely (would amplify noise/spikes).
            if (maxMag < targetMag * 0.1f) return;
            if (Mathf.Approximately(maxMag, targetMag)) return;

            float scale = targetMag / maxMag;
            for (int i = 0; i < deltas.Length; i++)
                deltas[i] *= scale;
        }

        /// <summary>
        /// Computes surface distances from the tube centerline for each vertex.
        /// Instead of straight-line Euclidean distance (which forms circles),
        /// this floods outward along mesh edges from seed vertices near the
        /// centerline, producing distances that follow the mesh surface contour.
        ///
        /// On an oval body part, the falloff wraps around the surface rather than
        /// cutting through the interior as a circle would.
        /// </summary>
        internal static float[] ComputeSurfaceDistances(
            Vector3[] vertices, List<int>[] adjacency,
            float[] euclideanDist, float maxRadius)
        {
            int vertCount = vertices.Length;
            var surfDist = new float[vertCount];
            for (int v = 0; v < vertCount; v++)
                surfDist[v] = float.MaxValue;

            // Seed: vertices within 10% of max radius from the centerline
            float seedThreshold = maxRadius * 0.1f;
            var queue = new Queue<int>();

            for (int v = 0; v < vertCount; v++)
            {
                if (euclideanDist[v] < seedThreshold)
                {
                    surfDist[v] = 0f;
                    queue.Enqueue(v);
                }
            }

            // If no seeds found (centerline doesn't pass near any vertex),
            // fall back to using the closest vertex as single seed - but only
            // when that vertex is actually reachable by the tube. If the whole
            // path is off-mesh (e.g. authored then avatar moved, or the empty-
            // position unit test), seeding anyway would fabricate a phantom
            // deformation at the nearest vertex even though nothing is in range.
            if (queue.Count == 0)
            {
                float minDist = float.MaxValue;
                int minIdx = 0;
                for (int v = 0; v < vertCount; v++)
                {
                    if (euclideanDist[v] < minDist)
                    {
                        minDist = euclideanDist[v];
                        minIdx = v;
                    }
                }
                if (minDist <= maxRadius)
                {
                    surfDist[minIdx] = 0f;
                    queue.Enqueue(minIdx);
                }
            }

            // SPFA: propagate along mesh edges, accumulating edge lengths
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                float vDist = surfDist[v];
                if (vDist > maxRadius) continue;

                foreach (int n in adjacency[v])
                {
                    float edgeLen = Vector3.Distance(vertices[v], vertices[n]);
                    float newDist = vDist + edgeLen;

                    if (newDist < surfDist[n] && newDist <= maxRadius * 1.2f)
                    {
                        surfDist[n] = newDist;
                        queue.Enqueue(n);
                    }
                }
            }

            // Fallback for topology-disconnected verts. When a renderer's mesh
            // contains both body and overlay geometry (fluff/fur) as separate
            // triangle islands with no shared or co-located verts, the flood
            // fill above can't cross the gap — it seeds inside whichever layer
            // the path sits on and leaves the other layer at MaxValue. Body
            // verts physically inside the tube would then fail the in-tube
            // check and never deform. Using Euclidean distance for these
            // stranded verts still respects the tube radius (they only get
            // rescued when Euclidean is inside the tube) and doesn't touch
            // anything the flood fill reached — so oval surface-wrap behavior
            // elsewhere is unaffected.
            for (int v = 0; v < vertCount; v++)
            {
                if (surfDist[v] >= float.MaxValue * 0.5f && euclideanDist[v] < maxRadius)
                    surfDist[v] = euclideanDist[v];
            }

            return surfDist;
        }

        /// <summary>
        /// Computes per-vertex weights for the entire path tube region.
        /// </summary>
        private static float[] ComputePathWeights(
            Vector3[] vertices,
            List<PathWaypoint> path,
            Transform meshTransform, Transform avatarRoot,
            List<int>[] adjacency,
            Vector3[] worldRefVerts = null)
        {
            var weights = new float[vertices.Length];
            int vertCount = vertices.Length;

            int segments = Mathf.Max(path.Count * 8, 20);
            var tube = CatmullRomSpline.BuildTube(path, segments);

            // Euclidean distances first (for seeding)
            var euclidDist = new float[vertCount];
            var tubeRadius = new float[vertCount];
            float maxRadius = 0f;

            for (int v = 0; v < vertCount; v++)
            {
                // Use baked world positions if provided (correct for bone-parented overlays);
                // fall back to transforming bind-pose verts via meshTransform.
                Vector3 worldVert = worldRefVerts != null
                    ? worldRefVerts[v]
                    : meshTransform.TransformPoint(vertices[v]);
                Vector3 localVert = avatarRoot.InverseTransformPoint(worldVert);
                euclidDist[v] = tube.DistanceToTube(localVert, out tubeRadius[v]);
                if (tubeRadius[v] > maxRadius) maxRadius = tubeRadius[v];
            }

            // Surface distances along mesh edges
            var surfDist = ComputeSurfaceDistances(
                vertices, adjacency, euclidDist, maxRadius);

            for (int v = 0; v < vertCount; v++)
            {
                if (surfDist[v] < tubeRadius[v])
                {
                    float w = 1f - (surfDist[v] / tubeRadius[v]);
                    weights[v] = SmoothStep(w);
                }
            }

            return weights;
        }

        /// <summary>
        /// Primary blendshape deltas captured for overlay transfer. Carries both
        /// vertex and normal deltas so overlays can inherit normals via NN mapping
        /// instead of recomputing them on different topology (which desyncs from
        /// the primary, especially at the affected-region boundary).
        /// </summary>
        private class PrimaryDeltaSet
        {
            public Vector3[] vertexDeltas;
            public Vector3[] normalDeltas;
        }

        /// <summary>
        /// Reads back vertex + normal deltas for each named blendshape (frame 0) from
        /// the baked primary mesh. Used by overlay transfer to mirror what the primary
        /// actually ended up with post-smoothing-and-normalization.
        /// </summary>
        private static Dictionary<string, PrimaryDeltaSet> ReadBlendshapeDeltas(
            Mesh mesh, List<string> names)
        {
            var result = new Dictionary<string, PrimaryDeltaSet>();
            if (mesh == null || names == null) return result;

            int vertCount = mesh.vertexCount;
            var tmpV = new Vector3[vertCount];
            var tmpN = new Vector3[vertCount];
            var tmpT = new Vector3[vertCount];

            foreach (var name in names)
            {
                int idx = mesh.GetBlendShapeIndex(name);
                if (idx < 0) continue;
                mesh.GetBlendShapeFrameVertices(idx, 0, tmpV, tmpN, tmpT);

                var vCopy = new Vector3[vertCount];
                var nCopy = new Vector3[vertCount];
                System.Array.Copy(tmpV, vCopy, vertCount);
                System.Array.Copy(tmpN, nCopy, vertCount);

                // If no normal deltas were written (recalculateNormals was off on
                // the primary), leave normalDeltas null so overlays skip transfer.
                bool anyNormal = false;
                for (int i = 0; i < vertCount && !anyNormal; i++)
                    if (nCopy[i].sqrMagnitude > 1e-20f) anyNormal = true;

                result[name] = new PrimaryDeltaSet
                {
                    vertexDeltas = vCopy,
                    normalDeltas = anyNormal ? nCopy : null
                };
            }
            return result;
        }

        /// <summary>
        /// Returns the set of indices to search for overlay-vertex matching:
        /// every primary vertex that moves in any blendshape, plus their 1-ring
        /// neighbors. Including the 1-ring matters for correctness - overlay
        /// vertices that happen to sit exactly on a primary boundary vert
        /// (delta = 0) need to find that vert in the search, otherwise NN picks
        /// a moved neighbor and the overlay deforms when it shouldn't.
        /// </summary>
        private static List<int> CollectSearchableIndices(
            Dictionary<string, PrimaryDeltaSet> deltasByName, Mesh primaryMesh)
        {
            int vertCount = 0;
            foreach (var kv in deltasByName)
            { vertCount = kv.Value.vertexDeltas.Length; break; }
            var moved = new bool[vertCount];
            foreach (var kv in deltasByName)
            {
                var deltas = kv.Value.vertexDeltas;
                for (int v = 0; v < deltas.Length; v++)
                    if (deltas[v].sqrMagnitude > 0.0000001f) moved[v] = true;
            }

            // Expand to 1-ring of moved verts
            var searchable = new bool[vertCount];
            var adjacency = BuildAdjacency(primaryMesh);
            for (int v = 0; v < vertCount; v++)
            {
                if (!moved[v]) continue;
                searchable[v] = true;
                foreach (int n in adjacency[v]) searchable[n] = true;
            }

            var indices = new List<int>();
            for (int v = 0; v < vertCount; v++)
                if (searchable[v]) indices.Add(v);
            return indices;
        }

        /// <summary>
        /// Generates blendshape frames on an overlay mesh by transferring the
        /// primary's deltas via nearest-neighbor matching in world space.
        /// Recomputing normals on the overlay's own topology produces a shading
        /// seam at the affected-region boundary (amplified by Boundary Blend Rings),
        /// so we inherit the primary's normals directly. At coincident verts we
        /// rewrite BOTH the base normal and the delta to match the primary's -
        /// matching only the final (w=1) normal still left the overlay shading
        /// differently at every intermediate weight, visible as a ghost during
        /// preview.
        /// </summary>
        private static BlendshapeResult GenerateOverlayByTransfer(
            SkinnedMeshRenderer overlayRenderer,
            SkinnedMeshRenderer primaryRenderer,
            Transform avatarRoot,
            List<PathWaypoint> path,
            Dictionary<string, PrimaryDeltaSet> primaryDeltasByName,
            Vector3[] primaryWorldVerts,
            List<int> searchablePrimaryIndices,
            string outputFolder,
            string meshAssetName,
            bool subdivide,
            int subdivisionPasses,
            bool recalculateNormals)
        {
            var mesh = CopyMesh(overlayRenderer);
            try
            {
                if (subdivide && subdivisionPasses > 0)
                {
                    var preSubdivWorldRefs = GetSkinnedWorldRefVerts(overlayRenderer, mesh.vertices);
                    mesh = MeshSubdivider.SubdivideInRegion(
                        mesh, path, overlayRenderer.transform, avatarRoot,
                        worldRefVerts: preSubdivWorldRefs,
                        passes: subdivisionPasses);
                    Undo.RecordObject(overlayRenderer, "Assign subdivided mesh");
                    overlayRenderer.sharedMesh = mesh;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(overlayRenderer);
                }

                var vertices = mesh.vertices;
                // Authored per-vert base normals for both meshes — needed so the
                // overlay's stored delta targets the primary's final rendered normal
                // regardless of how each mesh's custom normals were authored.
                var overlayBaseNormals = mesh.normals;
                var primaryBaseNormals = primaryRenderer.sharedMesh != null
                    ? primaryRenderer.sharedMesh.normals
                    : null;

                var overlayWorldVerts = GetSkinnedWorldRefVerts(overlayRenderer, vertices);

                // Nearest-primary-vert map. 2cm threshold lets tight overlays match
                // while keeping distant accessory parts (that shouldn't deform) out.
                // Search includes 1-ring of moved verts so an overlay vert exactly
                // on a primary boundary vert (delta=0) finds that vert and stays put,
                // instead of inheriting a neighbor's non-zero delta.
                // KDTree over the searchable subset turns the per-overlay-vert
                // lookup from O(|searchable|) linear to O(log |searchable|).
                const float matchThresholdSq = 0.02f * 0.02f;
                var searchablePoints = new Vector3[searchablePrimaryIndices.Count];
                for (int i = 0; i < searchablePrimaryIndices.Count; i++)
                    searchablePoints[i] = primaryWorldVerts[searchablePrimaryIndices[i]];
                var searchTree = new KDTreeNearest(searchablePoints);

                var nnMap = new int[vertices.Length];
                for (int v = 0; v < vertices.Length; v++)
                {
                    int localIdx = searchTree.FindNearest(overlayWorldVerts[v]);
                    if (localIdx < 0) { nnMap[v] = -1; continue; }
                    int primaryIdx = searchablePrimaryIndices[localIdx];
                    float sd = (primaryWorldVerts[primaryIdx] - overlayWorldVerts[v]).sqrMagnitude;
                    nnMap[v] = sd < matchThresholdSq ? primaryIdx : -1;
                }

                var primaryTransform = primaryRenderer.transform;
                var overlayTransform = overlayRenderer.transform;
                var names = new List<string>();

                // Copy primary base normals onto the overlay at matched verts.
                // Unity blends normals linearly (base + w·delta), so matching only
                // the delta leaves a (1-w)·(overlayBase - primaryBase) residual that
                // shows as a visible seam at every intermediate weight during preview.
                // Matching the base too makes the blend identical at every weight.
                bool overrideBaseNormals = recalculateNormals && primaryBaseNormals != null
                    && primaryBaseNormals.Length == primaryWorldVerts.Length;
                if (overrideBaseNormals)
                {
                    bool anyPrimaryHasNormals = false;
                    foreach (var kv in primaryDeltasByName)
                    {
                        if (kv.Value.normalDeltas != null) { anyPrimaryHasNormals = true; break; }
                    }
                    if (anyPrimaryHasNormals)
                    {
                        bool anyOverridden = false;
                        for (int v = 0; v < vertices.Length; v++)
                        {
                            int nn = nnMap[v];
                            if (nn < 0) continue;
                            Vector3 worldBase = primaryTransform.TransformDirection(primaryBaseNormals[nn]);
                            overlayBaseNormals[v] = overlayTransform.InverseTransformDirection(worldBase);
                            anyOverridden = true;
                        }
                        if (anyOverridden) mesh.normals = overlayBaseNormals;
                    }
                }

                foreach (var kv in primaryDeltasByName)
                {
                    string name = kv.Key;
                    var primaryVerts = kv.Value.vertexDeltas;
                    var primaryNormals = kv.Value.normalDeltas;

                    var overlayVerts = new Vector3[vertices.Length];
                    // Allocate the normal array only when we're going to write it,
                    // otherwise AddBlendShapeFrame skips normals (null) as intended.
                    bool writeNormals = recalculateNormals && primaryNormals != null
                        && primaryBaseNormals != null && primaryBaseNormals.Length == primaryVerts.Length;
                    Vector3[] overlayNormals = writeNormals ? new Vector3[vertices.Length] : null;

                    for (int v = 0; v < vertices.Length; v++)
                    {
                        int nn = nnMap[v];
                        if (nn < 0) continue;

                        // Vertex delta: primary mesh-local → world → overlay mesh-local
                        Vector3 worldDelta = primaryTransform.TransformVector(primaryVerts[nn]);
                        overlayVerts[v] = overlayTransform.InverseTransformVector(worldDelta);

                        // Normal delta: primary's delta rotated into overlay space.
                        // Base was already unified above, so this is all that's needed.
                        if (writeNormals)
                        {
                            Vector3 worldNormalDelta = primaryTransform.TransformDirection(primaryNormals[nn]);
                            overlayNormals[v] = overlayTransform.InverseTransformDirection(worldNormalDelta);
                        }
                    }

                    mesh.AddBlendShapeFrame(name, 100f, overlayVerts, overlayNormals, null);
                    names.Add(name);
                }

                string meshPath = $"{outputFolder}/{meshAssetName}.asset";
                SaveMesh(mesh, meshPath);

                Undo.RecordObject(overlayRenderer, "Assign generated mesh");
                overlayRenderer.sharedMesh = mesh;
                PrefabUtility.RecordPrefabInstancePropertyModifications(overlayRenderer);

                return new BlendshapeResult
                {
                    modifiedMesh = mesh,
                    blendshapeNames = names
                };
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Returns world-space positions of each vertex via SkinnedMeshRenderer.BakeMesh,
        /// which accounts for bone skinning. Required for overlay meshes whose renderer
        /// transform doesn't match the avatar root (e.g. overlays parented to bones).
        /// Falls back to bind-pose-transformed-by-renderer if BakeMesh fails or count
        /// changes mid-bake (defensive).
        /// </summary>
        internal static Vector3[] GetSkinnedWorldRefVerts(
            SkinnedMeshRenderer renderer, Vector3[] bindPoseVerts,
            Mesh bakedMeshScratch = null)
        {
            int count = bindPoseVerts.Length;
            var result = new Vector3[count];
            var rTransform = renderer.transform;

            var bakedMesh = bakedMeshScratch ?? new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            bool ownsScratch = bakedMeshScratch == null;
            try
            {
                renderer.BakeMesh(bakedMesh, true);
                var baked = bakedMesh.vertices;
                if (baked.Length == count)
                {
                    for (int v = 0; v < count; v++)
                        result[v] = rTransform.TransformPoint(baked[v]);
                    return result;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SPS] BakeMesh failed for '{renderer.name}', " +
                    $"overlay alignment may be inaccurate: {e.Message}");
            }
            finally
            {
                if (ownsScratch)
                    Object.DestroyImmediate(bakedMesh);
            }

            // Fallback: bind-pose × renderer transform (correct only when renderer is
            // at avatar root and bones are at bind pose)
            for (int v = 0; v < count; v++)
                result[v] = rTransform.TransformPoint(bindPoseVerts[v]);
            return result;
        }

        private static string SanitizeAssetName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "mesh";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static void SaveMesh(Mesh mesh, string path)
        {
            // AssetDatabase.CreateAsset requires the parent folder to exist.
            // Cover both generator callsites (Bulge primary / overlay) here
            // rather than repeating EnsureFolder at each call.
            string folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
                SpsAnimationUtility.EnsureFolder(folder);

            // Write to a temp path first, then atomically replace. If CreateAsset
            // throws (disk full, locked path), the existing asset at `path` is
            // untouched — callers that captured the previous mesh reference can
            // still use it. Use ".tmp.asset" so Unity accepts the extension.
            string tempPath = System.IO.Path.ChangeExtension(path, "tmp.asset");
            if (AssetDatabase.LoadAssetAtPath<Mesh>(tempPath) != null)
                AssetDatabase.DeleteAsset(tempPath);

            AssetDatabase.CreateAsset(mesh, tempPath);

            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
                AssetDatabase.DeleteAsset(path);

            string moveError = AssetDatabase.MoveAsset(tempPath, path);
            if (!string.IsNullOrEmpty(moveError))
                throw new System.IO.IOException(
                    $"[SPS Effects] Failed to rename '{tempPath}' to '{path}': {moveError}");

            AssetDatabase.SaveAssets();

            Debug.Log($"[SPS Effects] Generated mesh saved: {path} " +
                $"({mesh.blendShapeCount} blendshapes)");
        }
    }
}
