using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    public static class SpsAnimationUtility
    {
        public static AnimationClip CreateBoneScaleClip(
            string bonePath, Vector3 scale,
            bool scaleX, bool scaleY, bool scaleZ)
        {
            var clip = new AnimationClip();
            SetBoneScaleCurves(clip, bonePath, scale, scaleX, scaleY, scaleZ);
            return clip;
        }

        public static AnimationClip CreateBlendshapeClip(
            string rendererPath, string blendshapeName, float weight)
        {
            var clip = new AnimationClip();
            SetBlendshapeCurve(clip, rendererPath, blendshapeName, weight);
            return clip;
        }

        public static AnimationClip CreateRestClip(
            string targetPath, DeformationMode mode,
            bool scaleX, bool scaleY, bool scaleZ,
            string rendererPath = null,
            List<string> blendshapeNames = null)
        {
            var clip = new AnimationClip();
            if (mode == DeformationMode.BoneScale)
            {
                SetBoneScaleCurves(clip, targetPath, Vector3.one, scaleX, scaleY, scaleZ);
            }
            else if (blendshapeNames != null && rendererPath != null)
            {
                foreach (var name in blendshapeNames)
                    SetBlendshapeCurve(clip, rendererPath, name, 0f);
            }
            return clip;
        }

        /// <summary>
        /// Rest clip variant that zeros the blendshape list on multiple renderer paths
        /// (for multi-mesh effects where a primary + overlay meshes share blendshape names).
        /// </summary>
        public static AnimationClip CreateRestClip(
            List<string> rendererPaths,
            List<string> blendshapeNames)
        {
            var clip = new AnimationClip();
            if (rendererPaths == null || blendshapeNames == null) return clip;
            foreach (var path in rendererPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                foreach (var name in blendshapeNames)
                    SetBlendshapeCurve(clip, path, name, 0f);
            }
            return clip;
        }

        public static AnimationClip CreateMultiBoneScaleClip(
            List<(string bonePath, Vector3 scale)> boneScales,
            bool scaleX, bool scaleY, bool scaleZ)
        {
            var clip = new AnimationClip();
            foreach (var (bonePath, scale) in boneScales)
                SetBoneScaleCurves(clip, bonePath, scale, scaleX, scaleY, scaleZ);
            return clip;
        }

        public static AnimationClip CreateMultiBlendshapeClip(
            string rendererPath,
            List<(string blendshapeName, float weight)> blendshapeWeights)
        {
            var clip = new AnimationClip();
            foreach (var (blendshapeName, weight) in blendshapeWeights)
                SetBlendshapeCurve(clip, rendererPath, blendshapeName, weight);
            return clip;
        }

        /// <summary>
        /// Multi-renderer variant: emits a curve for each (rendererPath, blendshape)
        /// pair sharing the same weight, so multiple meshes deform together.
        /// Used for primary + overlay mesh setups.
        /// </summary>
        public static AnimationClip CreateMultiBlendshapeClip(
            List<string> rendererPaths,
            List<(string blendshapeName, float weight)> blendshapeWeights)
        {
            var clip = new AnimationClip();
            if (rendererPaths == null) return clip;
            foreach (var path in rendererPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                foreach (var (blendshapeName, weight) in blendshapeWeights)
                    SetBlendshapeCurve(clip, path, blendshapeName, weight);
            }
            return clip;
        }

        public static void SaveClip(AnimationClip clip, string folder, string name)
        {
            clip.name = name;
            string path = $"{folder}/{name}.anim";
            AssetDatabase.CreateAsset(clip, path);
        }

        /// <summary>
        /// Ensures a folder exists without deleting anything.
        /// Use this when saving configs alongside existing generated assets.
        /// </summary>
        public static void EnsureFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                throw new System.ArgumentException(
                    "folderPath is null or empty. Did the caller forget to assign avatarRoot?",
                    nameof(folderPath));
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        /// <summary>
        /// Creates output folder, cleaning existing animation clips and controllers.
        /// Call this before generating NEW assets (not when saving configs).
        /// </summary>
        public static string CreateOutputFolder(string folderPath)
        {
            // Loud early failure beats the silent NullReferenceException deep in
            // Split('/'). A null folderPath reaches us when a caller handed us
            // the result of BaseEffectConfig.GetConfigFolder() without checking
            // for avatarRoot == null first.
            if (string.IsNullOrEmpty(folderPath))
                throw new System.ArgumentException(
                    "folderPath is null or empty. Did the caller forget to assign avatarRoot?",
                    nameof(folderPath));
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                // Only delete animation clips and controllers - preserve mesh assets.
                // StartAssetEditing batches the deletes so the importer fires once
                // at StopAssetEditing instead of per-file.
                string fullPath = Path.GetFullPath(folderPath);
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var file in Directory.GetFiles(fullPath))
                    {
                        string ext = Path.GetExtension(file);
                        if (!string.Equals(ext, ".anim", System.StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(ext, ".controller", System.StringComparison.OrdinalIgnoreCase))
                            continue;

                        string assetPath = file.Replace("\\", "/");
                        int assetsIdx = assetPath.IndexOf("Assets/");
                        if (assetsIdx >= 0)
                            AssetDatabase.DeleteAsset(assetPath.Substring(assetsIdx));
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }
            else
            {
                EnsureFolder(folderPath);
            }
            return folderPath;
        }

        private static void SetBoneScaleCurves(
            AnimationClip clip, string bonePath, Vector3 scale,
            bool scaleX, bool scaleY, bool scaleZ)
        {
            if (scaleX)
                clip.SetCurve(bonePath, typeof(Transform), "m_LocalScale.x",
                    new AnimationCurve(new Keyframe(0f, scale.x)));
            if (scaleY)
                clip.SetCurve(bonePath, typeof(Transform), "m_LocalScale.y",
                    new AnimationCurve(new Keyframe(0f, scale.y)));
            if (scaleZ)
                clip.SetCurve(bonePath, typeof(Transform), "m_LocalScale.z",
                    new AnimationCurve(new Keyframe(0f, scale.z)));
        }

        private static void SetBlendshapeCurve(
            AnimationClip clip, string rendererPath,
            string blendshapeName, float weight)
        {
            if (string.IsNullOrEmpty(blendshapeName)) return;
            clip.SetCurve(rendererPath, typeof(SkinnedMeshRenderer),
                $"blendShape.{blendshapeName}",
                new AnimationCurve(new Keyframe(0f, weight)));
        }
    }
}
