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
        public void Surface_ProjectsFromInsideOntoTheFaceItLeavesThrough()
        {
            var surface = CreateUnitCubeSurface();

            Assert.IsTrue(surface.TryProject(Vector3.zero, Vector3.forward,
                out Vector3 point, out Vector3 normal));
            Assert.AreEqual(0.5f, point.z, 1e-4f);
            Assert.Greater(Vector3.Dot(normal, Vector3.forward), 0.9f);
        }

        [Test]
        public void Surface_RejectsPointsOutsideTheMesh()
        {
            var surface = CreateUnitCubeSurface();

            // Behind the cube, the first hit is a face looking back at the point.
            Assert.IsFalse(surface.TryProject(new Vector3(0f, 0f, -2f), Vector3.forward,
                out _, out _));
            // In front of the cube, nothing is hit.
            Assert.IsFalse(surface.TryProject(new Vector3(0f, 0f, 2f), Vector3.forward,
                out _, out _));
        }

        [Test]
        public void FindCoveredRange_TrimsPathEndsOutsideTheMesh()
        {
            // Straight path from y=1 down to y=-1 through a unit cube.
            var socket = Create("Socket", new Vector3(0f, 1f, 0f))
                .AddComponent<GuidedPathDummySocket>();
            socket.transform.rotation = Quaternion.LookRotation(Vector3.up);
            var stop = Create("Stop", new Vector3(0f, -1f, 0f));
            stop.transform.rotation = Quaternion.LookRotation(Vector3.up);
            socket.guidedPathStops.Add(new GuidedPathDummyStop { transform = stop.transform });
            var path = SpsGuidedPath.FromSocket(socket);

            var result = GuidedPathSurfaceProjector.FindCoveredRange(
                path, CreateUnitCubeSurface(), Vector3.forward);

            Assert.IsNotNull(result);
            Assert.AreEqual(0.25f, result.startFraction, 0.03f);
            Assert.AreEqual(0.75f, result.endFraction, 0.03f);
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

        private static GuidedPathSurfaceProjector.Surface CreateUnitCubeSurface()
        {
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            return new GuidedPathSurfaceProjector.Surface(
                cube.vertices, cube.normals, cube.triangles);
        }
    }
}
