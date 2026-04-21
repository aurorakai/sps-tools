using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Catmull-Rom spline interpolation for smooth path curves.
    /// Uses phantom endpoints for natural boundary tangents.
    /// </summary>
    public static class CatmullRomSpline
    {
        /// <summary>
        /// Evaluates a point on the Catmull-Rom spline at parameter t (0 = first point, 1 = last).
        /// </summary>
        public static Vector3 Evaluate(List<Vector3> points, float t)
        {
            if (points == null || points.Count == 0) return Vector3.zero;
            if (points.Count == 1) return points[0];
            if (points.Count == 2) return Vector3.Lerp(points[0], points[1], t);

            int spanCount = points.Count - 1;
            float scaledT = Mathf.Clamp01(t) * spanCount;
            int span = Mathf.Min((int)scaledT, spanCount - 1);
            float localT = scaledT - span;

            Vector3 p0 = GetPointOrPhantom(points, span - 1);
            Vector3 p1 = points[span];
            Vector3 p2 = points[Mathf.Min(span + 1, points.Count - 1)];
            Vector3 p3 = GetPointOrPhantom(points, span + 2);

            return EvaluateSegment(p0, p1, p2, p3, localT);
        }

        /// <summary>
        /// Returns N evenly-spaced sample points along the spline's arc length.
        /// </summary>
        public static List<Vector3> SampleEvenlySpaced(List<Vector3> points, int sampleCount)
        {
            if (points == null || points.Count < 2 || sampleCount < 2)
                return new List<Vector3>(points ?? new List<Vector3>());

            // Build arc-length lookup table
            int subdivisions = (points.Count - 1) * 50;
            var lut = BuildArcLengthLUT(points, subdivisions);
            float totalLength = lut[lut.Count - 1];

            var samples = new List<Vector3>();
            for (int i = 0; i < sampleCount; i++)
            {
                float targetLength = (i / (float)(sampleCount - 1)) * totalLength;
                float t = LookupT(lut, targetLength, subdivisions);
                samples.Add(Evaluate(points, t));
            }

            return samples;
        }

        /// <summary>
        /// Returns the total arc length of the spline (approximated).
        /// </summary>
        public static float ArcLength(List<Vector3> points, int subdivisions = 100)
        {
            if (points == null || points.Count < 2) return 0f;
            var lut = BuildArcLengthLUT(points, subdivisions);
            return lut[lut.Count - 1];
        }

        /// <summary>
        /// Returns an array of points for rendering the spline as a smooth polyline.
        /// </summary>
        public static Vector3[] ToPolyline(List<Vector3> points, int segmentsPerSpan = 10)
        {
            if (points == null || points.Count < 2)
                return points?.ToArray() ?? new Vector3[0];

            int spanCount = points.Count - 1;
            int totalSegments = spanCount * segmentsPerSpan;
            var result = new Vector3[totalSegments + 1];

            for (int i = 0; i <= totalSegments; i++)
            {
                float t = (float)i / totalSegments;
                result[i] = Evaluate(points, t);
            }

            return result;
        }

        /// <summary>
        /// Evaluates a point on the spline and also outputs interpolated values
        /// for normal and radius from the waypoint data.
        /// </summary>
        // Reusable list for EvaluateWithAttributes to avoid per-call allocation
        [System.ThreadStatic] private static List<Vector3> s_evalPositions;

        public static void EvaluateWithAttributes(
            List<PathWaypoint> waypoints, float t,
            out Vector3 position, out Vector3 normal, out float radius)
        {
            EvaluateWithAttributes(waypoints, t, out position, out normal, out radius, out _);
        }

        public static void EvaluateWithAttributes(
            List<PathWaypoint> waypoints, float t,
            out Vector3 position, out Vector3 normal, out float radius, out float aspectRatio)
        {
            if (waypoints == null || waypoints.Count == 0)
            {
                position = Vector3.zero;
                normal = Vector3.up;
                radius = 0.03f;
                aspectRatio = 1f;
                return;
            }

            if (waypoints.Count == 1)
            {
                position = waypoints[0].localPosition;
                normal = waypoints[0].localNormal;
                radius = waypoints[0].radius;
                aspectRatio = waypoints[0].aspectRatio;
                return;
            }

            if (s_evalPositions == null) s_evalPositions = new List<Vector3>();
            s_evalPositions.Clear();
            foreach (var wp in waypoints)
                s_evalPositions.Add(wp.localPosition);

            position = Evaluate(s_evalPositions, t);

            // Linear interpolation for normal, radius, aspect
            int spanCount = waypoints.Count - 1;
            float scaledT = Mathf.Clamp01(t) * spanCount;
            int span = Mathf.Min((int)scaledT, spanCount - 1);
            float localT = scaledT - span;

            int idx1 = span;
            int idx2 = Mathf.Min(span + 1, waypoints.Count - 1);

            normal = Vector3.Slerp(
                waypoints[idx1].localNormal,
                waypoints[idx2].localNormal, localT).normalized;
            radius = Mathf.Lerp(waypoints[idx1].radius, waypoints[idx2].radius, localT);
            aspectRatio = Mathf.Lerp(waypoints[idx1].aspectRatio, waypoints[idx2].aspectRatio, localT);
        }

        /// <summary>
        /// A pre-sampled spline stored as line segments with interpolated radii.
        /// Build once, then query many vertices cheaply with DistanceToTube().
        /// </summary>
        public struct SplineTube
        {
            public Vector3[] points;
            public float[] radii;
            public float[] aspects;   // per-sample aspect ratio (1 = circle)
            public int count;

            /// <summary>
            /// Returns the distance from queryPoint to the nearest point on the tube centerline,
            /// the interpolated radius, and the along-path parameter (0 = start, 1 = end).
            /// Uses point-to-line-segment math - no spline evaluation per query.
            /// </summary>
            public float DistanceToTube(Vector3 queryPoint,
                out float radiusAtClosest, out float pathT)
            {
                return DistanceToTube(queryPoint, out radiusAtClosest, out _, out pathT);
            }

            /// <summary>
            /// Overload returning aspect ratio at the closest point too.
            /// </summary>
            public float DistanceToTube(Vector3 queryPoint,
                out float radiusAtClosest, out float aspectAtClosest, out float pathT)
            {
                radiusAtClosest = 0f;
                aspectAtClosest = 1f;
                pathT = 0f;
                if (count == 0) return float.MaxValue;

                if (count == 1)
                {
                    radiusAtClosest = radii[0];
                    aspectAtClosest = aspects != null ? aspects[0] : 1f;
                    return Vector3.Distance(queryPoint, points[0]);
                }

                float bestSqrDist = float.MaxValue;
                float bestRadius = radii[0];
                float bestAspect = aspects != null ? aspects[0] : 1f;
                float bestPathT = 0f;
                int segCount = count - 1;

                for (int i = 0; i < segCount; i++)
                {
                    Vector3 a = points[i];
                    Vector3 b = points[i + 1];
                    Vector3 ab = b - a;
                    float segLenSq = ab.sqrMagnitude;

                    float t;
                    if (segLenSq < 0.000001f)
                        t = 0f;
                    else
                        t = Mathf.Clamp01(Vector3.Dot(queryPoint - a, ab) / segLenSq);

                    Vector3 closest = a + ab * t;
                    float sqrDist = (queryPoint - closest).sqrMagnitude;

                    if (sqrDist < bestSqrDist)
                    {
                        bestSqrDist = sqrDist;
                        bestRadius = Mathf.Lerp(radii[i], radii[i + 1], t);
                        if (aspects != null)
                            bestAspect = Mathf.Lerp(aspects[i], aspects[i + 1], t);
                        bestPathT = (i + t) / segCount;
                    }
                }

                radiusAtClosest = bestRadius;
                aspectAtClosest = bestAspect;
                pathT = bestPathT;
                return Mathf.Sqrt(bestSqrDist);
            }

            /// <summary>
            /// Convenience overload without pathT output.
            /// </summary>
            public float DistanceToTube(Vector3 queryPoint, out float radiusAtClosest)
            {
                return DistanceToTube(queryPoint, out radiusAtClosest, out _);
            }
        }

        /// <summary>
        /// Pre-samples the spline into a polyline of segments with interpolated radii.
        /// Use the returned SplineTube.DistanceToTube() for fast per-vertex queries.
        /// </summary>
        public static SplineTube BuildTube(List<PathWaypoint> waypoints, int segments)
        {
            var tube = new SplineTube();
            if (waypoints == null || waypoints.Count == 0)
            {
                tube.count = 0;
                return tube;
            }

            if (waypoints.Count == 1)
            {
                tube.points = new Vector3[] { waypoints[0].localPosition };
                tube.radii = new float[] { waypoints[0].radius };
                tube.aspects = new float[] { waypoints[0].aspectRatio };
                tube.count = 1;
                return tube;
            }

            int sampleCount = Mathf.Max(segments + 1, 3);
            tube.points = new Vector3[sampleCount];
            tube.radii = new float[sampleCount];
            tube.aspects = new float[sampleCount];
            tube.count = sampleCount;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = (float)i / (sampleCount - 1);
                EvaluateWithAttributes(waypoints, t,
                    out tube.points[i], out _, out tube.radii[i], out tube.aspects[i]);
            }

            return tube;
        }

        // --- Internal ---

        private static Vector3 GetPointOrPhantom(List<Vector3> points, int index)
        {
            if (index < 0)
                return 2f * points[0] - points[1]; // Start phantom
            if (index >= points.Count)
                return 2f * points[points.Count - 1] - points[points.Count - 2]; // End phantom
            return points[index];
        }

        private static Vector3 EvaluateSegment(
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        private static List<float> BuildArcLengthLUT(List<Vector3> points, int subdivisions)
        {
            var lut = new List<float>(subdivisions + 1);
            lut.Add(0f);

            Vector3 prev = Evaluate(points, 0f);
            for (int i = 1; i <= subdivisions; i++)
            {
                float t = (float)i / subdivisions;
                Vector3 curr = Evaluate(points, t);
                lut.Add(lut[i - 1] + Vector3.Distance(prev, curr));
                prev = curr;
            }

            return lut;
        }

        private static float LookupT(List<float> lut, float targetLength, int subdivisions)
        {
            // Binary search
            int lo = 0, hi = lut.Count - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (lut[mid] < targetLength)
                    lo = mid;
                else
                    hi = mid;
            }

            float segLength = lut[hi] - lut[lo];
            float frac = segLength > 0.0001f
                ? (targetLength - lut[lo]) / segLength
                : 0f;

            return (lo + frac) / subdivisions;
        }
    }
}
