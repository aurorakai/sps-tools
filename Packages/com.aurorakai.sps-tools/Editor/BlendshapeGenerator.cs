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
        /// An overlay vert counts as the same surface as the primary (and takes its
        /// normals) when its normal is within ~45° of the primary's there.
        /// </summary>
        internal const float SharedSurfaceMinNormalDot = 0.7f;

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
            var tube = CatmullRomSpline.BuildTube(path);
            var worldVerts = GetSkinnedWorldRefVerts(renderer, verts);
            var inRegion = ComputeVerticesInTube(mesh, verts, worldVerts, at, tube);

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
            return ComputeVerticesInTube(
                mesh, mesh != null ? mesh.vertices : null,
                worldRefVerts, avatarRoot, tube);
        }

        internal static bool[] ComputeVerticesInTube(
            Mesh mesh,
            Vector3[] vertices,
            Vector3[] worldRefVerts,
            Transform avatarRoot,
            CatmullRomSpline.SplineTube tube)
        {
            int vertCount = vertices != null ? vertices.Length : 0;
            if (vertCount == 0 || tube.count == 0)
                return new bool[vertCount];
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
                return new bool[vertCount];
            }

            var projection = new TubeProjection(worldRefVerts, avatarRoot, tube);
            // Adjacency is the expensive part, and most renderers an overlay
            // scan checks are nowhere near the path.
            if (projection.ReachesMesh)
                projection.ComputeInTube(vertices, BuildAdjacency(vertices, mesh.triangles));
            return projection.inTube;
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
            int normalBoundaryRings = 1,
            float overlayMatchDistance = 0.02f,
            string meshAssetSuffix = "")
        {
            var results = new List<BlendshapeResult>();
            if (renderers == null || renderers.Count == 0) return results;

            // Primary: full pipeline
            var primaryResult = GenerateBulgeBlendshapes(
                renderers[0], avatarRoot, path, positionCount, displacement,
                outputFolder, namingPattern, smoothingPasses, subdivide,
                subdivisionPasses, recalculateNormals, meshAssetSuffix,
                normalFalloffSoftness, normalSmoothingPasses, normalBoundaryRings);
            results.Add(primaryResult);

            if (renderers.Count <= 1) return results;

            // Overlays: transfer from primary's baked frames
            var primaryDeltasByName = ReadBlendshapeDeltas(
                primaryResult.modifiedMesh, primaryResult.blendshapeNames);

            var primaryWorldVerts = GetSkinnedWorldRefVerts(
                renderers[0], primaryResult.modifiedMesh.vertices);
            var movedPrimaryVerts = CollectMovedVertices(
                primaryDeltasByName, primaryWorldVerts.Length);
            var primarySampler = new PrimarySurfaceSampler(
                primaryWorldVerts, primaryResult.modifiedMesh.triangles,
                movedPrimaryVerts, overlayMatchDistance);
            // Every primary vert, moved or not: tells overlay verts that sit on the
            // primary's surface (which copy it) from ones that stick out of it.
            var primarySurfaceTree = new KDTreeNearest(primaryWorldVerts);

            for (int i = 1; i < renderers.Count; i++)
            {
                string suffix = "_" + SanitizeAssetName(renderers[i].gameObject.name) + $"_{i}";
                var overlay = GenerateOverlayByTransfer(
                    renderers[i], renderers[0], avatarRoot, path,
                    primaryDeltasByName, primaryWorldVerts, primarySampler,
                    primarySurfaceTree,
                    outputFolder, "SPSBulge_GeneratedMesh" + meshAssetSuffix + suffix,
                    subdivide, subdivisionPasses, recalculateNormals,
                    overlayMatchDistance);
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
                    mesh = SubdivideOnRenderer(renderer, mesh, path, avatarRoot, subdivisionPasses);

                EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", "Computing surface distances...", 0.1f);

                // Each Mesh array getter returns a copy, so read them once.
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                var meshNormals = mesh.normals;
                var smoothNormals = ComputeSmoothedNormals(vertices, triangles, meshNormals);
                var adjacency = BuildAdjacency(vertices, triangles);

                // Project each vertex onto the tube once for all positions. Baked
                // positions put overlay meshes parented to bones (not the avatar
                // root) at the correct world location.
                var projection = new TubeProjection(
                    GetSkinnedWorldRefVerts(renderer, vertices), avatarRoot,
                    CatmullRomSpline.BuildTube(path));
                projection.ComputeInTube(vertices, adjacency);

                var names = new List<string>();
                float mag = displacement;
                float sigma = PositionSigma(positionCount);

                EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", "Computing vertex projections...", 0.3f);

                // Phase 1: Compute and smooth deltas for all positions
                var allDeltas = new List<Vector3[]>();
                for (int pos = 0; pos < positionCount; pos++)
                {
                    EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", $"Position {pos+1}/{positionCount}...", 0.3f + 0.5f * pos / positionCount);

                    var deltas = projection.PositionDeltas(
                        smoothNormals, PositionT(pos, positionCount), sigma, mag,
                        out bool hasAnyWeight);

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
                for (int pos = 0; pos < allDeltas.Count; pos++)
                {
                    var deltas = allDeltas[pos];
                    if (deltas == null) continue;

                    if (!Mathf.Approximately(globalScale, 1f))
                    {
                        for (int v = 0; v < deltas.Length; v++)
                            deltas[v] *= globalScale;
                    }

                    string name = BaseEffectConfig.FormatBlendshapeName(
                        namingPattern, names.Count + 1, BulgeGenerator.PositionPrefix);

                    Vector3[] normalDeltas = recalculateNormals
                        ? ComputeNormalDeltas(vertices, deltas, triangles, meshNormals, adjacency, name,
                            normalFalloffSoftness, normalSmoothingPasses, normalBoundaryRings)
                        : null;
                    mesh.AddBlendShapeFrame(name, 100f, deltas, normalDeltas, null);
                    names.Add(name);
                }

                EditorUtility.DisplayProgressBar("Generating Bulge Blendshapes", "Saving mesh...", 0.95f);

                string meshPath = $"{outputFolder}/SPSBulge_GeneratedMesh{meshAssetSuffix}.asset";
                SaveMesh(mesh, meshPath);
                AssignSharedMesh(renderer, mesh, "Assign generated mesh");

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

            var triangles = mesh.triangles;
            var smoothNormals = ComputeSmoothedNormals(vertices, triangles, mesh.normals);
            var adjacency = BuildAdjacency(vertices, triangles);

            var worldVerts = new Vector3[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                worldVerts[v] = rendererTransform != null
                    ? rendererTransform.TransformPoint(vertices[v])
                    : vertices[v];
            }
            var projection = new TubeProjection(
                worldVerts, avatarRoot, CatmullRomSpline.BuildTube(path));
            projection.ComputeInTube(vertices, adjacency);

            var deltas = projection.PositionDeltas(
                smoothNormals, PositionT(targetPosition, positionCount),
                PositionSigma(positionCount), displacement, out _);

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

        /// <summary>
        /// Where position <paramref name="pos"/> of <paramref name="count"/> is
        /// centred along the path, from 0 (start) to 1 (end).
        /// </summary>
        internal static float PositionT(int pos, int count) =>
            count > 1 ? (float)pos / (count - 1) : 0.5f;

        /// <summary>
        /// Bell curve sigma along the path: the spacing between positions times
        /// an overlap factor, so each bell extends well into its neighbours.
        /// </summary>
        internal static float PositionSigma(int count) =>
            (count > 1 ? 1f / (count - 1) : 1f) * 0.6f;

        /// <summary>
        /// Each vertex projected onto the path's spline tube, and which vertices
        /// the tube deforms. Generation, the Normal Map Baker, the overlay scan
        /// and the region preview all go through this so they agree on what moves.
        /// </summary>
        internal sealed class TubeProjection
        {
            public readonly float[] pathT;
            public readonly float[] radius;
            public readonly float[] aspect;     // clamped to at least 0.1
            public readonly bool[] inTube;      // all false until ComputeInTube
            public float[] surfaceDistance;     // set by ComputeInTube

            private readonly float[] euclideanDistance;
            private readonly float maxRadius;
            private readonly float minDistance = float.MaxValue;

            public TubeProjection(
                Vector3[] worldVerts, Transform avatarRoot, CatmullRomSpline.SplineTube tube)
            {
                int vertCount = worldVerts.Length;
                pathT = new float[vertCount];
                radius = new float[vertCount];
                aspect = new float[vertCount];
                inTube = new bool[vertCount];
                euclideanDistance = new float[vertCount];

                for (int v = 0; v < vertCount; v++)
                {
                    Vector3 localVert = avatarRoot != null
                        ? avatarRoot.InverseTransformPoint(worldVerts[v])
                        : worldVerts[v];

                    float dist = tube.DistanceToTube(localVert,
                        out radius[v], out float vertAspect, out pathT[v]);
                    euclideanDistance[v] = dist;
                    aspect[v] = Mathf.Max(0.1f, vertAspect);
                    if (radius[v] > maxRadius) maxRadius = radius[v];
                    if (dist < minDistance) minDistance = dist;
                }
            }

            /// <summary>
            /// False when no vertex comes within the tube's widest radius.
            /// Nothing can be in the tube then.
            /// </summary>
            public bool ReachesMesh => maxRadius > 0f && minDistance <= maxRadius;

            /// <summary>
            /// Fills <see cref="surfaceDistance"/> (distance from the centreline
            /// along the mesh surface) and <see cref="inTube"/>. The test uses the
            /// aspect-narrowed radius so vertices within the elliptical across-path
            /// extent aren't falsely excluded.
            /// </summary>
            public void ComputeInTube(Vector3[] vertices, List<int>[] adjacency)
            {
                if (!ReachesMesh) return;
                surfaceDistance = ComputeSurfaceDistances(
                    vertices, adjacency, euclideanDistance, maxRadius);
                for (int v = 0; v < inTube.Length; v++)
                    inTube[v] = surfaceDistance[v] < (radius[v] / aspect[v]);
            }

            /// <summary>
            /// Displacement of the in-tube vertices for the position centred at
            /// <paramref name="centerT"/>: a gaussian along the path times a
            /// smoothstep falloff across it, along <paramref name="normals"/>.
            /// </summary>
            public Vector3[] PositionDeltas(
                Vector3[] normals, float centerT, float sigma, float magnitude,
                out bool anyWeight)
            {
                var deltas = new Vector3[inTube.Length];
                anyWeight = false;
                for (int v = 0; v < inTube.Length; v++)
                {
                    if (!inTube[v]) continue;

                    // Aspect stretches the ellipse: >1 elongates along path,
                    // <1 spreads across path. Preserves area (roughly).
                    float vertAspect = aspect[v];
                    float effectiveSigma = sigma * vertAspect;
                    float effectiveRadius = radius[v] / vertAspect;

                    float dt = pathT[v] - centerT;
                    float alongWeight = Mathf.Exp(-(dt * dt) / (2f * effectiveSigma * effectiveSigma));

                    float perpW = 1f - (surfaceDistance[v] / effectiveRadius);
                    perpW = SmoothStep(Mathf.Clamp01(perpW));

                    float w = alongWeight * perpW;
                    if (w > 0.001f)
                    {
                        deltas[v] = normals[v] * w * magnitude;
                        anyWeight = true;
                    }
                }
                return deltas;
            }
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
        /// Subdivides the path region of <paramref name="mesh"/> (a copy of the
        /// renderer's mesh, which is destroyed) and puts the result on the renderer.
        /// </summary>
        private static Mesh SubdivideOnRenderer(
            SkinnedMeshRenderer renderer, Mesh mesh,
            List<PathWaypoint> path, Transform avatarRoot, int passes)
        {
            var subdivided = MeshSubdivider.SubdivideInRegion(
                mesh, path, renderer.transform, avatarRoot,
                worldRefVerts: GetSkinnedWorldRefVerts(renderer, mesh.vertices),
                passes: passes);
            if (subdivided != mesh)
                Object.DestroyImmediate(mesh);
            // BakeMesh on the renderer needs to see the subdivided mesh so its
            // baked vertex count matches what we project against. Without this,
            // GetSkinnedWorldRefVerts silently falls into the bind-pose fallback,
            // which is wrong for any non-bind-pose avatar or bone-parented overlay.
            AssignSharedMesh(renderer, subdivided, "Assign subdivided mesh");
            return subdivided;
        }

        /// <summary>
        /// Puts <paramref name="mesh"/> on the renderer as an undoable change,
        /// recorded as a prefab override.
        /// </summary>
        internal static void AssignSharedMesh(
            SkinnedMeshRenderer renderer, Mesh mesh, string undoName)
        {
            Undo.RecordObject(renderer, undoName);
            renderer.sharedMesh = mesh;
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        /// <summary>
        /// Hermite smoothstep: 3t² - 2t³. Much gentler than quadratic w*w.
        /// </summary>
        internal static float SmoothStep(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// Area-weighted smoothed normals from raw arrays: averages normals across
        /// all faces sharing each vertex position, weighted by triangle area. On
        /// low-poly meshes, raw normals are faceted and cause spikey displacement.
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
            foreach (var group in ColocatedVertexGroups(vertices))
            {
                Vector3 merged = Vector3.zero;
                foreach (int v in group)
                    merged += accumulated[v];

                foreach (int v in group)
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
            foreach (var group in ColocatedVertexGroups(originalVertices, affected))
            {
                Vector3 merged = Vector3.zero;
                Vector3 mergedBase = Vector3.zero;
                foreach (int v in group) { merged += accumulated[v]; mergedBase += accumulatedBase[v]; }
                foreach (int v in group) { accumulated[v] = merged; accumulatedBase[v] = mergedBase; }
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
                float falloff = SmoothStep(neighborhoodMag / peakMag);
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
            long x = (long)System.Math.Round(pos.x * 10000.0);
            long y = (long)System.Math.Round(pos.y * 10000.0);
            long z = (long)System.Math.Round(pos.z * 10000.0);
            return x * 73856093L ^ y * 19349663L ^ z * 83492791L;
        }

        /// <summary>
        /// Groups of two or more vertices at the same position (see
        /// <see cref="QuantizePosition"/>), like the copies a mesh splits along
        /// UV seams and hard edges. With <paramref name="include"/>, only those
        /// vertices are grouped.
        /// </summary>
        private static IEnumerable<List<int>> ColocatedVertexGroups(
            Vector3[] vertices, bool[] include = null)
        {
            var buckets = new Dictionary<long, List<int>>();
            for (int v = 0; v < vertices.Length; v++)
            {
                if (include != null && !include[v]) continue;
                long key = QuantizePosition(vertices[v]);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>();
                    buckets[key] = bucket;
                }
                bucket.Add(v);
            }

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count > 1)
                    yield return bucket;
            }
        }

        /// <summary>
        /// Builds a vertex adjacency list from the mesh triangles.
        /// Also links co-located vertices (same position, different index)
        /// so displacement smoothing works across UV seams and hard edges.
        /// </summary>
        internal static List<int>[] BuildAdjacency(Vector3[] vertices, int[] triangles)
        {
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
            foreach (var group in ColocatedVertexGroups(vertices))
            {
                for (int i = 0; i < group.Count; i++)
                    for (int j = i + 1; j < group.Count; j++)
                    {
                        sets[group[i]].Add(group[j]);
                        sets[group[j]].Add(group[i]);
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
        /// Returns the primary vertices whose triangles are searched for overlay
        /// matching: every primary vertex that moves in any blendshape. Triangles
        /// on the edge of the moved region blend down to their still corners, so an
        /// overlay vert on the still surface just past it projects onto that edge
        /// and stays put. Searching still triangles too would only widen the band
        /// of overlay verts whose base normals get replaced with the primary's,
        /// changing their shading with nothing moving there.
        /// </summary>
        private static bool[] CollectMovedVertices(
            Dictionary<string, PrimaryDeltaSet> deltasByName, int vertCount)
        {
            var moved = new bool[vertCount];
            foreach (var kv in deltasByName)
            {
                var deltas = kv.Value.vertexDeltas;
                for (int v = 0; v < deltas.Length; v++)
                    if (deltas[v].sqrMagnitude > 0.0000001f) moved[v] = true;
            }
            return moved;
        }

        /// <summary>
        /// Generates blendshape frames on an overlay mesh by transferring the
        /// primary's deltas from the nearest point on its surface in world space
        /// (see <see cref="PrimarySurfaceSampler"/>).
        /// Recomputing normals on the overlay's own topology produces a shading
        /// seam at the affected-region boundary (amplified by Boundary Blend Rings),
        /// so we inherit the primary's normals directly. At matched verts we
        /// rewrite BOTH the base normal and the delta to match the primary's -
        /// matching only the final (w=1) normal still left the overlay shading
        /// differently at every intermediate weight, visible as a ghost during
        /// preview.
        ///
        /// Overlay verts further than the match distance from the primary (parts
        /// that stick out of the surface, like a valve or a handle) have no primary
        /// surface to copy, so they take a smooth blend of the deltas of the attached
        /// verts of their own island. See <see cref="FillDetachedDeltas"/>.
        /// </summary>
        private static BlendshapeResult GenerateOverlayByTransfer(
            SkinnedMeshRenderer overlayRenderer,
            SkinnedMeshRenderer primaryRenderer,
            Transform avatarRoot,
            List<PathWaypoint> path,
            Dictionary<string, PrimaryDeltaSet> primaryDeltasByName,
            Vector3[] primaryWorldVerts,
            PrimarySurfaceSampler primarySampler,
            KDTreeNearest primarySurfaceTree,
            string outputFolder,
            string meshAssetName,
            bool subdivide,
            int subdivisionPasses,
            bool recalculateNormals,
            float overlayMatchDistance)
        {
            var mesh = CopyMesh(overlayRenderer);
            try
            {
                if (subdivide && subdivisionPasses > 0)
                    mesh = SubdivideOnRenderer(overlayRenderer, mesh, path, avatarRoot, subdivisionPasses);

                var vertices = mesh.vertices;
                // Authored per-vert base normals for both meshes — needed so the
                // overlay's stored delta targets the primary's final rendered normal
                // regardless of how each mesh's custom normals were authored.
                var overlayBaseNormals = mesh.normals;
                var primaryBaseNormals = primaryRenderer.sharedMesh != null
                    ? primaryRenderer.sharedMesh.normals
                    : null;

                var overlayWorldVerts = GetSkinnedWorldRefVerts(overlayRenderer, vertices);

                // Nearest point on the primary's surface. The match distance (2 cm by
                // default) lets tight overlays match while keeping distant accessory
                // parts out. An overlay vert on a primary vert gets exactly that
                // vert's values.
                float matchThresholdSq = overlayMatchDistance * overlayMatchDistance;
                var samples = new PrimarySurfaceSampler.Sample[vertices.Length];
                var matched = new bool[vertices.Length];
                for (int v = 0; v < vertices.Length; v++)
                    matched[v] = primarySampler.TrySample(overlayWorldVerts[v], out samples[v]);

                // Verts within the match distance of ANY primary vert are attached to
                // the surface: they copy their match, or stay put where the primary
                // doesn't move. The rest stick out of it. A hard cut-off there tore
                // protruding parts in half (the part under the line moved, the part
                // above it didn't), so detached verts are filled from their island.
                var attached = new bool[vertices.Length];
                for (int v = 0; v < vertices.Length; v++)
                {
                    if (matched[v]) { attached[v] = true; continue; }
                    int nearest = primarySurfaceTree.FindNearest(overlayWorldVerts[v]);
                    attached[v] = nearest >= 0 &&
                        (primaryWorldVerts[nearest] - overlayWorldVerts[v]).sqrMagnitude < matchThresholdSq;
                }
                var overlayAdjacency = BuildAdjacency(vertices, mesh.triangles);
                var detachedFillOrder = BuildDetachedFillOrder(overlayAdjacency, attached);

                var primaryTransform = primaryRenderer.transform;
                var overlayTransform = overlayRenderer.transform;
                var names = new List<string>();

                // Primary's base normal at each matched vert (world space), and whether
                // the overlay there is the same surface: facing within ~45° of it, like
                // clothing or a copy of the body. The sides and underside of a part
                // standing on the primary (a valve, a handle) face elsewhere and keep
                // their own normals - giving them the primary's turned some by >90°.
                bool hasPrimaryBaseNormals = primaryBaseNormals != null
                    && primaryBaseNormals.Length == primaryWorldVerts.Length;
                var primaryBaseWorld = new Vector3[vertices.Length];
                var sharesSurface = new bool[vertices.Length];
                if (hasPrimaryBaseNormals)
                {
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        if (!matched[v]) continue;
                        Vector3 primaryBase = samples[v].Blend(primaryBaseNormals);
                        if (primaryBase.sqrMagnitude < 1e-12f) continue;
                        primaryBaseWorld[v] = primaryTransform.TransformDirection(primaryBase.normalized);
                        Vector3 ownWorld = overlayTransform.TransformDirection(overlayBaseNormals[v]);
                        sharesSurface[v] = Vector3.Dot(ownWorld.normalized, primaryBaseWorld[v]) >= SharedSurfaceMinNormalDot;
                    }
                }

                // Copy primary base normals onto the overlay where it shares the surface.
                // Unity blends normals linearly (base + w·delta), so matching only
                // the delta leaves a (1-w)·(overlayBase - primaryBase) residual that
                // shows as a visible seam at every intermediate weight during preview.
                // Matching the base too makes the blend identical at every weight.
                if (recalculateNormals && hasPrimaryBaseNormals)
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
                            if (!sharesSurface[v]) continue;
                            overlayBaseNormals[v] = overlayTransform.InverseTransformDirection(primaryBaseWorld[v]);
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
                        if (!matched[v]) continue;

                        // Vertex delta: primary mesh-local → world → overlay mesh-local
                        Vector3 worldDelta = primaryTransform.TransformVector(samples[v].Blend(primaryVerts));
                        overlayVerts[v] = overlayTransform.InverseTransformVector(worldDelta);

                        if (!writeNormals) continue;
                        Vector3 worldNormalDelta = primaryTransform.TransformDirection(samples[v].Blend(primaryNormals));
                        if (sharesSurface[v])
                        {
                            // Normal delta: primary's delta rotated into overlay space.
                            // Base was already unified above, so this is all that's needed.
                            overlayNormals[v] = overlayTransform.InverseTransformDirection(worldNormalDelta);
                        }
                        else if (primaryBaseWorld[v] != Vector3.zero)
                        {
                            // Own normal, turned the way the primary's surface turns.
                            Vector3 turnedPrimary = primaryBaseWorld[v] + worldNormalDelta;
                            if (turnedPrimary.sqrMagnitude < 1e-12f) continue;
                            var turn = Quaternion.FromToRotation(primaryBaseWorld[v], turnedPrimary.normalized);
                            Vector3 own = overlayTransform.TransformDirection(overlayBaseNormals[v]).normalized;
                            overlayNormals[v] = overlayTransform.InverseTransformDirection(turn * own - own);
                        }
                    }

                    FillDetachedDeltas(overlayVerts, overlayAdjacency, attached, detachedFillOrder);
                    if (overlayNormals != null)
                        FillDetachedDeltas(overlayNormals, overlayAdjacency, attached, detachedFillOrder);

                    mesh.AddBlendShapeFrame(name, 100f, overlayVerts, overlayNormals, null);
                    names.Add(name);
                }

                string meshPath = $"{outputFolder}/{meshAssetName}.asset";
                SaveMesh(mesh, meshPath);
                AssignSharedMesh(overlayRenderer, mesh, "Assign generated mesh");

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
        /// Detached verts (see <see cref="GenerateOverlayByTransfer"/>) that are
        /// connected to an attached vert, ordered by ring distance from the attached
        /// ones. Verts on islands with no attached vert are left out and stay put.
        /// </summary>
        internal static List<int> BuildDetachedFillOrder(List<int>[] adjacency, bool[] attached)
        {
            var order = new List<int>();
            var queued = (bool[])attached.Clone();
            var queue = new Queue<int>();
            for (int v = 0; v < attached.Length; v++)
                if (attached[v]) queue.Enqueue(v);

            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (int n in adjacency[v])
                {
                    if (queued[n]) continue;
                    queued[n] = true;
                    order.Add(n);
                    queue.Enqueue(n);
                }
            }
            return order;
        }

        /// <summary>
        /// Fills the deltas of detached verts with a smooth (harmonic) blend of the
        /// attached verts' deltas: a part that sticks out of a moving surface moves
        /// with it, and one bridging moving and still areas stretches evenly between
        /// them. Attached deltas are left untouched.
        /// </summary>
        internal static void FillDetachedDeltas(
            Vector3[] deltas, List<int>[] adjacency, bool[] attached, List<int> fillOrder)
        {
            if (fillOrder.Count == 0) return;

            // First guess in ring order: each vert averages its already-filled
            // neighbours, so a part attached on one side is exact after this pass.
            var filled = (bool[])attached.Clone();
            foreach (int v in fillOrder)
            {
                Vector3 sum = Vector3.zero;
                int count = 0;
                foreach (int n in adjacency[v])
                {
                    if (!filled[n]) continue;
                    sum += deltas[n];
                    count++;
                }
                deltas[v] = count > 0 ? sum / count : Vector3.zero;
                filled[v] = true;
            }

            // Gauss-Seidel relaxation towards the harmonic fill. Every neighbour of
            // a vert in the fill order is attached or in the order itself.
            const int maxIterations = 200;
            const float tolerance = 1e-7f;
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                float maxChange = 0f;
                foreach (int v in fillOrder)
                {
                    var neighbours = adjacency[v];
                    if (neighbours.Count == 0) continue;
                    Vector3 sum = Vector3.zero;
                    foreach (int n in neighbours)
                        sum += deltas[n];
                    Vector3 next = sum / neighbours.Count;
                    float change = (next - deltas[v]).sqrMagnitude;
                    if (change > maxChange) maxChange = change;
                    deltas[v] = next;
                }
                if (maxChange < tolerance * tolerance) break;
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
            SpsAnimationUtility.EnsureParentFolder(path);

            // Write to a temp path and move the old mesh aside before replacing.
            // If the final move fails, restore the old asset instead of leaving
            // renderers pointing at a mesh asset that was already deleted.
            string tempPath = BuildUniqueSidecarAssetPath(path, ".tmp.asset");
            string backupPath = null;
            bool movedExistingToBackup = false;

            try
            {
                AssetDatabase.CreateAsset(mesh, tempPath);

                var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (existing != null)
                {
                    backupPath = BuildUniqueSidecarAssetPath(path, ".backup.asset");
                    string backupError = AssetDatabase.MoveAsset(path, backupPath);
                    if (!string.IsNullOrEmpty(backupError))
                        throw new System.IO.IOException(
                            $"[SPS Effects] Failed to move existing mesh '{path}' " +
                            $"to backup '{backupPath}': {backupError}");
                    movedExistingToBackup = true;
                }

                string moveError = AssetDatabase.MoveAsset(tempPath, path);
                if (!string.IsNullOrEmpty(moveError))
                {
                    string restoreError = null;
                    if (movedExistingToBackup)
                        restoreError = AssetDatabase.MoveAsset(backupPath, path);

                    string restoreMessage = string.IsNullOrEmpty(restoreError)
                        ? "Existing mesh was restored."
                        : $"Existing mesh is still at backup path '{backupPath}' " +
                          $"because restore failed: {restoreError}";
                    throw new System.IO.IOException(
                        $"[SPS Effects] Failed to rename '{tempPath}' to '{path}': " +
                        $"{moveError} {restoreMessage}");
                }

                if (movedExistingToBackup && !AssetDatabase.DeleteAsset(backupPath))
                    Debug.LogWarning($"[SPS Effects] Could not delete old mesh backup: {backupPath}");

                AssetDatabase.SaveAssets();
            }
            catch
            {
                if (AssetDatabase.LoadAssetAtPath<Mesh>(tempPath) != null)
                    AssetDatabase.DeleteAsset(tempPath);
                throw;
            }

            Debug.Log($"[SPS Effects] Generated mesh saved: {path} " +
                $"({mesh.blendShapeCount} blendshapes)");
        }

        internal static string BuildUniqueSidecarAssetPath(string assetPath, string suffix)
        {
            string folder = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            string candidate = string.IsNullOrEmpty(folder)
                ? $"{fileName}{suffix}"
                : $"{folder}/{fileName}{suffix}";
            return AssetDatabase.GenerateUniqueAssetPath(candidate);
        }
    }
}
