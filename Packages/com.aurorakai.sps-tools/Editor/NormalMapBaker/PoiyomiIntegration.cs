using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Poiyomi-specific automation for the Normal Map Baker: unlocks materials
    /// (via Thry reflection), assigns the baked texture to the detail normal
    /// slot, marks it animated-when-locked, and generates the driving clip +
    /// controller + VRCFury FullController. Non-Poiyomi materials are rejected
    /// so callers can route them through the manual property-picker fallback.
    /// </summary>
    public static class PoiyomiIntegration
    {
        public const string PropDetailEnabled = "_DetailEnabled";
        public const string PropDetailNormalMap = "_DetailNormalMap";
        public const string PropDetailNormalMapScale = "_DetailNormalMapScale";

        public enum ApplyOutcome
        {
            Applied,
            NotPoiyomi,
            CancelledByUser,
            ThryMissing,
            MissingShaderProperty,
            Error,
        }

        public struct ApplyResult
        {
            public ApplyOutcome outcome;
            public string message;
            public Material material;
        }

        /// <summary>True if the material's shader name contains "poiyomi".</summary>
        public static bool IsPoiyomi(Material material)
        {
            if (material == null || material.shader == null) return false;
            string name = material.shader.name;
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Unlocks a Poiyomi material (via Thry reflection if available), assigns
        /// the baked texture to the detail normal slot, sets the scale to 0 so
        /// the animation curve drives it up, marks the scale property as
        /// animated-when-locked. Does not touch animation clips/controller —
        /// the caller does that once per bake across all applied materials.
        /// </summary>
        public static ApplyResult ApplyToMaterial(
            Material material, Texture2D bakedTexture, Texture2D priorBakedTexture)
        {
            var result = new ApplyResult { material = material };

            if (material == null)
            {
                result.outcome = ApplyOutcome.Error;
                result.message = "Material is null.";
                return result;
            }
            if (!IsPoiyomi(material))
            {
                result.outcome = ApplyOutcome.NotPoiyomi;
                result.message = "Not a Poiyomi material — route through manual apply popup.";
                return result;
            }

            // Unlock first: locked Poiyomi variants strip unused properties,
            // so HasProperty would false-report MissingShaderProperty.
            bool unlocked = TryUnlockViaThry(material, out string unlockWarning);
            if (!unlocked && !string.IsNullOrEmpty(unlockWarning))
                result.message = unlockWarning;

            if (!material.HasProperty(PropDetailNormalMap)
                || !material.HasProperty(PropDetailNormalMapScale)
                || !material.HasProperty(PropDetailEnabled))
            {
                result.outcome = ApplyOutcome.MissingShaderProperty;
                string extra = !unlocked && string.IsNullOrEmpty(unlockWarning)
                    ? " (Material may still be locked — check the shader name; locked Poiyomi strips unused properties.)"
                    : string.Empty;
                result.message = "Poiyomi material is missing expected detail-normal " +
                    "properties (_DetailEnabled / _DetailNormalMap / _DetailNormalMapScale). " +
                    "Shader may be a non-detail Poiyomi variant." + extra +
                    (string.IsNullOrEmpty(unlockWarning) ? "" : " — " + unlockWarning);
                return result;
            }

            var existing = material.GetTexture(PropDetailNormalMap);
            if (existing != null && existing != priorBakedTexture && existing != bakedTexture)
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Overwrite existing detail normal?",
                    $"Material '{material.name}' already has a detail normal texture:\n\n" +
                    $"  {existing.name}\n\n" +
                    "Continue and replace it with the baked normal map?",
                    "Overwrite", "Cancel");
                if (!proceed)
                {
                    result.outcome = ApplyOutcome.CancelledByUser;
                    result.message = "User cancelled overwrite.";
                    return result;
                }
            }

            Undo.RecordObject(material, "SPS Normal Map Bake");

            material.SetTexture(PropDetailNormalMap, bakedTexture);
            material.SetFloat(PropDetailEnabled, 1f);
            material.SetFloat(PropDetailNormalMapScale, 0f);

            // Thry tags the property as "animated when locked" with key
            // "{propertyName}Animated" = "1".
            material.SetOverrideTag(PropDetailNormalMapScale + "Animated", "1");

            EditorUtility.SetDirty(material);

            result.outcome = ApplyOutcome.Applied;
            if (!string.IsNullOrEmpty(unlockWarning))
                result.message = unlockWarning;
            return result;
        }

        /// <summary>
        /// Calls Thry.ShaderOptimizer.SetLockedForAllMaterials via reflection.
        /// Returns false (with message) when Thry is missing or the call fails;
        /// user must unlock manually in that case.
        /// </summary>
        private static bool TryUnlockViaThry(Material material, out string warning)
        {
            warning = null;

            var thryType = FindType("Thry.ShaderOptimizer");
            if (thryType == null)
            {
                warning = "Thry not detected. Locked Poiyomi materials can't be " +
                    "unlocked automatically — install Thry (ships with Poiyomi) or " +
                    "unlock this material manually before baking.";
                return false;
            }

            var method = FindBestLockMethod(thryType);
            if (method == null)
            {
                warning = "Thry.ShaderOptimizer.SetLockedForAllMaterials not found — " +
                    "Thry version may be incompatible. Unlock this material manually.";
                return false;
            }

            try
            {
                var parameters = method.GetParameters();
                var args = new object[parameters.Length];
                args[0] = new[] { material };
                args[1] = 0;

                for (int i = 2; i < parameters.Length; i++)
                    args[i] = parameters[i].ParameterType == typeof(bool) ? (object)false : null;

                method.Invoke(null, args);
                return true;
            }
            catch (Exception e)
            {
                warning = $"Thry unlock threw an exception: {e.Message}";
                return false;
            }
        }

        /// <summary>
        /// Picks the SetLockedForAllMaterials overload whose first param accepts
        /// Material[] (covers IEnumerable, IList, etc.) and whose second param
        /// is int or bool. Prefers shorter signatures when multiple match.
        /// </summary>
        private static MethodInfo FindBestLockMethod(Type thryType)
        {
            MethodInfo best = null;
            foreach (var m in thryType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "SetLockedForAllMaterials") continue;
                var p = m.GetParameters();
                if (p.Length < 2) continue;
                if (!p[0].ParameterType.IsAssignableFrom(typeof(Material[]))) continue;
                if (p[1].ParameterType != typeof(int) && p[1].ParameterType != typeof(bool)) continue;
                if (best == null || p.Length < best.GetParameters().Length)
                    best = m;
            }
            return best;
        }

        /// <summary>
        /// Resolves a type by full name, then by simple name across all loaded
        /// assemblies. Tolerates ReflectionTypeLoadException from dynamic or
        /// partially-loaded assemblies.
        /// </summary>
        private static Type FindType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (t != null) return t;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch (TypeLoadException) { /* dynamic / partial assembly */ }
                catch (System.IO.FileNotFoundException) { /* missing referenced asm */ }
            }

            string simpleName = fullName.Contains(".")
                ? fullName.Substring(fullName.LastIndexOf('.') + 1)
                : fullName;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }

                foreach (var type in types)
                {
                    if (type != null && type.Name == simpleName) return type;
                }
            }
            return null;
        }

        /// <summary>One (renderer, material slot) pair animated by the baker's clip.</summary>
        public struct MaterialAnimationTarget
        {
            public string rendererPath;
            public int materialIndex;
        }

        /// <summary>
        /// Creates the baker's AnimationClip + AnimatorController + a
        /// VRCFury FullController GameObject that drives <c>_DetailNormalMapScale</c>
        /// on each target using the given reference curve (rescaled to peakScale).
        /// Overwrites existing assets at the output paths on re-bake.
        /// </summary>
        public static GameObject CreateAnimationAndController(
            GameObject avatarRoot,
            string outputFolder,
            string configName,
            string depthParameter,
            AnimationCurve referenceCurve,
            float referenceCurveMaxValue,
            float peakScale,
            List<MaterialAnimationTarget> targets)
        {
            if (avatarRoot == null || string.IsNullOrEmpty(outputFolder)
                || string.IsNullOrEmpty(configName) || targets == null
                || targets.Count == 0)
                return null;

            // Sanitize before embedding in asset paths. configName flows straight
            // from user input; names with '/', ':', '*', etc. would cause
            // AssetDatabase.CreateAsset to fail or place assets at unexpected
            // paths. Every other writer in the package routes through this same
            // helper; this site was the outlier.
            string safeConfigName = BaseEffectConfig.SanitizeFileName(configName);

            string normalMapFolder = $"{outputFolder}/NormalMap";
            SpsAnimationUtility.EnsureFolder(normalMapFolder);

            var driveCurve = RescaleCurve(referenceCurve, referenceCurveMaxValue, peakScale);

            var clip = new AnimationClip { name = $"{safeConfigName}_DetailNormal" };
            foreach (var target in targets)
            {
                string propertyName = target.materialIndex <= 0
                    ? $"material.{PropDetailNormalMapScale}"
                    : $"material.{PropDetailNormalMapScale}_{target.materialIndex}";

                clip.SetCurve(target.rendererPath, typeof(SkinnedMeshRenderer), propertyName, driveCurve);
            }

            // Delete existing assets before writing so re-bakes start fresh.
            string clipPath = $"{normalMapFolder}/{safeConfigName}_DetailNormal.anim";
            string restClipPath = $"{normalMapFolder}/{safeConfigName}_DetailNormalRest.anim";
            string controllerPath = $"{normalMapFolder}/{safeConfigName}_DetailNormal.controller";
            foreach (var p in new[] { clipPath, restClipPath, controllerPath })
            {
                if (AssetDatabase.LoadMainAssetAtPath(p) != null)
                    AssetDatabase.DeleteAsset(p);
            }

            AssetDatabase.CreateAsset(clip, clipPath);

            // Empty rest clip anchors threshold 0 of the blend tree.
            var restClip = new AnimationClip { name = $"{safeConfigName}_DetailNormalRest" };
            AssetDatabase.CreateAsset(restClip, restClipPath);

            var entries = new List<(float threshold, AnimationClip clip)>
            {
                (0f, restClip),
                (1f, clip),
            };

            var controller = BlendTreeBuilder.CreateBlendTree(
                $"{safeConfigName}_DetailNormal",
                depthParameter,
                // Display label stays unsanitized - raw configName is fine in
                // the blend-tree name since it isn't used as a path.
                $"{configName} Detail Normal",
                entries,
                controllerPath);

            // GO name is a scene-hierarchy display label, not a path; keep the
            // raw configName so users see their literal name in the hierarchy.
            string goName = $"[Bulge NormalMap - {configName}]";
            var go = VRCFuryIntegration.CreateFullController(
                avatarRoot, goName, controller, new[] { depthParameter });

            AssetDatabase.SaveAssets();
            return go;
        }

        /// <summary>Returns a copy of <paramref name="source"/> with values rescaled so its peak = <paramref name="targetPeak"/>.</summary>
        private static AnimationCurve RescaleCurve(
            AnimationCurve source, float sourceMaxValue, float targetPeak)
        {
            if (source == null || source.length == 0)
                return AnimationCurve.Linear(0f, 0f, 1f, targetPeak);

            float sourcePeak = sourceMaxValue;
            if (sourcePeak <= 0f)
            {
                for (int i = 0; i < source.length; i++)
                    if (source[i].value > sourcePeak) sourcePeak = source[i].value;
            }
            if (sourcePeak <= 0f) return AnimationCurve.Linear(0f, 0f, 1f, targetPeak);

            float scale = targetPeak / sourcePeak;
            var keys = source.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i].value *= scale;
                keys[i].inTangent *= scale;
                keys[i].outTangent *= scale;
            }
            return new AnimationCurve(keys);
        }

        /// <summary>Reads a blendshape curve from an existing clip, or null if not bound.</summary>
        public static AnimationCurve ReadBlendshapeCurve(
            string clipAssetPath, string rendererPath, string blendshapeName)
        {
            if (string.IsNullOrEmpty(clipAssetPath)) return null;
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipAssetPath);
            if (clip == null) return null;

            var binding = EditorCurveBinding.FloatCurve(
                rendererPath, typeof(SkinnedMeshRenderer),
                $"blendShape.{blendshapeName}");
            return AnimationUtility.GetEditorCurve(clip, binding);
        }
    }
}
