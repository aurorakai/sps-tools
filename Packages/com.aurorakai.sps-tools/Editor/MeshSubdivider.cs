using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Subdivides triangles in a specified region of a mesh by splitting edges
    /// at their midpoints. Only affects triangles inside or bordering the region,
    /// keeping the rest of the mesh untouched.
    ///
    /// Handles all vertex attributes: positions, normals, tangents, UVs,
    /// bone weights, and vertex colors.
    /// </summary>
    public static class MeshSubdivider
    {
        /// <summary>
        /// Subdivides triangles in the tube region defined by the path waypoints.
        /// Returns a new mesh with additional geometry in the affected area.
        /// The original mesh is not modified.
        /// </summary>
        public static Mesh SubdivideInRegion(
            Mesh sourceMesh, List<PathWaypoint> path,
            Transform meshTransform, Transform avatarRoot,
            Vector3[] worldRefVerts = null,
            int passes = 1)
        {
            var mesh = Object.Instantiate(sourceMesh);
            var refs = worldRefVerts;

            for (int pass = 0; pass < passes; pass++)
            {
                EditorUtility.DisplayProgressBar("Subdividing Mesh", $"Pass {pass+1}/{passes}...", (float)pass / passes);
                mesh = SubdividePass(mesh, path, meshTransform, avatarRoot, refs);
                // After pass 0 the vert count grew; refs only describe the input mesh,
                // so we drop them and fall back to bind-pose-of-subdivided-mesh for
                // subsequent passes (the affected band is already a superset by virtue
                // of pass 0 having expanded it).
                refs = null;
            }

            return mesh;
        }

        private static Mesh SubdividePass(
            Mesh mesh, List<PathWaypoint> path,
            Transform meshTransform, Transform avatarRoot,
            Vector3[] worldRefVerts)
        {
            var vertices = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uv = mesh.uv;
            var uv2 = mesh.uv2;
            var boneWeights = mesh.boneWeights;
            var colors = mesh.colors;
            var bindposes = mesh.bindposes;
            int vertCount = vertices.Length;

            bool hasNormals = normals != null && normals.Length == vertCount;
            bool hasTangents = tangents != null && tangents.Length == vertCount;
            bool hasUV = uv != null && uv.Length == vertCount;
            bool hasUV2 = uv2 != null && uv2.Length == vertCount;
            bool hasBoneWeights = boneWeights != null && boneWeights.Length == vertCount;
            bool hasColors = colors != null && colors.Length == vertCount;

            // Build tube for region testing
            int segments = Mathf.Max(path.Count * 8, 20);
            var tube = CatmullRomSpline.BuildTube(path, segments);

            // Mark which vertices are inside the tube (with some margin)
            var isAffected = new bool[vertCount];
            bool useWorldRefs = worldRefVerts != null && worldRefVerts.Length == vertCount;
            for (int v = 0; v < vertCount; v++)
            {
                Vector3 worldVert = useWorldRefs
                    ? worldRefVerts[v]
                    : meshTransform.TransformPoint(vertices[v]);
                Vector3 localVert = avatarRoot.InverseTransformPoint(worldVert);
                float dist = tube.DistanceToTube(localVert, out float radius);
                // Include vertices slightly outside the tube to ensure clean borders
                isAffected[v] = dist < radius * 1.3f;
            }

            // Process each submesh
            var newVertices = new List<Vector3>(vertices);
            var newNormals = hasNormals ? new List<Vector3>(normals) : null;
            var newTangents = hasTangents ? new List<Vector4>(tangents) : null;
            var newUV = hasUV ? new List<Vector2>(uv) : null;
            var newUV2 = hasUV2 ? new List<Vector2>(uv2) : null;
            var newBoneWeights = hasBoneWeights ? new List<BoneWeight>(boneWeights) : null;
            var newColors = hasColors ? new List<Color>(colors) : null;

            // Edge midpoint cache: edge (a,b) → midpoint vertex index
            var edgeMidpoints = new Dictionary<long, int>();

            int subMeshCount = mesh.subMeshCount;
            var allNewTriangles = new List<int[]>(subMeshCount);

            for (int sub = 0; sub < subMeshCount; sub++)
            {
                EditorUtility.DisplayProgressBar("Subdividing Mesh", $"Submesh {sub+1}/{subMeshCount}...", (float)sub / subMeshCount);
                var triangles = mesh.GetTriangles(sub);
                var newTris = new List<int>();

                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];

                    bool triAffected = isAffected[a] || isAffected[b] || isAffected[c];

                    if (!triAffected)
                    {
                        newTris.Add(a);
                        newTris.Add(b);
                        newTris.Add(c);
                        continue;
                    }

                    // Subdivide: split all 3 edges at midpoints, create 4 sub-triangles.
                    // Shared edges reuse the same midpoint vertex (no cracks).
                    int ab = GetOrCreateMidpoint(a, b, edgeMidpoints,
                        newVertices, newNormals, newTangents, newUV, newUV2,
                        newBoneWeights, newColors,
                        hasNormals, hasTangents, hasUV, hasUV2, hasBoneWeights, hasColors);
                    int bc = GetOrCreateMidpoint(b, c, edgeMidpoints,
                        newVertices, newNormals, newTangents, newUV, newUV2,
                        newBoneWeights, newColors,
                        hasNormals, hasTangents, hasUV, hasUV2, hasBoneWeights, hasColors);
                    int ca = GetOrCreateMidpoint(c, a, edgeMidpoints,
                        newVertices, newNormals, newTangents, newUV, newUV2,
                        newBoneWeights, newColors,
                        hasNormals, hasTangents, hasUV, hasUV2, hasBoneWeights, hasColors);

                    newTris.Add(a);  newTris.Add(ab); newTris.Add(ca);
                    newTris.Add(ab); newTris.Add(b);  newTris.Add(bc);
                    newTris.Add(ca); newTris.Add(bc); newTris.Add(c);
                    newTris.Add(ab); newTris.Add(bc); newTris.Add(ca);
                }

                allNewTriangles.Add(newTris.ToArray());
            }

            // Build new mesh
            var result = new Mesh();
            result.name = mesh.name + "_Subdivided";
            result.indexFormat = newVertices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            result.vertices = newVertices.ToArray();
            if (hasNormals) result.normals = newNormals.ToArray();
            if (hasTangents) result.tangents = newTangents.ToArray();
            if (hasUV) result.uv = newUV.ToArray();
            if (hasUV2) result.uv2 = newUV2.ToArray();
            if (hasColors) result.colors = newColors.ToArray();
            if (hasBoneWeights)
            {
                result.boneWeights = newBoneWeights.ToArray();
                result.bindposes = bindposes;
            }

            result.subMeshCount = subMeshCount;
            for (int sub = 0; sub < subMeshCount; sub++)
                result.SetTriangles(allNewTriangles[sub], sub);

            result.RecalculateBounds();
            if (!hasNormals) result.RecalculateNormals();
            if (!hasTangents) result.RecalculateTangents();

            // Copy blend shapes from source
            for (int bs = 0; bs < mesh.blendShapeCount; bs++)
            {
                string bsName = mesh.GetBlendShapeName(bs);
                int frameCount = mesh.GetBlendShapeFrameCount(bs);
                for (int f = 0; f < frameCount; f++)
                {
                    float weight = mesh.GetBlendShapeFrameWeight(bs, f);
                    var dv = new Vector3[vertCount];
                    var dn = new Vector3[vertCount];
                    var dt = new Vector3[vertCount];
                    mesh.GetBlendShapeFrameVertices(bs, f, dv, dn, dt);

                    // Expand to new vertex count (new vertices get zero deltas)
                    var newDV = new Vector3[newVertices.Count];
                    var newDN = new Vector3[newVertices.Count];
                    var newDT = new Vector3[newVertices.Count];
                    System.Array.Copy(dv, newDV, vertCount);
                    System.Array.Copy(dn, newDN, vertCount);
                    System.Array.Copy(dt, newDT, vertCount);

                    // Interpolate deltas for edge midpoint vertices
                    foreach (var kvp in edgeMidpoints)
                    {
                        int va = (int)(kvp.Key >> 32);
                        int vb = (int)(kvp.Key & 0xFFFFFFFFL);
                        int mid = kvp.Value;
                        newDV[mid] = (newDV[va] + newDV[vb]) * 0.5f;
                        newDN[mid] = (newDN[va] + newDN[vb]) * 0.5f;
                        newDT[mid] = (newDT[va] + newDT[vb]) * 0.5f;
                    }

                    result.AddBlendShapeFrame(bsName, weight, newDV, newDN, newDT);
                }
            }

            Object.DestroyImmediate(mesh);
            return result;
        }

        /// <summary>
        /// Gets or creates a midpoint vertex for the edge (a, b).
        /// Reuses existing midpoints for shared edges.
        /// </summary>
        private static int GetOrCreateMidpoint(
            int a, int b, Dictionary<long, int> cache,
            List<Vector3> vertices, List<Vector3> normals, List<Vector4> tangents,
            List<Vector2> uv, List<Vector2> uv2,
            List<BoneWeight> boneWeights, List<Color> colors,
            bool hasN, bool hasT, bool hasUV, bool hasUV2, bool hasBW, bool hasC)
        {
            // Canonical edge key (smaller index first)
            long key = a < b
                ? ((long)a << 32) | (uint)b
                : ((long)b << 32) | (uint)a;

            if (cache.TryGetValue(key, out int existing))
                return existing;

            int idx = vertices.Count;

            vertices.Add((vertices[a] + vertices[b]) * 0.5f);

            if (hasN)
            {
                Vector3 na = normals[a], nb = normals[b];
                Vector3 sum = na + nb;
                // If endpoints are near-opposite, the average is ~zero; fall back to
                // one endpoint rather than emitting a zero normal.
                Vector3 midN = sum.sqrMagnitude > 1e-6f ? sum.normalized : na;
                normals.Add(midN);
            }

            if (hasT)
            {
                Vector4 ta = tangents[a], tb = tangents[b];
                // Tangent w stores handedness sign. When endpoints sit on opposite
                // sides of a mirrored-UV seam (sign mismatch), averaging xyz produces
                // a near-zero tangent; pick endpoint A's full tangent to keep a
                // consistent handedness rather than blending across the seam.
                if (Mathf.Sign(ta.w) != Mathf.Sign(tb.w))
                {
                    tangents.Add(ta);
                }
                else
                {
                    Vector3 sumT = new Vector3(
                        (ta.x + tb.x) * 0.5f,
                        (ta.y + tb.y) * 0.5f,
                        (ta.z + tb.z) * 0.5f);
                    if (sumT.sqrMagnitude < 1e-6f)
                        tangents.Add(ta);
                    else
                        tangents.Add(new Vector4(sumT.x, sumT.y, sumT.z, ta.w));
                }
            }

            if (hasUV) uv.Add((uv[a] + uv[b]) * 0.5f);
            if (hasUV2) uv2.Add((uv2[a] + uv2[b]) * 0.5f);
            if (hasC) colors.Add((colors[a] + colors[b]) * 0.5f);
            if (hasBW) boneWeights.Add(LerpBoneWeight(boneWeights[a], boneWeights[b]));

            cache[key] = idx;
            return idx;
        }

        /// <summary>
        /// Interpolates two BoneWeights by averaging weights for matching bones
        /// and picking the strongest influences.
        /// </summary>
        private static BoneWeight LerpBoneWeight(BoneWeight a, BoneWeight b)
        {
            // Collect all bone influences from both weights
            var influences = new Dictionary<int, float>();

            void AddInfluence(int bone, float weight)
            {
                if (weight <= 0f) return;
                if (influences.ContainsKey(bone))
                    influences[bone] += weight;
                else
                    influences[bone] = weight;
            }

            AddInfluence(a.boneIndex0, a.weight0 * 0.5f);
            AddInfluence(a.boneIndex1, a.weight1 * 0.5f);
            AddInfluence(a.boneIndex2, a.weight2 * 0.5f);
            AddInfluence(a.boneIndex3, a.weight3 * 0.5f);
            AddInfluence(b.boneIndex0, b.weight0 * 0.5f);
            AddInfluence(b.boneIndex1, b.weight1 * 0.5f);
            AddInfluence(b.boneIndex2, b.weight2 * 0.5f);
            AddInfluence(b.boneIndex3, b.weight3 * 0.5f);

            // Sort by weight descending, take top 4
            var sorted = new List<KeyValuePair<int, float>>(influences);
            sorted.Sort((x, y) => y.Value.CompareTo(x.Value));

            var result = new BoneWeight();
            float total = 0f;

            if (sorted.Count > 0) { result.boneIndex0 = sorted[0].Key; result.weight0 = sorted[0].Value; total += sorted[0].Value; }
            if (sorted.Count > 1) { result.boneIndex1 = sorted[1].Key; result.weight1 = sorted[1].Value; total += sorted[1].Value; }
            if (sorted.Count > 2) { result.boneIndex2 = sorted[2].Key; result.weight2 = sorted[2].Value; total += sorted[2].Value; }
            if (sorted.Count > 3) { result.boneIndex3 = sorted[3].Key; result.weight3 = sorted[3].Value; total += sorted[3].Value; }

            // Normalize
            if (total > 0f)
            {
                result.weight0 /= total;
                result.weight1 /= total;
                result.weight2 /= total;
                result.weight3 /= total;
            }

            return result;
        }
    }
}
