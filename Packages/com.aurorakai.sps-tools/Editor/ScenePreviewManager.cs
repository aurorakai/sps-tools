using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Blendshape-driven scene preview. Writes weights directly to renderers
    /// (not via AnimationMode) so the avatar's current pose is preserved -
    /// useful for avatars whose bones aren't in bind pose during editing and
    /// for overlay meshes that aren't skinned to the same skeleton. Snapshots
    /// original weights on start and restores them on StopPreview.
    /// </summary>
    public static class ScenePreviewManager
    {
        public static bool IsPreviewing { get; private set; }
        public static bool IsAutoAnimating => s_isAutoAnimating;
        public static event Action PreviewStopped;

        private static GameObject s_avatarRoot;
        private static List<GameObject> s_enabledObjects = new List<GameObject>();
        private static bool s_isAutoAnimating;
        private static float s_lastSampledDepth = -1f;
        private static float s_autoAnimateTime;
        private static float s_autoAnimateDuration;
        private static List<(float threshold, AnimationClip clip)> s_autoAnimateEntries;
        private static Action<float> s_onDepthChanged;

        // Direct-write preview: snapshot of original blendshape weights so we can
        // restore them on StopPreview without needing AnimationMode (which would
        // reset the avatar's pose to bind pose - breaks visual continuity with
        // non-skinned overlay meshes).
        private struct BlendshapeRef : System.IEquatable<BlendshapeRef>
        {
            public SkinnedMeshRenderer renderer;
            public string name;

            public bool Equals(BlendshapeRef other)
                => renderer == other.renderer && name == other.name;
            public override bool Equals(object obj)
                => obj is BlendshapeRef other && Equals(other);
            public override int GetHashCode()
                => System.HashCode.Combine(renderer != null ? renderer.GetInstanceID() : 0, name);
        }
        private static readonly Dictionary<BlendshapeRef, float> s_originalWeights
            = new Dictionary<BlendshapeRef, float>();

        // The preview entries read once into a table: every blendshape any entry's
        // clip animates, and each entry's value for it. Sampling a depth is then a
        // lerp per blendshape - during auto-animate it runs every editor tick, and
        // reading curves back out of clips there was the main cost.
        private struct PreviewTarget
        {
            public SkinnedMeshRenderer renderer;
            public int blendshapeIndex;
            public string blendshapeName;
        }
        private static List<(float threshold, AnimationClip clip)> s_tableEntries;
        private static readonly List<PreviewTarget> s_targets = new List<PreviewTarget>();
        // Per entry, the value for each target, or NaN where its clip doesn't animate it.
        private static float[][] s_entryValues;

        // One-shot warning set for blendshape names that cannot be resolved on a
        // bound renderer (e.g. the user renamed the shape on the mesh after
        // generation). Avoids flooding the console during auto-animate.
        private static readonly HashSet<(int, string)> s_warnedMissingBlendshapes
            = new HashSet<(int, string)>();

        // Bug Fix 2: track time manually since Time.deltaTime is unreliable in edit mode
        private static double s_lastUpdateTime;

        // Auto-stop on mesh change: remember which mesh was active when preview started
        private static Mesh s_previewStartMesh;
        private static string s_previewRendererPath;
        private static SkinnedMeshRenderer s_previewStartRenderer;

        public static void StartPreview(
            GameObject avatarRoot,
            List<string> targetPaths,
            Vector3 focusPosition)
        {
            if (IsPreviewing) StopPreview();

            // Null avatar hits us after a domain reload that cleared config.avatarRoot
            // between the caller's CanPreview() check and this call. Exit cleanly
            // without setting IsPreviewing - otherwise a later StopPreview would
            // run cleanup paths against partially-initialized state.
            if (avatarRoot == null) return;

            s_avatarRoot = avatarRoot;
            s_enabledObjects.Clear();
            s_originalWeights.Clear();

            IsPreviewing = true;

            // Auto-enable disabled objects along target paths
            foreach (var path in targetPaths)
            {
                var target = avatarRoot.transform.Find(path);
                if (target == null) continue;

                var current = target;
                while (current != null && current != avatarRoot.transform)
                {
                    if (!current.gameObject.activeSelf)
                    {
                        current.gameObject.SetActive(true);
                        s_enabledObjects.Add(current.gameObject);
                    }
                    current = current.parent;
                }
            }

            // Store the current mesh on the first renderer target so we can detect swaps
            s_previewStartMesh = null;
            s_previewRendererPath = null;
            s_previewStartRenderer = null;
            if (targetPaths.Count > 0)
            {
                s_previewRendererPath = targetPaths[0];
                s_previewStartRenderer = BaseEffectConfig.ResolveRenderer(
                    avatarRoot, s_previewRendererPath);
                if (s_previewStartRenderer != null)
                    s_previewStartMesh = s_previewStartRenderer.sharedMesh;
            }

            // Register safety nets (unregister first to prevent double-registration)
            SetSafetyNets(false);
            SetSafetyNets(true);
        }

        public static void SampleAtDepth(
            float depth,
            List<(float threshold, AnimationClip clip)> entries,
            bool force = false)
        {
            if (!IsPreviewing || s_avatarRoot == null) return;

            // Auto-stop if the mesh on the tracked renderer has changed since preview started
            if (s_previewStartMesh != null && s_previewRendererPath != null)
            {
                if (s_previewStartRenderer == null)
                    s_previewStartRenderer = BaseEffectConfig.ResolveRenderer(
                        s_avatarRoot, s_previewRendererPath);
                if (s_previewStartRenderer != null
                    && s_previewStartRenderer.sharedMesh != s_previewStartMesh)
                {
                    StopPreview();
                    return;
                }
            }

            // Skip if depth hasn't changed meaningfully
            if (!force && Mathf.Abs(depth - s_lastSampledDepth) < 0.001f) return;
            s_lastSampledDepth = depth;

            ApplyAtDepth(depth, entries);

            SceneView.RepaintAll();
        }

        /// <summary>
        /// Writes the blendshape weights the entries give at <paramref name="depth"/>,
        /// interpolating between the bracketing thresholds the way Unity's 1D
        /// blend tree does at runtime.
        /// </summary>
        private static void ApplyAtDepth(
            float depth, List<(float threshold, AnimationClip clip)> entries)
        {
            if (entries == null || entries.Count == 0) return;
            if (!ReferenceEquals(entries, s_tableEntries) || s_entryValues.Length != entries.Count)
                BuildPreviewTable(entries);

            // Find bracketing entries
            int lowerIdx = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].threshold <= depth)
                    lowerIdx = i;
                else
                    break;
            }
            int upperIdx = Mathf.Min(lowerIdx + 1, entries.Count - 1);

            // Exact match or same clip: that clip's values. Otherwise lerp every
            // blendshape either clip animates, missing values counting as 0.
            bool blend = lowerIdx != upperIdx && entries[lowerIdx].clip != entries[upperIdx].clip;
            float t = 0f;
            if (blend)
            {
                float range = entries[upperIdx].threshold - entries[lowerIdx].threshold;
                t = range > 0.0001f
                    ? Mathf.Clamp01((depth - entries[lowerIdx].threshold) / range)
                    : 0f;
            }

            var lower = s_entryValues[lowerIdx];
            var upper = s_entryValues[upperIdx];
            for (int k = 0; k < s_targets.Count; k++)
            {
                float lowerVal = lower[k];
                float upperVal = upper[k];
                if (!blend)
                {
                    if (!float.IsNaN(lowerVal)) ApplyWeight(k, lowerVal);
                }
                else if (!float.IsNaN(lowerVal) || !float.IsNaN(upperVal))
                {
                    ApplyWeight(k, Mathf.Lerp(
                        float.IsNaN(lowerVal) ? 0f : lowerVal,
                        float.IsNaN(upperVal) ? 0f : upperVal, t));
                }
            }
        }

        /// <summary>
        /// Reads every entry's clip into <see cref="s_targets"/> and
        /// <see cref="s_entryValues"/>. Blendshapes that can't be found on the
        /// avatar are left out.
        /// </summary>
        private static void BuildPreviewTable(List<(float threshold, AnimationClip clip)> entries)
        {
            s_tableEntries = entries;
            s_targets.Clear();

            var targetIndex = new Dictionary<(string path, string name), int>();
            var valuesByClip = new Dictionary<AnimationClip, Dictionary<int, float>>();
            foreach (var (_, clip) in entries)
            {
                if (clip == null || valuesByClip.ContainsKey(clip)) continue;

                var values = new Dictionary<int, float>();
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer)) continue;
                    if (!binding.propertyName.StartsWith(SpsAnimationUtility.BlendshapePropertyPrefix)) continue;

                    string bsName = binding.propertyName.Substring(
                        SpsAnimationUtility.BlendshapePropertyPrefix.Length);
                    if (!targetIndex.TryGetValue((binding.path, bsName), out int k))
                    {
                        k = AddTarget(binding.path, bsName);
                        targetIndex[(binding.path, bsName)] = k;
                    }
                    if (k < 0) continue;

                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve != null)
                        values[k] = curve.Evaluate(0f);
                }
                valuesByClip[clip] = values;
            }

            s_entryValues = new float[entries.Count][];
            for (int e = 0; e < entries.Count; e++)
            {
                var row = new float[s_targets.Count];
                for (int k = 0; k < row.Length; k++)
                    row[k] = float.NaN;
                var clip = entries[e].clip;
                if (clip != null)
                {
                    foreach (var kv in valuesByClip[clip])
                        row[kv.Key] = kv.Value;
                }
                s_entryValues[e] = row;
            }
        }

        /// <summary>
        /// Finds the blendshape a clip binding drives and adds it as a target.
        /// Returns its index, or -1 if the renderer or blendshape isn't there.
        /// </summary>
        private static int AddTarget(string path, string blendshapeName)
        {
            var t = path == ""
                ? s_avatarRoot.transform
                : s_avatarRoot.transform.Find(path);
            if (t == null) return -1;

            var r = t.GetComponent<SkinnedMeshRenderer>();
            if (r == null || r.sharedMesh == null) return -1;

            int idx = r.sharedMesh.GetBlendShapeIndex(blendshapeName);
            if (idx < 0) return -1;

            s_targets.Add(new PreviewTarget
            {
                renderer = r,
                blendshapeIndex = idx,
                blendshapeName = blendshapeName
            });
            return s_targets.Count - 1;
        }

        /// <summary>
        /// Writes a weight directly to a target's SkinnedMeshRenderer via
        /// SetBlendShapeWeight. Bypasses AnimationMode so the avatar's bone pose
        /// stays untouched. Snapshots the original weight on first write so
        /// StopPreview can restore it cleanly.
        /// </summary>
        private static void ApplyWeight(int targetIndex, float value)
        {
            var target = s_targets[targetIndex];
            if (target.renderer == null) return;

            // The mesh-swap guard in SampleAtDepth only watches the PRIMARY
            // renderer path. If any other bound renderer swaps meshes, the
            // cached blendshape index can stay in range while pointing at a
            // different name. Re-resolve by name on demand so preview keeps
            // driving the intended blendshape instead of a stale slot.
            var mesh = target.renderer.sharedMesh;
            if (mesh == null) return;

            int blendshapeIndex = target.blendshapeIndex;
            if (blendshapeIndex < 0
                || blendshapeIndex >= mesh.blendShapeCount
                || mesh.GetBlendShapeName(blendshapeIndex) != target.blendshapeName)
            {
                int oldIndex = blendshapeIndex;
                blendshapeIndex = mesh.GetBlendShapeIndex(target.blendshapeName);
                if (blendshapeIndex < 0)
                {
                    // Warn once per (renderer, blendshape-name) combo so the
                    // user notices a stale binding without console spam during
                    // the 10-30 Hz auto-animate tick.
                    var key = (target.renderer.GetInstanceID(), target.blendshapeName);
                    if (s_warnedMissingBlendshapes.Add(key))
                    {
                        Debug.LogWarning(
                            $"[SPS] Blendshape '{target.blendshapeName}' not found on " +
                            $"renderer '{target.renderer.name}'. This binding will be skipped " +
                            "during preview. Regenerate the blendshape if this is unexpected.");
                    }
                    return;
                }

                // If the blendshape moved to a different index (e.g. mesh was
                // swapped), restore the old slot. Snapshot the current weight at
                // the old index before writing — keyed by NAME so a later StopPreview
                // can find it via the new mesh's name lookup.
                if (oldIndex != blendshapeIndex
                    && oldIndex >= 0 && oldIndex < mesh.blendShapeCount)
                {
                    // The target's name is what was at oldIndex before the swap —
                    // that's where we already snapshotted the user's pre-preview
                    // value. Use that key, not mesh.GetBlendShapeName(oldIndex)
                    // (which on the new mesh is whatever the swap shifted into oldIndex).
                    var oldBref = new BlendshapeRef { renderer = target.renderer, name = target.blendshapeName };
                    if (!s_originalWeights.TryGetValue(oldBref, out float restoreValue))
                    {
                        // Rebind fired on the very first sample, before any snapshot —
                        // use the current slot value as a safe fallback.
                        restoreValue = target.renderer.GetBlendShapeWeight(oldIndex);
                        s_originalWeights[oldBref] = restoreValue;
                    }
                    target.renderer.SetBlendShapeWeight(oldIndex, restoreValue);
                }

                target.blendshapeIndex = blendshapeIndex;
                s_targets[targetIndex] = target;
            }

            var bref = new BlendshapeRef { renderer = target.renderer, name = target.blendshapeName };
            if (!s_originalWeights.ContainsKey(bref))
                s_originalWeights[bref] = target.renderer.GetBlendShapeWeight(blendshapeIndex);

            target.renderer.SetBlendShapeWeight(blendshapeIndex, value);
        }

        public static void StartAutoAnimate(
            float duration,
            List<(float threshold, AnimationClip clip)> entries,
            Action<float> onDepthChanged)
        {
            if (!IsPreviewing) return;

            s_isAutoAnimating = true;
            s_autoAnimateTime = 0f;
            s_autoAnimateDuration = duration;
            s_autoAnimateEntries = entries;
            s_onDepthChanged = onDepthChanged;

            // Bug Fix 2: initialize lastUpdateTime so the first frame delta is near zero
            s_lastUpdateTime = EditorApplication.timeSinceStartup;

            EditorApplication.update += OnAutoAnimateUpdate;
        }

        /// <summary>
        /// Updates the entries used during auto-animate playback.
        /// Call when config changes mid-playback to reflect new settings.
        /// </summary>
        public static void UpdateAutoAnimateEntries(
            List<(float threshold, AnimationClip clip)> entries)
        {
            if (s_isAutoAnimating && entries != null)
                s_autoAnimateEntries = entries;
        }

        public static void StopAutoAnimate()
        {
            s_isAutoAnimating = false;
            EditorApplication.update -= OnAutoAnimateUpdate;
            // The callback captures the window that started the animation.
            s_autoAnimateEntries = null;
            s_onDepthChanged = null;
        }

        public static void StopPreview()
        {
            bool wasPreviewing = IsPreviewing;
            try
            {
                if (s_isAutoAnimating)
                    StopAutoAnimate();
            }
            finally
            {
                // Restore original blendshape weights we snapshotted during preview
                foreach (var kv in s_originalWeights)
                {
                    var r = kv.Key.renderer;
                    if (r == null || r.sharedMesh == null) continue;
                    int idx = r.sharedMesh.GetBlendShapeIndex(kv.Key.name);
                    if (idx >= 0 && idx < r.sharedMesh.blendShapeCount)
                        r.SetBlendShapeWeight(idx, kv.Value);
                }
                s_originalWeights.Clear();

                // Restore objects we force-activated for preview
                foreach (var go in s_enabledObjects)
                {
                    if (go != null) go.SetActive(false);
                }
                s_enabledObjects.Clear();

                s_tableEntries = null;
                s_targets.Clear();
                s_entryValues = null;
                s_warnedMissingBlendshapes.Clear();
                s_lastSampledDepth = -1f;
                s_previewStartMesh = null;
                s_previewRendererPath = null;
                s_previewStartRenderer = null;

                SetSafetyNets(false);

                IsPreviewing = false;
                s_avatarRoot = null;

                SceneView.RepaintAll();
            }

            if (wasPreviewing)
                PreviewStopped?.Invoke();
        }

        // --- Internal ---

        /// <summary>
        /// Subscribes (or unsubscribes) the hooks that end the preview before
        /// anything could save or lose the previewed weights.
        /// </summary>
        private static void SetSafetyNets(bool subscribe)
        {
            if (subscribe)
            {
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
                UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += OnSceneSaving;
                UnityEditor.SceneManagement.EditorSceneManager.sceneOpening += OnSceneOpening;
                EditorApplication.quitting += OnEditorQuitting;
                Undo.undoRedoPerformed += OnUndoRedo;
            }
            else
            {
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
                UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= OnSceneSaving;
                UnityEditor.SceneManagement.EditorSceneManager.sceneOpening -= OnSceneOpening;
                EditorApplication.quitting -= OnEditorQuitting;
                Undo.undoRedoPerformed -= OnUndoRedo;
            }
        }

        private static void OnAutoAnimateUpdate()
        {
            if (!s_isAutoAnimating || !IsPreviewing)
            {
                StopAutoAnimate();
                return;
            }

            // Bug Fix 2: use EditorApplication.timeSinceStartup instead of Time.deltaTime
            // Time.deltaTime is unreliable (often 0 or very large) in edit mode
            double currentTime = EditorApplication.timeSinceStartup;
            float dt = Mathf.Min((float)(currentTime - s_lastUpdateTime), 0.1f); // Cap at 100ms to prevent jumps
            s_lastUpdateTime = currentTime;

            s_autoAnimateTime += dt;

            // Ping-pong: 0→1→0 (in then out, full cycle)
            float fullCycle = s_autoAnimateDuration * 2f;
            float phase = s_autoAnimateTime % fullCycle;
            float depth = phase < s_autoAnimateDuration
                ? phase / s_autoAnimateDuration                          // 0→1
                : 1f - (phase - s_autoAnimateDuration) / s_autoAnimateDuration; // 1→0
            depth = Mathf.Clamp01(depth);

            // Sampling can stop the preview, which clears the callback.
            var onDepthChanged = s_onDepthChanged;
            SampleAtDepth(depth, s_autoAnimateEntries);
            onDepthChanged?.Invoke(depth);
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
                StopPreview();
        }

        private static void OnBeforeReload()
        {
            StopPreview();
        }

        internal static void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
            => StopPreview();
        private static void OnSceneOpening(string path, UnityEditor.SceneManagement.OpenSceneMode mode)
            => StopPreview();
        private static void OnEditorQuitting() => StopPreview();
        private static void OnUndoRedo() => StopPreview();
    }
}
