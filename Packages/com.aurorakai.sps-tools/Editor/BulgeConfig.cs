using System;
using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    [CreateAssetMenu(fileName = "SPSBulge_Config", menuName = "SPS Effects/Bulge Config")]
    public class BulgeConfig : BaseEffectConfig
    {
        public override string EffectTypeName => "Bulge";


        // Travel range (0-1 matches SPS socket output)
        [Range(0f, 1f)] public float depthRangeStart = 0f;
        [Range(0f, 1f)] public float depthRangeEnd = 1f;

        // Bulge shape
        public float bulgeIntensity = 1f;      // 0-2 (shown as 0-200%)
        [Range(1, 5)] public int bulgeWidth = 3;

        // Bone chain mode
        public List<string> boneChain = new List<string>();

        // Blendshape mode (manual entry)
        public List<string> positionBlendshapes = new List<string>();

        // Blendshape mode (auto-generate)
        public int autoPositionCount = 5;

        [Tooltip("Maximum world-space distance (m) for an overlay vertex to inherit deltas from a primary vertex. Default 0.02 (2 cm) is appropriate for meter-scale avatars.")]
        [Range(0.001f, 0.2f)]
        public float overlayMatchDistance = 0.02f;

        /// <summary>
        /// Returns the number of positions based on the current deformation mode.
        /// </summary>
        public int PositionCount
        {
            get
            {
                if (EffectiveDeformationMode == DeformationMode.BoneScale)
                    return boneChain.Count;
                if (positionBlendshapes.Count > 0)
                    return positionBlendshapes.Count;
                return autoPositionCount;
            }
        }

        public override bool IsValid()
        {
            if (avatarRoot == null) return false;
            if (string.IsNullOrEmpty(depthParameter)) return false;
            if (depthRangeStart >= depthRangeEnd) return false;
            if (bulgeIntensity <= 0f) return false;
            if (bulgeWidth < 1 || bulgeWidth > 5) return false;

            if (EffectiveDeformationMode == DeformationMode.BoneScale)
            {
                if (boneChain.Count < 2) return false;
                if (!scaleX && !scaleY && !scaleZ) return false;
            }
            else
            {
                if (string.IsNullOrEmpty(rendererPath)) return false;
                bool hasManual = positionBlendshapes.Count >= 2;
                bool hasAutoPath = pathWaypoints != null && pathWaypoints.Count >= 2
                    && autoPositionCount >= 2;
                if (!hasManual && !hasAutoPath) return false;
            }

            return true;
        }

        /// <summary>
        /// Calculates the gaussian bell curve weight for a given offset from center.
        /// </summary>
        public float GetBellCurveWeight(int offset)
        {
            if (offset == 0) return 1f;
            float sigma = bulgeWidth / 3f;
            return Mathf.Exp(-(offset * offset) / (2f * sigma * sigma));
        }
    }
}
