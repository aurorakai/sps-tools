using System.IO;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>Writes a baked Texture2D to PNG and imports as a NormalMap asset.</summary>
    public static class NormalMapPngWriter
    {
        /// <summary>
        /// Writes <paramref name="baked"/> to <paramref name="assetPath"/>, imports
        /// it as NormalMap (linear, clamp, mipmaps on), and returns the imported
        /// asset. Creates parent directories as needed. Overwrites if present.
        /// </summary>
        public static Texture2D WritePng(Texture2D baked, string assetPath)
        {
            if (baked == null)
                throw new System.ArgumentNullException(nameof(baked));
            if (string.IsNullOrEmpty(assetPath))
                throw new System.ArgumentException("assetPath is empty", nameof(assetPath));

            string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir))
                SpsAnimationUtility.EnsureFolder(dir);

            byte[] pngBytes = baked.EncodeToPNG();
            File.WriteAllBytes(assetPath, pngBytes);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                bool changed = false;
                if (importer.textureType != TextureImporterType.NormalMap)
                { importer.textureType = TextureImporterType.NormalMap; changed = true; }
                if (importer.sRGBTexture)
                { importer.sRGBTexture = false; changed = true; }
                if (importer.wrapMode != TextureWrapMode.Clamp)
                { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }
                if (!importer.mipmapEnabled)
                { importer.mipmapEnabled = true; changed = true; }
                if (changed) importer.SaveAndReimport();
            }
            else
            {
                Debug.LogWarning($"[SPS Baker] No TextureImporter at {assetPath}; " +
                    "normal-map settings not applied. Asset might need manual import configuration.");
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>Returns the canonical asset path for a bake output.</summary>
        public static string BuildOutputPath(
            string configOutputFolder, string configName, string meshSuffix)
        {
            string baseName = string.IsNullOrEmpty(meshSuffix)
                ? configName
                : $"{configName}_{meshSuffix}";
            // Fall back to "bake" when the composed name is empty - an asset
            // path ending in "/.png" would fail AssetDatabase import.
            string safeName = string.IsNullOrEmpty(baseName)
                ? "bake"
                : BaseEffectConfig.SanitizeFileName(baseName);
            return $"{configOutputFolder}/NormalMap/{safeName}.png";
        }
    }
}
