using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    public static class BlendTreeBuilder
    {
        /// <summary>
        /// Creates a controller with a single blend tree layer.
        /// </summary>
        public static AnimatorController CreateBlendTree(
            string controllerName,
            string parameterName,
            string layerName,
            List<(float threshold, AnimationClip clip)> entries,
            string savePath)
        {
            var layers = new List<(string parameter, List<(float threshold, AnimationClip clip)> entries)>
            {
                (parameterName, entries)
            };
            return CreateLayeredBlendTree(controllerName, layers, layerName, savePath);
        }

        /// <summary>
        /// Creates a controller with one blend tree layer per parameter, each
        /// with its own thresholds. Used when sockets reach the same positions
        /// at different depths, such as sockets with SPS2 guided paths.
        /// </summary>
        public static AnimatorController CreateLayeredBlendTree(
            string controllerName,
            List<(string parameter, List<(float threshold, AnimationClip clip)> entries)> layerEntries,
            string layerName,
            string savePath)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(savePath);

            var parameterNames = new List<string>();
            var sortedLayerEntries = new List<List<(float threshold, AnimationClip clip)>>();
            foreach (var (parameter, entries) in layerEntries)
            {
                var sortedEntries = new List<(float threshold, AnimationClip clip)>(entries);
                sortedEntries.Sort((a, b) => a.threshold.CompareTo(b.threshold));
                DedupAdjacentThresholds(sortedEntries);
                parameterNames.Add(parameter);
                sortedLayerEntries.Add(sortedEntries);
            }

            // Add all parameters
            foreach (var paramName in parameterNames)
            {
                controller.AddParameter(new AnimatorControllerParameter
                {
                    name = paramName,
                    type = AnimatorControllerParameterType.Float,
                    defaultFloat = 0f
                });
            }

            // Create one layer per parameter
            for (int layerIdx = 0; layerIdx < parameterNames.Count; layerIdx++)
            {
                string paramName = parameterNames[layerIdx];
                string lName = parameterNames.Count > 1
                    ? $"{layerName} ({paramName})"
                    : layerName;

                if (layerIdx == 0)
                {
                    // Use the default layer
                    var layers = controller.layers;
                    layers[0].name = lName;
                    layers[0].defaultWeight = 1f;
                    controller.layers = layers;
                }
                else
                {
                    // Add a new layer - Additive blending so a resting layer
                    // (all blendshapes 0) doesn't override the active layer
                    controller.AddLayer(lName);
                    var layers = controller.layers;
                    layers[layerIdx].defaultWeight = 1f;
                    layers[layerIdx].blendingMode = AnimatorLayerBlendingMode.Additive;
                    controller.layers = layers;
                }

                // Clear default states
                var sm = controller.layers[layerIdx].stateMachine;
                foreach (var s in sm.states)
                    sm.RemoveState(s.state);

                // Create blend tree
                BlendTree blendTree;
                var blendState = controller.CreateBlendTreeInController(
                    controllerName, out blendTree, layerIdx);
                blendTree.blendParameter = paramName;
                blendTree.blendType = BlendTreeType.Simple1D;
                blendTree.useAutomaticThresholds = false;

                blendState.writeDefaultValues = true;

                foreach (var (threshold, clip) in sortedLayerEntries[layerIdx])
                    blendTree.AddChild(clip, threshold);

                sm.defaultState = blendState;
            }

            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>
        /// Removes adjacent duplicate-threshold entries in-place. Input must be
        /// pre-sorted by threshold.
        /// </summary>
        private static void DedupAdjacentThresholds(List<(float threshold, AnimationClip clip)> entries)
        {
            for (int i = entries.Count - 1; i > 0; i--)
            {
                if (Mathf.Approximately(entries[i].threshold, entries[i - 1].threshold))
                    entries.RemoveAt(i);
            }
        }
    }
}
