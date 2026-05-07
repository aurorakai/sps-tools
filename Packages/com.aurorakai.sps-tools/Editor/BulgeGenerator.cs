using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Maps BulgeConfig to blend tree threshold lists with bell curve weights.
    /// </summary>
    public static class BulgeGenerator
    {
        // Treat rangeEnd within this distance of 1.0 as "reaches full depth" and
        // skip the synthetic 1.0 entry that holds the peak position.
        private const float FullRangeEpsilon = 0.001f;

        /// <summary>
        /// Generates all assets for a Bulge configuration. Returns the controller path.
        /// </summary>
        public static string Generate(BulgeConfig config)
        {
            return Generate(config, new List<string> { config.depthParameter });
        }

        public static string Generate(BulgeConfig config, List<string> depthParameters)
        {
            string folder = SpsAnimationUtility.CreateOutputFolder(
                config.GetOutputFolder());

            var positionIds = GetPositionIdentifiers(config);
            var restClip = CreateRestClip(config, positionIds);
            SpsAnimationUtility.SaveClip(restClip, folder, "SPSBulge_Rest");

            var entries = BuildThresholdEntries(config, positionIds, restClip);

            int clipNum = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].clip != restClip)
                {
                    clipNum++;
                    SpsAnimationUtility.SaveClip(entries[i].clip, folder,
                        $"SPSBulge_Pos{clipNum}");
                }
            }

            string controllerPath = $"{folder}/SPSBulge_Controller.controller";
            BlendTreeBuilder.CreateMultiBlendTree(
                "SPS Bulge Blend",
                depthParameters,
                "SPS Bulge Effect",
                entries,
                controllerPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return controllerPath;
        }

        /// <summary>
        /// Builds the threshold list with temporary in-memory clips for scene preview.
        /// </summary>
        public static List<(float threshold, AnimationClip clip)> BuildPreviewThresholds(BulgeConfig config)
        {
            var positionIds = GetPositionIdentifiers(config);
            var restClip = CreateRestClip(config, positionIds);
            return BuildThresholdEntries(config, positionIds, restClip);
        }

        /// <summary>
        /// Builds the blend tree threshold entries with canonical 0–1 thresholds.
        ///
        /// The bulge follows the penetrating tip:
        ///   - depth 0 → rest (nothing inside)
        ///   - depth increases → bulge appears at entrance, travels inward
        ///   - depth 100% → bulge at deepest position (PEAK, not rest)
        ///   - depth decreases → bulge travels back out (blend tree is symmetric)
        ///
        /// Ramp-in from rest to the first position is smooth.
        /// No ramp-out at the end - the bulge stays at the deepest position
        /// at maximum depth, which is physically correct.
        ///
        /// Layout for 3 positions over range 0.0–1.0:
        ///   0.00 rest
        ///   0.00 rest (range start)
        ///   0.17 pos1 (entrance - ramp in from rest)
        ///   0.50 pos2 (middle)
        ///   0.83 pos3 (deepest)
        ///   1.00 pos3 (stays at deepest at max depth)
        /// </summary>
        private static List<(float threshold, AnimationClip clip)> BuildThresholdEntries(
            BulgeConfig config, List<string> positionIds, AnimationClip restClip)
        {
            int posCount = config.PositionCount;
            float rangeStart = config.depthRangeStart;
            float rangeEnd = config.depthRangeEnd;
            float rangeSize = rangeEnd - rangeStart;

            // Ramp-in margin: space before the first position for a smooth entry
            float rampMargin = posCount > 1
                ? rangeSize / (posCount - 1) * 0.5f
                : rangeSize * 0.25f;

            var entries = new List<(float threshold, AnimationClip clip)>();

            // Rest before range
            entries.Add((0f, restClip));
            if (rangeStart > FullRangeEpsilon)
                entries.Add((rangeStart, restClip));

            // First position is inset from range start (smooth ramp-in from rest)
            // Last position extends to range end (peak stays at max depth)
            float innerStart = rangeStart + rampMargin;

            for (int pos = 0; pos < posCount; pos++)
            {
                float t;
                if (posCount > 1)
                    t = innerStart + pos * (rangeEnd - innerStart) / (posCount - 1);
                else
                    t = (rangeStart + rangeEnd) * 0.5f;

                var clip = CreatePositionClip(config, positionIds, pos);
                entries.Add((t, clip));
            }

            // Hold the last position to depth 1.0 when user-chosen rangeEnd < 1.0.
            if (rangeEnd < 1f - FullRangeEpsilon)
            {
                var lastClip = CreatePositionClip(config, positionIds, posCount - 1);
                entries.Add((1f, lastClip));
            }

            return entries;
        }

        /// <summary>
        /// Creates a clip for when the bulge is centered at the given position.
        /// All positions get bell-curve-weighted values.
        /// </summary>
        private static AnimationClip CreatePositionClip(
            BulgeConfig config, List<string> positionIds, int centerPos)
        {
            if (config.EffectiveDeformationMode == DeformationMode.BoneScale)
            {
                var boneScales = new List<(string bonePath, Vector3 scale)>();

                for (int i = 0; i < positionIds.Count; i++)
                {
                    int offset = Mathf.Abs(i - centerPos);
                    float weight = config.GetBellCurveWeight(offset);

                    float scaleValue = 1f + (config.bulgeIntensity) * weight;
                    float sx = config.scaleX ? scaleValue : 1f;
                    float sy = config.scaleY ? scaleValue : 1f;
                    float sz = config.scaleZ ? scaleValue : 1f;
                    boneScales.Add((positionIds[i], new Vector3(sx, sy, sz)));
                }

                return SpsAnimationUtility.CreateMultiBoneScaleClip(
                    boneScales, config.scaleX, config.scaleY, config.scaleZ);
            }
            else
            {
                // Intensity scales the blendshape weight: 0=off, 0.5=50%, 1.0=100%, 2.0=overdrive
                float intensityScale = config.bulgeIntensity;

                var blendshapeWeights = new List<(string blendshapeName, float weight)>();

                for (int i = 0; i < positionIds.Count; i++)
                {
                    int offset = Mathf.Abs(i - centerPos);
                    float bsWeight = config.GetBellCurveWeight(offset) * 100f * intensityScale;
                    blendshapeWeights.Add((positionIds[i], Mathf.Max(0f, bsWeight)));
                }

                return SpsAnimationUtility.CreateMultiBlendshapeClip(
                    GetAllRendererPaths(config), blendshapeWeights);
            }
        }

        private static AnimationClip CreateRestClip(BulgeConfig config, List<string> positionIds)
        {
            if (config.EffectiveDeformationMode == DeformationMode.BoneScale)
            {
                var boneScales = new List<(string bonePath, Vector3 scale)>();
                foreach (var id in positionIds)
                    boneScales.Add((id, Vector3.one));
                return SpsAnimationUtility.CreateMultiBoneScaleClip(
                    boneScales, config.scaleX, config.scaleY, config.scaleZ);
            }
            else
            {
                return SpsAnimationUtility.CreateRestClip(
                    GetAllRendererPaths(config), positionIds);
            }
        }

        /// <summary>
        /// Returns all renderer paths that should receive blendshape curves -
        /// primary mesh + any additional overlay meshes.
        /// </summary>
        private static List<string> GetAllRendererPaths(BulgeConfig config)
        {
            var paths = new List<string> { config.rendererPath };
            if (config.additionalMeshes != null)
            {
                foreach (var m in config.additionalMeshes)
                {
                    if (!string.IsNullOrEmpty(m.rendererPath) && !paths.Contains(m.rendererPath))
                        paths.Add(m.rendererPath);
                }
            }
            return paths;
        }

        private static List<string> GetPositionIdentifiers(BulgeConfig config)
        {
            if (config.EffectiveDeformationMode == DeformationMode.BoneScale)
                return new List<string>(config.boneChain);
            if (config.positionBlendshapes.Count > 0)
                return new List<string>(config.positionBlendshapes);

            // Auto-generated blendshape names using config's naming pattern
            var names = new List<string>();
            for (int i = 0; i < config.autoPositionCount; i++)
                names.Add(config.GetBlendshapeName(i + 1, "SPSBulge_Pos"));
            return names;
        }
    }
}
