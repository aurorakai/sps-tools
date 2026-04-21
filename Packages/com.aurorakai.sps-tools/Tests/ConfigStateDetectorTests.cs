using NUnit.Framework;

namespace AuroraKai.SPSTools.Tests
{
    public class ConfigStateDetectorTests
    {
        [Test]
        public void DetectsNewWhenNoPath()
        {
            var state = ConfigStateDetector.Detect(
                currentConfigJson: "{\"name\":\"whatever\"}",
                lastSavedJson:     "",
                savedPath:         "",
                expectedAssetStem: "SPSBulge_Default",
                savedFileExists:   false);
            Assert.AreEqual(ConfigState.New, state);
        }

        [Test]
        public void DetectsNewWhenSavedFileMissing()
        {
            // Path was previously tracked but the asset was deleted externally.
            var state = ConfigStateDetector.Detect(
                currentConfigJson: "{\"name\":\"x\"}",
                lastSavedJson:     "{\"name\":\"x\"}",
                savedPath:         "Assets/SPSTools/A/Bulge/Default/SPSBulge_Default.asset",
                expectedAssetStem: "SPSBulge_Default",
                savedFileExists:   false);
            Assert.AreEqual(ConfigState.New, state);
        }

        [Test]
        public void DetectsSavedWhenIdentical()
        {
            const string json = "{\"name\":\"Default\",\"value\":1}";
            var state = ConfigStateDetector.Detect(
                currentConfigJson: json,
                lastSavedJson:     json,
                savedPath:         "Assets/SPSTools/A/Bulge/Default/SPSBulge_Default.asset",
                expectedAssetStem: "SPSBulge_Default",
                savedFileExists:   true);
            Assert.AreEqual(ConfigState.Saved, state);
        }

        [Test]
        public void DetectsDirtyWhenFieldDiffers()
        {
            var state = ConfigStateDetector.Detect(
                currentConfigJson: "{\"value\":2}",
                lastSavedJson:     "{\"value\":1}",
                savedPath:         "Assets/SPSTools/A/Bulge/Default/SPSBulge_Default.asset",
                expectedAssetStem: "SPSBulge_Default",
                savedFileExists:   true);
            Assert.AreEqual(ConfigState.Dirty, state);
        }

        [Test]
        public void DetectsRenamedWhenStemDiffers()
        {
            var state = ConfigStateDetector.Detect(
                currentConfigJson: "{\"value\":1}",
                lastSavedJson:     "{\"value\":1}",
                savedPath:         "Assets/SPSTools/A/Bulge/Default/SPSBulge_Default.asset",
                expectedAssetStem: "SPSBulge_Renamed",
                savedFileExists:   true);
            Assert.AreEqual(ConfigState.Renamed, state);
        }

        [Test]
        public void RenamePrioritizedOverDirty()
        {
            // Name changed AND another field changed: we show Renamed, not Dirty,
            // because "Rename & Save" is a superset of the dirty-write behaviour
            // and the rename label is more informative to the user.
            var state = ConfigStateDetector.Detect(
                currentConfigJson: "{\"value\":2}",
                lastSavedJson:     "{\"value\":1}",
                savedPath:         "Assets/SPSTools/A/Bulge/Default/SPSBulge_Default.asset",
                expectedAssetStem: "SPSBulge_Renamed",
                savedFileExists:   true);
            Assert.AreEqual(ConfigState.Renamed, state);
        }
    }
}
