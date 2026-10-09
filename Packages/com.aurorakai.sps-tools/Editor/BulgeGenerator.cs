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
        /// Default name of a position's blendshape and clip, followed by its
        /// 1-based index (e.g. "SPSBulge_Pos1").
        /// </summary>
        internal const string PositionPrefix = "SPSBulge_Pos";

        /// <summary>
        /// Asset path of the clip <see cref="Generate"/> saves for the 0-based
        /// position <paramref name="pos"/>.
        /// </summary>
        internal static string GetPositionClipPath(BulgeConfig config, int pos) =>
            $"{config.GetOutputFolder()}/{PositionPrefix}{pos + 1}.anim";

        /// <summary>
        /// Generates the controller with one blend tree layer per depth
        /// parameter. Parameters with an entry in <paramref name="guidedPaths"/>
        /// place each position at the depth where the plug tip reaches it along
        /// that socket's SPS2 guided path; the rest spread the positions evenly
        /// over the depth range.
        /// </summary>
        public static string Generate(BulgeConfig config, List<string> depthParameters,
            IDictionary<string, GuidedPathDepthMap> guidedPaths = null)
        {
            string folder = SpsAnimationUtility.CreateOutputFolder(
                config.GetOutputFolder());

            var positionIds = GetPositionIdentifiers(config);
            var restClip = CreateRestClip(config, positionIds);
            SpsAnimationUtility.SaveClip(restClip, folder, "SPSBulge_Rest");

            var positionClips = new AnimationClip[config.PositionCount];
            var layers = new List<(string parameter, List<(float threshold, AnimationClip clip)> entries)>();
            foreach (string parameter in depthParameters)
            {
                GuidedPathDepthMap guidedPath = null;
                guidedPaths?.TryGetValue(parameter, out guidedPath);
                var layout = ComputeDepthLayout(config, guidedPath);
                layers.Add((parameter, BuildThresholdEntries(
                    config, positionIds, restClip, layout, positionClips)));
            }

            // Positions the plug tip can't reach on any socket have no clip.
            for (int pos = 0; pos < positionClips.Length; pos++)
            {
                if (positionClips[pos] != null)
                    SpsAnimationUtility.SaveClip(positionClips[pos], folder,
                        $"{PositionPrefix}{pos + 1}");
            }

            string controllerPath = $"{folder}/SPSBulge_Controller.controller";
            BlendTreeBuilder.CreateLayeredBlendTree(
                "SPS Bulge Blend",
                layers,
                "SPS Bulge Effect",
                controllerPath);

            AssetDatabase.SaveAssets();
            return controllerPath;
        }

        /// <summary>
        /// Builds the threshold list with temporary in-memory clips for scene preview.
        /// </summary>
        public static List<(float threshold, AnimationClip clip)> BuildPreviewThresholds(
            BulgeConfig config, GuidedPathDepthMap guidedPath = null)
        {
            var positionIds = GetPositionIdentifiers(config);
            var restClip = CreateRestClip(config, positionIds);
            return BuildThresholdEntries(config, positionIds, restClip,
                ComputeDepthLayout(config, guidedPath),
                new AnimationClip[config.PositionCount]);
        }

        /// <summary>
        /// Builds the blend tree threshold entries for one depth parameter.
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
        /// Layout for 3 evenly spread positions over range 0.0–1.0:
        ///   0.00 rest
        ///   0.00 rest (range start)
        ///   0.17 pos1 (entrance - ramp in from rest)
        ///   0.50 pos2 (middle)
        ///   0.83 pos3 (deepest)
        ///   1.00 pos3 (stays at deepest at max depth)
        ///
        /// Position clips are created on first use and cached in
        /// <paramref name="positionClips"/>, so layers share them.
        /// </summary>
        private static List<(float threshold, AnimationClip clip)> BuildThresholdEntries(
            BulgeConfig config, List<string> positionIds, AnimationClip restClip,
            BulgeDepthLayout layout, AnimationClip[] positionClips)
        {
            var entries = new List<(float threshold, AnimationClip clip)>();

            // Rest before range
            entries.Add((0f, restClip));
            if (layout.restUntil > FullRangeEpsilon)
                entries.Add((layout.restUntil, restClip));

            AnimationClip lastClip = null;
            float lastDepth = 0f;
            foreach (var (depth, pos) in layout.stops)
            {
                if (positionClips[pos] == null)
                    positionClips[pos] = CreatePositionClip(config, positionIds, pos);
                entries.Add((depth, positionClips[pos]));
                lastClip = positionClips[pos];
                lastDepth = depth;
            }

            // Hold the deepest position through range end, and keep holding it
            // to depth 1.0 when the range ends early.
            if (lastClip != null && layout.holdFrom > lastDepth + FullRangeEpsilon)
                entries.Add((layout.holdFrom, lastClip));
            if (lastClip != null && layout.holdFrom < 1f - FullRangeEpsilon)
                entries.Add((1f, lastClip));

            return entries;
        }

        /// <summary>
        /// Works out the depth at which each position is the bulge's centre.
        /// With a guided path, that's the FX Float value when the plug tip
        /// reaches the point on the path closest to the position; otherwise the
        /// positions are spread evenly over the depth range. Falls back to even
        /// spacing when the positions' locations aren't known (manual
        /// blendshapes).
        /// </summary>
        internal static BulgeDepthLayout ComputeDepthLayout(
            BulgeConfig config, GuidedPathDepthMap guidedPath)
        {
            int posCount = config != null ? config.PositionCount : 0;
            if (guidedPath != null)
            {
                var centres = GetPositionWorldCentres(config);
                if (centres != null && centres.Count == posCount && posCount > 0)
                {
                    var depths = new List<float>(posCount);
                    foreach (var centre in centres)
                        depths.Add(guidedPath.ValueAt(centre));
                    return BuildGuidedLayout(depths, guidedPath);
                }
            }

            return BuildEvenLayout(config, posCount);
        }

        /// <summary>
        /// <paramref name="posCount"/> positions spread evenly over the
        /// config's depth range.
        /// </summary>
        internal static BulgeDepthLayout BuildEvenLayout(BulgeConfig config, int posCount)
        {
            var layout = new BulgeDepthLayout();
            if (config == null) return layout;
            layout.restUntil = config.depthRangeStart;
            layout.holdFrom = config.depthRangeEnd;
            var evenDepths = ComputePositionDepths(config, posCount);
            for (int pos = 0; pos < evenDepths.Count; pos++)
                layout.stops.Add((evenDepths[pos], pos));
            return layout;
        }

        /// <summary>
        /// Orders positions by the depth the plug tip reaches them at. The
        /// path may be drawn from either end, so positions are walked from the
        /// end nearer the entrance. A position the tip only reaches at or before
        /// an earlier one (or at depth 0, where nothing is inside) can't be
        /// shown on its own and is skipped.
        /// </summary>
        internal static BulgeDepthLayout BuildGuidedLayout(
            List<float> depths, GuidedPathDepthMap guidedPath = null)
        {
            var layout = new BulgeDepthLayout { guidedPath = guidedPath, holdFrom = 1f };
            int count = depths.Count;
            bool reversed = count > 1 && depths[count - 1] < depths[0];

            float previous = 0f;
            for (int i = 0; i < count; i++)
            {
                int pos = reversed ? count - 1 - i : i;
                if (depths[pos] <= previous + FullRangeEpsilon)
                {
                    layout.skippedPositions++;
                    continue;
                }
                layout.stops.Add((depths[pos], pos));
                previous = depths[pos];
            }

            // Ramp in from rest over half the gap to the next position, like
            // the evenly spread layout does.
            if (layout.stops.Count > 0)
            {
                float first = layout.stops[0].depth;
                float gap = layout.stops.Count > 1 ? layout.stops[1].depth - first : first;
                layout.restUntil = Mathf.Max(0f, first - gap * 0.5f);
            }
            return layout;
        }

        /// <summary>
        /// World-space centre of each position: the bone for bone chains, or
        /// the point on the drawn path the generated blendshape is centred on.
        /// Null when the positions are manual blendshapes, whose location
        /// isn't known. Generating stores the generated names as the position
        /// blendshapes, so names that match the drawn path's position count are
        /// taken to be generated along it.
        /// </summary>
        internal static List<Vector3> GetPositionWorldCentres(BulgeConfig config)
        {
            if (config == null || config.avatarRoot == null) return null;
            var root = config.avatarRoot.transform;
            var centres = new List<Vector3>();

            if (config.EffectiveDeformationMode == DeformationMode.BoneScale)
            {
                if (config.boneChain == null) return null;
                foreach (string bonePath in config.boneChain)
                {
                    var bone = root.Find(bonePath);
                    if (bone == null) return null;
                    centres.Add(bone.position);
                }
                return centres;
            }

            if (config.pathWaypoints == null || config.pathWaypoints.Count < 2)
                return null;
            if (config.positionBlendshapes != null && config.positionBlendshapes.Count > 0 &&
                config.positionBlendshapes.Count != config.autoPositionCount)
                return null;

            int count = config.autoPositionCount;
            for (int pos = 0; pos < count; pos++)
            {
                CatmullRomSpline.EvaluateWithAttributes(config.pathWaypoints,
                    BlendshapeGenerator.PositionT(pos, count),
                    out Vector3 local, out _, out _);
                centres.Add(root.TransformPoint(local));
            }
            return centres;
        }

        internal static List<float> ComputePositionDepths(BulgeConfig config, int posCount)
        {
            var depths = new List<float>();
            if (config == null || posCount <= 0) return depths;

            float rangeStart = config.depthRangeStart;
            float rangeEnd = config.depthRangeEnd;
            float rangeSize = rangeEnd - rangeStart;
            float step = rangeSize / posCount;
            float firstDepth = rangeStart + step * 0.5f;

            for (int pos = 0; pos < posCount; pos++)
                depths.Add(firstDepth + pos * step);

            return depths;
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
                var blendshapeWeights = new List<(string blendshapeName, float weight)>();
                for (int i = 0; i < positionIds.Count; i++)
                    blendshapeWeights.Add((positionIds[i], config.GetPositionWeight(i, centerPos)));

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

        internal static List<string> GetPositionIdentifiers(BulgeConfig config)
        {
            if (config == null)
                return new List<string>();

            if (config.EffectiveDeformationMode == DeformationMode.BoneScale)
                return config.boneChain != null
                    ? new List<string>(config.boneChain)
                    : new List<string>();
            if (config.positionBlendshapes != null && config.positionBlendshapes.Count > 0)
                return new List<string>(config.positionBlendshapes);

            // Auto-generated blendshape names using config's naming pattern
            var names = new List<string>();
            for (int i = 0; i < Mathf.Max(0, config.autoPositionCount); i++)
                names.Add(config.GetBlendshapeName(i + 1, PositionPrefix));
            return names;
        }
    }

    /// <summary>
    /// The depths at which a depth parameter centres the bulge on each
    /// position, in the order the plug tip reaches them.
    /// </summary>
    public class BulgeDepthLayout
    {
        /// <summary>Depth up to which the bulge stays at rest.</summary>
        public float restUntil;

        /// <summary>Depth and position index, by increasing depth.</summary>
        public List<(float depth, int position)> stops = new List<(float depth, int position)>();

        /// <summary>Depth from which the deepest position is held to 1.0.</summary>
        public float holdFrom = 1f;

        /// <summary>Set when the depths follow a socket's SPS2 guided path.</summary>
        public GuidedPathDepthMap guidedPath;

        /// <summary>Positions the plug tip doesn't reach on their own.</summary>
        public int skippedPositions;
    }
}
