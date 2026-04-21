using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class MeshSubdividerTests
    {
        private Mesh CreateSimpleTriMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(0.5f, 1, 0)
            };
            mesh.normals = new Vector3[]
            {
                Vector3.back, Vector3.back, Vector3.back
            };
            mesh.triangles = new int[] { 0, 1, 2 };
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 1)
            };
            return mesh;
        }

        [Test]
        public void SubdivideInRegion_SingleTriangle_Creates4Triangles()
        {
            var mesh = CreateSimpleTriMesh();
            var root = new GameObject("Root");

            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(0.5f, 0.5f, 0), localNormal = Vector3.back, radius = 5f },
                new PathWaypoint { localPosition = new Vector3(0.5f, 1.5f, 0), localNormal = Vector3.back, radius = 5f }
            };

            try
            {
                var result = MeshSubdivider.SubdivideInRegion(
                    mesh, path, root.transform, root.transform, 1);

                // 1 triangle → 4 triangles = 12 indices
                Assert.AreEqual(12, result.GetTriangles(0).Length);
                // 3 original + 3 midpoints = 6 vertices
                Assert.AreEqual(6, result.vertexCount);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SubdivideInRegion_OutOfRange_NoChange()
        {
            var mesh = CreateSimpleTriMesh();
            var root = new GameObject("Root");

            // Path far away
            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(100, 100, 0), localNormal = Vector3.back, radius = 0.01f },
                new PathWaypoint { localPosition = new Vector3(100, 101, 0), localNormal = Vector3.back, radius = 0.01f }
            };

            try
            {
                var result = MeshSubdivider.SubdivideInRegion(
                    mesh, path, root.transform, root.transform, 1);

                // No vertices affected — mesh unchanged
                Assert.AreEqual(3, result.GetTriangles(0).Length);
                Assert.AreEqual(3, result.vertexCount);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SubdivideInRegion_TwoPasses_Creates16Triangles()
        {
            var mesh = CreateSimpleTriMesh();
            var root = new GameObject("Root");

            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(0.5f, 0.5f, 0), localNormal = Vector3.back, radius = 5f },
                new PathWaypoint { localPosition = new Vector3(0.5f, 1.5f, 0), localNormal = Vector3.back, radius = 5f }
            };

            try
            {
                var result = MeshSubdivider.SubdivideInRegion(
                    mesh, path, root.transform, root.transform, 2);

                // 1 → 4 → 16 triangles
                Assert.AreEqual(48, result.GetTriangles(0).Length);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SubdivideInRegion_PreservesNormals()
        {
            var mesh = CreateSimpleTriMesh();
            var root = new GameObject("Root");

            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(0.5f, 0.5f, 0), localNormal = Vector3.back, radius = 5f },
                new PathWaypoint { localPosition = new Vector3(0.5f, 1.5f, 0), localNormal = Vector3.back, radius = 5f }
            };

            try
            {
                var result = MeshSubdivider.SubdivideInRegion(
                    mesh, path, root.transform, root.transform, 1);

                Assert.IsNotNull(result.normals);
                Assert.AreEqual(result.vertexCount, result.normals.Length);

                // All normals should be roughly Vector3.back (original normals were all back)
                foreach (var n in result.normals)
                {
                    Assert.AreEqual(-1f, n.z, 0.1f, "Normal Z should be approximately -1");
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SubdivideInRegion_PreservesUVs()
        {
            var mesh = CreateSimpleTriMesh();
            var root = new GameObject("Root");

            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(0.5f, 0.5f, 0), localNormal = Vector3.back, radius = 5f },
                new PathWaypoint { localPosition = new Vector3(0.5f, 1.5f, 0), localNormal = Vector3.back, radius = 5f }
            };

            try
            {
                var result = MeshSubdivider.SubdivideInRegion(
                    mesh, path, root.transform, root.transform, 1);

                Assert.IsNotNull(result.uv);
                Assert.AreEqual(result.vertexCount, result.uv.Length);

                // Midpoint UVs should be averages of edge endpoints
                // Original UVs: (0,0), (1,0), (0.5,1)
                // All midpoint UVs should be within the original UV triangle
                foreach (var uv in result.uv)
                {
                    Assert.IsTrue(uv.x >= -0.01f && uv.x <= 1.01f, $"UV x={uv.x} out of range");
                    Assert.IsTrue(uv.y >= -0.01f && uv.y <= 1.01f, $"UV y={uv.y} out of range");
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
