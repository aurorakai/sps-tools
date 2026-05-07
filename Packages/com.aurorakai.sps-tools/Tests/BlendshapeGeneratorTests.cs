using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class BlendshapeGeneratorTests
    {
        private Mesh CreateTestQuadMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
                new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
                new Vector3(0, 0, 0) // center vertex
            };
            mesh.normals = new Vector3[]
            {
                Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back
            };
            mesh.triangles = new int[]
            {
                0, 1, 4,
                1, 2, 4,
                2, 3, 4,
                3, 0, 4
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(0, 1),
                new Vector2(0.5f, 0.5f)
            };
            return mesh;
        }

        private Mesh CreateCenterStripMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0.5f, -0.05f, 0f), new Vector3(0.5f, 0.05f, 0f),
                new Vector3(1.0f, -0.05f, 0f), new Vector3(1.0f, 0.05f, 0f),
                new Vector3(1.5f, -0.05f, 0f), new Vector3(1.5f, 0.05f, 0f)
            };
            mesh.normals = new Vector3[]
            {
                Vector3.back, Vector3.back, Vector3.back,
                Vector3.back, Vector3.back, Vector3.back
            };
            mesh.triangles = new int[]
            {
                0, 1, 2,
                1, 3, 2,
                2, 3, 4,
                3, 5, 4
            };
            return mesh;
        }

        private Mesh CreateTinyRadiusMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0.015f, 0f, 0f),
                new Vector3(0.015f, 0.1f, 0f),
                new Vector3(0.115f, 0f, 0f)
            };
            mesh.normals = new Vector3[]
            {
                Vector3.back, Vector3.back, Vector3.back
            };
            mesh.triangles = new int[]
            {
                0, 1, 2
            };
            return mesh;
        }

        private GameObject CreateTestSetup(out SkinnedMeshRenderer renderer)
        {
            var root = new GameObject("TestAvatar");
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);

            renderer = bodyGo.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = CreateTestQuadMesh();

            return root;
        }

        private GameObject CreateTestSetup(
            Mesh mesh, out SkinnedMeshRenderer renderer)
        {
            var root = new GameObject("TestAvatar");
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);

            renderer = bodyGo.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;

            return root;
        }

        private List<PathWaypoint> CreateTestPath()
        {
            return new List<PathWaypoint>
            {
                new PathWaypoint
                {
                    localPosition = new Vector3(0, 0, 0),
                    localNormal = Vector3.back,
                    radius = 2f // large radius to cover the test quad
                },
                new PathWaypoint
                {
                    localPosition = new Vector3(0, 2, 0),
                    localNormal = Vector3.back,
                    radius = 2f
                }
            };
        }

        [Test]
        public void GenerateBulgeBlendshapes_CreatesPositionShapes()
        {
            var root = CreateTestSetup(out var renderer);
            var path = CreateTestPath();

            try
            {
                var result = BlendshapeGenerator.GenerateBulgeBlendshapes(
                    renderer, root.transform, path, 3, 0.01f,
                    "Assets/SPSTools/Test/Bulge/Default",
                    smoothingPasses: 0);

                Assert.IsNotNull(result);
                Assert.IsNotNull(result.modifiedMesh);
                Assert.IsTrue(result.blendshapeNames.Count > 0);
                Assert.IsTrue(result.blendshapeNames[0].StartsWith("SPSBulge_Pos"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GenerateBulgeBlendshapes_SkipsEmptyPositions()
        {
            var root = CreateTestSetup(out var renderer);

            // Path far away from the mesh — no vertices should be affected
            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(100, 100, 100), localNormal = Vector3.up, radius = 0.01f },
                new PathWaypoint { localPosition = new Vector3(100, 102, 100), localNormal = Vector3.up, radius = 0.01f }
            };

            try
            {
                var result = BlendshapeGenerator.GenerateBulgeBlendshapes(
                    renderer, root.transform, path, 3, 0.01f,
                    "Assets/SPSTools/Test/Bulge/Default",
                    smoothingPasses: 0);

                Assert.IsNotNull(result);
                // No vertices in range — all positions should be skipped
                Assert.AreEqual(0, result.blendshapeNames.Count);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ComputeAverageEdgeLengthInRegion_FollowsEntireSplineSpan()
        {
            var root = CreateTestSetup(CreateCenterStripMesh(), out var renderer);
            var path = new List<PathWaypoint>
            {
                new PathWaypoint
                {
                    localPosition = new Vector3(0f, 0f, 0f),
                    localNormal = Vector3.back,
                    radius = 0.2f
                },
                new PathWaypoint
                {
                    localPosition = new Vector3(2f, 0f, 0f),
                    localNormal = Vector3.back,
                    radius = 0.2f
                }
            };

            try
            {
                float avgEdge = BlendshapeGenerator.ComputeAverageEdgeLengthInRegion(
                    renderer, path, root.transform);

                Assert.Greater(avgEdge, 0.1f,
                    "Preview should include mesh triangles along the spline span, not just at waypoint centers.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ComputeAverageEdgeLengthInRegion_DoesNotInflateTinyRadii()
        {
            var root = CreateTestSetup(CreateTinyRadiusMesh(), out var renderer);
            var path = new List<PathWaypoint>
            {
                new PathWaypoint
                {
                    localPosition = Vector3.zero,
                    localNormal = Vector3.back,
                    radius = 0.005f
                }
            };

            try
            {
                float avgEdge = BlendshapeGenerator.ComputeAverageEdgeLengthInRegion(
                    renderer, path, root.transform);

                Assert.AreEqual(0f, avgEdge, 0.0001f,
                    "Preview should respect authored tiny radii instead of expanding them.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void GenerateBulgeBlendshapes_WithSubdivisionAndPosedRenderer_DeformsAtPosedLocation()
        {
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

            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = new Vector3(2f, 0f, 0f), localNormal = Vector3.back, radius = 0.5f },
                new PathWaypoint { localPosition = new Vector3(3f, 0f, 0f), localNormal = Vector3.back, radius = 0.5f },
            };

            string folder = "Assets/SPSTools/Test/SubdivisionPosedRegression";
            try
            {
                var result = BlendshapeGenerator.GenerateBulgeBlendshapes(
                    smr, avatar.transform, path,
                    positionCount: 1, displacement: 0.05f,
                    outputFolder: folder,
                    smoothingPasses: 0,
                    subdivide: true, subdivisionPasses: 1,
                    recalculateNormals: false);

                Assert.Greater(result.modifiedMesh.vertexCount, 4,
                    "Subdivision must happen at posed location.");

                int idx = result.modifiedMesh.GetBlendShapeIndex(result.blendshapeNames[0]);
                var dv = new Vector3[result.modifiedMesh.vertexCount];
                result.modifiedMesh.GetBlendShapeFrameVertices(idx, 0, dv, null, null);
                bool anyNonzero = false;
                foreach (var d in dv)
                    if (d.sqrMagnitude > 1e-10f) { anyNonzero = true; break; }
                Assert.IsTrue(anyNonzero,
                    "Generator must produce deltas; if BakeMesh fell into bind-pose fallback the path missed all verts.");
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folder);
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
