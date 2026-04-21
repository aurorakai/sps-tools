using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class ConfigPathTests
    {
        [Test]
        public void GetConfigFolder_WithAvatarRoot_ReturnsNormalizedPath()
        {
            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                var avatar = new GameObject("TestAvatar");
                try
                {
                    config.avatarRoot = avatar;
                    // Avatar / EffectType / ConfigName - one folder holds the
                    // config asset plus every generated artifact.
                    Assert.AreEqual("Assets/SPSTools/TestAvatar/Bulge/Default",
                        config.GetConfigFolder());
                }
                finally
                {
                    Object.DestroyImmediate(avatar);
                }
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void GetOutputFolder_MatchesConfigFolder()
        {
            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                var avatar = new GameObject("TestAvatar");
                try
                {
                    config.avatarRoot = avatar;
                    Assert.AreEqual(config.GetConfigFolder(),
                        config.GetOutputFolder(),
                        "Config and generated output must live in the same folder");
                }
                finally
                {
                    Object.DestroyImmediate(avatar);
                }
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void GetConfigFolder_WithNullAvatarRoot_ReturnsNull()
        {
            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                Assert.IsNull(config.GetConfigFolder());
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void GetConfigFolder_SanitizesInvalidChars()
        {
            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            try
            {
                // '/' is in Path.GetInvalidFileNameChars() on every platform
                // (Windows, macOS, Linux). ':' is Windows-only, so the
                // previous assertion passed vacuously on Unix CI runners.
                var avatar = new GameObject("Has/BadName");
                try
                {
                    config.avatarRoot = avatar;
                    string folder = config.GetConfigFolder();
                    const string prefix = "Assets/SPSTools/";
                    StringAssert.StartsWith(prefix, folder);
                    // After the prefix the path is Avatar / EffectType / Config
                    // (three segments). If the sanitizer skipped the embedded
                    // '/' in the avatar name, the avatar segment would split
                    // into two and the total would be four.
                    string[] segments = folder.Substring(prefix.Length).Split('/');
                    Assert.AreEqual(3, segments.Length,
                        "Expected exactly Avatar/EffectType/Config under the SPSTools root; " +
                        "a longer path means '/' leaked from the avatar name.");
                }
                finally
                {
                    Object.DestroyImmediate(avatar);
                }
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void FindAllConfigs_FindsAssetAnywhereInProject()
        {
            const string testFolder = "Assets/SPSTools/TestConfigs";
            if (!AssetDatabase.IsValidFolder("Assets/SPSTools"))
                AssetDatabase.CreateFolder("Assets", "SPSTools");
            if (!AssetDatabase.IsValidFolder(testFolder))
                AssetDatabase.CreateFolder("Assets/SPSTools", "TestConfigs");

            var config = ScriptableObject.CreateInstance<BulgeConfig>();
            string path = $"{testFolder}/FindAllTest.asset";
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();

            try
            {
                var results = ConfigAssetHelper.FindAllConfigs<BulgeConfig>();
                Assert.IsTrue(results.Contains(config),
                    "FindAllConfigs should return assets from arbitrary folders");
            }
            finally
            {
                AssetDatabase.DeleteAsset(testFolder);
            }
        }
    }
}
