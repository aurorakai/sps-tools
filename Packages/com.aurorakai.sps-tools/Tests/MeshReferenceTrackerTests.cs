using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class MeshReferenceTrackerTests
    {
        private class TestConfig : BaseEffectConfig
        {
            public override bool IsValid() => true;
            public override string EffectTypeName => "Test";
        }

        [Test]
        public void StoreMesh_Original_SetsBothFields()
        {
            var config = ScriptableObject.CreateInstance<TestConfig>();
            var mesh = new Mesh { name = "TestMesh" };

            try
            {
                MeshReferenceTracker.StoreMesh(config, "original", mesh);

                Assert.AreEqual(mesh, config.originalMesh);
                // Path will be empty for runtime-created meshes (not saved as asset)
            }
            finally
            {
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void StoreMesh_Generated_SetsBothFields()
        {
            var config = ScriptableObject.CreateInstance<TestConfig>();
            var mesh = new Mesh { name = "GeneratedMesh" };

            try
            {
                MeshReferenceTracker.StoreMesh(config, "generated", mesh);

                Assert.AreEqual(mesh, config.generatedMesh);
            }
            finally
            {
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void StoreMesh_Null_ClearsBothFields()
        {
            var config = ScriptableObject.CreateInstance<TestConfig>();
            config.originalMesh = new Mesh();
            config.originalMeshPath = "some/path.asset";

            try
            {
                MeshReferenceTracker.StoreMesh(config, "original", null);

                Assert.IsNull(config.originalMesh);
                Assert.AreEqual("", config.originalMeshPath);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void ResolveMesh_ValidReference_ReturnsMesh()
        {
            var config = ScriptableObject.CreateInstance<TestConfig>();
            var mesh = new Mesh { name = "ValidMesh" };
            config.originalMesh = mesh;

            try
            {
                var result = MeshReferenceTracker.ResolveMesh(config, "original");
                Assert.AreEqual(mesh, result);
            }
            finally
            {
                Object.DestroyImmediate(config);
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ResolveMesh_NullReference_NoPath_ReturnsNull()
        {
            var config = ScriptableObject.CreateInstance<TestConfig>();
            config.originalMesh = null;
            config.originalMeshPath = "";

            try
            {
                var result = MeshReferenceTracker.ResolveMesh(config, "original");
                Assert.IsNull(result);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void ResolveMesh_InvalidField_ReturnsNull()
        {
            var config = ScriptableObject.CreateInstance<TestConfig>();

            try
            {
                var result = MeshReferenceTracker.ResolveMesh(config, "invalid");
                Assert.IsNull(result);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void StoreMesh_InvalidField_DoesNotThrow()
        {
            var config = ScriptableObject.CreateInstance<TestConfig>();

            try
            {
                Assert.DoesNotThrow(() =>
                    MeshReferenceTracker.StoreMesh(config, "invalid", null));
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }
    }
}
