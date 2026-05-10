using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Editor window for the SPS Normal Map Baker. Picks a BulgeConfig, shows
    /// per-material + overlay controls, runs the bake, writes PNGs, applies
    /// to Poiyomi materials, and generates the driving clip + controller +
    /// VRCFury FullController.
    /// </summary>
    public class SpsNormalMapBakerWindow : EditorWindow
    {
        private BulgeConfig _config;
        private NormalMapBakerSettings _settings;
        private Vector2 _scroll;

        private readonly List<string> _logLines = new List<string>();

        [MenuItem("Tools/Kai/SPS/Normal Map Baker")]
        public static void ShowWindow()
        {
            var win = GetWindow<SpsNormalMapBakerWindow>("SPS Normal Map Baker");
            win.minSize = new Vector2(480, 520);
            win.Show();
        }

        /// <summary>Opens the window pre-scoped to the given Bulge config.</summary>
        public static void OpenFor(BulgeConfig config)
        {
            var win = GetWindow<SpsNormalMapBakerWindow>("SPS Normal Map Baker");
            win._config = config;
            win._settings = NormalMapBakerSettings.FindOrCreateFor(config);
            win.minSize = new Vector2(480, 520);
            win.Show();
        }

        private void OnEnable()
        {
            if (_config != null && _settings == null)
                _settings = NormalMapBakerSettings.FindOrCreateFor(_config);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "Automatic material setup is Poiyomi-only (unlocks via Thry, " +
                "writes to _DetailNormalMap, animates _DetailNormalMapScale " +
                "from the SPS depth parameter). Non-Poiyomi materials get a " +
                "manual texture-slot picker after the bake; animation wiring " +
                "on those is your responsibility.",
                MessageType.Info);
            EditorGUILayout.Space();

            DrawConfigPicker();
            EditorGUILayout.Space();

            if (_config == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick a Bulge config to enable baking.",
                    MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            if (_settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Can't create NormalMapBakerSettings for a config that isn't " +
                    "saved as an asset. Save the config first.",
                    MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawBakeSettings();
            EditorGUILayout.Space();

            DrawTargetMaterials();
            EditorGUILayout.Space();

            DrawBakeButton();
            EditorGUILayout.Space();

            DrawLog();

            EditorGUILayout.EndScrollView();
        }

        private void DrawConfigPicker()
        {
            EditorGUILayout.LabelField("Config", EditorStyles.boldLabel);

            var newConfig = (BulgeConfig)EditorGUILayout.ObjectField(
                "Bulge Config", _config, typeof(BulgeConfig), false);
            if (newConfig != _config)
            {
                _config = newConfig;
                _settings = _config != null
                    ? NormalMapBakerSettings.FindOrCreateFor(_config)
                    : null;
                _logLines.Clear();
            }
        }

        private void DrawBakeSettings()
        {
            EditorGUILayout.LabelField("Bake Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            _settings.resolution = (NormalMapBakerSettings.ResolutionPreset)
                EditorGUILayout.EnumPopup("Resolution", _settings.resolution);

            _settings.referenceSubdivision = EditorGUILayout.IntSlider(
                new GUIContent("Reference Subdivision",
                    "Subdivision passes applied in memory before baking. Each " +
                    "pass ~4x tri count in the path region. 3 is a good default; " +
                    "4+ may be slow on large paths."),
                _settings.referenceSubdivision, 1, 5);
            if (_settings.referenceSubdivision >= 4)
                EditorGUILayout.HelpBox(
                    "High subdivision counts explode vert count (~4x per pass). " +
                    "Bake may take a while.",
                    MessageType.None);

            _settings.seamPaddingTexels = EditorGUILayout.IntSlider(
                new GUIContent("Seam Padding",
                    "Texels of edge-dilation applied to UV seams. Prevents " +
                    "bilinear filtering from leaking neutral grey across seams."),
                _settings.seamPaddingTexels, 0, 8);

            _settings.blendStrengthAtPeak = EditorGUILayout.Slider(
                new GUIContent("Blend Strength at Peak",
                    "Target value for Poiyomi's _DetailNormalMapScale when the " +
                    "bulge is fully peaked. 1 = normal strength, < 1 dims, > 1 " +
                    "exaggerates."),
                _settings.blendStrengthAtPeak, 0f, 3f);

            _settings.sharedAcrossOverlays = EditorGUILayout.Toggle(
                new GUIContent("Share across compatible overlays",
                    "When on, overlay meshes with UV layouts matching the primary " +
                    "get the primary's baked PNG. When off, every mesh gets its " +
                    "own PNG."),
                _settings.sharedAcrossOverlays);

            if (EditorGUI.EndChangeCheck())
                EditorUtility.SetDirty(_settings);
        }

        private void DrawTargetMaterials()
        {
            EditorGUILayout.LabelField("Target Materials", EditorStyles.boldLabel);

            var primary = ResolveRenderer(_config.rendererPath);
            if (primary == null)
            {
                EditorGUILayout.HelpBox(
                    "Primary renderer couldn't be resolved. Make sure the config " +
                    "has a target renderer set and the avatar is loaded.",
                    MessageType.Warning);
                return;
            }

            DrawRendererRow(primary, _config.rendererPath, isPrimary: true);

            if (_config.additionalMeshes != null)
            {
                for (int i = 0; i < _config.additionalMeshes.Count; i++)
                {
                    var entry = _config.additionalMeshes[i];
                    if (entry == null || string.IsNullOrEmpty(entry.rendererPath)) continue;
                    var r = ResolveRenderer(entry.rendererPath);
                    if (r == null)
                    {
                        EditorGUILayout.HelpBox(
                            $"Overlay renderer '{entry.rendererPath}' couldn't be resolved.",
                            MessageType.Warning);
                        continue;
                    }
                    DrawRendererRow(r, entry.rendererPath, isPrimary: false);
                }
            }
        }

        private void DrawRendererRow(SkinnedMeshRenderer renderer, string path, bool isPrimary)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.ObjectField(
                isPrimary ? "Primary" : "Overlay",
                renderer, typeof(SkinnedMeshRenderer), true);
            if (!isPrimary)
            {
                var currentMode = _settings.GetOverlayMode(path);
                var newMode = (NormalMapBakerSettings.OverlayMode)
                    EditorGUILayout.EnumPopup(currentMode, GUILayout.Width(120));
                if (newMode != currentMode)
                    _settings.SetOverlayMode(path, newMode);
            }
            EditorGUILayout.EndHorizontal();

            var mats = renderer.sharedMaterials;
            if (mats == null || mats.Length == 0)
            {
                EditorGUILayout.LabelField("  (no materials)", EditorStyles.miniLabel);
            }
            else
            {
                for (int m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    bool isSelected = _settings.IsMaterialSelected(path, m);

                    EditorGUILayout.BeginHorizontal();

                    bool newSelected = EditorGUILayout.Toggle(isSelected, GUILayout.Width(18));
                    if (newSelected != isSelected)
                        _settings.SetMaterialSelected(path, m, newSelected);

                    string matName = mat != null ? mat.name : "(null)";
                    bool isPoiyomi = PoiyomiIntegration.IsPoiyomi(mat);
                    string suffix = isPoiyomi ? "" : " — not Poiyomi (manual apply only)";
                    EditorGUILayout.LabelField($"[{m}] {matName}{suffix}");

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawBakeButton()
        {
            GUI.enabled = CanBake(out string disableReason);
            if (GUILayout.Button("Bake Normal Map", GUILayout.Height(30)))
            {
                // Defer via delayCall: the bake's progress bars + manual-apply
                // popup disrupt IMGUI layout when run inside an OnGUI button.
                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        RunBake();
                    }
                    catch (System.Exception e)
                    {
                        Log($"ERROR: {e.GetType().Name}: {e.Message}");
                        Debug.LogException(e);
                        EditorUtility.ClearProgressBar();
                    }
                };
            }
            GUI.enabled = true;

            if (!string.IsNullOrEmpty(disableReason))
                EditorGUILayout.HelpBox(disableReason, MessageType.Info);
        }

        private bool CanBake(out string disableReason)
        {
            disableReason = null;
            if (_config == null) { disableReason = "Pick a Bulge config."; return false; }
            if (_settings == null) { disableReason = "Settings unavailable."; return false; }
            if (_config.avatarRoot == null)
            { disableReason = "Config has no avatar root."; return false; }
            if (_config.pathWaypoints == null || _config.pathWaypoints.Count < 2)
            { disableReason = "Config needs at least 2 path waypoints."; return false; }
            if (string.IsNullOrEmpty(_config.rendererPath))
            { disableReason = "Config has no target renderer."; return false; }

            // Baker reads the middle-position clip's curve shape for detail
            // normal timing — so the bulge has to have been Generated first.
            if (!System.IO.File.Exists(GetMiddlePositionClipPath()))
            {
                disableReason = "Run Bulge → Generate first; the baker reads its " +
                    "middle-position clip to match detail normal timing.";
                return false;
            }
            return true;
        }

        private void RunBake()
        {
            _logLines.Clear();

            var primary = ResolveRenderer(_config.rendererPath);
            if (primary == null) { Log("Primary renderer couldn't be resolved."); return; }

            int positionCount = Mathf.Max(1, _config.autoPositionCount);
            int targetPosition = positionCount / 2;

            var inputs = new NormalMapBaker.BakeInputs
            {
                primary = primary,
                avatarRoot = _config.avatarRoot.transform,
                path = _config.pathWaypoints,
                positionCount = positionCount,
                targetPosition = targetPosition,
                displacement = _config.blendshapeDisplacement,
                displacementSmoothingPasses = _config.smoothingPasses,
            };

            string outputFolder = _config.GetOutputFolder();
            SpsAnimationUtility.EnsureFolder($"{outputFolder}/NormalMap");

            Log("Baking primary...");
            var primaryResult = NormalMapBaker.BakePrimary(inputs, _settings);
            foreach (var w in primaryResult.warnings ?? new List<string>()) Log($"  {w}");
            if (primaryResult.texture == null)
            {
                Log($"Primary bake FAILED: {primaryResult.errorMessage}");
                return;
            }
            string primaryPath = NormalMapPngWriter.BuildOutputPath(
                outputFolder, _config.configurationName, SanitizeSuffix(primary.gameObject.name));
            var primaryAsset = NormalMapPngWriter.WritePng(primaryResult.texture, primaryPath);
            Object.DestroyImmediate(primaryResult.texture);
            _settings.lastBakedPrimary = primaryAsset;
            Log($"  saved {primaryPath}");

            var rendererTextures = new List<(SkinnedMeshRenderer r, string path, Texture2D tex)>
            {
                (primary, _config.rendererPath, primaryAsset),
            };

            var overlayBakes = new List<NormalMapBakerSettings.BakedOverlay>();
            if (_config.additionalMeshes != null)
            {
                foreach (var entry in _config.additionalMeshes)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.rendererPath)) continue;
                    var r = ResolveRenderer(entry.rendererPath);
                    if (r == null) continue;

                    var mode = _settings.sharedAcrossOverlays
                        ? _settings.GetOverlayMode(entry.rendererPath)
                        : NormalMapBakerSettings.OverlayMode.ForcePerMesh;

                    Log($"Baking overlay '{entry.rendererPath}' ({mode})...");
                    var overlayResult = NormalMapBaker.BakeOverlay(
                        inputs, primaryAsset, r, mode, _settings, out bool usedSharedPrimary);
                    foreach (var w in overlayResult.warnings ?? new List<string>()) Log($"  {w}");
                    if (overlayResult.texture == null)
                    {
                        Log($"  FAILED: {overlayResult.errorMessage}");
                        continue;
                    }

                    Texture2D overlayAsset;
                    if (usedSharedPrimary)
                    {
                        overlayAsset = primaryAsset;
                        Log("  reused primary's PNG (UVs compatible).");
                    }
                    else
                    {
                        string overlayPath = NormalMapPngWriter.BuildOutputPath(
                            outputFolder, _config.configurationName,
                            SanitizeSuffix(r.gameObject.name));
                        overlayAsset = NormalMapPngWriter.WritePng(overlayResult.texture, overlayPath);
                        Object.DestroyImmediate(overlayResult.texture);
                        Log($"  saved {overlayPath}");
                    }

                    overlayBakes.Add(new NormalMapBakerSettings.BakedOverlay
                    {
                        rendererPath = entry.rendererPath,
                        texture = overlayAsset,
                    });
                    rendererTextures.Add((r, entry.rendererPath, overlayAsset));
                }
            }
            _settings.lastBakedOverlays = overlayBakes;
            EditorUtility.SetDirty(_settings);

            // Apply to selected materials. Poiyomi auto-drives; non-Poiyomi
            // queued for the manual-apply popup.
            var animTargets = new List<PoiyomiIntegration.MaterialAnimationTarget>();
            var manualEntries = new List<NormalMapApplyPopup.Entry>();
            int poiyomiApplied = 0;
            foreach (var (r, path, tex) in rendererTextures)
            {
                var mats = r.sharedMaterials;
                for (int m = 0; m < (mats != null ? mats.Length : 0); m++)
                {
                    if (!_settings.IsMaterialSelected(path, m)) continue;
                    var mat = mats[m];
                    if (mat == null) continue;

                    Texture2D prior = (path == _config.rendererPath)
                        ? _settings.lastBakedPrimary
                        : FindPriorBakedOverlayFor(path);

                    var res = PoiyomiIntegration.ApplyToMaterial(mat, tex, prior);
                    Log($"  material '{mat.name}' ({path}, slot {m}): {res.outcome}" +
                        (string.IsNullOrEmpty(res.message) ? "" : $" — {res.message}"));

                    if (res.outcome == PoiyomiIntegration.ApplyOutcome.Applied)
                    {
                        animTargets.Add(new PoiyomiIntegration.MaterialAnimationTarget
                        {
                            rendererPath = path,
                            materialIndex = m,
                        });
                        poiyomiApplied++;
                    }
                    else if (res.outcome == PoiyomiIntegration.ApplyOutcome.NotPoiyomi)
                    {
                        manualEntries.Add(new NormalMapApplyPopup.Entry
                        {
                            material = mat,
                            rendererPaths = new List<string> { path },
                            targetTexture = tex,
                        });
                    }
                }
            }

            if (animTargets.Count > 0)
            {
                string clipPath = GetMiddlePositionClipPath();
                string blendshapeName = _config.GetBlendshapeName(
                    targetPosition + 1, "SPSBulge_Pos");
                var referenceCurve = PoiyomiIntegration.ReadBlendshapeCurve(
                    clipPath, _config.rendererPath, blendshapeName);
                // Bulge's blendshape weights peak at 100 (percentage).
                const float referencePeak = 100f;

                var go = PoiyomiIntegration.CreateAnimationAndController(
                    _config.avatarRoot, outputFolder,
                    _config.configurationName,
                    _config.depthParameter,
                    referenceCurve, referencePeak,
                    _settings.blendStrengthAtPeak,
                    animTargets);
                if (go != null)
                    Log($"  created {go.name} with {animTargets.Count} animated material binding(s).");
                else
                    Log("  FAILED to create controller / VRCFury FullController. " +
                        "Check if VRCFury is installed.");
            }

            AssetDatabase.SaveAssets();
            Log($"Done. Poiyomi auto-applied: {poiyomiApplied}. " +
                $"Non-Poiyomi queued for manual apply: {manualEntries.Count}.");

            if (manualEntries.Count > 0)
            {
                Log("  Opening manual apply popup...");
                NormalMapApplyPopup.Open(manualEntries);
            }
        }

        private string GetMiddlePositionClipPath()
        {
            if (_config == null) return null;
            int positionCount = Mathf.Max(1, _config.autoPositionCount);
            int middlePosition1Indexed = (positionCount / 2) + 1;
            string folder = _config.GetOutputFolder();
            return $"{folder}/SPSBulge_Pos{middlePosition1Indexed}.anim";
        }

        private Texture2D FindPriorBakedOverlayFor(string rendererPath)
        {
            if (_settings.lastBakedOverlays == null) return null;
            foreach (var entry in _settings.lastBakedOverlays)
                if (entry.rendererPath == rendererPath) return entry.texture;
            return null;
        }

        private SkinnedMeshRenderer ResolveRenderer(string path)
        {
            if (_config == null) return null;
            return BaseEffectConfig.ResolveRenderer(_config.avatarRoot, path);
        }

        private static string SanitizeSuffix(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mesh";
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return string.Join("_", s.Split(invalid)).Replace(' ', '_');
        }

        private void Log(string line)
        {
            _logLines.Add(line);
            Repaint();
        }

        private void DrawLog()
        {
            if (_logLines.Count == 0) return;
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            foreach (var line in _logLines)
                EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }
    }
}
