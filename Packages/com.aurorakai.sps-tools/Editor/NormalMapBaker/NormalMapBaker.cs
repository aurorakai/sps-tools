using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>Bakes a tangent-space detail normal map from a Bulge config.</summary>
    public static class NormalMapBaker
    {
        /// <summary>Bake output: the texture plus any non-fatal warnings, or an error message.</summary>
        public struct BakeResult
        {
            public Texture2D texture;
            public string errorMessage;
            public List<string> warnings;
        }

        /// <summary>Inputs to a bake call. Caller extracts these from its own config type.</summary>
        public struct BakeInputs
        {
            public SkinnedMeshRenderer primary;
            public Transform avatarRoot;
            public List<PathWaypoint> path;
            public int positionCount;
            public int targetPosition;
            public float displacement;
            public int displacementSmoothingPasses;
        }

        /// <summary>
        /// Bakes a detail normal map for one mesh. Returns an in-memory Texture2D;
        /// caller writes to PNG and imports as a NormalMap.
        /// </summary>
        public static BakeResult BakePrimary(BakeInputs inputs, NormalMapBakerSettings settings)
        {
            var result = new BakeResult { warnings = new List<string>() };

            string error = ValidateInputs(inputs, settings);
            if (error != null)
            {
                result.errorMessage = error;
                return result;
            }

            EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Subdividing reference mesh...", 0.1f);

            var primary = inputs.primary;
            var sourceMesh = primary.sharedMesh;
            var workMesh = Object.Instantiate(sourceMesh);
            workMesh.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var avatarRoot = inputs.avatarRoot != null ? inputs.avatarRoot : primary.transform;

                if (settings.referenceSubdivision > 0)
                {
                    var preSubdivisionMesh = workMesh;
                    workMesh = MeshSubdivider.SubdivideInRegion(
                        preSubdivisionMesh, inputs.path, primary.transform, avatarRoot,
                        passes: settings.referenceSubdivision);
                    if (preSubdivisionMesh != null && preSubdivisionMesh != workMesh)
                        Object.DestroyImmediate(preSubdivisionMesh);
                    workMesh.hideFlags = HideFlags.HideAndDontSave;
                }

                EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Smoothing + applying bulge...", 0.25f);

                // Cache mesh arrays — each accessor on a Mesh copies the array.
                var hrVerts = workMesh.vertices;
                var hrTris = workMesh.triangles;
                var hrAdjacency = BlendshapeGenerator.BuildAdjacency(workMesh);

                // Shared per-vert tube-distance computation. 3x radius threshold
                // covers the bulge + falloff tail so the normal map fades smoothly.
                float hrMaxRadius; float hrThreshold;
                var hrDistances = ComputePerVertTubeDistances(
                    hrVerts, inputs.path, primary.transform, avatarRoot,
                    thresholdMultiplier: 3f,
                    out hrMaxRadius, out hrThreshold);

                var nearTubeVertMask = new bool[hrVerts.Length];
                for (int v = 0; v < hrVerts.Length; v++)
                    nearTubeVertMask[v] = hrDistances[v] < hrThreshold;

                // Relax subdivision midpoints back toward the smooth surface.
                int positionSmoothPasses = Mathf.Max(2, settings.referenceSubdivision);
                LaplacianSmoothPositions(hrVerts, hrAdjacency, nearTubeVertMask, positionSmoothPasses);
                workMesh.vertices = hrVerts;

                var deltas = BlendshapeGenerator.ComputeBulgeDisplacementForPosition(
                    workMesh, primary.transform, avatarRoot,
                    inputs.path, inputs.positionCount, inputs.targetPosition,
                    inputs.displacement, inputs.displacementSmoothingPasses);

                if (deltas == null || deltas.Length != hrVerts.Length)
                {
                    result.errorMessage = "Failed to compute bulge displacement on the reference mesh.";
                    return result;
                }

                var deformedHighRes = new Vector3[hrVerts.Length];
                for (int i = 0; i < hrVerts.Length; i++)
                    deformedHighRes[i] = hrVerts[i] + deltas[i];

                EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Computing high-res normals...", 0.4f);

                // Symmetric baseline: subtracting the undeformed reconstruction from the
                // deformed one cancels reconstruction-method bias against the mesh's
                // authored normals, so unaffected areas encode as exactly neutral.
                var highResUndeformedNormals = ComputeAreaWeightedNormals(hrVerts, hrTris);
                var highResDeformedNormals = ComputeAreaWeightedNormals(deformedHighRes, hrTris);

                const int normalSmoothPasses = 2;
                LaplacianSmoothNormals(highResUndeformedNormals, hrAdjacency, nearTubeVertMask, normalSmoothPasses);
                LaplacianSmoothNormals(highResDeformedNormals, hrAdjacency, nearTubeVertMask, normalSmoothPasses);

                EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Building spatial lookup...", 0.5f);

                var kdTree = new KDTreeNearest(hrVerts);

                EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Reading low-poly tangent basis...", 0.55f);

                var lpVerts = sourceMesh.vertices;
                var lpNormals = sourceMesh.normals;
                var lpTangents = sourceMesh.tangents;
                var lpUVs = sourceMesh.uv;
                var lpTris = sourceMesh.triangles;

                if (lpTangents == null || lpTangents.Length != lpVerts.Length)
                {
                    result.warnings.Add(
                        "Source mesh had no tangents; auto-recalculated for baking. " +
                        "Consider importing the mesh with tangents enabled.");
                    var tmp = Object.Instantiate(sourceMesh);
                    tmp.RecalculateTangents();
                    lpTangents = tmp.tangents;
                    Object.DestroyImmediate(tmp);
                }

                EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Rasterizing...", 0.6f);

                // Filter out triangles far from the bulge — avoids UV-overlap pollution
                // from mirrored mesh halves sharing the same UV region.
                var lpTrisFiltered = BuildNearTubeTriangles(
                    lpVerts, lpTris, inputs.path, primary.transform, avatarRoot);

                int resolution = (int)settings.resolution;
                var pixels = new Color32[resolution * resolution];
                var written = new bool[resolution * resolution];
                var neutral = new Color32(128, 128, 255, 255);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = neutral;

                RasterizeBake(
                    lpVerts, lpNormals, lpTangents,
                    lpUVs, lpTrisFiltered,
                    highResUndeformedNormals, highResDeformedNormals, kdTree,
                    resolution, pixels, written,
                    out int pixelsWithDetail, out float tsMaxDeviation);

                float peakDeltaMag = 0f;
                for (int i = 0; i < deltas.Length; i++)
                {
                    float m = deltas[i].magnitude;
                    if (m > peakDeltaMag) peakDeltaMag = m;
                }

                if (peakDeltaMag < 1e-6f)
                    result.warnings.Add(
                        "Peak displacement was ~0 — bulge pipeline produced no deformation. " +
                        "Check the path, displacement, and position count on the config.");
                if (pixelsWithDetail == 0 || tsMaxDeviation < 0.01f)
                    result.warnings.Add(
                        "Baked normal map has no visible detail. Displacement may be too " +
                        "small relative to mesh scale, or the deformation is parallel to the " +
                        "surface normal everywhere (which cancels out in tangent space).");

                // 3x3 vector-space box blur — smooths Voronoi-cell artifacts
                // from the KD-tree's nearest-vert snap.
                const int outputSmoothPasses = 2;
                EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Smoothing output...", 0.88f);
                BlurNormalPixels(pixels, written, resolution, outputSmoothPasses);

                if (settings.seamPaddingTexels > 0)
                {
                    EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Seam padding...", 0.9f);
                    SeamPad(pixels, written, resolution, settings.seamPaddingTexels);
                }

                EditorUtility.DisplayProgressBar("SPS Normal Map Baker", "Creating texture...", 0.97f);

                var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, mipChain: false, linear: true);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.SetPixels32(pixels);
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);

                result.texture = tex;
                return result;
            }
            finally
            {
                if (workMesh != null && workMesh != sourceMesh)
                    Object.DestroyImmediate(workMesh);
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Resolves an overlay bake. Returns the primary's baked texture if UV layouts
        /// are compatible (via <see cref="NormalMapBakerSettings.OverlayMode.Auto"/>
        /// or forced shared), otherwise bakes fresh against the overlay's own topology.
        /// </summary>
        public static BakeResult BakeOverlay(
            BakeInputs primaryInputs, Texture2D primaryTexture,
            SkinnedMeshRenderer overlay,
            NormalMapBakerSettings.OverlayMode mode,
            NormalMapBakerSettings settings,
            out bool usedSharedPrimary)
        {
            usedSharedPrimary = false;
            var result = new BakeResult { warnings = new List<string>() };

            if (overlay == null || overlay.sharedMesh == null)
            {
                result.errorMessage = "Overlay renderer or mesh is null.";
                return result;
            }

            bool shared;
            switch (mode)
            {
                case NormalMapBakerSettings.OverlayMode.ForceShared: shared = true; break;
                case NormalMapBakerSettings.OverlayMode.ForcePerMesh: shared = false; break;
                default:
                    shared = IsCompatibleWithPrimary(primaryInputs.primary, overlay, primaryInputs);
                    break;
            }

            if (shared)
            {
                usedSharedPrimary = true;
                result.texture = primaryTexture;
                return result;
            }

            var overlayInputs = primaryInputs;
            overlayInputs.primary = overlay;
            return BakePrimary(overlayInputs, settings);
        }

        /// <summary>
        /// True when an overlay's UV layout matches the primary's within the path-
        /// affected region (8 sampled verts, ≥6 must land within 5mm of a primary vert
        /// with the same UV).
        /// </summary>
        private static bool IsCompatibleWithPrimary(
            SkinnedMeshRenderer primary, SkinnedMeshRenderer overlay, BakeInputs sharedInputs)
        {
            if (primary == null || overlay == null) return false;
            if (primary.sharedMesh == null || overlay.sharedMesh == null) return false;
            if (sharedInputs.path == null || sharedInputs.path.Count < 2) return false;

            var avatarRoot = sharedInputs.avatarRoot != null ? sharedInputs.avatarRoot : primary.transform;
            var primaryMesh = primary.sharedMesh;
            var overlayMesh = overlay.sharedMesh;
            var tube = CatmullRomSpline.BuildTube(
                sharedInputs.path, Mathf.Max(sharedInputs.path.Count * 8, 20));

            var overlayVerts = overlayMesh.vertices;
            var overlayWorldVerts = BlendshapeGenerator.GetSkinnedWorldRefVerts(
                overlay, overlayVerts);
            var overlayInRegion = BlendshapeGenerator.ComputeVerticesInTube(
                overlayMesh, overlayVerts, overlayWorldVerts, avatarRoot, tube);

            var inRegionIndices = new List<int>();
            for (int i = 0; i < overlayInRegion.Length; i++)
                if (overlayInRegion[i]) inRegionIndices.Add(i);
            if (inRegionIndices.Count < 8) return false;

            var overlayUVs = overlayMesh.uv;
            var primaryVerts = primaryMesh.vertices;
            var primaryUVs = primaryMesh.uv;
            var primaryWorldVerts = BlendshapeGenerator.GetSkinnedWorldRefVerts(
                primary, primaryVerts);

            if (overlayUVs == null || overlayUVs.Length != overlayVerts.Length) return false;
            if (primaryUVs == null || primaryUVs.Length != primaryVerts.Length) return false;

            var rng = new System.Random(12345);
            const float worldThresholdSq = 0.005f * 0.005f;
            int passCount = 0;
            for (int pick = 0; pick < 8; pick++)
            {
                int overlayVertIdx = inRegionIndices[rng.Next(inRegionIndices.Count)];
                Vector2 targetUV = overlayUVs[overlayVertIdx];

                int nearestPrimaryVert = -1;
                float nearestUvSqrDist = float.PositiveInfinity;
                for (int p = 0; p < primaryUVs.Length; p++)
                {
                    float d = (primaryUVs[p] - targetUV).sqrMagnitude;
                    if (d < nearestUvSqrDist) { nearestUvSqrDist = d; nearestPrimaryVert = p; }
                }
                if (nearestPrimaryVert < 0) continue;

                Vector3 overlayWorld = overlayWorldVerts[overlayVertIdx];
                Vector3 primaryWorld = primaryWorldVerts[nearestPrimaryVert];
                if ((overlayWorld - primaryWorld).sqrMagnitude <= worldThresholdSq)
                    passCount++;
            }

            return passCount >= 6;
        }

        /// <summary>Returns null if inputs are valid, else a user-readable error.</summary>
        private static string ValidateInputs(BakeInputs inputs, NormalMapBakerSettings settings)
        {
            if (inputs.primary == null) return "Target renderer is null.";
            if (inputs.primary.sharedMesh == null) return "Target renderer has no mesh.";
            if (inputs.path == null || inputs.path.Count < 2)
                return "Path must have at least 2 waypoints.";
            if (inputs.positionCount <= 0) return "Position count must be > 0.";
            if (inputs.targetPosition < 0 || inputs.targetPosition >= inputs.positionCount)
                return $"Target position {inputs.targetPosition} out of range [0, {inputs.positionCount}).";
            if (settings == null) return "Bake settings are null.";
            if ((int)settings.resolution <= 0) return "Resolution must be > 0.";

            var mesh = inputs.primary.sharedMesh;
            var uv = mesh.uv;
            if (uv == null || uv.Length == 0)
                return "Target mesh has no UV coordinates; can't bake a tangent-space texture.";
            var normals = mesh.normals;
            if (normals == null || normals.Length != mesh.vertexCount)
                return "Target mesh has missing or mismatched normals.";

            return null;
        }

        /// <summary>
        /// Per-vertex distance to the bulge path's tube in avatar-local space, plus
        /// the derived "near tube" threshold (<paramref name="thresholdMultiplier"/>
        /// × max path radius, with a scaled floor so small configs don't produce a
        /// zero threshold).
        /// </summary>
        private static float[] ComputePerVertTubeDistances(
            Vector3[] meshLocalVerts, List<PathWaypoint> path,
            Transform meshTransform, Transform avatarRoot,
            float thresholdMultiplier,
            out float maxRadius, out float threshold)
        {
            maxRadius = 0f;
            threshold = 0f;
            if (path == null || path.Count < 2) return new float[meshLocalVerts.Length];

            int segments = Mathf.Max(path.Count * 8, 20);
            var tube = CatmullRomSpline.BuildTube(path, segments);

            foreach (var wp in path)
                if (wp.radius > maxRadius) maxRadius = wp.radius;
            threshold = Mathf.Max(maxRadius * thresholdMultiplier, 0.01f * thresholdMultiplier);

            var distances = new float[meshLocalVerts.Length];
            for (int v = 0; v < meshLocalVerts.Length; v++)
            {
                Vector3 world = meshTransform != null
                    ? meshTransform.TransformPoint(meshLocalVerts[v])
                    : meshLocalVerts[v];
                Vector3 local = avatarRoot != null
                    ? avatarRoot.InverseTransformPoint(world)
                    : world;
                distances[v] = tube.DistanceToTube(local, out _, out _, out _);
            }
            return distances;
        }

        /// <summary>N Laplacian passes on vertex positions (masked to affected verts).</summary>
        private static void LaplacianSmoothPositions(
            Vector3[] verts, List<int>[] adjacency, bool[] mask, int passes)
        {
            if (passes <= 0 || verts == null) return;
            int vertCount = verts.Length;
            var scratch = new Vector3[vertCount];
            for (int p = 0; p < passes; p++)
            {
                for (int v = 0; v < vertCount; v++)
                {
                    if (!mask[v]) { scratch[v] = verts[v]; continue; }
                    var n = adjacency[v];
                    if (n.Count == 0) { scratch[v] = verts[v]; continue; }

                    Vector3 sum = Vector3.zero;
                    for (int i = 0; i < n.Count; i++) sum += verts[n[i]];
                    scratch[v] = sum / n.Count;
                }
                System.Array.Copy(scratch, verts, vertCount);
            }
        }

        /// <summary>N Laplacian passes on per-vertex normals (masked, renormalized).</summary>
        private static void LaplacianSmoothNormals(
            Vector3[] normals, List<int>[] adjacency, bool[] mask, int passes)
        {
            if (passes <= 0 || normals == null) return;
            int vertCount = normals.Length;
            var scratch = new Vector3[vertCount];
            for (int p = 0; p < passes; p++)
            {
                for (int v = 0; v < vertCount; v++)
                {
                    if (!mask[v]) { scratch[v] = normals[v]; continue; }
                    var n = adjacency[v];
                    if (n.Count == 0) { scratch[v] = normals[v]; continue; }

                    Vector3 sum = normals[v];
                    for (int i = 0; i < n.Count; i++) sum += normals[n[i]];
                    scratch[v] = sum.sqrMagnitude > 1e-20f ? sum.normalized : normals[v];
                }
                System.Array.Copy(scratch, normals, vertCount);
            }
        }

        /// <summary>
        /// Filters a triangle list to only those with at least one vert near the
        /// bulge tube (3× max radius). Skips non-deformed triangles that would
        /// pollute overlapping UVs (e.g. mirrored mesh halves).
        /// </summary>
        private static int[] BuildNearTubeTriangles(
            Vector3[] meshLocalVerts, int[] tris,
            List<PathWaypoint> path, Transform meshTransform, Transform avatarRoot)
        {
            if (path == null || path.Count < 2 || tris == null || tris.Length == 0)
                return tris;

            var distances = ComputePerVertTubeDistances(
                meshLocalVerts, path, meshTransform, avatarRoot,
                thresholdMultiplier: 3f,
                out _, out float threshold);

            var keep = new List<int>(tris.Length);
            for (int i = 0; i < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                if (distances[i0] < threshold
                    || distances[i1] < threshold
                    || distances[i2] < threshold)
                {
                    keep.Add(i0); keep.Add(i1); keep.Add(i2);
                }
            }
            return keep.ToArray();
        }

        /// <summary>Area-weighted per-vertex normals. Degenerate tris contribute nothing.</summary>
        private static Vector3[] ComputeAreaWeightedNormals(Vector3[] verts, int[] tris)
        {
            var accum = new Vector3[verts.Length];
            for (int i = 0; i < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                // Cross product magnitude is 2× triangle area — gives area-weighted
                // averaging when the accumulated sum is normalized at the end.
                Vector3 faceN = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                accum[i0] += faceN;
                accum[i1] += faceN;
                accum[i2] += faceN;
            }
            for (int v = 0; v < accum.Length; v++)
                accum[v] = accum[v].sqrMagnitude > 1e-20f ? accum[v].normalized : Vector3.up;
            return accum;
        }

        /// <summary>
        /// Rasterizes low-poly triangles in UV space. Per pixel: interpolates the
        /// low-poly tangent basis, looks up the nearest high-res deformed normal,
        /// converts to tangent space, encodes as Color32. Most-deviation-wins on
        /// UV-overlap collisions.
        /// </summary>
        private static void RasterizeBake(
            Vector3[] lpVerts, Vector3[] lpNormals, Vector4[] lpTangents,
            Vector2[] lpUVs, int[] lpTris,
            Vector3[] hrUndeformedNormals, Vector3[] hrDeformedNormals,
            KDTreeNearest kdTree,
            int resolution, Color32[] pixels, bool[] written,
            out int pixelsWithDetail, out float tsMaxDeviation)
        {
            int detailCount = 0;
            float maxDev = 0f;
            UvRasterizer.Rasterize(lpUVs, lpTris, resolution,
                (px, py, w0, w1, w2, triIndex) =>
                {
                    int i0 = lpTris[triIndex * 3];
                    int i1 = lpTris[triIndex * 3 + 1];
                    int i2 = lpTris[triIndex * 3 + 2];

                    Vector3 pos = w0 * lpVerts[i0] + w1 * lpVerts[i1] + w2 * lpVerts[i2];

                    Vector3 normal = w0 * lpNormals[i0] + w1 * lpNormals[i1] + w2 * lpNormals[i2];
                    normal.Normalize();

                    Vector4 tanRaw = w0 * lpTangents[i0] + w1 * lpTangents[i1] + w2 * lpTangents[i2];
                    Vector3 tangent = new Vector3(tanRaw.x, tanRaw.y, tanRaw.z);
                    tangent.Normalize();

                    // tangent.w is the bitangent sign (±1). Pick corner 0 to avoid
                    // interpolation drift when corners disagree.
                    float bitangentSign = lpTangents[i0].w;
                    Vector3 bitangent = Vector3.Cross(normal, tangent) * bitangentSign;

                    int hrIdx = kdTree.FindNearest(pos);
                    if (hrIdx < 0) return;

                    // Symmetric-baseline: undeformed recon cancels reconstruction
                    // bias against authored normals so unaffected areas → zero delta.
                    Vector3 worldDelta = hrDeformedNormals[hrIdx] - hrUndeformedNormals[hrIdx];

                    float tx = Vector3.Dot(worldDelta, tangent);
                    float ty = Vector3.Dot(worldDelta, bitangent);
                    float tz = Vector3.Dot(worldDelta, normal);
                    // Add to neutral (0, 0, 1) and renormalize so undeformed pixels
                    // encode exactly as (0, 0, 1).
                    Vector3 tsNormal = new Vector3(tx, ty, 1f + tz);
                    if (tsNormal.sqrMagnitude > 1e-10f) tsNormal.Normalize();
                    else tsNormal = new Vector3(0f, 0f, 1f);

                    float dev = Mathf.Max(Mathf.Abs(tsNormal.x), Mathf.Abs(tsNormal.y));

                    // Most-deviation-wins: preserves the bulge-side contribution when
                    // mirrored UV halves compete for the same pixel.
                    int pixelIdx = py * resolution + px;
                    if (written[pixelIdx])
                    {
                        var existing = pixels[pixelIdx];
                        float exX = existing.r / 127.5f - 1f;
                        float exY = existing.g / 127.5f - 1f;
                        float exDev = Mathf.Max(Mathf.Abs(exX), Mathf.Abs(exY));
                        if (exDev >= dev) return;
                    }

                    pixels[pixelIdx] = new Color32(
                        EncodeAxis(tsNormal.x),
                        EncodeAxis(tsNormal.y),
                        EncodeAxis(tsNormal.z),
                        255);
                    written[pixelIdx] = true;

                    if (dev > maxDev) maxDev = dev;
                    if (dev > 0.015f) detailCount++;
                });

            pixelsWithDetail = detailCount;
            tsMaxDeviation = maxDev;
        }

        private static byte EncodeAxis(float v)
        {
            float u = (Mathf.Clamp(v, -1f, 1f) * 0.5f + 0.5f) * 255f;
            return (byte)Mathf.RoundToInt(u);
        }

        /// <summary>
        /// N box-blur passes on written pixels. Decodes to tangent-space vectors,
        /// averages 3×3 written neighbours, renormalizes. Removes Voronoi-cell
        /// artifacts from the KD-tree's nearest-vert snap.
        /// </summary>
        private static void BlurNormalPixels(
            Color32[] pixels, bool[] written, int resolution, int passes)
        {
            if (passes <= 0) return;
            int total = resolution * resolution;
            var scratch = new Color32[total];

            for (int pass = 0; pass < passes; pass++)
            {
                System.Array.Copy(pixels, scratch, total);

                for (int y = 0; y < resolution; y++)
                {
                    for (int x = 0; x < resolution; x++)
                    {
                        int idx = y * resolution + x;
                        if (!written[idx]) continue;

                        float sumX = 0f, sumY = 0f, sumZ = 0f;
                        int count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int py = y + dy;
                            if (py < 0 || py >= resolution) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int px = x + dx;
                                if (px < 0 || px >= resolution) continue;
                                int nIdx = py * resolution + px;
                                if (!written[nIdx]) continue;
                                var c = pixels[nIdx];
                                sumX += c.r / 127.5f - 1f;
                                sumY += c.g / 127.5f - 1f;
                                sumZ += c.b / 127.5f - 1f;
                                count++;
                            }
                        }
                        if (count == 0) continue;

                        Vector3 avg = new Vector3(sumX / count, sumY / count, sumZ / count);
                        if (avg.sqrMagnitude > 1e-10f) avg.Normalize();
                        else avg = new Vector3(0f, 0f, 1f);

                        scratch[idx] = new Color32(
                            (byte)Mathf.RoundToInt((avg.x * 0.5f + 0.5f) * 255f),
                            (byte)Mathf.RoundToInt((avg.y * 0.5f + 0.5f) * 255f),
                            (byte)Mathf.RoundToInt((avg.z * 0.5f + 0.5f) * 255f),
                            255);
                    }
                }

                System.Array.Copy(scratch, pixels, total);
            }
        }

        /// <summary>
        /// Dilates written pixels outward into unwritten neighbours. Prevents
        /// bilinear filtering from leaking neutral grey into the baked region
        /// across UV seams.
        /// </summary>
        private static void SeamPad(
            Color32[] pixels, bool[] written, int resolution, int iterations)
        {
            int total = resolution * resolution;
            var scratchPixels = new Color32[total];
            var scratchWritten = new bool[total];

            for (int pass = 0; pass < iterations; pass++)
            {
                System.Array.Copy(pixels, scratchPixels, total);
                System.Array.Copy(written, scratchWritten, total);

                for (int y = 0; y < resolution; y++)
                {
                    for (int x = 0; x < resolution; x++)
                    {
                        int idx = y * resolution + x;
                        if (written[idx]) continue;

                        int r = 0, g = 0, b = 0, count = 0;
                        TryAccum(written, pixels, resolution, x - 1, y, ref r, ref g, ref b, ref count);
                        TryAccum(written, pixels, resolution, x + 1, y, ref r, ref g, ref b, ref count);
                        TryAccum(written, pixels, resolution, x, y - 1, ref r, ref g, ref b, ref count);
                        TryAccum(written, pixels, resolution, x, y + 1, ref r, ref g, ref b, ref count);

                        if (count == 0) continue;
                        scratchPixels[idx] = new Color32(
                            (byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
                        scratchWritten[idx] = true;
                    }
                }

                System.Array.Copy(scratchPixels, pixels, total);
                System.Array.Copy(scratchWritten, written, total);
            }
        }

        private static void TryAccum(
            bool[] written, Color32[] pixels, int resolution, int x, int y,
            ref int r, ref int g, ref int b, ref int count)
        {
            if (x < 0 || x >= resolution || y < 0 || y >= resolution) return;
            int idx = y * resolution + x;
            if (!written[idx]) return;
            var c = pixels[idx];
            r += c.r; g += c.g; b += c.b; count++;
        }
    }
}
