using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Finds the nearest point on the primary mesh's surface for overlay transfer,
    /// as a primary triangle and barycentric weights, within a maximum distance.
    ///
    /// Sampling the surface rather than the nearest primary vertex keeps
    /// neighbouring overlay verts in step where the overlay is denser than the
    /// primary: with a nearest-vertex lookup, two overlay verts a few mm apart
    /// could snap to different primary verts that move by quite different
    /// amounts, so the overlay moved in steps (visible as creases at full weight).
    /// </summary>
    internal sealed class PrimarySurfaceSampler
    {
        public struct Sample
        {
            public int i0, i1, i2;
            public Vector3 weights;

            /// <summary>Barycentric blend of a per-primary-vertex value.</summary>
            public Vector3 Blend(Vector3[] values)
                => values[i0] * weights.x + values[i1] * weights.y + values[i2] * weights.z;
        }

        private readonly Vector3[] _verts;
        private readonly List<int> _triangles = new List<int>();
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private readonly float _maxDistanceSq;
        private readonly float _cellSize;

        /// <param name="worldVerts">Primary verts in world space (posed).</param>
        /// <param name="triangles">Primary triangle indices.</param>
        /// <param name="includeVertex">Only triangles with at least one included
        /// vertex are searched; null searches every triangle.</param>
        /// <param name="maxDistance">Points further than this from the surface
        /// get no sample.</param>
        public PrimarySurfaceSampler(
            Vector3[] worldVerts, int[] triangles, bool[] includeVertex, float maxDistance)
        {
            _verts = worldVerts;
            _maxDistanceSq = maxDistance * maxDistance;

            float extentSum = 0f;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (includeVertex != null &&
                    !includeVertex[a] && !includeVertex[b] && !includeVertex[c])
                    continue;
                // Zero-area triangles have no usable barycentric weights.
                if (Vector3.Cross(worldVerts[b] - worldVerts[a],
                        worldVerts[c] - worldVerts[a]).sqrMagnitude < 1e-20f)
                    continue;
                _triangles.Add(a);
                _triangles.Add(b);
                _triangles.Add(c);
                var size = TriangleBounds(a, b, c, 0f).size;
                extentSum += Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            }

            int triCount = _triangles.Count / 3;
            // Each triangle goes in every cell its bounds (grown by maxDistance)
            // touch, so a query only needs its own cell and any cell size is
            // correct. Matching cells to typical triangle size keeps both the
            // per-triangle cell count and the per-cell triangle count low.
            float meanExtent = triCount > 0 ? extentSum / triCount : 0f;
            _cellSize = Mathf.Max(Mathf.Max(maxDistance, meanExtent), 1e-4f);

            for (int i = 0; i < triCount; i++)
            {
                var bounds = TriangleBounds(_triangles[i * 3], _triangles[i * 3 + 1],
                    _triangles[i * 3 + 2], maxDistance);
                Vector3Int min = Cell(bounds.min), max = Cell(bounds.max);
                for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                {
                    long key = Key(x, y, z);
                    if (!_cells.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        _cells[key] = list;
                    }
                    list.Add(i);
                }
            }
        }

        /// <summary>
        /// Nearest point on the searched surface within the maximum distance.
        /// Returns false if there's none.
        /// </summary>
        public bool TrySample(Vector3 point, out Sample sample)
        {
            sample = default;
            var c = Cell(point);
            if (!_cells.TryGetValue(Key(c.x, c.y, c.z), out var candidates))
                return false;

            float bestSq = _maxDistanceSq;
            bool found = false;
            foreach (int i in candidates)
            {
                int a = _triangles[i * 3], b = _triangles[i * 3 + 1], d = _triangles[i * 3 + 2];
                var closest = ClosestPointOnTriangle(point, _verts[a], _verts[b], _verts[d],
                    out var weights);
                float sq = (closest - point).sqrMagnitude;
                if (sq >= bestSq) continue;
                bestSq = sq;
                found = true;
                sample = new Sample { i0 = a, i1 = b, i2 = d, weights = weights };
            }
            return found;
        }

        /// <summary>
        /// Closest point to <paramref name="p"/> on triangle abc, with its
        /// barycentric weights (Ericson, Real-Time Collision Detection 5.1.5).
        /// A point on a corner gets exactly that corner's weight of 1.
        /// </summary>
        internal static Vector3 ClosestPointOnTriangle(
            Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 weights)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { weights = new Vector3(1f, 0f, 0f); return a; }

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { weights = new Vector3(0f, 1f, 0f); return b; }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                weights = new Vector3(1f - v, v, 0f);
                return a + v * ab;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { weights = new Vector3(0f, 0f, 1f); return c; }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                weights = new Vector3(1f - w, 0f, w);
                return a + w * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                weights = new Vector3(0f, 1f - w, w);
                return b + w * (c - b);
            }

            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            weights = new Vector3(1f - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }

        private Bounds TriangleBounds(int a, int b, int c, float grow)
        {
            var bounds = new Bounds(_verts[a], Vector3.zero);
            bounds.Encapsulate(_verts[b]);
            bounds.Encapsulate(_verts[c]);
            bounds.Expand(grow * 2f);
            return bounds;
        }

        private Vector3Int Cell(Vector3 p) => new Vector3Int(
            Mathf.FloorToInt(p.x / _cellSize),
            Mathf.FloorToInt(p.y / _cellSize),
            Mathf.FloorToInt(p.z / _cellSize));

        private static long Key(int x, int y, int z)
            => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    }
}
