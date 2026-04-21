using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class KDTreeNearestTests
    {
        [Test]
        public void FindNearest_SinglePoint_ReturnsThatPoint()
        {
            var points = new[] { new Vector3(1f, 2f, 3f) };
            var tree = new KDTreeNearest(points);

            Assert.AreEqual(0, tree.FindNearest(Vector3.zero));
            Assert.AreEqual(0, tree.FindNearest(new Vector3(100, 100, 100)));
        }

        [Test]
        public void FindNearest_EmptyTree_ReturnsMinusOne()
        {
            var tree = new KDTreeNearest(new Vector3[0]);
            Assert.AreEqual(-1, tree.FindNearest(Vector3.zero));
        }

        [Test]
        public void FindNearest_QueryAtPoint_ReturnsThatPoint()
        {
            var points = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(5f, 0f, 0f),
                new Vector3(0f, 5f, 0f),
                new Vector3(0f, 0f, 5f),
            };
            var tree = new KDTreeNearest(points);

            Assert.AreEqual(0, tree.FindNearest(new Vector3(0f, 0f, 0f)));
            Assert.AreEqual(1, tree.FindNearest(new Vector3(5f, 0f, 0f)));
            Assert.AreEqual(2, tree.FindNearest(new Vector3(0f, 5f, 0f)));
            Assert.AreEqual(3, tree.FindNearest(new Vector3(0f, 0f, 5f)));
        }

        [Test]
        public void FindNearest_QueryBetweenPoints_ReturnsCloser()
        {
            var points = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(10f, 0f, 0f),
            };
            var tree = new KDTreeNearest(points);

            Assert.AreEqual(0, tree.FindNearest(new Vector3(4f, 0f, 0f)));
            Assert.AreEqual(1, tree.FindNearest(new Vector3(6f, 0f, 0f)));
        }

        [Test]
        public void FindNearest_MatchesBruteForce_RandomCloud()
        {
            const int count = 500;
            var rng = new System.Random(42);
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                points[i] = new Vector3(
                    (float)(rng.NextDouble() * 10.0 - 5.0),
                    (float)(rng.NextDouble() * 10.0 - 5.0),
                    (float)(rng.NextDouble() * 10.0 - 5.0));
            }

            var tree = new KDTreeNearest(points);

            const int queries = 100;
            for (int q = 0; q < queries; q++)
            {
                var query = new Vector3(
                    (float)(rng.NextDouble() * 12.0 - 6.0),
                    (float)(rng.NextDouble() * 12.0 - 6.0),
                    (float)(rng.NextDouble() * 12.0 - 6.0));

                int treeIdx = tree.FindNearest(query);

                int bruteIdx = 0;
                float bruteSqrDist = float.PositiveInfinity;
                for (int i = 0; i < count; i++)
                {
                    float d = (points[i] - query).sqrMagnitude;
                    if (d < bruteSqrDist) { bruteSqrDist = d; bruteIdx = i; }
                }

                // KD-tree and brute-force should agree; in the rare case of
                // exact ties, compare by distance rather than index.
                float treeSqrDist = (points[treeIdx] - query).sqrMagnitude;
                Assert.AreEqual(bruteSqrDist, treeSqrDist, 1e-5f,
                    $"Query {q}: tree found idx {treeIdx} (sqrDist {treeSqrDist}), " +
                    $"brute found {bruteIdx} (sqrDist {bruteSqrDist})");
            }
        }

        [Test]
        public void Constructor_NullPoints_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new KDTreeNearest(null));
        }
    }
}
