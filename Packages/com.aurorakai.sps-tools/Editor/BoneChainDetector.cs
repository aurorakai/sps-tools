using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Auto-detects bone chains between a start and end bone for Bulge bone chain mode.
    /// </summary>
    public static class BoneChainDetector
    {
        /// <summary>
        /// Finds the bone chain from startBone down to endBone by walking the hierarchy.
        /// endBone must be a descendant of startBone.
        /// Returns null if no direct chain exists.
        /// </summary>
        public static List<Transform> FindChain(Transform startBone, Transform endBone)
        {
            if (startBone == null || endBone == null) return null;
            if (startBone == endBone) return new List<Transform> { startBone };

            // Walk from endBone up to startBone
            var path = new List<Transform>();
            var current = endBone;

            while (current != null)
            {
                path.Add(current);
                if (current == startBone)
                {
                    path.Reverse();
                    return path;
                }
                current = current.parent;
            }

            return null; // endBone is not a descendant of startBone
        }

        /// <summary>
        /// Finds bones in the avatar that lie along a drawn path.
        /// Returns bones sorted by their position along the path,
        /// filtered to only include bones that form a parent-child chain.
        /// </summary>
        public static List<Transform> FindChainAlongPath(
            Transform avatarRoot, List<PathWaypoint> path, float maxDistance)
        {
            if (avatarRoot == null || path == null || path.Count < 2) return null;

            var allBones = avatarRoot.GetComponentsInChildren<Transform>(true);

            int segments = Mathf.Max(path.Count * 8, 20);
            var tube = CatmullRomSpline.BuildTube(path, segments);

            // Find bones near the path
            var candidates = new List<(Transform bone, float pathT, float dist)>();
            foreach (var bone in allBones)
            {
                // Skip the root itself and non-bone objects (those with renderers)
                if (bone == avatarRoot) continue;
                if (bone.GetComponent<SkinnedMeshRenderer>() != null) continue;
                if (bone.GetComponent<MeshRenderer>() != null) continue;

                Vector3 localPos = avatarRoot.InverseTransformPoint(bone.position);
                float dist = tube.DistanceToTube(localPos, out _, out float pathT);

                if (dist < maxDistance)
                    candidates.Add((bone, pathT, dist));
            }

            if (candidates.Count == 0) return null;

            // Sort by path position
            candidates.Sort((a, b) => a.pathT.CompareTo(b.pathT));

            // Filter to bones that form a connected chain
            // Start with the bone closest to the path, then greedily extend
            var chain = new List<Transform>();
            var used = new HashSet<Transform>();

            // Find the candidate closest to the centerline as the seed
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            var seed = candidates[0].bone;
            candidates.Sort((a, b) => a.pathT.CompareTo(b.pathT));

            // Walk up and down from seed, following parent-child relationships
            // that stay near the path
            var candidateSet = new HashSet<Transform>(candidates.ConvertAll(c => c.bone));

            // Build chain by walking the hierarchy along the path
            Transform current = seed;
            var ordered = new List<Transform>();

            // Walk up to find the start of the chain. Don't break on a non-candidate
            // ancestor — helper bones (twist, constraint helpers) can sit between two
            // valid chain bones; the chain itself only includes the candidates.
            while (current != null && current != avatarRoot)
            {
                if (candidateSet.Contains(current))
                    ordered.Insert(0, current);
                current = current.parent;
            }

            // Walk down from seed through children
            current = seed;
            while (current != null)
            {
                if (!ordered.Contains(current) && candidateSet.Contains(current))
                    ordered.Add(current);

                Transform bestChild = null;
                float bestDist = float.MaxValue;
                foreach (Transform child in current)
                {
                    if (candidateSet.Contains(child) && !ordered.Contains(child))
                    {
                        Vector3 localPos = avatarRoot.InverseTransformPoint(child.position);
                        float dist = tube.DistanceToTube(localPos, out _);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestChild = child;
                        }
                    }
                }
                current = bestChild;
            }

            return ordered.Count >= 2 ? ordered : null;
        }

        /// <summary>
        /// Converts a chain of Transforms to relative paths from the avatar root.
        /// </summary>
        public static List<string> ChainToPaths(Transform avatarRoot, List<Transform> chain)
        {
            var paths = new List<string>(chain.Count);
            foreach (var bone in chain)
                paths.Add(BaseEffectConfig.GetRelativePath(avatarRoot, bone));
            return paths;
        }
    }
}
