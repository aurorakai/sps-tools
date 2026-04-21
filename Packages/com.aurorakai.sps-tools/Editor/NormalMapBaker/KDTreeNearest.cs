using System;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Median-split KD-tree for 3D nearest-neighbour queries. Used by the baker
    /// to map each pixel's world-space position to its closest high-res vertex.
    /// </summary>
    public sealed class KDTreeNearest
    {
        private readonly Vector3[] _points;
        private readonly int[] _indices;
        private readonly int _pointCount;
        private readonly AxisComparer[] _comparers;

        /// <summary>Builds the tree. Captures <paramref name="points"/> by reference.</summary>
        public KDTreeNearest(Vector3[] points)
        {
            _points = points ?? throw new ArgumentNullException(nameof(points));
            _pointCount = points.Length;

            _indices = new int[_pointCount];
            for (int i = 0; i < _pointCount; i++) _indices[i] = i;

            _comparers = new[]
            {
                new AxisComparer(points, 0),
                new AxisComparer(points, 1),
                new AxisComparer(points, 2),
            };

            if (_pointCount > 0) Build(0, _pointCount, depth: 0);
        }

        /// <summary>Returns the index of the nearest point to <paramref name="query"/>, or -1 if empty.</summary>
        public int FindNearest(Vector3 query)
        {
            if (_pointCount == 0) return -1;

            int bestIdx = -1;
            float bestSqrDist = float.PositiveInfinity;
            SearchNearest(0, _pointCount, depth: 0, query, ref bestIdx, ref bestSqrDist);
            return bestIdx;
        }

        /// <summary>Median-split build: sort the index range by axis (depth mod 3), recurse on halves.</summary>
        private void Build(int start, int end, int depth)
        {
            if (end - start <= 1) return;

            int axis = depth % 3;
            int mid = (start + end) / 2;

            Array.Sort(_indices, start, end - start, _comparers[axis]);

            Build(start, mid, depth + 1);
            Build(mid + 1, end, depth + 1);
        }

        private void SearchNearest(
            int start, int end, int depth, Vector3 query,
            ref int bestIdx, ref float bestSqrDist)
        {
            if (end - start <= 0) return;

            int mid = (start + end) / 2;
            int pivotIdx = _indices[mid];
            Vector3 pivot = _points[pivotIdx];

            float sqrDist = (pivot - query).sqrMagnitude;
            if (sqrDist < bestSqrDist) { bestSqrDist = sqrDist; bestIdx = pivotIdx; }

            if (end - start == 1) return;

            int axis = depth % 3;
            float axisDiff = GetAxis(query, axis) - GetAxis(pivot, axis);

            // Descend near side first (better pruning on the far side).
            if (axisDiff < 0f)
            {
                SearchNearest(start, mid, depth + 1, query, ref bestIdx, ref bestSqrDist);
                if (axisDiff * axisDiff < bestSqrDist)
                    SearchNearest(mid + 1, end, depth + 1, query, ref bestIdx, ref bestSqrDist);
            }
            else
            {
                SearchNearest(mid + 1, end, depth + 1, query, ref bestIdx, ref bestSqrDist);
                if (axisDiff * axisDiff < bestSqrDist)
                    SearchNearest(start, mid, depth + 1, query, ref bestIdx, ref bestSqrDist);
            }
        }

        private static float GetAxis(Vector3 v, int axis)
        {
            switch (axis)
            {
                case 0: return v.x;
                case 1: return v.y;
                default: return v.z;
            }
        }

        private sealed class AxisComparer : System.Collections.Generic.IComparer<int>
        {
            private readonly Vector3[] _pts;
            private readonly int _axis;
            public AxisComparer(Vector3[] pts, int axis) { _pts = pts; _axis = axis; }
            public int Compare(int a, int b)
            {
                switch (_axis)
                {
                    case 0: return _pts[a].x.CompareTo(_pts[b].x);
                    case 1: return _pts[a].y.CompareTo(_pts[b].y);
                    default: return _pts[a].z.CompareTo(_pts[b].z);
                }
            }
        }
    }
}
