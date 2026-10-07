using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Converts a point on a socket's SPS2 guided path to the value the
    /// socket's depth FX Float reports when the plug tip reaches it.
    ///
    /// SPS2 plugs bend along the guided path, so the length of plug inside the
    /// socket is the distance the tip has travelled along the path. VRCFury
    /// measures that as a negative distance (in the depth action's units) and
    /// maps it from range max (FX Float 0) to range min (FX Float 1), clamped.
    /// </summary>
    public class GuidedPathDepthMap
    {
        public SpsGuidedPath Path { get; }
        public string Parameter { get; }
        public string SocketName { get; }

        private readonly float pathLengthInUnits;
        private readonly float rangeMin;
        private readonly float rangeMax;

        internal GuidedPathDepthMap(
            SpsGuidedPath path, string parameter, string socketName,
            float pathLengthInUnits, Vector2 range)
        {
            Path = path;
            Parameter = parameter;
            SocketName = socketName;
            this.pathLengthInUnits = pathLengthInUnits;
            rangeMin = Mathf.Min(range.x, range.y);
            rangeMax = Mathf.Max(range.x, range.y);
        }

        /// <summary>
        /// Builds the map for <paramref name="parameter"/> on
        /// <paramref name="socket"/>. Returns null when the socket has no guided
        /// path, or the FX Float's depth animation uses Plugs units, which
        /// depend on the length of whichever plug is inserted.
        /// </summary>
        public static GuidedPathDepthMap For(DetectedSocket socket, string parameter)
        {
            if (socket == null || !socket.HasGuidedPath ||
                string.IsNullOrEmpty(parameter))
            {
                return null;
            }

            var path = SpsGuidedPath.FromSocket(socket.component);
            if (path == null || path.Length <= 1e-6f) return null;
            if (!DepthParameterDetector.TryGetFxFloatDepthRange(
                    socket.component, parameter, out string units, out Vector2 range))
            {
                return null;
            }

            float lengthInUnits;
            if (units == "Local") lengthInUnits = path.LocalLength;
            else if (units == "Meters") lengthInUnits = path.Length;
            else return null;

            return new GuidedPathDepthMap(path, parameter, socket.DisplayName,
                lengthInUnits, range);
        }

        /// <summary>
        /// FX Float value when the plug tip is <paramref name="fraction"/> of
        /// the way along the path.
        /// </summary>
        public float ValueAtFraction(float fraction)
        {
            float distance = -Mathf.Clamp01(fraction) * pathLengthInUnits;
            float span = rangeMin - rangeMax;
            if (Mathf.Abs(span) < 1e-6f)
                return distance <= rangeMin ? 1f : 0f;
            return Mathf.Clamp01((distance - rangeMax) / span);
        }

        /// <summary>
        /// FX Float value when the plug tip reaches the point on the path
        /// closest to <paramref name="worldPoint"/>.
        /// </summary>
        public float ValueAt(Vector3 worldPoint) =>
            ValueAtFraction(Path.ClosestFraction(worldPoint));
    }
}
