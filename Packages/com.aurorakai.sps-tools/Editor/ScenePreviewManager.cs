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
        private static AnimationClip s_cachedComposite;
        private static bool s_cachedIsOwned; // true if we created it (safe to destroy)
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

        // Resolved binding cache for the current composite clip - avoids per-tick
        // GetCurveBindings/Find/Substring/GetEditorCurve allocation cost during auto-animate.
        private struct ResolvedBinding
        {
            public SkinnedMeshRenderer renderer;
            public int blendshapeIndex;
            public string blendshapeName;
            public AnimationCurve curve;
        }
        private static AnimationClip s_resolvedForClip;
        private static readonly List<ResolvedBinding> s_resolvedBindings = new List<ResolvedBinding>();

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
            if (targetPaths.Count > 0)
            {
                s_previewRendererPath = targetPaths[0];
                var rendererTransform = avatarRoot.transform.Find(s_previewRendererPath);
                if (rendererTransform != null)
                {
                    var smr = rendererTransform.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null)
                        s_previewStartMesh = smr.sharedMesh;
                }
            }

            // Register safety nets (unregister first to prevent double-registration)
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        public static void SampleAtDepth(
            float depth,
            List<(float threshold, AnimationClip clip)> entries)
        {
            if (!IsPreviewing || s_avatarRoot == null) return;

            // Auto-stop if the mesh on the tracked renderer has changed since preview started
            if (s_previewStartMesh != null && s_previewRendererPath != null)
            {
                var rendererTransform = s_avatarRoot.transform.Find(s_previewRendererPath);
                if (rendererTransform != null)
                {
                    var smr = rendererTransform.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null && smr.sharedMesh != s_previewStartMesh)
                    {
                        StopPreview();
                        return;
                    }
                }
            }

            // Skip if depth hasn't changed meaningfully
            if (Mathf.Abs(depth - s_lastSampledDepth) < 0.001f) return;
            s_lastSampledDepth = depth;

            // Only destroy if we created it (interpolated clip), not a source clip
            if (s_cachedComposite != null && s_cachedIsOwned)
                UnityEngine.Object.DestroyImmediate(s_cachedComposite);

            s_cachedComposite = CompositeClipAtDepth(depth, entries, out s_cachedIsOwned);
            if (s_cachedComposite != null)
                ApplyClipDirectly(s_cachedComposite);

            SceneView.RepaintAll();
        }

        /// <summary>
        /// Writes the clip's blendshape curve values directly to each referenced
        /// SkinnedMeshRenderer via SetBlendShapeWeight. Bypasses AnimationMode so
        /// the avatar's bone pose stays untouched. Snapshots original weights on
        /// first write so StopPreview can restore them cleanly.
        ///
        /// The composite clip is rebuilt every depth sample, but its bindings rarely
        /// change between samples - we resolve renderer+index+curve once per clip
        /// instance and reuse during the per-tick hot loop.
        /// </summary>
        private static void ApplyClipDirectly(AnimationClip clip)
        {
            if (s_avatarRoot == null || clip == null) return;

            if (clip != s_resolvedForClip)
                ResolveClipBindings(clip);

            for (int i = 0; i < s_resolvedBindings.Count; i++)
            {
                var rb = s_resolvedBindings[i];
                if (rb.renderer == null || rb.curve == null) continue;

                // The mesh-swap guard in SampleAtDepth only watches the PRIMARY
                // renderer path. If any other bound renderer swaps meshes, the
                // cached blendshape index can stay in range while pointing at a
                // different name. Re-resolve by name on demand so preview keeps
                // driving the intended blendshape instead of a stale slot.
                var mesh = rb.renderer.sharedMesh;
                if (mesh == null) continue;

                int blendshapeIndex = rb.blendshapeIndex;
                if (blendshapeIndex < 0
                    || blendshapeIndex >= mesh.blendShapeCount
                    || mesh.GetBlendShapeName(blendshapeIndex) != rb.blendshapeName)
                {
                    int oldIndex = blendshapeIndex;
                    blendshapeIndex = mesh.GetBlendShapeIndex(rb.blendshapeName);
                    if (blendshapeIndex < 0)
                    {
                        // Warn once per (renderer, blendshape-name) combo so the
                        // user notices a stale binding without console spam during
                        // the 10-30 Hz auto-animate tick.
                        var key = (rb.renderer.GetInstanceID(), rb.blendshapeName);
                        if (s_warnedMissingBlendshapes.Add(key))
                        {
                            Debug.LogWarning(
                                $"[SPS] Blendshape '{rb.blendshapeName}' not found on " +
                                $"renderer '{rb.renderer.name}'. This binding will be skipped " +
                                "during preview. Regenerate the blendshape if this is unexpected.");
                        }
                        continue;
                    }

                    // If the blendshape moved to a different index (e.g. mesh was
                    // swapped), restore the old slot. Snapshot the current weight at
                    // the old index before writing — keyed by NAME so a later StopPreview
                    // can find it via the new mesh's name lookup.
                    if (oldIndex != blendshapeIndex
                        && oldIndex >= 0 && oldIndex < mesh.blendShapeCount)
                    {
                        // The binding's name (rb.blendshapeName) is what was at oldIndex
                        // before the swap — that's where we already snapshotted the user's
                        // pre-preview value. Use that key, not mesh.GetBlendShapeName(oldIndex)
                        // (which on the new mesh is whatever the swap shifted into oldIndex).
                        var oldBref = new BlendshapeRef { renderer = rb.renderer, name = rb.blendshapeName };
                        if (!s_originalWeights.TryGetValue(oldBref, out float restoreValue))
                        {
                            // Rebind fired on the very first sample, before any snapshot —
                            // use the current slot value as a safe fallback.
                            restoreValue = rb.renderer.GetBlendShapeWeight(oldIndex);
                            s_originalWeights[oldBref] = restoreValue;
                        }
                        rb.renderer.SetBlendShapeWeight(oldIndex, restoreValue);
                    }

                    rb.blendshapeIndex = blendshapeIndex;
                    s_resolvedBindings[i] = rb;
                }

                var bref = new BlendshapeRef { renderer = rb.renderer, name = rb.blendshapeName };
                if (!s_originalWeights.ContainsKey(bref))
                    s_originalWeights[bref] = rb.renderer.GetBlendShapeWeight(blendshapeIndex);

                rb.renderer.SetBlendShapeWeight(blendshapeIndex, rb.curve.Evaluate(0f));
            }
        }

        private static void ResolveClipBindings(AnimationClip clip)
        {
            s_resolvedBindings.Clear();
            s_resolvedForClip = clip;

            const string BS_PREFIX = "blendShape.";
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer)) continue;
                if (!binding.propertyName.StartsWith(BS_PREFIX)) continue;

                string bsName = binding.propertyName.Substring(BS_PREFIX.Length);
                var t = binding.path == ""
                    ? s_avatarRoot.transform
                    : s_avatarRoot.transform.Find(binding.path);
                if (t == null) continue;

                var r = t.GetComponent<SkinnedMeshRenderer>();
                if (r == null || r.sharedMesh == null) continue;

                int idx = r.sharedMesh.GetBlendShapeIndex(bsName);
                if (idx < 0) continue;

                s_resolvedBindings.Add(new ResolvedBinding
                {
                    renderer = r,
                    blendshapeIndex = idx,
                    blendshapeName = bsName,
                    curve = AnimationUtility.GetEditorCurve(clip, binding)
                });
            }
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

                if (s_cachedComposite != null && s_cachedIsOwned)
                    UnityEngine.Object.DestroyImmediate(s_cachedComposite);
                s_cachedComposite = null;
                s_cachedIsOwned = false;
                s_resolvedForClip = null;
                s_resolvedBindings.Clear();
                s_warnedMissingBlendshapes.Clear();
                s_lastSampledDepth = -1f;
                s_previewStartMesh = null;
                s_previewRendererPath = null;

                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;

                IsPreviewing = false;
                s_avatarRoot = null;

                SceneView.RepaintAll();
            }

            if (wasPreviewing)
                PreviewStopped?.Invoke();
        }

        // --- Internal ---

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

            SampleAtDepth(depth, s_autoAnimateEntries);
            s_onDepthChanged?.Invoke(depth);
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

        /// <summary>
        /// Composites animation clips at a given depth using 1D blend tree interpolation.
        /// Reads curve bindings from the bracketing clips and lerps property values.
        /// </summary>
        private static AnimationClip CompositeClipAtDepth(
            float depth,
            List<(float threshold, AnimationClip clip)> entries,
            out bool isOwned)
        {
            isOwned = false;
            if (entries == null || entries.Count == 0) return null;

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

            var lowerClip = entries[lowerIdx].clip;
            var upperClip = entries[upperIdx].clip;

            // Exact match or same clip - return directly
            if (lowerIdx == upperIdx || lowerClip == upperClip)
                return lowerClip;

            // Linear blend factor - matches how Unity's 1D blend tree interpolates at runtime
            float range = entries[upperIdx].threshold - entries[lowerIdx].threshold;
            float t = range > 0.0001f
                ? (depth - entries[lowerIdx].threshold) / range
                : 0f;
            t = Mathf.Clamp01(t);

            // Build interpolated clip from curve bindings (we own this clip)
            isOwned = true;
            var composited = new AnimationClip();
            var lowerBindings = AnimationUtility.GetCurveBindings(lowerClip);
            var upperBindings = AnimationUtility.GetCurveBindings(upperClip);

            // Collect all unique bindings from both clips
            var allBindings = new HashSet<EditorCurveBinding>();
            foreach (var b in lowerBindings) allBindings.Add(b);
            foreach (var b in upperBindings) allBindings.Add(b);

            foreach (var binding in allBindings)
            {
                var lowerCurve = AnimationUtility.GetEditorCurve(lowerClip, binding);
                var upperCurve = AnimationUtility.GetEditorCurve(upperClip, binding);

                float lowerVal = lowerCurve != null ? lowerCurve.Evaluate(0f) : 0f;
                float upperVal = upperCurve != null ? upperCurve.Evaluate(0f) : 0f;

                float blended = Mathf.Lerp(lowerVal, upperVal, t);
                var newCurve = new AnimationCurve(new Keyframe(0f, blended));

                AnimationUtility.SetEditorCurve(composited, binding, newCurve);
            }

            return composited;
        }
    }
}
