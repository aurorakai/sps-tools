using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class CatmullRomSplineTests
    {
        [Test]
        public void Evaluate_TwoPoints_ReturnsLerp()
        {
            var points = new List<Vector3>
            {
                Vector3.zero,
                Vector3.right
            };

            Assert.AreEqual(Vector3.zero, CatmullRomSpline.Evaluate(points, 0f));
            Assert.AreEqual(Vector3.right, CatmullRomSpline.Evaluate(points, 1f));

            var mid = CatmullRomSpline.Evaluate(points, 0.5f);
            Assert.AreEqual(0.5f, mid.x, 0.001f);
        }

        [Test]
        public void Evaluate_SinglePoint_ReturnsThatPoint()
        {
            var points = new List<Vector3> { new Vector3(3, 4, 5) };
            var result = CatmullRomSpline.Evaluate(points, 0.5f);
            Assert.AreEqual(new Vector3(3, 4, 5), result);
        }

        [Test]
        public void Evaluate_EmptyList_ReturnsZero()
        {
            var result = CatmullRomSpline.Evaluate(new List<Vector3>(), 0.5f);
            Assert.AreEqual(Vector3.zero, result);
        }

        [Test]
        public void Evaluate_Null_ReturnsZero()
        {
            Assert.AreEqual(Vector3.zero, CatmullRomSpline.Evaluate(null, 0.5f));
        }

        [Test]
        public void EvaluateWithAttributes_SingleWaypoint_ReturnsThatWaypoint()
        {
            var waypoints = new List<PathWaypoint>
            {
                new PathWaypoint
                {
                    localPosition = new Vector3(1, 2, 3),
                    localNormal = Vector3.up,
                    radius = 0.05f
                }
            };

            CatmullRomSpline.EvaluateWithAttributes(waypoints, 0.5f,
                out Vector3 pos, out Vector3 normal, out float radius);

            Assert.AreEqual(new Vector3(1, 2, 3), pos);
            Assert.AreEqual(Vector3.up, normal);
            Assert.AreEqual(0.05f, radius, 0.001f);
        }

        [Test]
        public void EvaluateWithAttributes_EmptyList_ReturnsDefaults()
        {
            var waypoints = new List<PathWaypoint>();

            CatmullRomSpline.EvaluateWithAttributes(waypoints, 0.5f,
                out Vector3 pos, out Vector3 normal, out float radius);

            Assert.AreEqual(Vector3.zero, pos);
            Assert.AreEqual(Vector3.up, normal);
        }

        [Test]
        public void EvaluateWithAttributes_InterpolatesRadius()
        {
            var waypoints = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = Vector3.zero, localNormal = Vector3.up, radius = 0.02f },
                new PathWaypoint { localPosition = Vector3.right, localNormal = Vector3.up, radius = 0.08f }
            };

            CatmullRomSpline.EvaluateWithAttributes(waypoints, 0.5f,
                out _, out _, out float radius);

            Assert.AreEqual(0.05f, radius, 0.001f);
        }

        [Test]
        public void ArcLength_StraightLine_ReturnsDistance()
        {
            var points = new List<Vector3>
            {
                Vector3.zero,
                new Vector3(3, 0, 4) // length 5
            };

            float length = CatmullRomSpline.ArcLength(points);
            Assert.AreEqual(5f, length, 0.1f);
        }

        [Test]
        public void ArcLength_SinglePoint_ReturnsZero()
        {
            var points = new List<Vector3> { Vector3.one };
            Assert.AreEqual(0f, CatmullRomSpline.ArcLength(points));
        }

        [Test]
        public void BuildTube_CreatesCorrectPointCount()
        {
            var waypoints = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = Vector3.zero, localNormal = Vector3.up, radius = 0.03f },
                new PathWaypoint { localPosition = Vector3.right, localNormal = Vector3.up, radius = 0.03f }
            };

            var tube = CatmullRomSpline.BuildTube(waypoints, 10);

            Assert.AreEqual(11, tube.count); // segments + 1
            Assert.AreEqual(11, tube.points.Length);
            Assert.AreEqual(11, tube.radii.Length);
        }

        [Test]
        public void BuildTube_SingleWaypoint_CountIsOne()
        {
            var waypoints = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = Vector3.one, localNormal = Vector3.up, radius = 0.05f }
            };

            var tube = CatmullRomSpline.BuildTube(waypoints, 10);
            Assert.AreEqual(1, tube.count);
        }

        [Test]
        public void SplineTube_DistanceToTube_PointOnLine_ReturnsZero()
        {
            var waypoints = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = Vector3.zero, localNormal = Vector3.up, radius = 0.1f },
                new PathWaypoint { localPosition = Vector3.right, localNormal = Vector3.up, radius = 0.1f }
            };

            var tube = CatmullRomSpline.BuildTube(waypoints, 20);

            float dist = tube.DistanceToTube(new Vector3(0.5f, 0, 0), out float radius);
            Assert.AreEqual(0f, dist, 0.01f);
            Assert.AreEqual(0.1f, radius, 0.01f);
        }

        [Test]
        public void SplineTube_DistanceToTube_ReturnsPathT()
        {
            var waypoints = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = Vector3.zero, localNormal = Vector3.up, radius = 0.1f },
                new PathWaypoint { localPosition = new Vector3(10, 0, 0), localNormal = Vector3.up, radius = 0.1f }
            };

            var tube = CatmullRomSpline.BuildTube(waypoints, 20);

            tube.DistanceToTube(new Vector3(5, 0, 0), out _, out float pathT);
            Assert.AreEqual(0.5f, pathT, 0.05f);

            tube.DistanceToTube(new Vector3(0, 0, 0), out _, out float pathT0);
            Assert.AreEqual(0f, pathT0, 0.05f);
        }

        [Test]
        public void SplineTube_DistanceToTube_PerpendicularPoint()
        {
            var waypoints = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = Vector3.zero, localNormal = Vector3.up, radius = 0.1f },
                new PathWaypoint { localPosition = new Vector3(1, 0, 0), localNormal = Vector3.up, radius = 0.1f }
            };

            var tube = CatmullRomSpline.BuildTube(waypoints, 20);

            float dist = tube.DistanceToTube(new Vector3(0.5f, 0.05f, 0), out float radius);
            Assert.AreEqual(0.05f, dist, 0.01f);
        }
    }
}
