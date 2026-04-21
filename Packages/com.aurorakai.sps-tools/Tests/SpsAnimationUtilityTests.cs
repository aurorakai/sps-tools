using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class SpsAnimationUtilityTests
    {
        [Test]
        public void CreateBlendshapeClip_SetsCorrectCurve()
        {
            var clip = SpsAnimationUtility.CreateBlendshapeClip("Body", "TestShape", 75f);

            Assert.IsNotNull(clip);
            var bindings = AnimationUtility.GetCurveBindings(clip);
            Assert.AreEqual(1, bindings.Length);
            Assert.AreEqual("Body", bindings[0].path);
            Assert.IsTrue(bindings[0].propertyName.Contains("TestShape"));

            var curve = AnimationUtility.GetEditorCurve(clip, bindings[0]);
            Assert.AreEqual(75f, curve.Evaluate(0f), 0.01f);
        }

        [Test]
        public void CreateBoneScaleClip_SetsCorrectAxes()
        {
            // Unity couples m_LocalScale.* - setting any single axis implicitly
            // binds all three to keep the Transform's Vector3 scale coherent.
            // The axes the caller flagged off stay at identity (1.0) rather
            // than being suppressed from the clip. Use Y=2.5 in the scale
            // vector so a regression that writes the passed Vector3.y into
            // the Y curve (instead of identity) would be visible.
            var clip = SpsAnimationUtility.CreateBoneScaleClip(
                "Armature/Hips/Spine", new Vector3(1.5f, 2.5f, 1.2f),
                true, false, true);

            var bindings = AnimationUtility.GetCurveBindings(clip);
            Assert.AreEqual(3, bindings.Length);

            float ValueOf(string property)
            {
                foreach (var b in bindings)
                    if (b.propertyName == property)
                        return AnimationUtility.GetEditorCurve(clip, b).Evaluate(0f);
                Assert.Fail($"Missing binding {property}");
                return float.NaN;
            }

            Assert.AreEqual(1.5f, ValueOf("m_LocalScale.x"), 0.001f);
            Assert.AreEqual(1.0f, ValueOf("m_LocalScale.y"), 0.001f,
                "Disabled axis must stay at identity");
            Assert.AreEqual(1.2f, ValueOf("m_LocalScale.z"), 0.001f);
        }

        [Test]
        public void CreateRestClip_Blendshape_AllZero()
        {
            var names = new System.Collections.Generic.List<string>
                { "Shape1", "Shape2", "Shape3" };

            var clip = SpsAnimationUtility.CreateRestClip(
                "", DeformationMode.Blendshape, false, false, false,
                "Body", names);

            var bindings = AnimationUtility.GetCurveBindings(clip);
            Assert.AreEqual(3, bindings.Length);

            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                Assert.AreEqual(0f, curve.Evaluate(0f), 0.01f);
            }
        }

        [Test]
        public void CreateMultiBlendshapeClip_SetsAllWeights()
        {
            var weights = new System.Collections.Generic.List<(string, float)>
            {
                ("Shape1", 100f),
                ("Shape2", 50f),
                ("Shape3", 0f)
            };

            var clip = SpsAnimationUtility.CreateMultiBlendshapeClip("Body", weights);

            var bindings = AnimationUtility.GetCurveBindings(clip);
            Assert.AreEqual(3, bindings.Length);
        }

        [Test]
        public void CreateBlendshapeClip_EmptyName_NoBinding()
        {
            var clip = SpsAnimationUtility.CreateBlendshapeClip("Body", "", 50f);

            var bindings = AnimationUtility.GetCurveBindings(clip);
            Assert.AreEqual(0, bindings.Length);
        }

        [Test]
        public void EnsureFolder_DoesNotDelete_ExistingFiles()
        {
            string testFolder = "Assets/SPSTools/TestEnsure";
            if (!AssetDatabase.IsValidFolder("Assets/SPSTools"))
                AssetDatabase.CreateFolder("Assets", "SPSTools");
            if (!AssetDatabase.IsValidFolder(testFolder))
                AssetDatabase.CreateFolder("Assets/SPSTools", "TestEnsure");

            // Create a test file
            var clip = new AnimationClip();
            string clipPath = testFolder + "/TestClip.anim";
            AssetDatabase.CreateAsset(clip, clipPath);
            AssetDatabase.SaveAssets();

            try
            {
                // EnsureFolder should NOT delete the clip
                SpsAnimationUtility.EnsureFolder(testFolder);

                var loaded = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                Assert.IsNotNull(loaded, "EnsureFolder should not delete existing files");
            }
            finally
            {
                AssetDatabase.DeleteAsset(testFolder);
            }
        }
    }
}
