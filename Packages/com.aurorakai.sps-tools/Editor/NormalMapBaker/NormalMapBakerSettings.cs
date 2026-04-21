using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Per-config bake settings. Created on demand when the user first opens
    /// the baker for a given config, stored as a sibling asset. Deleting the
    /// asset reverts the baker for that config.
    /// </summary>
    public class NormalMapBakerSettings : ScriptableObject
    {
        public enum ResolutionPreset
        {
            _512 = 512,
            _1024 = 1024,
            _2048 = 2048,
            _4096 = 4096,
        }

        public enum OverlayMode
        {
            Auto,
            ForceShared,
            ForcePerMesh,
        }

        [Serializable]
        public struct OverlayOverride
        {
            public string rendererPath;
            public OverlayMode mode;
        }

        [Serializable]
        public struct MaterialSelection
        {
            public string rendererPath;
            public int materialIndex;
        }

        [Serializable]
        public struct BakedOverlay
        {
            public string rendererPath;
            public Texture2D texture;
        }

        public BaseEffectConfig targetConfig;

        public ResolutionPreset resolution = ResolutionPreset._1024;

        [Range(1, 5)] public int referenceSubdivision = 3;
        [Range(0, 8)] public int seamPaddingTexels = 4;

        public bool sharedAcrossOverlays = true;

        [Range(0f, 3f)] public float blendStrengthAtPeak = 1f;

        public List<OverlayOverride> overlayOverrides = new List<OverlayOverride>();
        public List<MaterialSelection> selectedMaterials = new List<MaterialSelection>();

        public Texture2D lastBakedPrimary;
        public List<BakedOverlay> lastBakedOverlays = new List<BakedOverlay>();

        /// <summary>
        /// Returns the settings asset sibling to <paramref name="config"/>,
        /// creating it if absent. Returns null when the config isn't a
        /// persisted asset.
        /// </summary>
        public static NormalMapBakerSettings FindOrCreateFor(BaseEffectConfig config)
        {
            if (config == null) return null;

            string configPath = AssetDatabase.GetAssetPath(config);
            if (string.IsNullOrEmpty(configPath))
            {
                Debug.LogWarning("[SPS Baker] Config is not a persisted asset; " +
                    "can't attach a NormalMapBakerSettings asset to it.");
                return null;
            }

            string configDir = Path.GetDirectoryName(configPath);
            string fileName = Path.GetFileNameWithoutExtension(configPath) + "_NormalMapBakerSettings.asset";
            string settingsPath = $"{configDir}/{fileName}".Replace('\\', '/');

            var existing = AssetDatabase.LoadAssetAtPath<NormalMapBakerSettings>(settingsPath);
            if (existing != null)
            {
                if (existing.targetConfig != config)
                {
                    existing.targetConfig = config;
                    EditorUtility.SetDirty(existing);
                }
                return existing;
            }

            var created = CreateInstance<NormalMapBakerSettings>();
            created.targetConfig = config;
            AssetDatabase.CreateAsset(created, settingsPath);
            AssetDatabase.SaveAssets();
            return created;
        }

        /// <summary>Returns the overlay mode for a renderer path, or Auto if unset.</summary>
        public OverlayMode GetOverlayMode(string rendererPath)
        {
            if (overlayOverrides == null) return OverlayMode.Auto;
            for (int i = 0; i < overlayOverrides.Count; i++)
            {
                if (overlayOverrides[i].rendererPath == rendererPath)
                    return overlayOverrides[i].mode;
            }
            return OverlayMode.Auto;
        }

        /// <summary>Sets (or creates) the overlay mode entry for a renderer path.</summary>
        public void SetOverlayMode(string rendererPath, OverlayMode mode)
        {
            if (overlayOverrides == null) overlayOverrides = new List<OverlayOverride>();
            for (int i = 0; i < overlayOverrides.Count; i++)
            {
                if (overlayOverrides[i].rendererPath == rendererPath)
                {
                    var entry = overlayOverrides[i];
                    entry.mode = mode;
                    overlayOverrides[i] = entry;
                    EditorUtility.SetDirty(this);
                    return;
                }
            }
            overlayOverrides.Add(new OverlayOverride { rendererPath = rendererPath, mode = mode });
            EditorUtility.SetDirty(this);
        }

        /// <summary>True if (rendererPath, materialIndex) is in the selected list.</summary>
        public bool IsMaterialSelected(string rendererPath, int materialIndex)
        {
            if (selectedMaterials == null) return false;
            for (int i = 0; i < selectedMaterials.Count; i++)
            {
                var sel = selectedMaterials[i];
                if (sel.rendererPath == rendererPath && sel.materialIndex == materialIndex)
                    return true;
            }
            return false;
        }

        /// <summary>Adds or removes a (rendererPath, materialIndex) entry.</summary>
        public void SetMaterialSelected(string rendererPath, int materialIndex, bool selected)
        {
            if (selectedMaterials == null) selectedMaterials = new List<MaterialSelection>();

            int existingIndex = -1;
            for (int i = 0; i < selectedMaterials.Count; i++)
            {
                var sel = selectedMaterials[i];
                if (sel.rendererPath == rendererPath && sel.materialIndex == materialIndex)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (selected && existingIndex < 0)
            {
                selectedMaterials.Add(new MaterialSelection
                {
                    rendererPath = rendererPath,
                    materialIndex = materialIndex,
                });
                EditorUtility.SetDirty(this);
            }
            else if (!selected && existingIndex >= 0)
            {
                selectedMaterials.RemoveAt(existingIndex);
                EditorUtility.SetDirty(this);
            }
        }
    }
}
