using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public enum GuidedPathDummyAddLight
    {
        None,
        Hole,
        Ring,
        Auto
    }

    public class GuidedPathDummyStop
    {
        public Transform transform;
        public bool customizeTangentIn;
        public bool customizeTangentOut;
        public Vector3 tangentInLocal;
        public Vector3 tangentOutLocal;
    }

    public class GuidedPathDummySocket : MonoBehaviour
    {
        public GuidedPathDummyAddLight addLight = GuidedPathDummyAddLight.Auto;
        public Vector3 position;
        public Vector3 rotation;
        public bool useRadiusOffset;
        public List<GuidedPathDummyStop> guidedPathStops = new List<GuidedPathDummyStop>();
    }

    public class GuidedPathDummyDepthAction
    {
        public object units;
        public Vector2 range = new Vector2(-0.25f, 0f);
    }

    public class SpsGuidedPathTests
    {
        private readonly List<GameObject> created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
        }

        private GameObject Create(string name, Vector3 position, Transform parent = null)
        {
            var go = new GameObject(name);
            created.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = position;
            return go;
        }

        private GuidedPathDummySocket CreateStraightSocket(params float[] stopDepths)
        {
            var socket = Create("Socket", Vector3.zero).AddComponent<GuidedPathDummySocket>();
            foreach (float depth in stopDepths)
            {
                var stop = Create($"Stop {depth}", new Vector3(0f, 0f, -depth));
                socket.guidedPathStops.Add(new GuidedPathDummyStop { transform = stop.transform });
            }
            return socket;
        }

        [Test]
        public void BuildSegment_DefaultTangentsFollowStopForwardAxes()
        {
            var segment = SpsGuidedPath.BuildSegment(
                Matrix4x4.identity,
                Matrix4x4.Translate(new Vector3(0f, 0f, -2f)),
                false, Vector3.zero, false, Vector3.zero);

            Assert.AreEqual(Vector3.zero, segment.p0);
            Assert.AreEqual(new Vector3(0f, 0f, -1f), segment.p1);
            Assert.AreEqual(new Vector3(0f, 0f, -1f), segment.p2);
            Assert.AreEqual(new Vector3(0f, 0f, -2f), segment.p3);
        }

        [Test]
        public void BuildSegment_CustomTangentsAreInTheirOwnPointsSpace()
        {
            var segment = SpsGuidedPath.BuildSegment(
                Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * 2f),
                Matrix4x4.Translate(new Vector3(0f, 0f, -2f)),
                true, new Vector3(0f, 1f, 0f), true, new Vector3(1f, 0f, 0f));

            Assert.AreEqual(new Vector3(0f, 2f, 0f), segment.p1);
            Assert.AreEqual(new Vector3(1f, 0f, -2f), segment.p2);
        }

        [Test]
        public void FromSocket_StraightPath_MeasuresLengthAndEvaluatesByArcLength()
        {
            var socket = CreateStraightSocket(0.2f, 0.5f);

            var path = SpsGuidedPath.FromSocket(socket);

            Assert.IsNotNull(path);
            Assert.AreEqual(2, path.StopCount);
            Assert.AreEqual(0.5f, path.Length, 1e-3f);
            Assert.AreEqual(0.5f, path.LocalLength, 1e-3f);
            Assert.AreEqual(Vector3.zero, path.Entrance);
            Assert.AreEqual(-0.25f, path.Evaluate(0.5f).z, 1e-3f);
            Assert.AreEqual(-0.5f, path.End.z, 1e-4f);

            var samples = path.SampleEvenly(3);
            Assert.AreEqual(3, samples.Count);
            Assert.AreEqual(-0.25f, samples[1].z, 1e-3f);
        }

        [Test]
        public void FromSocket_UsesSocketOffsetAndRadiusOffset()
        {
            var socket = CreateStraightSocket(1f);
            socket.position = new Vector3(0f, 0f, 0.1f);
            socket.useRadiusOffset = true;

            var path = SpsGuidedPath.FromSocket(socket);

            Assert.AreEqual(0f, Vector3.Distance(
                new Vector3(0f, SpsGuidedPath.RadiusOffset, 0.1f), path.Entrance), 1e-5f);
        }

        [Test]
        public void FromSocket_TypeNoneUsesObjectTransform()
        {
            var socket = CreateStraightSocket(1f);
            socket.addLight = GuidedPathDummyAddLight.None;
            socket.position = new Vector3(0f, 0f, 0.1f);

            var path = SpsGuidedPath.FromSocket(socket);

            Assert.AreEqual(Vector3.zero, path.Entrance);
        }

        [Test]
        public void FromSocket_LocalLengthDividesBySocketScale()
        {
            var socket = CreateStraightSocket(1f);
            socket.transform.localScale = Vector3.one * 2f;

            var path = SpsGuidedPath.FromSocket(socket);

            Assert.AreEqual(1f, path.Length, 1e-3f);
            Assert.AreEqual(0.5f, path.LocalLength, 1e-3f);
        }

        [Test]
        public void FromSocket_ReturnsNullWithoutUsableStops()
        {
            var socket = CreateStraightSocket();
            Assert.IsNull(SpsGuidedPath.FromSocket(socket));

            socket.guidedPathStops.Add(new GuidedPathDummyStop());
            Assert.IsNull(SpsGuidedPath.FromSocket(socket));
        }

        [Test]
        public void ClosestFraction_ReturnsArcLengthFractionOfNearestPoint()
        {
            var path = SpsGuidedPath.FromSocket(CreateStraightSocket(0.2f, 0.5f));

            Assert.AreEqual(0.5f, path.ClosestFraction(new Vector3(0.1f, 0f, -0.25f)), 1e-3f);
            Assert.AreEqual(0f, path.ClosestFraction(new Vector3(0f, 0.3f, 0.2f)), 1e-3f);
            Assert.AreEqual(1f, path.ClosestFraction(new Vector3(0f, 0f, -0.9f)), 1e-3f);
        }

        [Test]
        public void DepthMap_FittedRangeReportsFractionOfPath()
        {
            var path = SpsGuidedPath.FromSocket(CreateStraightSocket(0.5f));
            var map = new GuidedPathDepthMap(path, "Depth", "Socket",
                path.LocalLength, new Vector2(-path.LocalLength, 0f));

            Assert.AreEqual(0f, map.ValueAtFraction(0f), 1e-4f);
            Assert.AreEqual(0.3f, map.ValueAtFraction(0.3f), 1e-4f);
            Assert.AreEqual(1f, map.ValueAtFraction(1f), 1e-4f);
        }

        [Test]
        public void DepthMap_ShortRangeSaturatesPartWayAlongPath()
        {
            // VRCFury's default (-0.25, 0) m on a 0.5 m path
            var path = SpsGuidedPath.FromSocket(CreateStraightSocket(0.5f));
            var map = new GuidedPathDepthMap(path, "Depth", "Socket",
                path.Length, new Vector2(-0.25f, 0f));

            Assert.AreEqual(0.4f, map.ValueAtFraction(0.2f), 1e-3f);
            Assert.AreEqual(1f, map.ValueAtFraction(0.5f), 1e-3f);
            Assert.AreEqual(1f, map.ValueAtFraction(0.8f), 1e-3f);
        }

        [Test]
        public void GuidedLayout_OrdersPositionsByDepthFromEitherEnd()
        {
            var forward = BulgeGenerator.BuildGuidedLayout(new List<float> { 0.2f, 0.5f, 0.8f });
            Assert.AreEqual(new[] { 0, 1, 2 }, Positions(forward));
            Assert.AreEqual(0.05f, forward.restUntil, 1e-4f);
            Assert.AreEqual(0, forward.skippedPositions);

            var reversed = BulgeGenerator.BuildGuidedLayout(new List<float> { 0.8f, 0.5f, 0.2f });
            Assert.AreEqual(new[] { 2, 1, 0 }, Positions(reversed));
            Assert.AreEqual(0.2f, reversed.stops[0].depth, 1e-4f);
        }

        [Test]
        public void GuidedLayout_SkipsPositionsTheTipDoesNotReachOnTheirOwn()
        {
            // Before the entrance (0), then past where the FX Float saturates (1).
            var layout = BulgeGenerator.BuildGuidedLayout(
                new List<float> { 0f, 0.4f, 1f, 1f, 1f });

            Assert.AreEqual(new[] { 1, 2 }, Positions(layout));
            Assert.AreEqual(3, layout.skippedPositions);
        }

        [Test]
        public void BulgeThresholds_FollowGuidedPathAlongsideDrawnPath()
        {
            var avatar = Create("Avatar", Vector3.zero);
            var path = SpsGuidedPath.FromSocket(CreateStraightSocket(0.5f));
            var map = new GuidedPathDepthMap(path, "Depth", "Socket",
                path.LocalLength, new Vector2(-path.LocalLength, 0f));

            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                config.avatarRoot = avatar;
                config.rendererPath = "Body";
                config.autoPositionCount = 3;
                // Drawn on the surface 10 cm out from the path, entrance end last.
                config.pathWaypoints = new List<PathWaypoint>
                {
                    new PathWaypoint { localPosition = new Vector3(0.1f, 0f, -0.4f) },
                    new PathWaypoint { localPosition = new Vector3(0.1f, 0f, -0.1f) },
                };

                var layout = BulgeGenerator.ComputeDepthLayout(config, map);
                Assert.AreSame(map, layout.guidedPath);
                Assert.AreEqual(new[] { 2, 1, 0 }, Positions(layout));

                // Generating stores the generated names; they still follow the path.
                config.positionBlendshapes = new List<string> { "Gen_Pos1", "Gen_Pos2", "Gen_Pos3" };
                Assert.AreEqual(new[] { 2, 1, 0 },
                    Positions(BulgeGenerator.ComputeDepthLayout(config, map)));

                var entries = BulgeGenerator.BuildPreviewThresholds(config, map);
                var thresholds = new List<float>();
                foreach (var entry in entries) thresholds.Add(entry.threshold);
                CollectionAssert.AreEqual(new[] { 0f, 0.05f, 0.2f, 0.5f, 0.8f, 1f },
                    thresholds, new FloatTolerance(1e-3f));
                Assert.AreSame(entries[4].clip, entries[5].clip,
                    "The deepest position holds to depth 1.");
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void BulgeThresholds_ManualBlendshapesIgnoreGuidedPath()
        {
            var path = SpsGuidedPath.FromSocket(CreateStraightSocket(0.5f));
            var map = new GuidedPathDepthMap(path, "Depth", "Socket",
                path.LocalLength, new Vector2(-path.LocalLength, 0f));

            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                config.avatarRoot = Create("Avatar", Vector3.zero);
                config.positionBlendshapes = new List<string> { "A", "B" };
                // A stale path with a different position count isn't where these are.
                config.autoPositionCount = 3;
                config.pathWaypoints = new List<PathWaypoint>
                {
                    new PathWaypoint { localPosition = new Vector3(0.1f, 0f, -0.4f) },
                    new PathWaypoint { localPosition = new Vector3(0.1f, 0f, -0.1f) },
                };

                var layout = BulgeGenerator.ComputeDepthLayout(config, map);

                Assert.IsNull(layout.guidedPath);
                Assert.AreEqual(0.25f, layout.stops[0].depth, 1e-4f);
                Assert.AreEqual(0.75f, layout.stops[1].depth, 1e-4f);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        private static int[] Positions(BulgeDepthLayout layout)
        {
            var positions = new int[layout.stops.Count];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = layout.stops[i].position;
            return positions;
        }

        private class FloatTolerance : System.Collections.IComparer
        {
            private readonly float tolerance;
            public FloatTolerance(float tolerance) { this.tolerance = tolerance; }
            public int Compare(object x, object y)
            {
                float a = (float)x, b = (float)y;
                return Mathf.Abs(a - b) <= tolerance ? 0 : a.CompareTo(b);
            }
        }

        [Test]
        public void ConfigureDepthRange_GuidedPathUsesLocalUnitsOverPathLength()
        {
            var depthAction = new GuidedPathDummyDepthAction();
            var path = new SpsGuidedPath(new List<(Vector3, Vector3, Vector3, Vector3)>
            {
                (Vector3.zero, new Vector3(0f, 0f, -0.1f), new Vector3(0f, 0f, -0.3f),
                    new Vector3(0f, 0f, -0.4f))
            }, 2f);

            string description = DepthParameterDetector.ConfigureDepthRange(depthAction, path);
            Assume.That(description, Is.Not.Null, "VRCFury DepthActionUnits is not available.");

            Assert.AreEqual("Local", depthAction.units.ToString());
            Assert.AreEqual(-0.2f, depthAction.range.x, 1e-3f);
            Assert.AreEqual(0f, depthAction.range.y);
        }

        [Test]
        public void ConfigureDepthRange_WithoutGuidedPathUsesPlugs()
        {
            var depthAction = new GuidedPathDummyDepthAction();

            string description = DepthParameterDetector.ConfigureDepthRange(depthAction, null);
            Assume.That(description, Is.Not.Null, "VRCFury DepthActionUnits is not available.");

            Assert.AreEqual("Plugs", depthAction.units.ToString());
            Assert.AreEqual(new Vector2(-1f, 0f), depthAction.range);
        }
    }
}
