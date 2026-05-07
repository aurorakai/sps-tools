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
                    mesh, path, root.transform, root.transform, passes: 1);

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
                    mesh, path, root.transform, root.transform, passes: 1);

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
                    mesh, path, root.transform, root.transform, passes: 2);

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
                    mesh, path, root.transform, root.transform, passes: 1);

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
                    mesh, path, root.transform, root.transform, passes: 1);

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

        [Test]
        public void SubdivideInRegion_RespectsBakedSkinningWhenWorldRefsProvided()
        {
            // Build a strip skinned to a single bone. Pose the bone away from
            // bind. The path overlaps the POSED location; subdivision must
            // happen there, not at the bind-pose location.
            var avatar = new GameObject("Avatar");
            var bone = new GameObject("Bone");
            bone.transform.SetParent(avatar.transform, false);
            bone.transform.localPosition = new Vector3(2f, 0f, 0f);

            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(0f, -0.05f, 0f), new Vector3(0f, 0.05f, 0f),
                new Vector3(1f, -0.05f, 0f), new Vector3(1f, 0.05f, 0f),
            };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2 };
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.RecalculateBounds();

            var rendererGo = new GameObject("Body");
            rendererGo.transform.SetParent(avatar.transform, false);
            var smr = rendererGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = new[] { bone.transform };
            smr.rootBone = bone.transform;

            // Path overlaps the posed location (x ≈ 2..3 in world space).
            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(2f, 0f, 0f), localNormal = Vector3.back, radius = 0.5f },
                new PathWaypoint { localPosition = new Vector3(3f, 0f, 0f), localNormal = Vector3.back, radius = 0.5f },
            };

            try
            {
                var worldRefs = BlendshapeGenerator.GetSkinnedWorldRefVerts(smr, mesh.vertices);

                var subdivided = MeshSubdivider.SubdivideInRegion(
                    mesh, path, smr.transform, avatar.transform,
                    worldRefVerts: worldRefs,
                    passes: 1);

                Assert.Greater(subdivided.vertexCount, mesh.vertexCount,
                    "Subdivision must happen at the posed location, not bind-pose.");
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void Subdivide_MidpointNormalIsNotZero_WhenEndpointNormalsAreOpposite()
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0.5f, 1f, 0f),
            };
            // Endpoints 0 and 1 have near-opposite normals (a sharp fold).
            mesh.normals = new[]
            {
                new Vector3(0f, 1f, 0f), new Vector3(0f, -1f, 0f), Vector3.up,
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateBounds();

            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(0.5f, 0f, 0f), localNormal = Vector3.up, radius = 1f },
                new PathWaypoint { localPosition = new Vector3(0.5f, 0.1f, 0f), localNormal = Vector3.up, radius = 1f },
            };

            var avatar = new GameObject("Avatar");
            var go = new GameObject("Mesh");
            go.transform.SetParent(avatar.transform, false);

            try
            {
                var result = MeshSubdivider.SubdivideInRegion(
                    mesh, path, go.transform, avatar.transform, passes: 1);

                Assert.Greater(result.vertexCount, mesh.vertexCount,
                    "Sanity: subdivision must add midpoints.");

                var resultNormals = result.normals;
                for (int i = mesh.vertexCount; i < result.vertexCount; i++)
                {
                    Assert.Greater(resultNormals[i].sqrMagnitude, 0.001f,
                        $"Midpoint normal at vert {i} is near-zero — averaged opposites then normalized.");
                }
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
