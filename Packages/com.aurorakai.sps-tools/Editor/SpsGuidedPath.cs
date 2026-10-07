using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// The guided path of an SPS2 socket. SPS2 plugs deform (in the shader)
    /// along this path, so the plug tip travels along it as depth increases.
    ///
    /// Rebuilt from the VRCFury socket's <c>guidedPathStops</c> as the same
    /// cubic bezier chain VRCFury draws in its socket gizmo: one segment from
    /// the socket entrance to the first stop, then one per following stop.
    /// Positions are world space; lengths are arc lengths.
    /// </summary>
    public class SpsGuidedPath
    {
        // VRCFuryHapticSocketEditor.GizmoRadiusOffset
        internal const float RadiusOffset = 0.04f;
        private const int SamplesPerSegment = 32;

        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<float> cumulative = new List<float>();

        public int StopCount { get; }

        /// <summary>Arc length in world metres.</summary>
        public float Length { get; }

        /// <summary>
        /// Arc length in the socket's local units, which is what VRCFury's
        /// "Local" depth action units measure. Unlike metres, this stays correct
        /// when the avatar is scaled in game.
        /// </summary>
        public float LocalLength { get; }

        public Vector3 Entrance => points[0];
        public Vector3 End => points[points.Count - 1];

        internal SpsGuidedPath(
            IList<(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)> segments,
            float socketWorldScale)
        {
            StopCount = segments.Count;
            points.Add(segments[0].p0);
            cumulative.Add(0f);

            foreach (var (p0, p1, p2, p3) in segments)
            {
                for (int i = 1; i <= SamplesPerSegment; i++)
                {
                    var point = EvaluateBezier(p0, p1, p2, p3, i / (float)SamplesPerSegment);
                    cumulative.Add(cumulative[cumulative.Count - 1] +
                        Vector3.Distance(points[points.Count - 1], point));
                    points.Add(point);
                }
            }

            Length = cumulative[cumulative.Count - 1];
            float scale = Mathf.Abs(socketWorldScale);
            LocalLength = scale > 1e-6f ? Length / scale : Length;
        }

        /// <summary>
        /// Reads the guided path from a VRCFury SPS socket component. Returns
        /// null when the socket has no usable stops (a plain SPS socket).
        /// </summary>
        public static SpsGuidedPath FromSocket(Component socket)
        {
            if (socket == null) return null;

            if (!(Field(socket, "guidedPathStops") is IList stops) ||
                stops.Count == 0)
            {
                return null;
            }

            Matrix4x4 current = GetSocketWorldMatrix(socket);
            if (Field(socket, "useRadiusOffset") is bool useRadiusOffset &&
                useRadiusOffset)
            {
                Vector3 entrance = current.GetPosition() +
                    current.rotation * (Vector3.up * RadiusOffset);
                current.SetColumn(3, new Vector4(entrance.x, entrance.y, entrance.z, 1f));
            }

            var segments = new List<(Vector3, Vector3, Vector3, Vector3)>();
            foreach (object stop in stops)
            {
                if (stop == null) continue;
                if (!(Field(stop, "transform") is Transform stopTransform) ||
                    stopTransform == null)
                {
                    continue;
                }

                Matrix4x4 next = stopTransform.localToWorldMatrix;
                segments.Add(BuildSegment(
                    current, next,
                    Field(stop, "customizeTangentOut") is bool customOut && customOut,
                    Field(stop, "tangentOutLocal") is Vector3 tangentOut
                        ? tangentOut : Vector3.zero,
                    Field(stop, "customizeTangentIn") is bool customIn && customIn,
                    Field(stop, "tangentInLocal") is Vector3 tangentIn
                        ? tangentIn : Vector3.zero));
                current = next;
            }

            if (segments.Count == 0) return null;
            return new SpsGuidedPath(segments, socket.transform.lossyScale.x);
        }

        /// <summary>
        /// Builds one bezier segment between two path points the way VRCFury's
        /// gizmo does. A stop's "tangent out" belongs to the point before it.
        /// Default tangents leave the previous point along its -forward and
        /// arrive at the next point along its +forward, each half the chord
        /// length long.
        /// </summary>
        internal static (Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3) BuildSegment(
            Matrix4x4 previous, Matrix4x4 next,
            bool customizeTangentOut, Vector3 tangentOutLocal,
            bool customizeTangentIn, Vector3 tangentInLocal)
        {
            Vector3 start = previous.GetPosition();
            Vector3 end = next.GetPosition();
            float handle = Vector3.Distance(start, end) * 0.5f;

            Vector3 startHandle = customizeTangentOut
                ? previous.MultiplyPoint3x4(tangentOutLocal)
                : start - previous.rotation * Vector3.forward * handle;
            Vector3 endHandle = customizeTangentIn
                ? next.MultiplyPoint3x4(tangentInLocal)
                : end + next.rotation * Vector3.forward * handle;

            return (start, startHandle, endHandle, end);
        }

        /// <summary>
        /// Socket entrance in world space. VRCFury stores it as a local
        /// position/rotation on the socket's object unless the socket type is
        /// "None", in which case it reads it from legacy lights; we fall back to
        /// the object's own transform there.
        /// </summary>
        private static Matrix4x4 GetSocketWorldMatrix(Component socket)
        {
            Matrix4x4 owner = socket.transform.localToWorldMatrix;
            object addLight = Field(socket, "addLight");
            if (addLight == null || addLight.ToString() == "None")
                return owner;

            var position = Field(socket, "position") is Vector3 p ? p : Vector3.zero;
            var rotation = Field(socket, "rotation") is Vector3 r ? r : Vector3.zero;
            return owner * Matrix4x4.TRS(position, Quaternion.Euler(rotation), Vector3.one);
        }

        /// <summary>
        /// Point at a fraction (0 = entrance, 1 = end) of the path's arc length.
        /// </summary>
        public Vector3 Evaluate(float fraction)
        {
            float target = Mathf.Clamp01(fraction) * Length;
            int hi = cumulative.BinarySearch(target);
            if (hi >= 0) return points[hi];

            hi = ~hi;
            if (hi <= 0) return points[0];
            if (hi >= points.Count) return points[points.Count - 1];

            int lo = hi - 1;
            float span = cumulative[hi] - cumulative[lo];
            float t = span > 1e-8f ? (target - cumulative[lo]) / span : 0f;
            return Vector3.Lerp(points[lo], points[hi], t);
        }

        /// <summary>
        /// Fraction of the path's arc length (0 = entrance, 1 = end) at the
        /// point on the path closest to <paramref name="worldPoint"/>.
        /// </summary>
        public float ClosestFraction(Vector3 worldPoint)
        {
            if (Length <= 1e-8f) return 0f;

            float bestSqr = float.MaxValue;
            float bestLength = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                Vector3 a = points[i - 1];
                Vector3 ab = points[i] - a;
                float abSqr = ab.sqrMagnitude;
                float t = abSqr > 1e-12f
                    ? Mathf.Clamp01(Vector3.Dot(worldPoint - a, ab) / abSqr)
                    : 0f;
                float sqr = (a + ab * t - worldPoint).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestLength = Mathf.Lerp(cumulative[i - 1], cumulative[i], t);
                }
            }
            return bestLength / Length;
        }

        /// <summary>
        /// Returns <paramref name="count"/> points evenly spaced by arc length,
        /// from the entrance to the end.
        /// </summary>
        public List<Vector3> SampleEvenly(int count)
        {
            var samples = new List<Vector3>();
            if (count < 2)
            {
                samples.Add(Entrance);
                return samples;
            }

            for (int i = 0; i < count; i++)
                samples.Add(Evaluate(i / (float)(count - 1)));
            return samples;
        }

        private static object Field(object obj, string name) =>
            DepthParameterDetector.GetFieldValueRecursive(obj, name);

        private static Vector3 EvaluateBezier(
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 +
                   3f * u * u * t * p1 +
                   3f * u * t * t * p2 +
                   t * t * t * p3;
        }
    }
}
