using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class PrimarySurfaceSamplerTests
    {
        private static readonly Vector3 A = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 B = new Vector3(1f, 0f, 0f);
        private static readonly Vector3 C = new Vector3(0f, 1f, 0f);

        [Test]
        public void ClosestPoint_OnCorner_GivesThatCornerFullWeight()
        {
            // Overlays that copy the primary exactly (NMS-style part swaps) rely on
            // a vert on a primary vert getting exactly that vert's values.
            PrimarySurfaceSampler.ClosestPointOnTriangle(C, A, B, C, out var weights);
            Assert.AreEqual(new Vector3(0f, 0f, 1f), weights);
            PrimarySurfaceSampler.ClosestPointOnTriangle(B, A, B, C, out weights);
            Assert.AreEqual(new Vector3(0f, 1f, 0f), weights);
        }

        [Test]
        public void ClosestPoint_AboveInterior_ProjectsOntoTriangle()
        {
            var p = new Vector3(0.25f, 0.25f, 0.3f);
            var closest = PrimarySurfaceSampler.ClosestPointOnTriangle(p, A, B, C, out var weights);
            Assert.AreEqual(0.25f, closest.x, 1e-6f);
            Assert.AreEqual(0.25f, closest.y, 1e-6f);
            Assert.AreEqual(0f, closest.z, 1e-6f);
            Assert.AreEqual(1f, weights.x + weights.y + weights.z, 1e-6f);
            var rebuilt = A * weights.x + B * weights.y + C * weights.z;
            Assert.Less((rebuilt - closest).magnitude, 1e-6f);
        }

        [Test]
        public void ClosestPoint_OutsideEdge_ClampsToEdge()
        {
            var p = new Vector3(0.5f, -0.5f, 0f);
            var closest = PrimarySurfaceSampler.ClosestPointOnTriangle(p, A, B, C, out var weights);
            Assert.AreEqual(0.5f, closest.x, 1e-6f);
            Assert.AreEqual(0f, closest.y, 1e-6f);
            Assert.AreEqual(0.5f, weights.x, 1e-6f);
            Assert.AreEqual(0.5f, weights.y, 1e-6f);
            Assert.AreEqual(0f, weights.z, 1e-6f);
        }

        [Test]
        public void TrySample_BlendsAcrossTriangle()
        {
            var sampler = new PrimarySurfaceSampler(
                new[] { A, B, C }, new[] { 0, 1, 2 }, null, maxDistance: 0.1f);
            Assert.IsTrue(sampler.TrySample(new Vector3(0.5f, 0.25f, 0.05f), out var sample));

            var values = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var blended = sample.Blend(values);
            Assert.AreEqual(0.5f, blended.x, 1e-5f);
            Assert.AreEqual(0.25f, blended.y, 1e-5f);
        }

        [Test]
        public void TrySample_RespectsMaxDistanceAndVertexFilter()
        {
            var verts = new[] { A, B, C };
            var tris = new[] { 0, 1, 2 };

            var sampler = new PrimarySurfaceSampler(verts, tris, null, maxDistance: 0.1f);
            Assert.IsFalse(sampler.TrySample(new Vector3(0.25f, 0.25f, 0.2f), out _),
                "Points further than the maximum distance must not match.");

            var filtered = new PrimarySurfaceSampler(
                verts, tris, new[] { false, false, false }, maxDistance: 0.1f);
            Assert.IsFalse(filtered.TrySample(new Vector3(0.25f, 0.25f, 0f), out _),
                "Triangles with no included vertex must not be searched.");
        }
    }
}
