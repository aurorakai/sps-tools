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

        // Body layer (y=0) and overlay/"fluff" layer (y=0.02) bundled into one
        // SkinnedMesh with no shared triangles and no shared vertex positions —
        // a disconnected-island topology. Mirrors avatars where fluff is
        // imported as a separate Blender object and Unity merges them into one
        // renderer at export. The surface-distance flood fill in
        // BlendshapeGenerator cannot cross the gap between islands.
        private static Mesh CreateLayeredIslandMesh()
        {
            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                // Body layer at y=0 (4 cm × 4 cm)
                new Vector3(-0.02f, 0f, -0.02f),
                new Vector3( 0.02f, 0f, -0.02f),
                new Vector3( 0.02f, 0f,  0.02f),
                new Vector3(-0.02f, 0f,  0.02f),
                // Fluff layer 2 cm above, same XZ span — distinct vert indices.
                new Vector3(-0.02f, 0.02f, -0.02f),
                new Vector3( 0.02f, 0.02f, -0.02f),
                new Vector3( 0.02f, 0.02f,  0.02f),
                new Vector3(-0.02f, 0.02f,  0.02f),
            };
            mesh.normals = new Vector3[]
            {
                Vector3.down, Vector3.down, Vector3.down, Vector3.down,
                Vector3.up, Vector3.up, Vector3.up, Vector3.up,
            };
            mesh.triangles = new int[]
            {
                0, 1, 2,  0, 2, 3,  // body quad
                4, 5, 6,  4, 6, 7,  // fluff quad - no vertex shared with body
            };
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
        public void ComputeVerticesInTube_IncludesBody_WhenOverlayIsTopologyIsland()
        {
            // Regression: when a renderer's mesh contains both body geometry and
            // an overlay layer (fluff, fur, etc.) as disconnected triangle
            // islands — no shared triangles, no co-located verts — the path's
            // Euclidean-nearest seed set lands entirely on one layer and the
            // surface-distance flood fill can't cross to the other. Users
            // reported the whole deformation landing on the overlay while the
            // body underneath went untouched. Body verts physically inside the
            // tube must still be reported in-tube so they receive displacement.
            var root = new GameObject("Avatar");
            var mesh = CreateLayeredIslandMesh();
            CreateRenderer(root.transform, "Body", mesh, out var renderer);

            // Path sits on the overlay layer (y=0.02). 5 cm tube radius
            // comfortably contains the body verts 2 cm below by Euclidean
            // distance, but body is unreachable via mesh adjacency.
            var path = new List<PathWaypoint>
            {
                new PathWaypoint
                {
                    localPosition = new Vector3(-0.01f, 0.02f, 0f),
                    localNormal = Vector3.up,
                    radius = 0.05f
                },
                new PathWaypoint
                {
                    localPosition = new Vector3(0.01f, 0.02f, 0f),
                    localNormal = Vector3.up,
                    radius = 0.05f
                }
            };

            try
            {
                int segments = Mathf.Max(path.Count * 8, 20);
                var tube = CatmullRomSpline.BuildTube(path, segments);
                var worldVerts = BlendshapeGenerator.GetSkinnedWorldRefVerts(
                    renderer, mesh.vertices);
                var inTube = BlendshapeGenerator.ComputeVerticesInTube(
                    mesh, worldVerts, root.transform, tube);

                int bodyInTube = 0;
                for (int i = 0; i < 4; i++)
                    if (inTube[i]) bodyInTube++;

                Assert.Greater(bodyInTube, 0,
                    "Body verts must be reported in-tube even when the overlay " +
                    "layer is a disconnected triangle island that traps the " +
                    "surface-distance flood fill inside itself.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mesh);
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
        public void OverlayTransfer_MatchesPrimaryNormalsAtAllWeights_WhenBaseNormalsDiffer()
        {
            // Regression: overlay and primary with different authored base normals
            // (e.g. clothing over body with custom split normals) must match at
            // every blendshape weight during preview, not just weight=1. Unity
            // blends normals linearly, so both the base and the delta have to
            // agree for the overlay to avoid ghosting as the effect ramps in.
            var avatar = new GameObject("Avatar");

            var primaryMesh = new Mesh();
            primaryMesh.vertices = new Vector3[]
            {
                new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
                new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f),
                new Vector3(0f, 0f, 0f),
            };
            primaryMesh.normals = new Vector3[]
            {
                Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            };
            primaryMesh.triangles = new int[]
            {
                0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4,
            };
            primaryMesh.RecalculateBounds();

            var overlayMesh = new Mesh();
            overlayMesh.vertices = new Vector3[]
            {
                new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
                new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f),
                new Vector3(0f, 0f, 0f),
            };
            // Intentionally different authored normals — the regression case.
            var tilted = new Vector3(0f, 0.3f, -0.95f).normalized;
            overlayMesh.normals = new Vector3[] { tilted, tilted, tilted, tilted, tilted };
            overlayMesh.triangles = new int[]
            {
                0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4,
            };
            overlayMesh.RecalculateBounds();

            var primaryGo = new GameObject("Primary");
            primaryGo.transform.SetParent(avatar.transform, false);
            var primaryRenderer = primaryGo.AddComponent<SkinnedMeshRenderer>();
            primaryRenderer.sharedMesh = primaryMesh;

            var overlayGo = new GameObject("Overlay");
            overlayGo.transform.SetParent(avatar.transform, false);
            var overlayRenderer = overlayGo.AddComponent<SkinnedMeshRenderer>();
            overlayRenderer.sharedMesh = overlayMesh;

            var path = new List<PathWaypoint>
            {
                new PathWaypoint { localPosition = Vector3.zero, localNormal = Vector3.back, radius = 2f },
                new PathWaypoint { localPosition = new Vector3(0f, 2f, 0f), localNormal = Vector3.back, radius = 2f },
            };

            try
            {
                var results = BlendshapeGenerator.GenerateBulgeBlendshapes(
                    new List<SkinnedMeshRenderer> { primaryRenderer, overlayRenderer },
                    avatar.transform, path,
                    positionCount: 1, displacement: 0.05f,
                    outputFolder: "Assets/SPSTools/Test/Bulge/OverlayNormalsSeam",
                    smoothingPasses: 0,
                    recalculateNormals: true);

                Assert.AreEqual(2, results.Count, "Both primary and overlay results expected.");
                Assert.Greater(results[0].blendshapeNames.Count, 0,
                    "Primary must have at least one generated blendshape for the test to be meaningful.");
                Assert.AreEqual(results[0].blendshapeNames.Count, results[1].blendshapeNames.Count,
                    "Overlay must produce the same number of blendshapes as the primary.");

                var primaryOut = results[0].modifiedMesh;
                var overlayOut = results[1].modifiedMesh;
                var primaryVerts = primaryOut.vertices;
                var overlayVerts = overlayOut.vertices;
                var primaryBase = primaryOut.normals;
                var overlayBase = overlayOut.normals;

                int bsPrimary = primaryOut.GetBlendShapeIndex(results[0].blendshapeNames[0]);
                int bsOverlay = overlayOut.GetBlendShapeIndex(results[1].blendshapeNames[0]);
                var pDV = new Vector3[primaryOut.vertexCount];
                var pDN = new Vector3[primaryOut.vertexCount];
                var oDV = new Vector3[overlayOut.vertexCount];
                var oDN = new Vector3[overlayOut.vertexCount];
                primaryOut.GetBlendShapeFrameVertices(bsPrimary, 0, pDV, pDN, null);
                overlayOut.GetBlendShapeFrameVertices(bsOverlay, 0, oDV, oDN, null);

                int coincidentWithNonzeroDelta = 0;
                for (int ov = 0; ov < overlayVerts.Length; ov++)
                {
                    int pv = -1;
                    float minSqr = float.MaxValue;
                    for (int pi = 0; pi < primaryVerts.Length; pi++)
                    {
                        float sd = (primaryVerts[pi] - overlayVerts[ov]).sqrMagnitude;
                        if (sd < minSqr) { minSqr = sd; pv = pi; }
                    }
                    if (minSqr > 0.0001f) continue; // not coincident

                    // Skip verts the primary didn't deform - nothing to compare.
                    if (pDN[pv].sqrMagnitude < 1e-10f) continue;
                    coincidentWithNonzeroDelta++;

                    // Both base and delta must match so the linear base+w·delta
                    // blend produces the same normal at every weight.
                    Assert.AreEqual(primaryBase[pv].x, overlayBase[ov].x, 0.001f,
                        $"Overlay base normal x mismatch at vert {ov} ↔ primary {pv}.");
                    Assert.AreEqual(primaryBase[pv].y, overlayBase[ov].y, 0.001f,
                        $"Overlay base normal y mismatch at vert {ov} ↔ primary {pv}.");
                    Assert.AreEqual(primaryBase[pv].z, overlayBase[ov].z, 0.001f,
                        $"Overlay base normal z mismatch at vert {ov} ↔ primary {pv}.");

                    Assert.AreEqual(pDN[pv].x, oDN[ov].x, 0.001f,
                        $"Overlay normal delta x mismatch at vert {ov} ↔ primary {pv}.");
                    Assert.AreEqual(pDN[pv].y, oDN[ov].y, 0.001f,
                        $"Overlay normal delta y mismatch at vert {ov} ↔ primary {pv}.");
                    Assert.AreEqual(pDN[pv].z, oDN[ov].z, 0.001f,
                        $"Overlay normal delta z mismatch at vert {ov} ↔ primary {pv}.");

                    // Spot-check the blend at w=0.5 - the weight most likely to
                    // reveal a seam during ramp-in.
                    Vector3 primaryAtHalf = primaryBase[pv] + 0.5f * pDN[pv];
                    Vector3 overlayAtHalf = overlayBase[ov] + 0.5f * oDN[ov];
                    Assert.AreEqual(primaryAtHalf.x, overlayAtHalf.x, 0.001f,
                        $"w=0.5 normal x mismatch (vert {ov} ↔ {pv}).");
                    Assert.AreEqual(primaryAtHalf.y, overlayAtHalf.y, 0.001f,
                        $"w=0.5 normal y mismatch (vert {ov} ↔ {pv}).");
                    Assert.AreEqual(primaryAtHalf.z, overlayAtHalf.z, 0.001f,
                        $"w=0.5 normal z mismatch (vert {ov} ↔ {pv}).");
                }

                Assert.Greater(coincidentWithNonzeroDelta, 0,
                    "No coincident vert was deformed - assertions were vacuous.");
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(primaryMesh);
                Object.DestroyImmediate(overlayMesh);
            }
        }

        [Test]
        public void Preview_RestoresNonZeroPreSetWeightAfterRebind()
        {
            var root = new GameObject("Avatar");
            var primaryMesh = CreateStripMesh();
            // Both meshes have "Foo" but at different indices — swap shifts it.
            var overlayA = CreateBlendshapeMesh("Foo");
            var overlayB = CreateBlendshapeMesh("Bar", "Foo");
            var clip = new AnimationClip();
            CreateRenderer(root.transform, "Primary", primaryMesh, out _);
            CreateRenderer(root.transform, "Overlay", overlayA, out var overlay);

            // User had a non-zero weight on the old slot before preview started.
            overlay.SetBlendShapeWeight(0, 25f);

            AnimationUtility.SetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(
                    "Overlay", typeof(SkinnedMeshRenderer), "blendShape.Foo"),
                new AnimationCurve(new Keyframe(0f, 50f)));

            try
            {
                ScenePreviewManager.StartPreview(
                    root, new List<string> { "Primary" }, Vector3.zero);
                var entries = new List<(float, AnimationClip)> { (0f, clip) };

                ScenePreviewManager.SampleAtDepth(0f, entries);
                Assert.AreEqual(50f, overlay.GetBlendShapeWeight(0), 0.001f);

                overlay.sharedMesh = overlayB;
                ScenePreviewManager.SampleAtDepth(1f, entries);

                // After swap, "Foo" lives at index 1 on overlayB. The old slot 0
                // (on overlayB this is "Bar") must be restored to the user's
                // pre-preview value of 25f, not zeroed.
                Assert.AreEqual(25f, overlay.GetBlendShapeWeight(0), 0.001f,
                    "Restored weight on the rebind path must come from the user's pre-preview value, not 0.");
                Assert.AreEqual(50f, overlay.GetBlendShapeWeight(1), 0.001f);

                ScenePreviewManager.StopPreview();
                Assert.AreEqual(25f, overlay.GetBlendShapeWeight(0), 0.001f,
                    "StopPreview must also restore the pre-preview value, not the zero we wrote during rebind.");
            }
            finally
            {
                ScenePreviewManager.StopPreview();
                Object.DestroyImmediate(clip);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(primaryMesh);
                Object.DestroyImmediate(overlayA);
                Object.DestroyImmediate(overlayB);
            }
        }

        [Test]
        public void Preview_AutoStopsOnSceneSaving()
        {
            var root = new GameObject("Avatar");
            var primaryMesh = CreateStripMesh();
            CreateRenderer(root.transform, "Primary", primaryMesh, out _);

            try
            {
                ScenePreviewManager.StartPreview(
                    root, new List<string> { "Primary" }, Vector3.zero);
                Assert.IsTrue(ScenePreviewManager.IsPreviewing);

                // Simulate the editor's pre-save hook firing.
                ScenePreviewManager.OnSceneSaving(
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene(),
                    "dummy/path.unity");

                Assert.IsFalse(ScenePreviewManager.IsPreviewing,
                    "Preview must stop before the scene is saved so temporary blendshape weights aren't persisted.");
            }
            finally
            {
                ScenePreviewManager.StopPreview();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(primaryMesh);
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
