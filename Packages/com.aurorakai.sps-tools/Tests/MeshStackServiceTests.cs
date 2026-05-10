using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class MeshStackServiceTests
    {
        private const string TestRootFolder = "Assets/SPSTools/MeshStackServiceTests";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(TestRootFolder);
            SpsAnimationUtility.EnsureFolder(TestRootFolder);
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TestRootFolder);
        }

        [Test]
        public void GenerateOrUpdateBulge_StacksPrimaryAndAdditionalMeshesPerRenderer()
        {
            var avatar = new GameObject("MeshStackServiceTests");
            try
            {
                var body = CreateRenderer(avatar, "Body");
                var shirt = CreateRenderer(avatar, "Shirt");
                var jacket = CreateRenderer(avatar, "Jacket");

                var configA = CreateConfig(avatar, "A", body, new[] { shirt });
                var pathA = SaveConfigAsset(configA);
                var resultA = MeshStackService.GenerateOrUpdateBulge(configA, pathA);
                configA.positionBlendshapes = resultA.primaryBlendshapeNames;

                var configB = CreateConfig(avatar, "B", body, new SkinnedMeshRenderer[0]);
                var pathB = SaveConfigAsset(configB);
                var resultB = MeshStackService.GenerateOrUpdateBulge(configB, pathB);
                configB.positionBlendshapes = resultB.primaryBlendshapeNames;

                var configC = CreateConfig(avatar, "C", shirt, new[] { jacket });
                var pathC = SaveConfigAsset(configC);
                var resultC = MeshStackService.GenerateOrUpdateBulge(configC, pathC);
                configC.positionBlendshapes = resultC.primaryBlendshapeNames;

                string aName = FirstName(configA);
                string bName = FirstName(configB);
                string cName = FirstName(configC);

                AssertHasBlendshape(body.sharedMesh, aName, "Body should keep A.");
                AssertHasBlendshape(body.sharedMesh, bName, "Body should receive B.");
                AssertNoBlendshape(body.sharedMesh, cName, "Body should not receive C.");

                AssertHasBlendshape(shirt.sharedMesh, aName, "Shirt should receive A as an additional mesh.");
                AssertNoBlendshape(shirt.sharedMesh, bName, "Shirt should not receive B.");
                AssertHasBlendshape(shirt.sharedMesh, cName, "Shirt should receive C as primary.");

                AssertNoBlendshape(jacket.sharedMesh, aName, "Jacket should not receive A.");
                AssertNoBlendshape(jacket.sharedMesh, bName, "Jacket should not receive B.");
                AssertHasBlendshape(jacket.sharedMesh, cName, "Jacket should receive C as an additional mesh.");
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void RegisterLegacyBulgeGeneration_KeepsExistingBlendshapeNames()
        {
            var avatar = new GameObject("MeshStackServiceTests");
            try
            {
                var body = CreateRenderer(avatar, "Body");
                var config = CreateConfig(avatar, "Legacy", body, new SkinnedMeshRenderer[0]);
                config.positionBlendshapes = new List<string> { "SPSBulge_Pos1" };

                var generated = Object.Instantiate(body.sharedMesh);
                generated.name = "LegacyGenerated";
                var deltas = new Vector3[generated.vertexCount];
                deltas[0] = Vector3.back * 0.01f;
                generated.AddBlendShapeFrame("SPSBulge_Pos1", 100f, deltas, null, null);

                string meshFolder = $"{TestRootFolder}/Legacy";
                SpsAnimationUtility.EnsureFolder(meshFolder);
                string generatedPath = $"{meshFolder}/LegacyGenerated.asset";
                AssetDatabase.CreateAsset(generated, generatedPath);

                MeshReferenceTracker.StoreMesh(config, "original", body.sharedMesh);
                MeshReferenceTracker.StoreMesh(config, "generated", generated);
                body.sharedMesh = generated;

                string path = SaveConfigAsset(config);
                MeshStackService.RegisterLegacyBulgeGeneration(config, path);

                AssertHasBlendshape(body.sharedMesh, "SPSBulge_Pos1",
                    "Legacy migration should preserve existing generated blendshape names.");
                Assert.IsTrue(MeshStackService.HasBulgeLayer(config));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void GenerateOrUpdateBulge_MigratesAssignedLegacyMeshBeforeStacking()
        {
            var avatar = new GameObject("MeshStackServiceTests");
            try
            {
                var body = CreateRenderer(avatar, "Body");

                var legacy = CreateConfig(avatar, "Legacy", body, new SkinnedMeshRenderer[0]);
                legacy.positionBlendshapes = new List<string> { "SPSBulge_Pos1" };
                var legacyGenerated = Object.Instantiate(body.sharedMesh);
                legacyGenerated.name = "LegacyGenerated";
                var legacyDeltas = new Vector3[legacyGenerated.vertexCount];
                legacyDeltas[0] = Vector3.back * 0.01f;
                legacyGenerated.AddBlendShapeFrame("SPSBulge_Pos1", 100f, legacyDeltas, null, null);
                string legacyFolder = $"{TestRootFolder}/LegacyAssigned";
                SpsAnimationUtility.EnsureFolder(legacyFolder);
                AssetDatabase.CreateAsset(legacyGenerated, $"{legacyFolder}/LegacyGenerated.asset");
                MeshReferenceTracker.StoreMesh(legacy, "original", body.sharedMesh);
                MeshReferenceTracker.StoreMesh(legacy, "generated", legacyGenerated);
                body.sharedMesh = legacyGenerated;
                SaveConfigAsset(legacy);

                var next = CreateConfig(avatar, "Next", body, new SkinnedMeshRenderer[0]);
                string nextPath = SaveConfigAsset(next);
                var result = MeshStackService.GenerateOrUpdateBulge(next, nextPath);
                next.positionBlendshapes = result.primaryBlendshapeNames;

                AssertHasBlendshape(body.sharedMesh, "SPSBulge_Pos1",
                    "New stack generation should preserve the assigned legacy mesh layer.");
                AssertHasBlendshape(body.sharedMesh, FirstName(next),
                    "New config should stack on top of the migrated legacy layer.");
                Assert.IsTrue(MeshStackService.HasBulgeLayer(legacy));
                Assert.IsTrue(MeshStackService.HasBulgeLayer(next));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        private static BulgeConfig CreateConfig(
            GameObject avatar,
            string name,
            SkinnedMeshRenderer primary,
            IReadOnlyList<SkinnedMeshRenderer> additional)
        {
            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            config.configurationName = name;
            config.EnsureStableConfigId();
            config.avatarRoot = avatar;
            config.avatarRootName = avatar.name;
            config.rendererPath = BaseEffectConfig.GetRelativePath(
                avatar.transform, primary.transform);
            config.autoPositionCount = 2;
            config.blendshapeDisplacement = 0.01f;
            config.smoothingPasses = 0;
            config.recalculateNormals = false;
            config.pathWaypoints = new List<PathWaypoint>
            {
                new PathWaypoint
                {
                    localPosition = new Vector3(0f, -0.5f, 0f),
                    localNormal = Vector3.back,
                    radius = 2f
                },
                new PathWaypoint
                {
                    localPosition = new Vector3(0f, 0.5f, 0f),
                    localNormal = Vector3.back,
                    radius = 2f
                }
            };

            foreach (var renderer in additional)
            {
                config.additionalMeshes.Add(new TrackedMesh
                {
                    rendererPath = BaseEffectConfig.GetRelativePath(
                        avatar.transform, renderer.transform)
                });
            }

            return config;
        }

        private static string SaveConfigAsset(BulgeConfig config)
        {
            string folder = $"{TestRootFolder}/Configs/{config.configurationName}";
            SpsAnimationUtility.EnsureFolder(folder);
            string path = $"{folder}/SPSBulge_{config.configurationName}.asset";
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();
            return path;
        }

        private static SkinnedMeshRenderer CreateRenderer(GameObject avatar, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(avatar.transform, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();

            var mesh = CreateMesh();
            string meshFolder = $"{TestRootFolder}/Meshes";
            SpsAnimationUtility.EnsureFolder(meshFolder);
            string meshPath = $"{meshFolder}/{name}_Base.asset";
            AssetDatabase.CreateAsset(mesh, meshPath);
            renderer.sharedMesh = mesh;
            return renderer;
        }

        private static Mesh CreateMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-1, -1, 0), new Vector3(1, -1, 0),
                new Vector3(1, 1, 0), new Vector3(-1, 1, 0),
                new Vector3(0, 0, 0)
            };
            mesh.normals = new[]
            {
                Vector3.back, Vector3.back, Vector3.back,
                Vector3.back, Vector3.back
            };
            mesh.triangles = new[]
            {
                0, 1, 4,
                1, 2, 4,
                2, 3, 4,
                3, 0, 4
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static string FirstName(BulgeConfig config)
            => string.Format(MeshStackService.BuildScopedBulgeNamingPattern(config), 1);

        private static void AssertHasBlendshape(Mesh mesh, string name, string message)
        {
            Assert.IsNotNull(mesh);
            Assert.GreaterOrEqual(mesh.GetBlendShapeIndex(name), 0, message);
        }

        private static void AssertNoBlendshape(Mesh mesh, string name, string message)
        {
            Assert.IsNotNull(mesh);
            Assert.Less(mesh.GetBlendShapeIndex(name), 0, message);
        }
    }
}
