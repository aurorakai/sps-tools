using System.IO;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Lifecycle states of an in-memory config relative to its on-disk asset.
    /// Rendered as the inline status badge and drives Save button behaviour.
    /// </summary>
    internal enum ConfigState
    {
        /// <summary>No backing file yet, or the previously-tracked file is gone.</summary>
        New,
        /// <summary>Backing file exists and in-memory state matches it.</summary>
        Saved,
        /// <summary>Backing file exists with the right name, but in-memory state differs.</summary>
        Dirty,
        /// <summary>Backing file exists but the user has typed a different name — Save will rename.</summary>
        Renamed
    }

    /// <summary>
    /// Pure state classifier. All inputs are strings / bools so the logic is
    /// trivially unit-testable without Unity editor APIs.
    /// </summary>
    internal static class ConfigStateDetector
    {
        /// <summary>
        /// Priority: New → Renamed → Dirty → Saved. Rename beats Dirty because
        /// "Rename &amp; Save" is a superset action and the label is more informative.
        /// </summary>
        /// <param name="currentConfigJson">EditorJsonUtility.ToJson of the working config.</param>
        /// <param name="lastSavedJson">Snapshot captured at the last successful load/save.</param>
        /// <param name="savedPath">Tracked asset path (from trackedAssetPath); empty if never saved.</param>
        /// <param name="expectedAssetStem">Filename stem the current config would save to.</param>
        /// <param name="savedFileExists">Whether the asset at savedPath still exists on disk.</param>
        internal static ConfigState Detect(
            string currentConfigJson,
            string lastSavedJson,
            string savedPath,
            string expectedAssetStem,
            bool savedFileExists)
        {
            if (string.IsNullOrEmpty(savedPath) || !savedFileExists)
                return ConfigState.New;

            string diskStem = Path.GetFileNameWithoutExtension(savedPath);
            // Filesystem case sensitivity differs by OS. Use ordinal-ignore-case on
            // Windows/macOS (the supported Unity hosts) so a case-only rename
            // doesn't trigger a destructive Save (DeleteAsset/CreateAsset race).
            var comparison = (Application.platform == RuntimePlatform.WindowsEditor
                              || Application.platform == RuntimePlatform.OSXEditor)
                ? System.StringComparison.OrdinalIgnoreCase
                : System.StringComparison.Ordinal;
            if (!string.Equals(diskStem, expectedAssetStem, comparison))
                return ConfigState.Renamed;

            if (currentConfigJson != lastSavedJson)
                return ConfigState.Dirty;

            return ConfigState.Saved;
        }
    }
}
