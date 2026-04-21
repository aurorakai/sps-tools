using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class PreviewAndOverlayRegressionTests
    {
        private static Mesh CreateStripMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0f, -0.05f, 0f), new Vector3(0f, 0.05f, 0f),
                new Vector3(0.5f, -0.05f, 0f), new Vector3(0.5f, 0.05f, 0f),
                new Vector3(1f, -0.05f, 0f), new Vector3(1f, 0.05f, 0f),
            };
            mesh.normals = new Vector3[]
            {
                Vector3.back, Vector3.back, Vector3.back,
                Vector3.back, Vector3.back, Vector3.back,
            };
            mesh.triangles = new int[]
            {
                0, 1, 2,
                1, 3, 2,
                2, 3, 4,
                3, 5, 4,
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateOffsetTriangleMesh(float yOffset)
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(0.45f, yOffset - 0.02f, 0f),
                new Vector3(0.55f, yOffset, 0f),
                new Vector3(0.45f, yOffset + 0.02f, 0f),
            };
            mesh.normals = new Vector3[]
            {
                Vector3.back, Vector3.back, Vector3.back,
            };
            mesh.triangles = new int[] { 0, 1, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateBlendshapeMesh(params string[] blendshapeNames)
        {
            var mesh = CreateOffsetTriangleMesh(0f);
            var deltaVertices = new Vector3[mesh.vertexCount];
            var deltaNormals = new Vector3[mesh.vertexCount];
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                deltaVertices[i] = new Vector3(0f, 0f, 0.01f);
                deltaNormals[i] = Vector3.back;
            }

            foreach (var name in blendshapeNames)
                mesh.AddBlendShapeFrame(name, 100f, deltaVertices, deltaNormals, null);

            return mesh;
        }

        private static GameObject CreateRenderer(
            Transform parent, string name, Mesh mesh, out SkinnedMeshRenderer renderer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return go;
        }

        [Test]
        public void FindCandidateOverlays_DoesNotSuggestMeshesOnlyInsideInflatedThreshold()
        {
            var root = new GameObject("Avatar");
            var primaryMesh = CreateStripMesh();
            var overlayMesh = CreateOffsetTriangleMesh(0.29f);
            CreateRenderer(root.transform, "Primary", primaryMesh, out var primaryRenderer);
            CreateRenderer(root.transform, "Overlay", overlayMesh, out _);

            var path = new List<PathWaypoint>
            {
                new PathWaypoint
                {
                    localPosition = new Vector3(0f, 0f, 0f),
                    localNormal = Vector3.back,
                    radius = 0.25f
                },
                new PathWaypoint
                {
                    localPosition = new Vector3(1f, 0f, 0f),
                    localNormal = Vector3.back,
                    radius = 0.25f
                }
            };

            try
            {
                var candidates = OverlayMeshDetector.FindCandidateOverlays(
                    root, path, primaryRenderer);

                Assert.AreEqual(0, candidates.Count,
                    "Overlay scan should not suggest meshes that only pass the old inflated max-radius threshold.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(primaryMesh);
                Object.DestroyImmediate(overlayMesh);
            }
        }

        [Test]
        public void Preview_RebindsBlendshapeNameAfterSecondaryMeshSwap()
        {
            var root = new GameObject("Avatar");
            var primaryMesh = CreateStripMesh();
            var overlayMeshA = CreateBlendshapeMesh("Foo");
            var overlayMeshB = CreateBlendshapeMesh("Bar", "Foo");
            var clip = new AnimationClip();
            CreateRenderer(root.transform, "Primary", primaryMesh, out _);
            CreateRenderer(root.transform, "Overlay", overlayMeshA, out var overlayRenderer);

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    "Overlay", typeof(SkinnedMeshRenderer), "blendShape.Foo"),
                new AnimationCurve(new Keyframe(0f, 50f)));

            try
            {
                ScenePreviewManager.StartPreview(
                    root, new List<string> { "Primary" }, Vector3.zero);

                var entries = new List<(float threshold, AnimationClip clip)>
                {
                    (0f, clip)
                };

                ScenePreviewManager.SampleAtDepth(0f, entries);
                Assert.AreEqual(50f, overlayRenderer.GetBlendShapeWeight(0), 0.001f);

                overlayRenderer.sharedMesh = overlayMeshB;
                ScenePreviewManager.SampleAtDepth(1f, entries);

                Assert.AreEqual(0f, overlayRenderer.GetBlendShapeWeight(0), 0.001f,
                    "Preview should not keep writing to the stale blendshape slot after a mesh swap.");
                Assert.AreEqual(50f, overlayRenderer.GetBlendShapeWeight(1), 0.001f,
                    "Preview should re-resolve the intended blendshape name on the new mesh.");
            }
            finally
            {
                ScenePreviewManager.StopPreview();
                Object.DestroyImmediate(clip);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(primaryMesh);
                Object.DestroyImmediate(overlayMeshA);
                Object.DestroyImmediate(overlayMeshB);
            }
        }

        [Test]
        public void Preview_FiresStoppedEventWhenTrackedMeshChanges()
        {
            var root = new GameObject("Avatar");
            var primaryMeshA = CreateStripMesh();
            var primaryMeshB = CreateOffsetTriangleMesh(0f);
            CreateRenderer(root.transform, "Primary", primaryMeshA, out var primaryRenderer);

            bool stopped = false;
            void OnStopped() => stopped = true;

            try
            {
                ScenePreviewManager.PreviewStopped += OnStopped;
                ScenePreviewManager.StartPreview(
                    root, new List<string> { "Primary" }, Vector3.zero);

                primaryRenderer.sharedMesh = primaryMeshB;
                ScenePreviewManager.SampleAtDepth(
                    0f, new List<(float threshold, AnimationClip clip)>());

                Assert.IsFalse(ScenePreviewManager.IsPreviewing,
                    "Preview should auto-stop when the tracked mesh changes.");
                Assert.IsTrue(stopped,
                    "Preview should raise PreviewStopped when it auto-stops on a mesh swap.");
            }
            finally
            {
                ScenePreviewManager.PreviewStopped -= OnStopped;
                ScenePreviewManager.StopPreview();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(primaryMeshA);
                Object.DestroyImmediate(primaryMeshB);
            }
        }
    }
}
