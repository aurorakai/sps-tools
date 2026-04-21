using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Abstract base class for SPS effect editor windows.
    /// Provides shared UI sections (avatar picker, socket detection, scene preview,
    /// generate button) so that each effect only implements its unique sections.
    /// </summary>
    public abstract class BaseEffectWindow<TConfig> : EditorWindow
        where TConfig : BaseEffectConfig
    {
        // Unicode "×" (U+00D7) used for list-row remove buttons. Centralised so
        // the glyph can't drift between the different list sections.
        protected const string RemoveGlyph = "\u00D7";

        // =====================================================================
        // Shared state
        // =====================================================================

        protected TConfig config;
        protected Vector2 scrollPosition;
        protected string statusMessage = "";
        protected MessageType statusType = MessageType.None;

        // Tracked path of the currently-loaded config asset. Persisted per-window via
        // [SerializeField] so two windows of the same subclass don't share one global
        // slot. Empty string = no tracked asset (state is New).
        [SerializeField] private string trackedAssetPath = "";

        // Snapshot of config JSON at the last successful load/save. Compared against
        // the live JSON each OnGUI to detect the Dirty state without touching disk.
        private string lastSavedJson = "";

        // Per-frame cache for GetCurrentConfigState. Recomputed only on EventType.Layout
        // to avoid per-keystroke EditorJsonUtility.ToJson on the whole ScriptableObject,
        // which was the source of visible typing lag in the name field.
        private ConfigState _cachedState = ConfigState.New;
        private bool _cachedStateInitialized;

        /// <summary>
        /// Captures the current in-memory config's serialized JSON as the "clean" baseline.
        /// Call after: loading a config from disk, saving a config, or replacing the
        /// working config (new / duplicate). Leaves Saved state representing the newly
        /// captured snapshot.
        /// </summary>
        private void CaptureSavedJson()
        {
            lastSavedJson = config != null ? EditorJsonUtility.ToJson(config) : "";
        }

        // Object pickers (not serialized in config -- resolved from paths)
        protected SkinnedMeshRenderer targetRenderer;

        // Scene preview state
        protected float previewDepth;
        protected List<(float threshold, AnimationClip clip)> previewEntries;
        protected int previewConfigHash;

        // Displacement preview cache — avg edge length in meters of tris in the
        // path region. Recomputed only when the inputs hash changes.
        private float _cachedAvgEdge;
        private int _avgEdgeCacheKey;
        private bool _showDisplacementPreview;

        // Blendshape auto-generate toggle
        protected bool blendshapeAutoGenerate;

        // Socket detection
        protected List<DetectedSocket> detectedSockets = new List<DetectedSocket>();
        protected int selectedSocketIndex = -1;
        private int socketDropdownIndex;

        // Preview-time visibility snapshot per tracked mesh: the ancestor
        // GameObjects this entry contributed to activating, plus (optionally) the
        // renderer.enabled flag we forced true. Ancestor *original* state and
        // refcount live in session-wide dicts below so two entries sharing an
        // ancestor cooperate correctly (toggling one off must not deactivate an
        // ancestor the other still depends on).
        private struct PreviewVisibilitySnapshot
        {
            public List<GameObject> activatedAncestors;
            public bool rendererEnabledChanged;
            public bool rendererEnabledOriginal;
        }
        private readonly Dictionary<TrackedMesh, PreviewVisibilitySnapshot>
            previewVisibilitySnapshots = new Dictionary<TrackedMesh, PreviewVisibilitySnapshot>();

        // Session-wide ancestor bookkeeping. Key = ancestor GameObject we may
        // have activated during preview. `Refcount` = number of tracked meshes
        // currently holding the ancestor active; `OriginalActive` = the first-
        // observed activeSelf before we touched it, used on final restore.
        private readonly Dictionary<GameObject, int> ancestorActivationRefcount
            = new Dictionary<GameObject, int>();
        private readonly Dictionary<GameObject, bool> ancestorOriginalActive
            = new Dictionary<GameObject, bool>();

        // Overlays auto-hidden because they hadn't been baked yet - restored on preview end.
        private readonly Dictionary<TrackedMesh, bool> previewHideSnapshots
            = new Dictionary<TrackedMesh, bool>();

        // =====================================================================
        // Abstract / virtual members -- subclasses must or may override
        // =====================================================================

        /// <summary>
        /// Human-readable effect name used in dialogs and log messages.
        /// Example: "SPS Bulge"
        /// </summary>
        protected abstract string EffectName { get; }

        /// <summary>
        /// Theme color used for path overlay in the scene view.
        /// </summary>
        protected abstract Color ThemeColor { get; }

        /// <summary>
        /// Config asset filename prefix (e.g. "SPSBulge").
        /// </summary>
        protected abstract string ConfigAssetPrefix { get; }

        /// <summary>
        /// Creates a default config instance when no saved config exists.
        /// </summary>
        protected abstract TConfig CreateDefaultConfig();

        /// <summary>
        /// Draws the effect-specific UI sections between the socket section
        /// and the scene preview section.
        /// </summary>
        protected abstract void DrawEffectSpecificSections();

        /// <summary>
        /// Builds preview threshold entries for the scene preview system.
        /// </summary>
        protected abstract List<(float threshold, AnimationClip clip)> BuildPreviewThresholds();

        /// <summary>
        /// Generates assets (animator controller, animations, etc.) and returns
        /// the path to the generated RuntimeAnimatorController asset.
        /// </summary>
        protected abstract string GenerateAssets();

        /// <summary>
        /// Generate assets with multiple depth parameters for multi-socket support.
        /// Default implementation calls GenerateAssets() (single parameter).
        /// Override in subclass to pass parameter list to BlendTreeBuilder.CreateMultiBlendTree.
        /// </summary>
        protected virtual string GenerateAssetsMulti(List<string> depthParameters)
        {
            // Default: use primary parameter
            config.depthParameter = depthParameters[0];
            return GenerateAssets();
        }

        /// <summary>
        /// Collects depth parameter names from all enabled sockets.
        /// </summary>
        protected List<string> GetEnabledDepthParameters()
        {
            var result = new List<string>();
            foreach (int idx in config.enabledSocketIndices)
            {
                if (idx >= detectedSockets.Count) continue;
                var socket = detectedSockets[idx];
                if (socket.depthFxFloats.Count > 0 &&
                    !result.Contains(socket.depthFxFloats[0]))
                {
                    result.Add(socket.depthFxFloats[0]);
                }
            }
            return result;
        }

        /// <summary>
        /// Computes a hash of config values that affect preview output.
        /// Used to detect config changes during live preview.
        /// </summary>
        protected abstract int ComputePreviewConfigHash();

        /// <summary>
        /// Returns target paths for the scene preview focus camera.
        /// Override to provide effect-specific target paths.
        /// </summary>
        protected abstract List<string> GetPreviewTargetPaths();

        /// <summary>
        /// Returns whether preview can proceed. Subclasses can add extra checks
        /// (e.g. blendshape existence) on top of config.IsValid().
        /// </summary>
        protected virtual bool CanPreview()
        {
            return config.IsValid();
        }

        /// <summary>
        /// Called after base OnEnable completes. Override for effect-specific init.
        /// </summary>
        protected virtual void OnEnableExtra() { }

        /// <summary>
        /// Called before base OnDisable cleanup. Override for effect-specific cleanup.
        /// </summary>
        protected virtual void OnDisableExtra() { }

        /// <summary>
        /// Called during ReconnectObjectReferences after renderer reconnection.
        /// Override to reconnect effect-specific object references (e.g. bones).
        /// </summary>
        protected virtual void ReconnectExtraReferences() { }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        protected virtual void OnEnable()
        {
            EnsureConfigLoaded();

            // Self-heal stale "(Clone)(Clone)..." names carried over from
            // before the Instantiate fix landed.
            if (config != null && config.name != null && config.name.Contains("(Clone)"))
            {
                int cloneAt = config.name.IndexOf("(Clone)", System.StringComparison.Ordinal);
                config.name = config.name.Substring(0, cloneAt).TrimEnd();
            }

            // Auto-detect avatar root if not set
            if (config.avatarRoot == null)
            {
                config.avatarRoot = DepthParameterDetector.FindSingleAvatarRoot();
                if (config.avatarRoot != null)
                    ReconnectObjectReferences();
            }

            // Always refresh sockets on enable (survives recompile/domain reload)
            if (config.avatarRoot != null)
                RefreshSockets();

            // Refresh the snapshot AFTER all config mutations in OnEnable (name
            // cleanup, avatar auto-detect) so the badge doesn't show false-Dirty on
            // first OnGUI for changes the user didn't make.
            CaptureSavedJson();

            ScenePreviewManager.PreviewStopped -= OnScenePreviewStopped;
            ScenePreviewManager.PreviewStopped += OnScenePreviewStopped;
            SceneView.duringSceneGui += OnSceneGUIOverlay;

            // Autosave hooks - every path where the in-memory config could be lost
            AssemblyReloadEvents.beforeAssemblyReload -= AutoSaveBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += AutoSaveBeforeReload;
            EditorApplication.playModeStateChanged -= AutoSaveOnPlayModeChange;
            EditorApplication.playModeStateChanged += AutoSaveOnPlayModeChange;

            OnEnableExtra();
        }

        private void AutoSaveBeforeReload() => AutoSaveConfig();
        private void AutoSaveOnPlayModeChange(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) AutoSaveConfig();
        }

        /// <summary>
        /// Ensures `config` is non-null. Tries the last-used path, then any config
        /// in the project, then a fresh default. Safe to call anytime - used from
        /// OnEnable and as a recovery step at the top of OnGUI after domain reloads
        /// or Play-mode exits that destroy the in-memory ScriptableObject.
        /// </summary>
        protected void EnsureConfigLoaded()
        {
            if (config != null) return;

            // 1. Prefer the last-used config if the path still resolves.
            string lastConfigPath = trackedAssetPath;
            if (!string.IsNullOrEmpty(lastConfigPath))
            {
                var saved = AssetDatabase.LoadAssetAtPath<TConfig>(lastConfigPath);
                if (saved != null)
                {
                    config = InstantiateWorkingCopy(saved);
                    selectedSocketIndex = config.selectedSocketIndex;
                    ReconnectObjectReferences();
                    CaptureSavedJson();
                    return;
                }
            }

            // 2. Fall back to a global scan. Configs may live anywhere the user
            //    saved them - not just under Assets/SPSTools/ - so we
            //    use the same helper the chevron picker uses.
            var found = ConfigAssetHelper.FindAllConfigs<TConfig>();
            if (found.Count == 1)
            {
                var only = found[0];
                config = InstantiateWorkingCopy(only);
                selectedSocketIndex = config.selectedSocketIndex;
                string onlyPath = AssetDatabase.GetAssetPath(only);
                if (!string.IsNullOrEmpty(onlyPath))
                    trackedAssetPath = onlyPath;
                ReconnectObjectReferences();
                CaptureSavedJson();
                return;
            }
            if (found.Count > 1)
            {
                // Ambiguous - don't silently pick. The user will use the chevron
                // picker to choose. Start with a fresh default so the UI isn't
                // broken, and surface a hint. Note: this hint lives in the
                // shared statusMessage slot and will be overwritten by the
                // first user action (Save/Generate/Restore); it's a one-shot
                // nudge on window open, not a persistent indicator.
                config = CreateDefaultConfig();
                CaptureSavedJson();
                statusMessage = "Multiple saved configurations found. Use the ▾ dropdown next to the name to pick one.";
                statusType = MessageType.Info;
                return;
            }

            // 3. No configs anywhere - start fresh.
            config = CreateDefaultConfig();
            CaptureSavedJson();
        }

        /// <summary>
        /// Instantiates a working copy of a persisted config asset, stripping
        /// the "(Clone)" suffix Unity appends. Without this, every window
        /// open / domain reload accumulates another "(Clone)" on the in-memory
        /// reference, and <see cref="EditorUtility.CopySerialized"/> later
        /// carries that name onto the persisted asset during Save.
        /// </summary>
        private static TConfig InstantiateWorkingCopy(TConfig source)
        {
            var copy = Instantiate(source);
            copy.name = source.name;
            return copy;
        }

        protected virtual void OnDisable()
        {
            OnDisableExtra();

            AutoSaveConfig();  // window close is the last chance to persist changes

            ScenePreviewManager.PreviewStopped -= OnScenePreviewStopped;
            if (PathDrawingTool.IsDrawing)
                PathDrawingTool.CancelDrawing();
            if (ScenePreviewManager.IsPreviewing)
                ScenePreviewManager.StopPreview();
            RestoreAllPreviewVisibility();
            SceneView.duringSceneGui -= OnSceneGUIOverlay;

            AssemblyReloadEvents.beforeAssemblyReload -= AutoSaveBeforeReload;
            EditorApplication.playModeStateChanged -= AutoSaveOnPlayModeChange;
        }

        private void OnScenePreviewStopped()
        {
            RestoreAllPreviewVisibility();
            previewEntries = null;
            previewDepth = 0f;
            Repaint();
        }

        // =====================================================================
        // Scene GUI overlay
        // =====================================================================

        /// <summary>
        /// Draws the stored path as a persistent overlay when the window is open.
        /// Uses the effect's ThemeColor for coloring.
        /// </summary>
        private void OnSceneGUIOverlay(SceneView sceneView)
        {
            if (config == null || config.avatarRoot == null) return;
            if (PathDrawingTool.IsDrawing) return;
            if (config.pathWaypoints == null || config.pathWaypoints.Count == 0) return;

            PathDrawingTool.DrawPathOverlay(
                config.pathWaypoints,
                config.avatarRoot.transform,
                ThemeColor);
        }

        // =====================================================================
        // Object reference management
        // =====================================================================

        /// <summary>
        /// Resolves object references from stored path strings
        /// when a config is loaded or the avatar root changes.
        /// </summary>
        protected void ReconnectObjectReferences()
        {
            // Try to re-find avatar root by name if the reference was lost (e.g. loaded from asset)
            if (config.avatarRoot == null && !string.IsNullOrEmpty(config.avatarRootName))
            {
                var go = GameObject.Find(config.avatarRootName);
                if (go != null && DepthParameterDetector.HasAvatarDescriptor(go))
                    config.avatarRoot = go;
            }

            if (config.avatarRoot == null) return;

            // Reconnect target renderer
            if (!string.IsNullOrEmpty(config.rendererPath))
            {
                var found = config.avatarRoot.transform.Find(config.rendererPath);
                if (found != null)
                    targetRenderer = found.GetComponent<SkinnedMeshRenderer>();
            }

            // Auto-detect mesh if not set
            if (targetRenderer == null)
                targetRenderer = AutoDetectMesh();

            // Let subclass reconnect its own references
            ReconnectExtraReferences();
        }

        /// <summary>
        /// Auto-detects the main SkinnedMeshRenderer on the avatar.
        /// Prefers one named "Body", falls back to the largest mesh.
        /// </summary>
        protected SkinnedMeshRenderer AutoDetectMesh()
        {
            if (config.avatarRoot == null) return null;

            var renderers = config.avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return null;

            // Prefer "Body" by name
            foreach (var r in renderers)
            {
                if (r.gameObject.name.Equals("Body", StringComparison.OrdinalIgnoreCase))
                {
                    config.rendererPath = GetRelativePath(config.avatarRoot.transform, r.transform);
                    return r;
                }
            }

            // Fallback: pick the renderer with the most vertices
            SkinnedMeshRenderer best = null;
            int bestVerts = 0;
            foreach (var r in renderers)
            {
                if (r.sharedMesh != null && r.sharedMesh.vertexCount > bestVerts)
                {
                    bestVerts = r.sharedMesh.vertexCount;
                    best = r;
                }
            }

            if (best != null)
                config.rendererPath = GetRelativePath(config.avatarRoot.transform, best.transform);

            return best;
        }

        // =====================================================================
        // OnGUI template method
        // =====================================================================

        protected virtual void OnGUI()
        {
            // VRCFury gate
            if (!VRCFuryIntegration.IsVRCFuryInstalled())
            {
                DrawVRCFuryMissing();
                return;
            }

            // Recover if the in-memory config was destroyed by a domain reload
            // (happens on Play-mode exit when VRCFury build failed, among other cases).
            EnsureConfigLoaded();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            DrawAvatarSection();
            EditorGUILayout.Space(4);
            DrawConfigNameSection();
            EditorGUILayout.Space(4);
            DrawDepthParameterSection();
            EditorGUILayout.Space(8);
            DrawEffectSpecificSections();
            EditorGUILayout.Space(8);
            DrawScenePreviewSection();
            EditorGUILayout.Space(12);
            DrawGenerateButton();

            if (!string.IsNullOrEmpty(statusMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(statusMessage, statusType);
            }

            // Push footer link to bottom when content is short
            GUILayout.FlexibleSpace();
            EditorGUILayout.Space(12);
            DrawFooterLink();

            EditorGUILayout.EndScrollView();
        }

        private const string SupportUrl = "https://folfkai.dev/donate";
        private static GUIStyle s_footerLinkStyle;
        private static readonly GUIContent s_footerLinkContent =
            new GUIContent("Created by AuroraKai - Donate");

        private void DrawFooterLink()
        {
            Rect sep = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(1), GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                Color sepColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.06f)
                    : new Color(0f, 0f, 0f, 0.12f);
                EditorGUI.DrawRect(sep, sepColor);
            }

            EditorGUILayout.Space(8);

            if (s_footerLinkStyle == null)
            {
                Color baseColor = EditorGUIUtility.isProSkin
                    ? new Color(0.55f, 0.55f, 0.55f)
                    : new Color(0.40f, 0.40f, 0.40f);
                Color hoverColor = EditorGUIUtility.isProSkin
                    ? new Color(0.42f, 0.66f, 1f)
                    : new Color(0.20f, 0.45f, 0.85f);
                s_footerLinkStyle = new GUIStyle(EditorStyles.label)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = baseColor },
                    hover = { textColor = hoverColor },
                };
            }

            Rect labelRect = GUILayoutUtility.GetRect(s_footerLinkContent, s_footerLinkStyle,
                GUILayout.ExpandWidth(true));

            EditorGUIUtility.AddCursorRect(labelRect, MouseCursor.Link);

            if (GUI.Button(labelRect, s_footerLinkContent, s_footerLinkStyle))
            {
                Application.OpenURL(SupportUrl);
            }

            EditorGUILayout.Space(8);
        }

        // =====================================================================
        // VRCFury gate
        // =====================================================================

        protected void DrawVRCFuryMissing()
        {
            DrawVRCFuryStatus(false);
            EditorGUILayout.Space(40);
            EditorGUILayout.BeginVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("VRCFury Required", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                $"This tool requires VRCFury to be installed in your project. " +
                $"{EffectName} generates VRCFury FullController components for avatar integration.",
                MessageType.Error);
            EditorGUILayout.Space(8);
            if (GUILayout.Button("Open VRCFury Website", GUILayout.Height(28)))
                Application.OpenURL("https://vrcfury.com");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        protected void DrawVRCFuryStatus(bool installed)
        {
            var color = installed
                ? new Color(0.18f, 0.49f, 0.2f)
                : new Color(0.78f, 0.16f, 0.16f);
            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(24), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, color);

            string label;
            if (installed)
            {
                string version = VRCFuryIntegration.GetVRCFuryVersion();
                label = version != "unknown"
                    ? $"  VRCFury detected (v{version})"
                    : "  VRCFury detected";
            }
            else
            {
                label = "  VRCFury not found";
            }

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleLeft
            };
            EditorGUI.LabelField(rect, label, style);
        }

        // =====================================================================
        // Avatar section
        // =====================================================================

        protected void DrawAvatarSection()
        {
            EditorGUILayout.LabelField("Avatar", EditorStyles.miniBoldLabel);
            var newAvatar = (GameObject)EditorGUILayout.ObjectField(
                "Avatar Root", config.avatarRoot, typeof(GameObject), true);

            if (newAvatar != config.avatarRoot)
            {
                config.avatarRoot = newAvatar;
                config.avatarRootName = newAvatar != null ? newAvatar.name : "";

                ReconnectObjectReferences();

                // Detect SPS Sockets
                if (config.avatarRoot != null)
                {
                    selectedSocketIndex = -1;
                    RefreshSockets();
                }
                else
                {
                    detectedSockets.Clear();
                    selectedSocketIndex = -1;
                }
            }

            // Avatar Descriptor validation
            if (config.avatarRoot != null && !DepthParameterDetector.HasAvatarDescriptor(config.avatarRoot))
            {
                EditorGUILayout.HelpBox(
                    "Selected object does not have a VRC Avatar Descriptor. " +
                    "This is required for VRChat avatars.",
                    MessageType.Warning);
            }
        }

        // =====================================================================
        // Configuration name section
        // =====================================================================

        protected void DrawConfigNameSection()
        {
            string savedPath = trackedAssetPath;
            ConfigState state = GetCurrentConfigState(savedPath);

            EditorGUILayout.BeginHorizontal();

            config.configurationName = EditorGUILayout.TextField(
                TooltipContent.ConfigName, config.configurationName);

            GUILayout.Space(4);

            if (EditorGUILayout.DropdownButton(
                    new GUIContent("▾"), FocusType.Passive,
                    EditorStyles.miniButton, GUILayout.Width(24)))
            {
                ShowConfigPickerMenu();
            }

            GUILayout.Space(6);

            DrawStatusBadge(state);

            GUILayout.Space(6);

            string saveLabel = state == ConfigState.Renamed ? "Rename & Save" : "Save";
            bool saveEnabled = state != ConfigState.Saved;
            using (new EditorGUI.DisabledScope(!saveEnabled))
            {
                if (GUILayout.Button(saveLabel, GUILayout.Width(110)))
                {
                    if (string.IsNullOrWhiteSpace(config.configurationName))
                    {
                        EditorUtility.DisplayDialog(EffectName,
                            "Enter a configuration name before saving.", "OK");
                    }
                    else if (SaveConfig())
                    {
                        statusMessage = $"Configuration \"{config.configurationName}\" saved.";
                        statusType = MessageType.Info;
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            // Secondary line showing the previous name when a rename is pending.
            if (state == ConfigState.Renamed)
            {
                string oldStem = System.IO.Path.GetFileNameWithoutExtension(savedPath);
                // Strip the "{ConfigAssetPrefix}_" prefix from the stem for display.
                string prefix = $"{ConfigAssetPrefix}_";
                string oldName = oldStem.StartsWith(prefix) ? oldStem.Substring(prefix.Length) : oldStem;
                EditorGUILayout.LabelField(
                    $"↦ will rename from \"{oldName}\"",
                    EditorStyles.miniLabel);
            }
        }

        // =====================================================================
        // Depth parameter / socket detection section
        // =====================================================================

        protected void DrawDepthParameterSection()
        {
            EditorGUILayout.LabelField("SPS Sockets", EditorStyles.miniBoldLabel);

            if (detectedSockets.Count > 0)
            {
                // Ensure enabledSocketIndices list is valid
                config.enabledSocketIndices.RemoveAll(idx => idx >= detectedSockets.Count);

                // Build list of available (not-yet-added) sockets
                var availableIndices = new List<int>();
                for (int i = 0; i < detectedSockets.Count; i++)
                {
                    if (!config.enabledSocketIndices.Contains(i))
                        availableIndices.Add(i);
                }

                // Dropdown + Add button
                if (availableIndices.Count > 0)
                {
                    EditorGUILayout.BeginHorizontal();

                    var displayNames = new string[availableIndices.Count];
                    for (int i = 0; i < availableIndices.Count; i++)
                    {
                        var socket = detectedSockets[availableIndices[i]];
                        displayNames[i] = $"{socket.DisplayName} ({socket.gameObjectName})";
                    }

                    if (socketDropdownIndex >= availableIndices.Count)
                        socketDropdownIndex = 0;

                    socketDropdownIndex = EditorGUILayout.Popup(socketDropdownIndex, displayNames);

                    if (GUILayout.Button("+", GUILayout.Width(24)))
                    {
                        config.enabledSocketIndices.Add(availableIndices[socketDropdownIndex]);
                        socketDropdownIndex = 0;
                    }

                    EditorGUILayout.EndHorizontal();
                }
                else if (config.enabledSocketIndices.Count > 0)
                {
                    EditorGUILayout.LabelField("All sockets added", EditorStyles.miniLabel);
                }

                // Enabled sockets list
                if (config.enabledSocketIndices.Count > 0)
                {
                    EditorGUILayout.Space(4);

                    int removeAt = -1;
                    for (int i = 0; i < config.enabledSocketIndices.Count; i++)
                    {
                        int socketIdx = config.enabledSocketIndices[i];
                        if (socketIdx >= detectedSockets.Count) continue;
                        var socket = detectedSockets[socketIdx];

                        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                        EditorGUILayout.BeginVertical();
                        EditorGUILayout.LabelField(
                            $"{socket.DisplayName} ({socket.gameObjectName})",
                            EditorStyles.boldLabel);

                        if (socket.depthFxFloats.Count > 0)
                        {
                            EditorGUILayout.LabelField(
                                $"Parameter: {socket.depthFxFloats[0]}",
                                EditorStyles.miniLabel);
                        }
                        else
                        {
                            EditorGUILayout.LabelField("No FX Float set", EditorStyles.miniLabel);
                            string suggested = DepthParameterDetector.SuggestParameterName(socket);
                            if (GUILayout.Button($"Add \"{suggested}\"", GUILayout.Height(18)))
                            {
                                if (DepthParameterDetector.AddFxFloatToSocket(socket.component, suggested))
                                    RefreshSockets();
                            }
                        }

                        EditorGUILayout.EndVertical();

                        if (GUILayout.Button(RemoveGlyph, GUILayout.Width(22), GUILayout.Height(22)))
                            removeAt = i;

                        EditorGUILayout.EndHorizontal();
                    }

                    if (removeAt >= 0)
                        config.enabledSocketIndices.RemoveAt(removeAt);
                }

                // Set depthParameter from first enabled socket (for preview/backward compat)
                foreach (int idx in config.enabledSocketIndices)
                {
                    if (idx < detectedSockets.Count && detectedSockets[idx].depthFxFloats.Count > 0)
                    {
                        config.depthParameter = detectedSockets[idx].depthFxFloats[0];
                        break;
                    }
                }
            }
            else if (config.avatarRoot != null)
            {
                EditorGUILayout.HelpBox(
                    "No SPS Sockets found on this avatar. Add an SPS Socket via VRCFury, " +
                    "or enter a depth parameter name manually below.",
                    MessageType.Warning);
            }

            // Manual fallback - only show when no sockets detected
            if (detectedSockets.Count == 0)
            {
                EditorGUILayout.Space(2);
                config.depthParameter = EditorGUILayout.TextField(
                    TooltipContent.DepthParameter, config.depthParameter);
            }

            if (config.avatarRoot != null)
            {
                if (GUILayout.Button("Refresh Sockets", GUILayout.Height(20)))
                    RefreshSockets();
            }
        }

        // =====================================================================
        // Socket helpers
        // =====================================================================

        protected void ApplySocketSelection()
        {
            if (selectedSocketIndex < 0 || selectedSocketIndex >= detectedSockets.Count) return;
            var socket = detectedSockets[selectedSocketIndex];
            if (!string.IsNullOrEmpty(socket.depthParameter))
                config.depthParameter = socket.depthParameter;
        }

        protected void RefreshSockets()
        {
            if (config.avatarRoot == null) return;

            detectedSockets = DepthParameterDetector.FindSPSSockets(config.avatarRoot);

            if (detectedSockets.Count > 0 && selectedSocketIndex < 0)
            {
                selectedSocketIndex = 0;
                ApplySocketSelection();
            }

            Repaint();
        }

        // =====================================================================
        // Additional Meshes section
        // =====================================================================

        private bool showAdditionalMeshes;

        protected void DrawAdditionalMeshesSection()
        {
            if (config.additionalMeshes == null)
                config.additionalMeshes = new List<TrackedMesh>();

            int count = config.additionalMeshes.Count;
            string label = count > 0
                ? $"Additional Meshes ({count})"
                : "Additional Meshes";
            showAdditionalMeshes = EditorGUILayout.Foldout(showAdditionalMeshes, label, true);
            if (!showAdditionalMeshes) return;

            EditorGUI.indentLevel++;

            EditorGUILayout.HelpBox(
                "Overlay meshes (e.g. clothing) that should receive matching blendshapes " +
                "so they deform together with the primary mesh.",
                MessageType.None);

            // Resolve all once (Transform.Find is the expensive bit) - hot path.
            var resolved = new SkinnedMeshRenderer[count];
            for (int i = 0; i < count; i++)
                resolved[i] = ResolveAdditionalRenderer(config.additionalMeshes[i]);

            int removeIdx = -1;
            for (int i = 0; i < count; i++)
            {
                var entry = config.additionalMeshes[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                var current = resolved[i];
                var picked = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                    current, typeof(SkinnedMeshRenderer), true);

                if (picked != current)
                {
                    if (picked == null)
                    {
                        entry.rendererPath = "";
                    }
                    else if (config.avatarRoot != null && picked.transform.IsChildOf(config.avatarRoot.transform)
                             && picked != targetRenderer)
                    {
                        bool dup = false;
                        for (int j = 0; j < count; j++)
                            if (j != i && resolved[j] == picked) { dup = true; break; }
                        if (!dup)
                        {
                            entry.rendererPath = GetRelativePath(config.avatarRoot.transform, picked.transform);
                            entry.originalMesh = null;
                            entry.originalMeshPath = "";
                            entry.generatedMesh = null;
                            entry.generatedMeshPath = "";
                        }
                    }
                }

                if (GUILayout.Button(RemoveGlyph, GUILayout.Width(22), GUILayout.Height(18)))
                    removeIdx = i;

                EditorGUILayout.EndHorizontal();

                // Status line + per-mesh restore
                EditorGUILayout.BeginHorizontal();
                string status;
                if (string.IsNullOrEmpty(entry.rendererPath)) status = "(empty - pick a renderer)";
                else if (entry.generatedMesh != null || !string.IsNullOrEmpty(entry.generatedMeshPath)) status = "generated";
                else if (entry.originalMesh != null || !string.IsNullOrEmpty(entry.originalMeshPath)) status = "original tracked";
                else status = "not yet generated";

                // Visibility indicator - animation still works on hidden renderers
                if (current != null && !current.gameObject.activeInHierarchy)
                    status += "  ·  hidden (still animated)";

                EditorGUILayout.LabelField(status, EditorStyles.miniLabel);

                bool canRestore = entry.originalMesh != null || !string.IsNullOrEmpty(entry.originalMeshPath);
                GUI.enabled = canRestore;
                if (GUILayout.Button("Restore", GUILayout.Width(70), GUILayout.Height(18)))
                    RestoreSingleAdditionalMesh(entry);
                GUI.enabled = true;
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
            }

            if (removeIdx >= 0)
                config.additionalMeshes.RemoveAt(removeIdx);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Mesh", GUILayout.Height(20)))
                config.additionalMeshes.Add(new TrackedMesh());
            bool canScan = config.avatarRoot != null
                && config.pathWaypoints != null && config.pathWaypoints.Count > 0;
            GUI.enabled = canScan;
            if (GUILayout.Button("Scan for Overlay Meshes", GUILayout.Height(20)))
                ShowOverlayScanResults();
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private void ShowOverlayScanResults()
        {
            var candidates = OverlayMeshDetector.FindCandidateOverlays(
                config.avatarRoot, config.pathWaypoints, targetRenderer, config.rendererPath);

            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog(EffectName,
                    "No candidate overlay meshes found in the path's affected region.",
                    "OK");
                return;
            }

            OverlayScanPopup.Show(candidates, selected =>
            {
                foreach (var c in selected)
                {
                    if (c.renderer == null) continue;
                    string path = GetRelativePath(config.avatarRoot.transform, c.renderer.transform);
                    // Skip duplicates
                    bool dup = false;
                    foreach (var existing in config.additionalMeshes)
                        if (existing.rendererPath == path) { dup = true; break; }
                    if (dup) continue;
                    config.additionalMeshes.Add(new TrackedMesh { rendererPath = path });
                }
                Repaint();
            });
        }

        // =====================================================================
        // Scene preview section
        // =====================================================================

        protected void DrawScenePreviewSection()
        {
            EditorGUILayout.LabelField("Scene Preview", EditorStyles.miniBoldLabel);

            bool canPreview = CanPreview();

            // Rebuild preview entries when config changes mid-preview
            if (ScenePreviewManager.IsPreviewing && previewEntries != null)
            {
                int hash = ComputePreviewConfigHash();
                if (hash != previewConfigHash)
                {
                    previewConfigHash = hash;
                    previewEntries = BuildPreviewThresholds();
                    ScenePreviewManager.UpdateAutoAnimateEntries(previewEntries);
                    ScenePreviewManager.SampleAtDepth(previewDepth, previewEntries);
                }
            }

            // Depth slider with value label
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Depth", GUILayout.Width(40));
            GUI.enabled = canPreview && ScenePreviewManager.IsPreviewing;
            EditorGUI.BeginChangeCheck();
            previewDepth = GUILayout.HorizontalSlider(previewDepth, 0f, 1f);
            if (EditorGUI.EndChangeCheck() && ScenePreviewManager.IsPreviewing && previewEntries != null)
            {
                ScenePreviewManager.SampleAtDepth(previewDepth, previewEntries);
            }
            GUI.enabled = true;
            EditorGUILayout.LabelField(previewDepth.ToString("F2"),
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight },
                GUILayout.Width(32));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // Transport buttons row
            GUI.enabled = canPreview;
            EditorGUILayout.BeginHorizontal();

            if (!ScenePreviewManager.IsPreviewing)
            {
                if (GUILayout.Button("Preview", GUILayout.Height(26)))
                {
                    previewEntries = BuildPreviewThresholds();

                    var targetPaths = GetPreviewTargetPaths();

                    Vector3 focusPos = Vector3.zero;
                    if (config.avatarRoot != null && targetPaths.Count > 0)
                    {
                        var target = config.avatarRoot.transform.Find(targetPaths[0]);
                        if (target != null) focusPos = target.position;
                    }

                    // Startup needs to be atomic: if any of Start/HideUngenerated/
                    // SampleAtDepth throws, we can end up with IsPreviewing=true
                    // but no valid snapshot state, which makes the next Stop run
                    // restore paths against garbage. Wrap the whole sequence and
                    // roll back on any exception.
                    try
                    {
                        ScenePreviewManager.StartPreview(config.avatarRoot, targetPaths, focusPos);
                        HideUngeneratedOverlaysForPreview();
                        ScenePreviewManager.SampleAtDepth(previewDepth, previewEntries);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[SPS Effects] Preview startup failed: {e.Message}");
                        if (ScenePreviewManager.IsPreviewing)
                            ScenePreviewManager.StopPreview();
                        RestoreAllPreviewVisibility();
                        previewEntries = null;
                    }
                }
            }
            else
            {
                // Animate / Stop Animation toggle
                GUI.enabled = canPreview && previewEntries != null;
                if (!ScenePreviewManager.IsAutoAnimating)
                {
                    if (GUILayout.Button("Animate", GUILayout.Height(26)))
                    {
                        previewDepth = 0f;
                        ScenePreviewManager.StartAutoAnimate(
                            3f,
                            previewEntries,
                            (depth) =>
                            {
                                previewDepth = depth;
                                Repaint();
                            });
                    }
                }
                else
                {
                    if (GUILayout.Button("Stop Animation", GUILayout.Height(26)))
                    {
                        ScenePreviewManager.StopAutoAnimate();
                    }
                }
                GUI.enabled = canPreview;

                // End Preview button
                if (GUILayout.Button("End Preview", GUILayout.Height(26)))
                {
                    ScenePreviewManager.StopPreview();
                    RestoreAllPreviewVisibility();
                    previewEntries = null;
                    previewDepth = 0f;
                }
            }

            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;

            // Preview visibility toggles for additional meshes
            if (ScenePreviewManager.IsPreviewing && config != null
                && config.additionalMeshes != null && config.additionalMeshes.Count > 0)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Show Overlay Meshes", EditorStyles.miniBoldLabel);

                bool anyNotGenerated = false;

                for (int i = 0; i < config.additionalMeshes.Count; i++)
                {
                    var entry = config.additionalMeshes[i];
                    var r = ResolveAdditionalRenderer(entry);
                    if (r == null) continue;

                    string name = r.gameObject.name;
                    bool isOnForPreview = previewVisibilitySnapshots.ContainsKey(entry);
                    bool effectivelyVisible = IsRendererEffectivelyVisible(r);
                    bool shown = isOnForPreview || effectivelyVisible;

                    bool notGenerated = entry.generatedMesh == null
                        && string.IsNullOrEmpty(entry.generatedMeshPath);
                    if (notGenerated) anyNotGenerated = true;

                    string suffix;
                    if (notGenerated) suffix = "  (needs regenerate)";
                    else if (isOnForPreview) suffix = "  (forced visible)";
                    else if (!effectivelyVisible) suffix = "  (hidden)";
                    else suffix = "";

                    EditorGUILayout.BeginHorizontal();
                    bool wantShown = EditorGUILayout.ToggleLeft(name + suffix, shown);
                    EditorGUILayout.EndHorizontal();

                    if (wantShown != shown)
                    {
                        if (wantShown && !effectivelyVisible)
                            SetPreviewVisibility(entry, true);
                        else if (!wantShown && isOnForPreview)
                            SetPreviewVisibility(entry, false);
                        // If the mesh was naturally visible and user toggled it off,
                        // we don't hide it - that would affect avatar state permanently.
                        // Toggle only has effect when forcing a hidden mesh visible.
                    }
                }

                if (anyNotGenerated)
                {
                    EditorGUILayout.HelpBox(
                        "Overlays marked \"needs regenerate\" are temporarily hidden in preview " +
                        "to avoid visual disconnect from the deforming primary. " +
                        "Run \"Generate & Apply\" to bake their blendshapes in.",
                        MessageType.Info);
                }
            }
        }

        // =====================================================================
        // Displacement preview
        // =====================================================================

        /// <summary>
        /// Collapsible mesh-aware preview: baseline + bell-curve bulge whose peak
        /// scales with mm, with a reference tick at the average tri edge length in
        /// the path region. Always calls exactly one HelpBox so the layout slot
        /// count is stable between Layout and Repaint events (varying it per ratio
        /// tier was causing downstream UI to collapse).
        /// </summary>
        protected void DrawDisplacementPreview()
        {
            _showDisplacementPreview = EditorGUILayout.Foldout(
                _showDisplacementPreview, "Displacement Preview", true);
            if (!_showDisplacementPreview) return;

            float displacementM = config.blendshapeDisplacement;
            float rawEdge = GetCachedAvgEdgeLength();
            bool subdivided = config.subdivideAffectedRegion && config.subdivisionPasses > 0;
            float subdivMultiplier = subdivided ? Mathf.Pow(2f, config.subdivisionPasses) : 1f;
            // Threshold = raw edge × subdivision multiplier. Represents how much
            // displacement the local topology can spread smoothly: subdivision
            // widens this because more tris interpolate the bulge, reducing the
            // per-tri size of each facet the user sees.
            float threshold = rawEdge * subdivMultiplier;
            float ratio = threshold > 0f ? displacementM / threshold : 0f;
            string edgeLabel = subdivided
                ? $"faceting threshold ≈ {threshold * 1000f:F1} mm " +
                  $"({rawEdge * 1000f:F1} mm raw × {config.subdivisionPasses} subdivision" +
                  $"{(config.subdivisionPasses == 1 ? "" : "s")})"
                : $"faceting threshold ≈ {threshold * 1000f:F1} mm (avg tri edge)";

            // Reserve full-width rect — one layout slot, consistent across events.
            const float rectHeight = 64f;
            Rect r = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.Height(rectHeight), GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
                DrawDisplacementVisualization(r, displacementM, threshold, ratio, edgeLabel);

            // One HelpBox, text/type vary. Always the same call shape — no layout drift.
            string subdivNote = subdivided
                ? $" Subdivide Region is on ({config.subdivisionPasses} " +
                  $"pass{(config.subdivisionPasses == 1 ? "" : "es")}), which multiplies the " +
                  $"threshold by {subdivMultiplier:F0}× — more tris blend the bulge, so the ratio drops."
                : " Enabling Subdivide Region doubles the threshold per pass, lowering the " +
                  "ratio — the main way to fit a large displacement on low-poly geometry.";

            string explain =
                "\n\nHow to read this: the grey baseline is your mesh surface. " +
                "The coloured bulge peak is the displacement at blendshape weight 100. " +
                "The blue tick is the faceting threshold — roughly the largest displacement " +
                "the local topology can blend smoothly. " +
                "‘×N threshold’ means the bulge peak is N times that limit: " +
                "green ≤ 1× (subtle), amber 1–2× (some faceting possible), " +
                "red > 2× (tri edges will likely be visible)." + subdivNote;

            string msg;
            MessageType type;
            if (rawEdge <= 0f)
            {
                msg = "Draw a path and assign a target mesh to see displacement-vs-topology scale." + explain;
                type = MessageType.None;
            }
            else if (ratio > 2f)
            {
                msg = $"Displacement is {ratio:F1}× the faceting threshold — " +
                      "tri faceting will likely be visible. Consider enabling Subdivide Region, " +
                      "lowering displacement, or increasing Boundary Blend Rings under " +
                      "Recalculate Normals → Advanced." + explain;
                type = MessageType.Warning;
            }
            else
            {
                msg = $"Displacement = {displacementM * 1000f:F0} mm, faceting threshold = " +
                      $"{threshold * 1000f:F1} mm → ratio ×{ratio:F2}." + explain;
                type = MessageType.None;
            }
            EditorGUILayout.HelpBox(msg, type);
        }

        private static void DrawDisplacementVisualization(
            Rect r, float displacementM, float threshold, float ratio, string thresholdLabel)
        {
            EditorGUI.DrawRect(r, new Color(0.14f, 0.14f, 0.14f));

            const float pad = 6f;
            float yBase = r.yMax - pad - 2f;
            float innerW = r.width - pad * 2f;
            if (innerW < 20f) return;

            // baseline (mesh surface reference)
            EditorGUI.DrawRect(new Rect(r.x + pad, yBase, innerW, 1f),
                new Color(0.55f, 0.55f, 0.55f));

            // vertical scale: show up to max(displacement, 2.5× threshold, small floor)
            float maxScale = Mathf.Max(displacementM, threshold * 2.5f, 0.005f);
            float pxPerMeter = (r.height - pad * 2f - 10f) / maxScale;

            // reference tick at the faceting threshold
            if (threshold > 0f)
            {
                float yEdge = yBase - threshold * pxPerMeter;
                var tickColor = new Color(0.45f, 0.65f, 1f, 0.9f);
                for (float x = r.x + pad; x < r.xMax - pad; x += 4f)
                    EditorGUI.DrawRect(new Rect(x, yEdge, 2f, 1f), tickColor);
                GUI.Label(new Rect(r.x + pad + 2f, yEdge - 14f, r.width - pad * 2f - 4f, 14f),
                    thresholdLabel,
                    new GUIStyle(EditorStyles.miniLabel)
                    { normal = { textColor = tickColor } });
            }

            Color curveColor =
                ratio > 2f ? new Color(1f, 0.40f, 0.25f) :
                ratio > 1f ? new Color(1f, 0.78f, 0.25f) :
                             new Color(0.40f, 0.85f, 0.45f);
            Color fillColor = new Color(curveColor.r, curveColor.g, curveColor.b, 0.25f);

            // bell curve bulge
            float peakH = displacementM * pxPerMeter;
            const int samples = 80;
            float colW = innerW / samples;
            for (int i = 0; i < samples; i++)
            {
                float tx = (i + 0.5f) / samples;
                float g = Mathf.Exp(-Mathf.Pow((tx - 0.5f) / 0.25f, 2f));
                float h = g * peakH;
                float x = r.x + pad + i * colW;
                EditorGUI.DrawRect(new Rect(x, yBase - h, colW + 1f, h), fillColor);
                EditorGUI.DrawRect(new Rect(x, yBase - h - 1f, colW + 1f, 2f), curveColor);
            }

            string peakLabel = threshold > 0f
                ? $"{displacementM * 1000f:F0} mm  (×{ratio:F2} threshold)"
                : $"{displacementM * 1000f:F0} mm";
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = curveColor }
            };
            GUI.Label(new Rect(r.center.x - 100f, yBase - peakH - 16f, 200f, 14f),
                peakLabel, labelStyle);
        }

        // Raw mesh-region edge length. Subdivision effect is applied at display time
        // (threshold = raw × 2^passes) rather than baked into the cache, so toggling
        // Subdivide Region updates the visualization without recomputing mesh metrics.
        private float GetCachedAvgEdgeLength()
        {
            int key = ComputeAvgEdgeCacheKey();
            if (key != _avgEdgeCacheKey)
            {
                _avgEdgeCacheKey = key;
                _cachedAvgEdge = BlendshapeGenerator.ComputeAverageEdgeLengthInRegion(
                    targetRenderer,
                    config != null ? config.pathWaypoints : null,
                    config != null && config.avatarRoot != null
                        ? config.avatarRoot.transform
                        : null);
            }
            return _cachedAvgEdge;
        }

        private int ComputeAvgEdgeCacheKey()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (targetRenderer != null ? targetRenderer.GetInstanceID() : 0);
                h = h * 31 + (targetRenderer != null && targetRenderer.sharedMesh != null
                    ? targetRenderer.sharedMesh.GetInstanceID() : 0);
                if (config != null && config.pathWaypoints != null)
                {
                    h = h * 31 + config.pathWaypoints.Count;
                    foreach (var wp in config.pathWaypoints)
                    {
                        h = h * 31 + wp.localPosition.GetHashCode();
                        h = h * 31 + wp.radius.GetHashCode();
                        h = h * 31 + wp.aspectRatio.GetHashCode();
                    }
                }
                return h;
            }
        }

        // =====================================================================
        // Generate button
        // =====================================================================

        protected void DrawGenerateButton()
        {

            GUI.enabled = config.IsValid();

            // Debug: dump blend tree entries to console (developer flag)
            if (FeatureFlags.DebugUiEnabled &&
                GUILayout.Button("Debug: Log Blend Tree", EditorStyles.miniButton))
            {
                var debugEntries = BuildPreviewThresholds();
                Debug.Log($"[{EffectName}] Blend tree has {debugEntries.Count} entries.");
                foreach (var entry in debugEntries)
                {
                    var bindings = AnimationUtility.GetCurveBindings(entry.clip);
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"  depth={entry.threshold:F3} → ");
                    foreach (var b in bindings)
                    {
                        var curve = AnimationUtility.GetEditorCurve(entry.clip, b);
                        float val = curve != null ? curve.Evaluate(0f) : 0f;
                        string prop = b.propertyName.Replace("blendShape.", "");
                        sb.Append($"{prop}={val:F1} ");
                    }
                    Debug.Log(sb.ToString());
                }
            }

            if (GUILayout.Button("Generate & Apply to Avatar", GUILayout.Height(32)))
            {
                try
                {
                    // Collect depth parameters from enabled sockets
                    var depthParams = GetEnabledDepthParameters();
                    if (depthParams.Count == 0)
                    {
                        // Fallback to manual parameter
                        config.depthParameter = config.depthParameter.Trim();
                        if (!DepthParameterDetector.IsValidParameterName(config.depthParameter))
                        {
                            EditorUtility.DisplayDialog(EffectName,
                                "No sockets selected and no depth parameter entered.",
                                "OK");
                            return;
                        }
                        depthParams.Add(config.depthParameter);
                    }

                    // Register undo
                    Undo.RegisterFullObjectHierarchyUndo(
                        config.avatarRoot, $"{EffectName} Generate");

                    // Generate assets - pass all parameters for multi-layer blend tree
                    config.depthParameter = depthParams[0]; // primary for preview
                    string controllerPath = GenerateAssetsMulti(depthParams);

                    // Apply VRCFury FullController with every depth parameter so
                    // multi-socket controllers keep all globals registered.
                    var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                        controllerPath);
                    string goName = config.GetGameObjectName(EffectName);
                    var controllerGo = VRCFuryIntegration.CreateFullController(
                        config.avatarRoot, goName, controller, depthParams);
                    if (controllerGo == null)
                    {
                        statusMessage = "Generation failed: could not configure the VRCFury Full Controller.";
                        statusType = MessageType.Error;
                        return;
                    }

                    // Save config
                    if (!SaveConfig())
                    {
                        // SaveConfig already wrote an error message; don't overwrite it
                        // with a misleading "Generated successfully" line.
                        return;
                    }

                    statusMessage = $"Generated successfully! {goName} added to avatar.";
                    statusType = MessageType.Info;
                }
                catch (Exception e)
                {
                    statusMessage = $"Generation failed: {e.Message}";
                    statusType = MessageType.Error;
                    Debug.LogException(e);
                }
            }

            GUI.enabled = true;
        }

        // =====================================================================
        // Config persistence
        // =====================================================================

        /// <summary>
        /// If the current config has unsaved changes, prompts the user to save,
        /// discard, or cancel. Returns true if the caller should proceed with the
        /// action that would replace the working config, false if the user cancelled
        /// (including the case where "Save first" fails because save validation
        /// rejected the config).
        /// </summary>
        private bool ConfirmDiscardUnsavedIfNeeded()
        {
            string savedPath = trackedAssetPath;
            ConfigState state = GetCurrentConfigState(savedPath);
            if (state != ConfigState.Dirty && state != ConfigState.Renamed)
                return true;  // nothing to discard

            string name = string.IsNullOrWhiteSpace(config.configurationName)
                ? "This configuration"
                : $"\"{config.configurationName}\"";

            // DisplayDialogComplex returns: 0 = ok (primary), 1 = cancel, 2 = alt
            int choice = EditorUtility.DisplayDialogComplex(
                EffectName,
                $"{name} has unsaved changes.\n\nWhat would you like to do?",
                "Save first",            // ok  (index 0, primary action)
                "Cancel",                // cancel (index 1)
                "Discard and continue"); // alt (index 2)

            switch (choice)
            {
                case 0:  // Save first
                    if (string.IsNullOrWhiteSpace(config.configurationName))
                    {
                        EditorUtility.DisplayDialog(EffectName,
                            "Enter a configuration name before saving.", "OK");
                        return false;
                    }
                    return SaveConfig();
                case 2: return true;             // Discard and continue
                case 1:
                default: return false;           // Cancel
            }
        }

        /// <summary>
        /// Loads a persisted config asset as the new working copy. Tracks its path,
        /// reconnects object references, refreshes sockets, and captures the clean
        /// snapshot so the next OnGUI classifies it as Saved rather than Dirty.
        /// </summary>
        private void LoadConfigFromAsset(TConfig asset, string assetPath)
        {
            if (!ConfirmDiscardUnsavedIfNeeded()) return;
            config = InstantiateWorkingCopy(asset);
            selectedSocketIndex = config.selectedSocketIndex;
            ReconnectObjectReferences();
            if (config.avatarRoot != null)
            {
                RefreshSockets();
            }
            else
            {
                detectedSockets.Clear();
                selectedSocketIndex = -1;
            }
            trackedAssetPath = assetPath;
            CaptureSavedJson();
            Repaint();
        }

        /// <summary>
        /// Starts a pristine config: clears the tracked path, replaces the working
        /// copy with a fresh default, captures the clean snapshot. Next state is New.
        /// </summary>
        private void BeginNewConfig()
        {
            if (!ConfirmDiscardUnsavedIfNeeded()) return;
            trackedAssetPath = "";
            config = CreateDefaultConfig();
            detectedSockets.Clear();
            selectedSocketIndex = config.selectedSocketIndex;
            CaptureSavedJson();
            Repaint();
        }

        /// <summary>
        /// Starts a new config preloaded with the current working config's settings.
        /// Clears the tracked path and suffixes the name with " Copy" so Save creates
        /// a separate asset. Next state is New.
        /// </summary>
        /// <summary>
        /// Produces a "Copy" variant of a base configuration name that doesn't
        /// accumulate " Copy" suffixes across repeated duplicates. "X" → "X Copy",
        /// "X Copy" → "X Copy 2", "X Copy N" → "X Copy N+1".
        /// </summary>
        private static string NextCopyName(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
                return "Copy";

            const string copySuffix = " Copy";

            // "X Copy N" — find trailing " Copy <integer>" and increment the integer.
            int lastSpace = baseName.LastIndexOf(' ');
            if (lastSpace > 0 && int.TryParse(baseName.Substring(lastSpace + 1), out int n))
            {
                string withoutNumber = baseName.Substring(0, lastSpace);
                if (withoutNumber.EndsWith(copySuffix, System.StringComparison.Ordinal))
                    return $"{withoutNumber} {n + 1}";
            }

            // "X Copy" — append " 2" to make "X Copy 2".
            if (baseName.EndsWith(copySuffix, System.StringComparison.Ordinal))
                return $"{baseName} 2";

            // "X" — append " Copy".
            return $"{baseName}{copySuffix}";
        }

        private void DuplicateCurrentConfig()
        {
            if (config == null) return;
            var copy = InstantiateWorkingCopy(config);
            copy.configurationName = NextCopyName(config.configurationName);
            trackedAssetPath = "";
            config = copy;
            CaptureSavedJson();
            Repaint();
        }

        /// <summary>
        /// Builds and shows the dropdown menu invoked by the chevron. Structure:
        ///   + New Configuration
        ///   ----
        ///   (current avatar's configs, alphabetical, current one checkmarked)
        ///   ----
        ///   (other avatars' configs, grouped by avatar, alphabetical)
        ///   ----
        ///   Duplicate current…   (enabled iff a config is loaded)
        /// With no avatar selected: single flat list "Name - Avatar".
        /// With no saved configs at all: just "+ New Configuration" and a disabled
        /// "(no saved configurations found)" info item.
        /// </summary>
        /// <summary>
        /// GenericMenu treats '/' in GUIContent.text as a submenu separator. Replaces
        /// it with U+2215 DIVISION SLASH — visually identical, not a separator.
        /// </summary>
        private static string SafeMenuLabel(string raw)
        {
            return string.IsNullOrEmpty(raw) ? raw : raw.Replace('/', '∕');
        }

        private void ShowConfigPickerMenu()
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("+ New Configuration"), false, BeginNewConfig);
            menu.AddSeparator("");

            var all = ConfigAssetHelper.FindAllConfigs<TConfig>();

            string currentPath = trackedAssetPath;
            string currentAvatarName = config != null && config.avatarRoot != null
                ? config.avatarRoot.name
                : "";

            if (all.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("(no saved configurations found)"));
            }
            else if (string.IsNullOrEmpty(currentAvatarName))
            {
                // No avatar: flat list "Name - Avatar" sorted by avatar then name.
                all.Sort((a, b) =>
                {
                    int byAvatar = string.Compare(a.avatarRootName ?? "", b.avatarRootName ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
                    if (byAvatar != 0) return byAvatar;
                    return string.Compare(a.configurationName ?? "", b.configurationName ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
                });
                foreach (var cfg in all)
                {
                    string path = AssetDatabase.GetAssetPath(cfg);
                    string avatarName = string.IsNullOrEmpty(cfg.avatarRootName)
                        ? "Unknown Avatar" : cfg.avatarRootName;
                    string label = SafeMenuLabel($"{cfg.configurationName} - {avatarName}");
                    bool isCurrent = path == currentPath;
                    var captured = cfg;
                    string capturedPath = path;
                    menu.AddItem(new GUIContent(label), isCurrent,
                        () => LoadConfigFromAsset(captured, capturedPath));
                }
            }
            else
            {
                // Split into "this avatar" and "other avatars".
                var thisAvatar = new List<TConfig>();
                var other = new List<TConfig>();
                foreach (var cfg in all)
                {
                    if (string.Equals(cfg.avatarRootName, currentAvatarName,
                        System.StringComparison.OrdinalIgnoreCase))
                        thisAvatar.Add(cfg);
                    else
                        other.Add(cfg);
                }

                thisAvatar.Sort((a, b) => string.Compare(a.configurationName ?? "",
                    b.configurationName ?? "", System.StringComparison.OrdinalIgnoreCase));
                other.Sort((a, b) =>
                {
                    int byAvatar = string.Compare(a.avatarRootName ?? "", b.avatarRootName ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
                    if (byAvatar != 0) return byAvatar;
                    return string.Compare(a.configurationName ?? "", b.configurationName ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
                });

                foreach (var cfg in thisAvatar)
                {
                    string path = AssetDatabase.GetAssetPath(cfg);
                    bool isCurrent = path == currentPath;
                    string label = SafeMenuLabel(cfg.configurationName);
                    var captured = cfg;
                    string capturedPath = path;
                    menu.AddItem(new GUIContent(label), isCurrent,
                        () => LoadConfigFromAsset(captured, capturedPath));
                }

                if (other.Count > 0)
                {
                    menu.AddSeparator("");
                    foreach (var cfg in other)
                    {
                        string path = AssetDatabase.GetAssetPath(cfg);
                        string avatarName = string.IsNullOrEmpty(cfg.avatarRootName)
                            ? "Unknown Avatar" : cfg.avatarRootName;
                        string label = SafeMenuLabel($"{cfg.configurationName} - {avatarName}");
                        bool isCurrent = path == currentPath;
                        var captured = cfg;
                        string capturedPath = path;
                        menu.AddItem(new GUIContent(label), isCurrent,
                            () => LoadConfigFromAsset(captured, capturedPath));
                    }
                }
            }

            menu.AddSeparator("");
            if (config != null)
                menu.AddItem(new GUIContent("Duplicate current…"), false, DuplicateCurrentConfig);
            else
                menu.AddDisabledItem(new GUIContent("Duplicate current…"));

            menu.ShowAsContext();
        }

        /// <summary>
        /// Lightweight autosave: copies the in-memory config's state back into the
        /// on-disk asset. Called at every lifecycle moment where the in-memory
        /// ScriptableObject is about to be destroyed (assembly reload, play-mode
        /// entry, window close) so the user never loses unsaved edits.
        ///
        /// No-op if the config has never been manually saved (no tracked path
        /// yet) - the user needs to click Save once to pick a name/location.
        /// </summary>
        protected void AutoSaveConfig()
        {
            if (config == null) return;
            string path = trackedAssetPath;
            if (string.IsNullOrEmpty(path)) return;

            // If configurationName has changed since the last manual Save the
            // expected stem no longer matches the tracked file, so bail -
            // the next manual Save creates the new file and deletes the old.
            if (System.IO.Path.GetFileNameWithoutExtension(path) != GetConfigAssetStem())
                return;

            var disk = AssetDatabase.LoadAssetAtPath<TConfig>(path);
            if (disk == null) return;  // asset was deleted; can't autosave, skip

            // Mirror in-memory state into the disk asset. SetDirty marks it for
            // Unity to persist at the next natural save point - explicit SaveAssets
            // is unsafe during beforeAssemblyReload.
            if (config.avatarRoot != null)
                config.avatarRootName = config.avatarRoot.name;
            config.selectedSocketIndex = selectedSocketIndex;

            // Skip the disk write if nothing changed since the last save/capture.
            // The sync mutations above are idempotent against lastSavedJson: if the
            // avatar reference and socket index haven't changed, ToJson yields the
            // same bytes.
            if (EditorJsonUtility.ToJson(config) == lastSavedJson)
                return;

            EditorUtility.CopySerialized(config, disk);
            EditorUtility.SetDirty(disk);
            CaptureSavedJson();
        }

        protected bool SaveConfig()
        {
            // Guard: if config got destroyed (e.g. domain reload during Generate),
            // bail out BEFORE touching disk - otherwise we'd delete the existing
            // asset and then fail to recreate it, leaving nothing behind.
            if (config == null)
            {
                Debug.LogError("[SPS Effects] Cannot save - config has been destroyed. " +
                    "The saved asset (if any) was not modified.");
                statusMessage = "Cannot save - config was lost. Reopen the window and retry.";
                statusType = MessageType.Error;
                return false;
            }

            // Persist preferences that don't survive asset serialization
            if (config.avatarRoot != null)
                config.avatarRootName = config.avatarRoot.name;
            config.selectedSocketIndex = selectedSocketIndex;

            string folder = config.GetConfigFolder();
            if (string.IsNullOrEmpty(folder))
            {
                Debug.LogError("[SPS Effects] Cannot save - avatar root is not set. " +
                    "Assign an avatar before saving.");
                statusMessage = "Cannot save - assign an avatar first.";
                statusType = MessageType.Error;
                return false;
            }
            string configPath = $"{folder}/{GetConfigAssetStem()}.asset";

            // Create the snapshot FIRST - if config is somehow broken, this
            // throws and we haven't touched the disk yet.
            TConfig snapshot;
            try
            {
                snapshot = Instantiate(config);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SPS Effects] Cannot snapshot config for save: {e.Message}. " +
                    "The saved asset (if any) was not modified.");
                statusMessage = "Cannot save - snapshot failed. See Console for details.";
                statusType = MessageType.Error;
                return false;
            }

            // Capture the currently tracked path BEFORE touching disk so we
            // can detect a rename (old path != new path) and delete the
            // orphan after the new asset is written.
            string oldPath = trackedAssetPath;

            // Once CreateAsset succeeds the AssetDatabase owns the snapshot's
            // lifetime - we must not DestroyImmediate it or we delete the
            // on-disk asset. Track ownership so the finally block only
            // cleans up when no branch took over.
            bool snapshotHandedOff = false;
            try
            {
                // Strip the "(Clone)" suffix that Instantiate appends. Unity's
                // EditorUtility.CopySerialized copies the m_Name field along
                // with everything else, so if we don't reset the snapshot's
                // name here, each save accumulates another "(Clone)" on the
                // persisted asset.
                string desiredAssetName = System.IO.Path.GetFileNameWithoutExtension(configPath);
                snapshot.name = desiredAssetName;

                // Ensure folder exists WITHOUT cleaning - GenerateAssets already
                // placed the controller and clips here, don't delete them
                SpsAnimationUtility.EnsureFolder(folder);

                var existing = AssetDatabase.LoadAssetAtPath<TConfig>(configPath);
                if (existing != null && oldPath != configPath)
                {
                    // Writing to a path that already has an asset we weren't
                    // tracking - this would silently overwrite another user's
                    // config. Confirm before clobbering.
                    bool confirmed = EditorUtility.DisplayDialog(EffectName,
                        $"A configuration already exists at:\n\n{configPath}\n\n" +
                        "Saving will overwrite it. Continue?",
                        "Overwrite", "Cancel");
                    if (!confirmed)
                    {
                        statusMessage = "Save cancelled - another configuration already has that name.";
                        statusType = MessageType.Info;
                        return false;
                    }
                }
                if (existing != null)
                {
                    // Overwrite in place - keeps the existing asset GUID and
                    // avoids the delete+create race that lost configs when
                    // Instantiate failed.
                    EditorUtility.CopySerialized(snapshot, existing);
                    // Some Unity versions re-set m_Name during CopySerialized;
                    // force it back so the asset is always clean.
                    existing.name = desiredAssetName;
                    EditorUtility.SetDirty(existing);
                    DestroyImmediate(snapshot);
                    // Post-destroy fake-null means the finally skips cleanly
                    // without needing the handoff flag.
                }
                else
                {
                    AssetDatabase.CreateAsset(snapshot, configPath);
                    snapshotHandedOff = true;
                }
                AssetDatabase.SaveAssets();

                // Renamed config: clean up the old asset so it doesn't
                // orphan. DeleteAsset is a no-op on missing paths. Swallow
                // failures - an orphan is a usability hiccup, not data loss.
                if (!string.IsNullOrEmpty(oldPath) && oldPath != configPath)
                {
                    try { AssetDatabase.DeleteAsset(oldPath); }
                    catch (Exception cleanupError)
                    {
                        Debug.LogWarning(
                            $"[SPS Effects] Saved new config at {configPath} but " +
                            $"could not delete old asset at {oldPath}: {cleanupError.Message}");
                    }
                }

                trackedAssetPath = configPath;
                CaptureSavedJson();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(
                    $"[SPS Effects] SaveConfig failed while writing asset: {e.Message}. " +
                    "Previous saved state (if any) is unchanged.");
                statusMessage = "Save failed - see Console for details.";
                statusType = MessageType.Error;
                return false;
            }
            finally
            {
                // Release the snapshot when the write path threw before any
                // branch took ownership. Skips the fake-null (post-destroy)
                // and CreateAsset-succeeded cases automatically.
                if (!snapshotHandedOff && snapshot != null)
                    DestroyImmediate(snapshot);
            }
        }

        /// <summary>
        /// Classifies the live config against its tracked on-disk asset. Recomputes only
        /// on EventType.Layout (once per repaint) to avoid per-keystroke
        /// EditorJsonUtility.ToJson on the whole ScriptableObject, which was the source
        /// of visible typing lag in the name field.
        /// </summary>
        private ConfigState GetCurrentConfigState(string savedPath)
        {
            // Recompute only on Layout events (once per repaint). Every other event
            // type (Repaint, KeyDown, KeyUp, MouseMove, ...) returns the cached value.
            if (_cachedStateInitialized && Event.current != null &&
                Event.current.type != EventType.Layout)
            {
                return _cachedState;
            }

            bool fileExists = !string.IsNullOrEmpty(savedPath)
                && AssetDatabase.LoadAssetAtPath<TConfig>(savedPath) != null;
            string currentJson = config != null ? EditorJsonUtility.ToJson(config) : "";
            _cachedState = ConfigStateDetector.Detect(
                currentConfigJson: currentJson,
                lastSavedJson:     lastSavedJson,
                savedPath:         savedPath,
                expectedAssetStem: GetConfigAssetStem(),
                savedFileExists:   fileExists);
            _cachedStateInitialized = true;
            return _cachedState;
        }

        // Cached style so we don't allocate a GUIStyle per frame.
        private static GUIStyle s_statusBadgeStyle;

        private static (string glyph, string text, Color color) StatusBadgeContent(
            ConfigState state, bool isProSkin)
        {
            // Tints validated against both Unity skins. Glyph + text for wide windows,
            // glyph-only for narrow (see DrawStatusBadge).
            switch (state)
            {
                case ConfigState.Saved:
                    return ("✓", "Saved",
                        isProSkin ? new Color(0.40f, 0.73f, 0.42f)
                                  : new Color(0.30f, 0.69f, 0.31f));
                case ConfigState.Dirty:
                    return ("●", "Unsaved",
                        isProSkin ? new Color(1.00f, 0.72f, 0.30f)
                                  : new Color(1.00f, 0.60f, 0.00f));
                case ConfigState.Renamed:
                    return ("↦", "Will rename",
                        isProSkin ? new Color(0.39f, 0.71f, 0.96f)
                                  : new Color(0.13f, 0.59f, 0.95f));
                case ConfigState.New:
                default:
                    return ("·", "New", Color.gray);
            }
        }

        private static void DrawStatusBadge(ConfigState state)
        {
            if (s_statusBadgeStyle == null)
            {
                s_statusBadgeStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft
                };
                // Scrub all state backgrounds - GUILayout.Label shouldn't light up on
                // hover, but the inherited miniLabel carries them in some Unity skins.
                s_statusBadgeStyle.normal.background = null;
                s_statusBadgeStyle.hover.background = null;
                s_statusBadgeStyle.active.background = null;
                s_statusBadgeStyle.focused.background = null;
                s_statusBadgeStyle.onNormal.background = null;
                s_statusBadgeStyle.onHover.background = null;
                s_statusBadgeStyle.onActive.background = null;
                s_statusBadgeStyle.onFocused.background = null;
            }
            var (glyph, text, color) = StatusBadgeContent(state, EditorGUIUtility.isProSkin);
            bool narrow = EditorGUIUtility.currentViewWidth < 370f;
            string label = narrow ? glyph : $"{glyph} {text}";

            // Snapshot every state that GUILayout.Label might render from, set them
            // all to the target color, then restore on exit. This kills the hover
            // tint (miniLabel.hover.textColor differs from normal).
            Color prevNormal   = s_statusBadgeStyle.normal.textColor;
            Color prevHover    = s_statusBadgeStyle.hover.textColor;
            Color prevActive   = s_statusBadgeStyle.active.textColor;
            Color prevFocused  = s_statusBadgeStyle.focused.textColor;
            Color prevOnNormal = s_statusBadgeStyle.onNormal.textColor;
            Color prevOnHover  = s_statusBadgeStyle.onHover.textColor;
            s_statusBadgeStyle.normal.textColor   = color;
            s_statusBadgeStyle.hover.textColor    = color;
            s_statusBadgeStyle.active.textColor   = color;
            s_statusBadgeStyle.focused.textColor  = color;
            s_statusBadgeStyle.onNormal.textColor = color;
            s_statusBadgeStyle.onHover.textColor  = color;
            try
            {
                GUILayout.Label(label, s_statusBadgeStyle, GUILayout.Width(narrow ? 24f : 90f));
            }
            finally
            {
                s_statusBadgeStyle.normal.textColor   = prevNormal;
                s_statusBadgeStyle.hover.textColor    = prevHover;
                s_statusBadgeStyle.active.textColor   = prevActive;
                s_statusBadgeStyle.focused.textColor  = prevFocused;
                s_statusBadgeStyle.onNormal.textColor = prevOnNormal;
                s_statusBadgeStyle.onHover.textColor  = prevOnHover;
            }
        }

        // =====================================================================
        // Utility
        // =====================================================================

        protected static string GetRelativePath(Transform root, Transform target)
            => BaseEffectConfig.GetRelativePath(root, target);

        private const string DefaultConfigName = "Default";

        /// <summary>
        /// The canonical file stem for the current config's on-disk asset
        /// (no folder, no ".asset"). AutoSaveConfig compares against this to
        /// detect a rename; SaveConfig builds the full path from it. Must
        /// stay single-source so the two can't drift.
        /// </summary>
        private string GetConfigAssetStem()
        {
            string name = config != null ? config.configurationName : null;
            string safeName = string.IsNullOrWhiteSpace(name)
                ? DefaultConfigName
                : BaseEffectConfig.SanitizeFileName(name);
            return $"{ConfigAssetPrefix}_{safeName}";
        }

        /// <summary>
        /// Resolves the SkinnedMeshRenderer referenced by a TrackedMesh entry
        /// (via its rendererPath under the avatar root). Returns null if not found.
        /// </summary>
        protected SkinnedMeshRenderer ResolveAdditionalRenderer(TrackedMesh entry)
        {
            if (config == null || config.avatarRoot == null) return null;
            if (entry == null || string.IsNullOrEmpty(entry.rendererPath)) return null;
            var t = config.avatarRoot.transform.Find(entry.rendererPath);
            return t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
        }

        /// <summary>
        /// Returns whether the renderer is effectively visible (GameObject active
        /// in hierarchy AND renderer component enabled).
        /// </summary>
        protected bool IsRendererEffectivelyVisible(SkinnedMeshRenderer r)
        {
            return r != null && r.gameObject.activeInHierarchy && r.enabled;
        }

        /// <summary>
        /// Forces the renderer (and any inactive ancestors up to the avatar root)
        /// visible, recording original states so they can be restored later.
        /// </summary>
        protected void SetPreviewVisibility(TrackedMesh entry, bool visible)
        {
            var r = ResolveAdditionalRenderer(entry);
            if (r == null) return;

            if (visible)
            {
                if (previewVisibilitySnapshots.ContainsKey(entry)) return;

                var snapshot = new PreviewVisibilitySnapshot
                {
                    activatedAncestors = new List<GameObject>()
                };
                Transform root = config.avatarRoot != null ? config.avatarRoot.transform : null;

                Transform t = r.transform;
                while (t != null)
                {
                    var go = t.gameObject;

                    // Remember the FIRST observed activeSelf for this ancestor.
                    // Subsequent entries see the (now-forced-active) state, so
                    // only the first reading is the true "original".
                    if (!ancestorOriginalActive.ContainsKey(go))
                        ancestorOriginalActive[go] = go.activeSelf;

                    ancestorActivationRefcount.TryGetValue(go, out int rc);
                    ancestorActivationRefcount[go] = rc + 1;
                    snapshot.activatedAncestors.Add(go);

                    if (!go.activeSelf)
                    {
                        Undo.RecordObject(go, "SPS Preview Toggle");
                        go.SetActive(true);
                    }

                    if (t == root) break;
                    t = t.parent;
                }

                if (!r.enabled)
                {
                    Undo.RecordObject(r, "SPS Preview Toggle");
                    snapshot.rendererEnabledChanged = true;
                    snapshot.rendererEnabledOriginal = false;
                    r.enabled = true;
                }

                previewVisibilitySnapshots[entry] = snapshot;
            }
            else
            {
                if (!previewVisibilitySnapshots.TryGetValue(entry, out var snapshot)) return;
                foreach (var go in snapshot.activatedAncestors)
                {
                    if (go == null) continue;
                    if (!ancestorActivationRefcount.TryGetValue(go, out int rc)) continue;
                    rc--;
                    if (rc > 0)
                    {
                        ancestorActivationRefcount[go] = rc;
                        continue;
                    }
                    // Last entry holding this ancestor active - restore and forget.
                    ancestorActivationRefcount.Remove(go);
                    if (ancestorOriginalActive.TryGetValue(go, out bool original))
                    {
                        if (go.activeSelf != original)
                        {
                            Undo.RecordObject(go, "SPS Preview Toggle");
                            go.SetActive(original);
                        }
                        ancestorOriginalActive.Remove(go);
                    }
                }
                if (snapshot.rendererEnabledChanged && r != null)
                    r.enabled = snapshot.rendererEnabledOriginal;
                previewVisibilitySnapshots.Remove(entry);
            }
        }

        /// <summary>
        /// Hides any ungenerated additional meshes when preview starts, so they
        /// don't float relative to the deforming primary. Called by PreviewStart.
        /// </summary>
        protected void HideUngeneratedOverlaysForPreview()
        {
            if (config == null || config.additionalMeshes == null) return;

            foreach (var entry in config.additionalMeshes)
            {
                bool notGenerated = entry.generatedMesh == null
                    && string.IsNullOrEmpty(entry.generatedMeshPath);
                if (!notGenerated) continue;

                var r = ResolveAdditionalRenderer(entry);
                if (r == null) continue;

                if (!r.enabled) continue;  // already hidden via component

                Undo.RecordObject(r, "SPS Preview Hide Ungenerated");
                previewHideSnapshots[entry] = r.enabled;
                r.enabled = false;
            }
        }

        /// <summary>
        /// Restores every preview-visibility modification we've made this session.
        /// Called when preview ends or the window closes. Uses the session-wide
        /// ancestorOriginalActive map as the source of truth for "what was each
        /// ancestor's original state" - per-entry snapshots only carry the list
        /// of which ancestors an entry contributed to activating.
        /// </summary>
        protected void RestoreAllPreviewVisibility()
        {
            try
            {
                // Restore per-renderer enabled flags for entries that were
                // force-visible (the ancestor state is handled by the session
                // maps below).
                foreach (var kv in previewVisibilitySnapshots)
                {
                    var snap = kv.Value;
                    if (snap.rendererEnabledChanged)
                    {
                        var r = ResolveAdditionalRenderer(kv.Key);
                        if (r != null) r.enabled = snap.rendererEnabledOriginal;
                    }
                }

                // Restore ancestor active state in a single pass keyed off the
                // original-state map so shared ancestors are only touched once.
                foreach (var kv in ancestorOriginalActive)
                {
                    var go = kv.Key;
                    if (go == null) continue;
                    if (go.activeSelf != kv.Value)
                        go.SetActive(kv.Value);
                }

                // Restore renderer.enabled for auto-hidden ungenerated overlays.
                foreach (var kv in previewHideSnapshots)
                {
                    var r = ResolveAdditionalRenderer(kv.Key);
                    if (r != null) r.enabled = kv.Value;
                }
            }
            finally
            {
                previewVisibilitySnapshots.Clear();
                previewHideSnapshots.Clear();
                ancestorActivationRefcount.Clear();
                ancestorOriginalActive.Clear();
            }
        }

        /// <summary>
        /// Restores a single additional mesh to its tracked original.
        /// </summary>
        protected void RestoreSingleAdditionalMesh(TrackedMesh entry)
        {
            if (entry == null) return;
            var r = ResolveAdditionalRenderer(entry);
            var orig = MeshReferenceTracker.ResolveMesh(entry, "original");
            if (r != null && orig != null)
            {
                Undo.RecordObject(r, "Restore Additional Mesh");
                r.sharedMesh = orig;
            }
            MeshReferenceTracker.StoreMesh(entry, "original", null);
            MeshReferenceTracker.StoreMesh(entry, "generated", null);
        }

        /// <summary>
        /// Restores the primary mesh and every additional mesh to its tracked original.
        /// </summary>
        protected void RestoreAllMeshes()
        {
            if (ScenePreviewManager.IsPreviewing)
            {
                ScenePreviewManager.StopPreview();
                previewEntries = null;
            }

            // Primary
            var primaryOrig = MeshReferenceTracker.ResolveMesh(config, "original");
            if (targetRenderer != null && primaryOrig != null)
            {
                Undo.RecordObject(targetRenderer, "Restore All Meshes");
                targetRenderer.sharedMesh = primaryOrig;
            }
            MeshReferenceTracker.StoreMesh(config, "original", null);
            MeshReferenceTracker.StoreMesh(config, "generated", null);

            // Additionals
            if (config.additionalMeshes != null)
            {
                foreach (var entry in config.additionalMeshes)
                    RestoreSingleAdditionalMesh(entry);
            }

            statusMessage = "Restored all meshes to their originals.";
            statusType = MessageType.Info;
        }
    }
}
