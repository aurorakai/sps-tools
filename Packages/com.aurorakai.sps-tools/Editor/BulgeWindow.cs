using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    public class BulgeWindow : BaseEffectWindow<BulgeConfig>
    {
        // Tracks the single live BulgeWindow instance. A duplicate OnEnable closes
        // itself and focuses this one. Reset on domain reload (statics are cleared)
        // and on OnDisable. Not persisted - rebuilt by the first OnEnable after reload.
        private static BulgeWindow s_instance;

        // Bone chain quick-setup references
        private Transform startBone;
        private Transform endBone;

        // Bone chain manual-add helper
        private Transform manualAddBone;

        private bool _showNormalAdvanced;
        private bool _showBlendshapeAdvanced;

        // Normal-delta visualization (Advanced → Show Normals).
        // Off by default; drawn via the scene-view overlay subscribed in
        // OnEnableExtra. The baked mesh, the authored-delta mask, and the
        // blend-frame read buffers are all reused across frames - the first
        // pass of this feature reallocated them per repaint and burned
        // ~2 MB/frame on a 50k-vert mesh.
        private bool _showNormals;
        private float _normalLength = 0.015f;
        private bool _highlightChanged = true;
        private Mesh _bakedNormalsMesh;
        private Mesh _normalsMaskMesh;         // identity of the mesh _hasNormalDelta belongs to
        private int _normalsMaskBlendCount;    // blendshape count baked into _hasNormalDelta
        private bool[] _hasNormalDelta;        // per-vert: did any blendshape author a normal delta?
        private Vector3[] _frameVertsBuf;
        private Vector3[] _frameNormalsBuf;
        private Vector3[] _frameTangentsBuf;
        // Mesh.vertices/.normals property getters allocate; GetVertices/GetNormals
        // reuse List buffers instead. Hand-rolled segment arrays feed one batched
        // Handles.DrawLines per color class (changed / unchanged) - same motivation
        // as the spline batching in PathDrawingTool: thousands of DrawLine calls
        // per repaint were a real GPU-submission cost.
        private readonly List<Vector3> _bakedVertsList = new List<Vector3>();
        private readonly List<Vector3> _bakedNormalsList = new List<Vector3>();
        private Vector3[] _changedSegments;
        private Vector3[] _unchangedSegments;

        // =====================================================================
        // Abstract / virtual member implementations
        // =====================================================================

        protected override string EffectName => "SPS Bulge";
        protected override Color ThemeColor => new Color(0.81f, 0.58f, 0.93f);
        protected override string ConfigAssetPrefix => "SPSBulge";

        protected override string WelcomeWindowTitle => "Welcome to Bulge Configurator";

        private static readonly IReadOnlyList<(string headline, string body)> _welcomeSteps =
            new (string headline, string body)[]
            {
                ("Add an SPS socket to your avatar",
                    "Bulge reacts to depth from a VRCFury SPS socket. If your avatar doesn't have one yet, set it up in VRCFury first."),
                ("Drop your avatar into Avatar Root",
                    "The tool detects existing SPS sockets on the avatar automatically."),
                ("Pick the Target Mesh",
                    "Choose the mesh that should bulge — usually the body. You can add extra meshes (clothing, overlays) later."),
                ("Choose Auto-Generate or Manual",
                    "Auto-Generate builds blendshapes from a path you draw on the mesh. Manual lets you point at blendshapes you already made in a 3D app."),
                ("Define where the bulge happens",
                    "In Auto-Generate, click \"Draw Path on Mesh\" and click points along the surface. In Manual, fill in your blendshape names."),
                ("Tune Displacement and Generate",
                    "Set Displacement for bulge height, preview, then click Generate to add it to your avatar."),
            };

        protected override IReadOnlyList<(string headline, string body)> WelcomeSteps =>
            _welcomeSteps;

        protected override BulgeConfig CreateDefaultConfig() => CreateInstance<BulgeConfig>();

        protected override List<(float threshold, AnimationClip clip)> BuildPreviewThresholds()
            => BulgeGenerator.BuildPreviewThresholds(config);

        protected override string GenerateAssets()
            => BulgeGenerator.Generate(config);

        protected override string GenerateAssetsMulti(List<string> depthParameters)
            => BulgeGenerator.Generate(config, depthParameters);

        protected override int ComputePreviewConfigHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + config.depthRangeStart.GetHashCode();
                h = h * 31 + config.depthRangeEnd.GetHashCode();
                h = h * 31 + config.bulgeIntensity.GetHashCode();
                h = h * 31 + config.bulgeWidth.GetHashCode();
                h = h * 31 + config.PositionCount.GetHashCode();
                h = h * 31 + (int)config.EffectiveDeformationMode;
                return h;
            }
        }

        protected override List<string> GetPreviewTargetPaths()
        {
            var targetPaths = new List<string>();
            if (config.EffectiveDeformationMode == DeformationMode.BoneScale)
            {
                if (config.boneChain.Count > 0)
                    targetPaths.Add(config.boneChain[config.boneChain.Count / 2]);
            }
            else
            {
                targetPaths.Add(config.rendererPath);
            }
            return targetPaths;
        }

        protected override void DrawEffectSpecificSections()
        {
            // Note: base OnGUI already adds Space(8) before calling this method
            DrawBulgeTravelRange();
            EditorGUILayout.Space(8);
            DrawBulgeSettings();
            EditorGUILayout.Space(8);
            DrawDeformationModeSection();
            EditorGUILayout.Space(8);
            DrawBellCurvePreview();
            // Note: base OnGUI already adds Space(8) after this method
        }

        protected override bool CanPreview()
        {
            if (!base.CanPreview()) return false;
            if (config.EffectiveDeformationMode == DeformationMode.Blendshape)
            {
                var genMesh = MeshReferenceTracker.ResolveMesh(config, "generated");
                bool hasBlendshapes = config.positionBlendshapes.Count >= 2 ||
                    (genMesh != null && genMesh.blendShapeCount > 0);
                if (!hasBlendshapes) return false;
            }
            return true;
        }

        protected override void ReconnectExtraReferences()
        {
            // Reconnect bone chain transforms (start/end cleared on load -- user can re-pick)
            startBone = null;
            endBone = null;
        }

        protected override void OnEnableExtra()
        {
            SceneView.duringSceneGui += DrawNormalsOverlay;
        }

        protected override void OnDisableExtra()
        {
            SceneView.duringSceneGui -= DrawNormalsOverlay;
            if (_bakedNormalsMesh != null)
            {
                DestroyImmediate(_bakedNormalsMesh);
                _bakedNormalsMesh = null;
            }
        }

        // =====================================================================
        // Single-instance enforcement
        // =====================================================================

        protected override void OnEnable()
        {
            // Single-instance enforcement. If another BulgeWindow is already live,
            // close this duplicate and focus the existing one. Deferred via delayCall
            // because Close() during OnEnable is unsafe mid-initialization.
            if (s_instance != null && s_instance != this)
            {
                var keep = s_instance;
                var self = this;
                EditorApplication.delayCall += () =>
                {
                    if (self != null) self.Close();
                    if (keep != null) keep.Focus();
                };
                return;
            }

            s_instance = this;
            base.OnEnable();
        }

        protected override void OnDisable()
        {
            if (s_instance == this)
                s_instance = null;
            base.OnDisable();
        }

        // =====================================================================
        // MenuItem
        // =====================================================================

        [MenuItem("Tools/Kai/SPS/Bulge Configurator")]
        public static void ShowWindow()
        {
            var window = GetWindow<BulgeWindow>("SPS Tools - Bulge Configurator");
            window.minSize = new Vector2(420, 600);
        }

        // =====================================================================
        // Bulge-specific UI sections
        // =====================================================================

        // --- Bulge Travel Range ---

        private bool showAdvancedRange;

        private void DrawBulgeTravelRange()
        {
            showAdvancedRange = EditorGUILayout.Foldout(showAdvancedRange, "Advanced: Depth Range");
            if (showAdvancedRange)
            {
                EditorGUI.indentLevel++;

                EditorGUILayout.HelpBox(
                    "Restricts the depth range the bulge plays across. Lowering End " +
                    "also compensates when a socket's FX Float can't reach 1.0 at " +
                    "full insertion — set End to the saturation value so the full " +
                    "animation plays within the reachable range.",
                    MessageType.None);

                EditorGUILayout.MinMaxSlider(TooltipContent.DepthRange,
                    ref config.depthRangeStart, ref config.depthRangeEnd, 0f, 1f);

                EditorGUILayout.BeginHorizontal();
                config.depthRangeStart = EditorGUILayout.FloatField(
                    TooltipContent.DepthRangeStart, config.depthRangeStart);
                config.depthRangeEnd = EditorGUILayout.FloatField(
                    TooltipContent.DepthRangeEnd, config.depthRangeEnd);
                EditorGUILayout.EndHorizontal();

                config.depthRangeStart = Mathf.Clamp01(config.depthRangeStart);
                config.depthRangeEnd = Mathf.Clamp01(config.depthRangeEnd);
                if (config.depthRangeEnd <= config.depthRangeStart)
                    config.depthRangeEnd = Mathf.Min(config.depthRangeStart + 0.01f, 1f);

                if (GUILayout.Button("Reset to 0-1", GUILayout.Width(100)))
                {
                    config.depthRangeStart = 0f;
                    config.depthRangeEnd = 1f;
                }
                EditorGUI.indentLevel--;
            }
        }

        // --- Bulge Settings ---

        private void DrawBulgeSettings()
        {
            EditorGUILayout.LabelField("Bulge Settings", EditorStyles.miniBoldLabel);

            config.bulgeIntensity = EditorGUILayout.Slider(
                TooltipContent.BulgeIntensity, config.bulgeIntensity, 0.01f, 2.0f);
            EditorGUILayout.LabelField("", $"{(config.bulgeIntensity * 100f):F0}%",
                EditorStyles.miniLabel);
            config.bulgeWidth = EditorGUILayout.IntSlider(
                TooltipContent.BulgeWidth, config.bulgeWidth, 1, 5);
        }

        // --- Deformation Mode ---

        private void DrawDeformationModeSection()
        {
            if (!FeatureFlags.BoneChainEnabled)
            {
                DrawBlendshapeSettings();
                return;
            }

            EditorGUILayout.LabelField("Deformation Mode", EditorStyles.miniBoldLabel);
            config.deformationMode = (DeformationMode)GUILayout.Toolbar(
                (int)config.deformationMode, new[] { "Bone Chain", "Blendshapes" });
            EditorGUILayout.Space(4);

            if (config.deformationMode == DeformationMode.BoneScale)
            {
                EditorGUILayout.HelpBox(
                    "Bone Chain mode is a work in progress and may not work as expected. " +
                    "Use Blendshapes for reliable results.",
                    MessageType.Warning);
                DrawBoneChainSettings();
            }
            else
                DrawBlendshapeSettings();
        }

        // --- Bone Chain Settings ---

        private void DrawBoneChainSettings()
        {
            EditorGUI.indentLevel++;

            // Quick Setup row
            EditorGUILayout.LabelField("Quick Setup", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            startBone = (Transform)EditorGUILayout.ObjectField(
                "Start Bone", startBone, typeof(Transform), true);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            endBone = (Transform)EditorGUILayout.ObjectField(
                "End Bone", endBone, typeof(Transform), true);
            EditorGUILayout.EndHorizontal();

            GUI.enabled = startBone != null && endBone != null && config.avatarRoot != null;
            if (GUILayout.Button("Auto-Fill Chain", GUILayout.Height(22)))
            {
                var chain = BoneChainDetector.FindChain(startBone, endBone);
                if (chain == null || chain.Count < 2)
                {
                    EditorUtility.DisplayDialog("Auto-Fill Failed",
                        "Could not find a bone chain between the selected start and end bones. " +
                        "Ensure both bones are part of the same hierarchy.",
                        "OK");
                }
                else
                {
                    config.boneChain = BoneChainDetector.ChainToPaths(
                        config.avatarRoot.transform, chain);
                    statusMessage = $"Auto-filled {config.boneChain.Count} bones in chain.";
                    statusType = MessageType.Info;
                    Repaint();
                }
            }
            GUI.enabled = true;

            // Auto-detect from path
            bool hasPath = config.pathWaypoints != null && config.pathWaypoints.Count >= 2;
            GUI.enabled = hasPath && config.avatarRoot != null;
            if (GUILayout.Button("Auto-detect from Path", GUILayout.Height(22)))
            {
                float searchRadius = 0.05f; // 5cm search radius
                if (config.pathWaypoints.Count > 0)
                    searchRadius = Mathf.Max(searchRadius, config.pathWaypoints[0].radius * 2f);

                var chain = BoneChainDetector.FindChainAlongPath(
                    config.avatarRoot.transform, config.pathWaypoints, searchRadius);
                if (chain == null || chain.Count < 2)
                {
                    EditorUtility.DisplayDialog("Auto-detect Failed",
                        "Could not find bones along the drawn path. Try increasing the path radius or drawing the path closer to the bones.",
                        "OK");
                }
                else
                {
                    config.boneChain = BoneChainDetector.ChainToPaths(
                        config.avatarRoot.transform, chain);
                    statusMessage = $"Auto-detected {config.boneChain.Count} bones along path.";
                    statusType = MessageType.Info;
                    Repaint();
                }
            }
            GUI.enabled = true;

            EditorGUILayout.Space(4);

            // Ordered list of bone path entries
            EditorGUILayout.LabelField("Bone Chain", EditorStyles.miniBoldLabel);
            for (int i = 0; i < config.boneChain.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{i + 1}.", GUILayout.Width(24));
                EditorGUILayout.LabelField(config.boneChain[i], EditorStyles.miniLabel);

                if (GUILayout.Button(RemoveGlyph, GUILayout.Width(20)))
                {
                    config.boneChain.RemoveAt(i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                // PhysBone warning per bone
                if (config.avatarRoot != null)
                {
                    var boneTransform = config.avatarRoot.transform.Find(config.boneChain[i]);
                    if (boneTransform != null)
                    {
                        var conflict = PhysBoneDetector.CheckConflict(boneTransform);
                        if (conflict != null)
                        {
                            EditorGUILayout.HelpBox(
                                $"Bone '{config.boneChain[i]}' is affected by PhysBone on '{conflict}'. " +
                                "Scale animations may conflict with physics simulation.",
                                MessageType.Warning);
                        }
                    }
                }
            }

            // Add Bone button (manual add via ObjectField)
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            manualAddBone = (Transform)EditorGUILayout.ObjectField(
                manualAddBone, typeof(Transform), true);
            GUI.enabled = manualAddBone != null && config.avatarRoot != null;
            if (GUILayout.Button("Add Bone", GUILayout.Width(70)))
            {
                string path = GetRelativePath(config.avatarRoot.transform, manualAddBone);
                if (!string.IsNullOrEmpty(path) && !config.boneChain.Contains(path))
                {
                    config.boneChain.Add(path);
                    manualAddBone = null;
                }
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // Scale axis checkboxes
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Scale Axes");
            EditorGUILayout.BeginHorizontal();
            config.scaleX = GUILayout.Toggle(config.scaleX, "X", "Button");
            config.scaleY = GUILayout.Toggle(config.scaleY, "Y", "Button");
            config.scaleZ = GUILayout.Toggle(config.scaleZ, "Z", "Button");
            EditorGUILayout.EndHorizontal();

            // FX layer info
            EditorGUILayout.HelpBox(
                "Bone scale animations are placed in the FX layer. This works when no other " +
                "animator layer (Base, Gesture) animates the same bones. If you see conflicts, " +
                "use Blendshapes mode instead.",
                MessageType.Info);

            EditorGUI.indentLevel--;
        }

        // --- Blendshape Settings ---

        private void DrawBlendshapeSettings()
        {
            EditorGUI.indentLevel++;

            var newRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                "Target Mesh", targetRenderer, typeof(SkinnedMeshRenderer), true);
            if (newRenderer != targetRenderer)
            {
                targetRenderer = newRenderer;
                if (targetRenderer != null && config.avatarRoot != null)
                    config.rendererPath = GetRelativePath(
                        config.avatarRoot.transform, targetRenderer.transform);
            }

            // Additional overlay meshes (collapsible)
            DrawAdditionalMeshesSection();

            // Mode toggle: manual vs auto-generate
            EditorGUILayout.LabelField("Blendshape Source", EditorStyles.miniBoldLabel);
            int sourceMode = blendshapeAutoGenerate ? 1 : 0;
            sourceMode = GUILayout.Toolbar(sourceMode, new[] { "Manual Entry", "Auto-Generate" });
            blendshapeAutoGenerate = sourceMode == 1;

            if (blendshapeAutoGenerate)
            {
                DrawAutoGenerateBlendshapes();
            }
            else
            {
                DrawManualBlendshapes();
            }

            EditorGUI.indentLevel--;
        }

        private void DrawAutoGenerateBlendshapes()
        {
            // Path drawing
            bool hasPath = config.pathWaypoints != null && config.pathWaypoints.Count >= 2;

            if (PathDrawingTool.IsDrawing)
            {
                EditorGUILayout.HelpBox("Drawing path in Scene view... Click on the mesh to add waypoints. Press Enter to confirm, Escape to cancel.", MessageType.Info);
            }
            else
            {
                if (hasPath)
                    EditorGUILayout.HelpBox($"Path: {config.autoPositionCount} positions", MessageType.None);

                // Draw Path button (full width)
                if (GUILayout.Button(hasPath ? "Redraw Path" : "Draw Path on Mesh", GUILayout.Height(24)))
                {
                    if (targetRenderer != null && config.avatarRoot != null)
                    {
                        PathDrawingTool.BeginDrawing(
                            targetRenderer.gameObject,
                            config.avatarRoot.transform,
                            new Color(0.81f, 0.58f, 0.93f), // purple for Bulge
                            hasPath ? config.pathWaypoints : null,
                            (waypoints) =>
                            {
                                config.pathWaypoints = waypoints;
                                // Auto-set position count to match waypoint count
                                config.autoPositionCount = Mathf.Clamp(waypoints.Count, 2, 10);
                                Repaint();
                            },
                            () => { Repaint(); });

                        // Focus the Scene view
                        var sceneView = SceneView.lastActiveSceneView;
                        if (sceneView != null) sceneView.Focus();
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("SPS Bulge",
                            "Please select a Target Mesh above before drawing a path.", "OK");
                    }
                }

                float dispMM = Mathf.Round(config.blendshapeDisplacement * 1000f);
                dispMM = EditorGUILayout.IntSlider(TooltipContent.Displacement, (int)dispMM, 1, 100);
                config.blendshapeDisplacement = dispMM * 0.001f;
                DrawDisplacementPreview();

                config.recalculateNormals = EditorGUILayout.Toggle(
                    TooltipContent.RecalculateNormals, config.recalculateNormals);

                if (config.recalculateNormals)
                {
                    EditorGUI.indentLevel++;
                    _showNormalAdvanced = EditorGUILayout.Foldout(
                        _showNormalAdvanced, "Advanced", true);
                    if (_showNormalAdvanced)
                    {
                        EditorGUI.indentLevel++;
                        float prevLabelWidth = EditorGUIUtility.labelWidth;
                        EditorGUIUtility.labelWidth = 170f;
                        config.normalFalloffSoftness = EditorGUILayout.Slider(
                            TooltipContent.NormalFalloffSoftness,
                            config.normalFalloffSoftness, 0.5f, 3f);
                        config.normalSmoothingPasses = EditorGUILayout.IntSlider(
                            TooltipContent.NormalSmoothingPasses,
                            config.normalSmoothingPasses, 0, 3);
                        config.normalBoundaryRings = EditorGUILayout.IntSlider(
                            TooltipContent.NormalBoundaryRings,
                            config.normalBoundaryRings, 1, 4);

                        EditorGUILayout.Space(4);
                        // One-shot repaint when the toggle (or a sub-control) changes.
                        // The scene view already repaints on its own whenever the
                        // mesh pose changes, so we don't need a per-frame RepaintAll
                        // loop - that was burning CPU while the window was open.
                        bool prevShow = _showNormals;
                        float prevLen = _normalLength;
                        bool prevHighlight = _highlightChanged;

                        _showNormals = EditorGUILayout.Toggle(
                            new GUIContent("Show Normals",
                                "Visualize baked vertex normals in the scene view. " +
                                "Uses SkinnedMeshRenderer.BakeMesh so you see the real " +
                                "deformed state, including blendshape normal deltas."),
                            _showNormals);
                        if (_showNormals)
                        {
                            EditorGUI.indentLevel++;
                            _normalLength = EditorGUILayout.Slider(
                                "Length (m)", _normalLength, 0.002f, 0.1f);
                            _highlightChanged = EditorGUILayout.Toggle(
                                new GUIContent("Only Changed Verts",
                                    "Only draw normals where a blendshape wrote a normal delta. " +
                                    "Off = draw every vert (yellow = has delta, cyan = no delta)."),
                                _highlightChanged);
                            EditorGUI.indentLevel--;
                        }

                        if (_showNormals != prevShow
                            || _normalLength != prevLen
                            || _highlightChanged != prevHighlight)
                        {
                            SceneView.RepaintAll();
                        }
                        EditorGUIUtility.labelWidth = prevLabelWidth;
                        EditorGUI.indentLevel--;
                    }
                    EditorGUI.indentLevel--;
                }

                // Rarely-tuned blendshape parameters tucked away by default.
                // Labeled "More options" so it doesn't collide with the
                // "Advanced" sub-foldout under Recalculate Normals above.
                _showBlendshapeAdvanced = EditorGUILayout.Foldout(
                    _showBlendshapeAdvanced, "More options", true);
                if (_showBlendshapeAdvanced)
                {
                    EditorGUI.indentLevel++;

                    config.blendshapeNamingPattern = EditorGUILayout.TextField(
                        new GUIContent("Naming Pattern", "Custom blendshape naming. Use {0} as a placeholder — Bulge substitutes it with the position index (1, 2, 3…).\nLeave empty for default."),
                        config.blendshapeNamingPattern);
                    {
                        string pattern = config.blendshapeNamingPattern;
                        bool hasSlot = !string.IsNullOrEmpty(pattern) && pattern.Contains("{0}");
                        int positions = Mathf.Max(1, config.autoPositionCount);

                        string Name(int i) =>
                            hasSlot ? string.Format(pattern, i) : $"SPSBulge_Pos{i}";

                        string preview = positions == 1
                            ? Name(1)
                            : positions == 2
                                ? $"{Name(1)}, {Name(2)}"
                                : $"{Name(1)}, {Name(2)}, …, {Name(positions)}";

                        string prefix;
                        if (hasSlot)
                            prefix = $"Blendshape names ({positions} shape{(positions == 1 ? "" : "s")}):";
                        else if (string.IsNullOrEmpty(pattern))
                            prefix = $"Using default names ({positions} shape{(positions == 1 ? "" : "s")}):";
                        else
                            prefix = "Pattern is missing {0} — using defaults:";

                        EditorGUILayout.HelpBox($"{prefix} {preview}", MessageType.None);
                    }

                    config.smoothingPasses = EditorGUILayout.IntSlider(
                        TooltipContent.SmoothingPasses, config.smoothingPasses, 0, 10);

                    config.subdivideAffectedRegion = EditorGUILayout.Toggle(
                        TooltipContent.SubdivideRegion, config.subdivideAffectedRegion);
                    if (config.subdivideAffectedRegion)
                    {
                        EditorGUI.indentLevel++;
                        config.subdivisionPasses = EditorGUILayout.IntSlider(
                            TooltipContent.SubdivisionPasses, config.subdivisionPasses, 1, 3);
                        if (config.subdivisionPasses >= 2)
                            EditorGUILayout.HelpBox(
                                "Multiple passes significantly increase vertex count in the affected region. " +
                                "This may impact avatar performance.",
                                MessageType.Warning);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUI.indentLevel--;
                }

                // Generate Blendshapes button (full width, separate)
                GUI.enabled = hasPath && targetRenderer != null;
                if (GUILayout.Button("Generate Blendshapes", GUILayout.Height(24)))
                {
                    int extra = config.additionalMeshes != null ? config.additionalMeshes.Count : 0;
                    string dialogMsg = extra > 0
                        ? $"This will create modified copies of your mesh plus {extra} additional mesh(es) with generated blendshapes.\n\n" +
                          "All originals are preserved and can be restored, but please " +
                          "ensure you have a backup of your project before continuing."
                        : "This will create a modified copy of your mesh with generated blendshapes.\n\n" +
                          "Your original mesh is preserved and can be restored, but please " +
                          "ensure you have a backup of your project before continuing.";
                    if (!EditorUtility.DisplayDialog("SPS Bulge - Generate Blendshapes",
                        dialogMsg, "Continue", "Cancel"))
                        return;

                    // Collect all participating renderers (primary + additionals)
                    var renderers = new List<SkinnedMeshRenderer> { targetRenderer };
                    var trackedEntries = new List<TrackedMesh> { null };  // null = primary
                    var snapshotMeshes = new List<Mesh> { targetRenderer.sharedMesh };
                    var originalMeshes = new List<Mesh> { targetRenderer.sharedMesh };

                    if (targetRenderer.sharedMesh == null)
                    {
                        EditorUtility.DisplayDialog("SPS Bulge",
                            "No mesh found on the target renderer.", "OK");
                        return;
                    }

                    // Resolve primary original
                    var storedPrimary = MeshReferenceTracker.ResolveMesh(config, "original");
                    if (storedPrimary != null) originalMeshes[0] = storedPrimary;

                    // Resolve additionals
                    bool missingRenderer = false;
                    if (config.additionalMeshes != null)
                    {
                        foreach (var entry in config.additionalMeshes)
                        {
                            var r = ResolveAdditionalRenderer(entry);
                            if (r == null || r.sharedMesh == null)
                            {
                                missingRenderer = true;
                                continue;
                            }
                            renderers.Add(r);
                            trackedEntries.Add(entry);
                            snapshotMeshes.Add(r.sharedMesh);
                            var storedAdd = MeshReferenceTracker.ResolveMesh(entry, "original");
                            originalMeshes.Add(storedAdd != null ? storedAdd : r.sharedMesh);
                        }
                    }

                    if (missingRenderer)
                        Debug.LogWarning("[SPS Bulge] One or more additional meshes could not be resolved - skipped.");

                    Undo.SetCurrentGroupName("Generate Bulge Blendshapes");
                    int undoGroup = Undo.GetCurrentGroup();

                    try
                    {
                        // Reset each renderer to its original before running
                        for (int i = 0; i < renderers.Count; i++)
                            renderers[i].sharedMesh = originalMeshes[i];

                        string folder = SpsAnimationUtility.CreateOutputFolder(
                            config.GetOutputFolder());

                        var results = BlendshapeGenerator.GenerateBulgeBlendshapes(
                            renderers, config.avatarRoot.transform,
                            config.pathWaypoints,
                            config.autoPositionCount,
                            config.blendshapeDisplacement,
                            folder, config.blendshapeNamingPattern,
                            config.smoothingPasses,
                            config.subdivideAffectedRegion, config.subdivisionPasses,
                            config.recalculateNormals,
                            config.normalFalloffSoftness, config.normalSmoothingPasses,
                            config.normalBoundaryRings);

                        // Store per-mesh results via tracker
                        for (int i = 0; i < results.Count; i++)
                        {
                            if (trackedEntries[i] == null)
                            {
                                MeshReferenceTracker.StoreMesh(config, "original", originalMeshes[i]);
                                MeshReferenceTracker.StoreMesh(config, "generated", results[i].modifiedMesh);
                            }
                            else
                            {
                                MeshReferenceTracker.StoreMesh(trackedEntries[i], "original", originalMeshes[i]);
                                MeshReferenceTracker.StoreMesh(trackedEntries[i], "generated", results[i].modifiedMesh);
                            }
                        }
                        config.positionBlendshapes = new List<string>(results[0].blendshapeNames);

                        statusMessage = renderers.Count > 1
                            ? $"Generated {results[0].blendshapeNames.Count} blendshapes on {renderers.Count} meshes."
                            : $"Generated {results[0].blendshapeNames.Count} bulge blendshapes.";
                        statusType = MessageType.Info;
                        Repaint();
                        Undo.CollapseUndoOperations(undoGroup);
                    }
                    catch (System.Exception e)
                    {
                        // Rollback - restore each renderer to its pre-generation state
                        for (int i = 0; i < renderers.Count; i++)
                            renderers[i].sharedMesh = snapshotMeshes[i];
                        statusMessage = $"Blendshape generation failed, meshes restored: {e.Message}";
                        statusType = MessageType.Error;
                        Debug.LogError($"[SPS Bulge] {statusMessage}");
                        Debug.LogException(e);
                        Repaint();
                    }
                }
                GUI.enabled = true;

                // Restore Original Mesh button - show whenever we have a record,
                // so the user can always get back to the pre-generation state
                bool hasOriginalRecord =
                    !string.IsNullOrEmpty(config.originalMeshPath) || config.originalMesh != null;
                if (hasOriginalRecord)
                {
                    var resolvedOriginal = MeshReferenceTracker.ResolveMesh(config, "original");
                    if (resolvedOriginal != null)
                    {
                        if (GUILayout.Button("Restore Original Mesh"))
                        {
                            if (ScenePreviewManager.IsPreviewing)
                            {
                                ScenePreviewManager.StopPreview();
                                previewEntries = null;
                            }
                            if (targetRenderer != null)
                            {
                                Undo.RecordObject(targetRenderer, "Restore Original Mesh");
                                targetRenderer.sharedMesh = resolvedOriginal;
                            }
                            MeshReferenceTracker.StoreMesh(config, "original", null);
                            MeshReferenceTracker.StoreMesh(config, "generated", null);
                            config.positionBlendshapes.Clear();
                        }
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            $"Original mesh record exists but couldn't be loaded from:\n{config.originalMeshPath}",
                            MessageType.Warning);
                        if (GUILayout.Button("Clear Original Mesh Record"))
                        {
                            MeshReferenceTracker.StoreMesh(config, "original", null);
                            MeshReferenceTracker.StoreMesh(config, "generated", null);
                        }
                    }
                }

                // Restore All shortcut when additional meshes are tracked
                if (config.additionalMeshes != null && config.additionalMeshes.Count > 0)
                {
                    if (GUILayout.Button("Restore All Meshes"))
                        RestoreAllMeshes();
                }

                if (MeshReferenceTracker.ResolveMesh(config, "generated") != null)
                    EditorGUILayout.HelpBox(
                        "Blendshapes generated. Ready to preview and generate.",
                        MessageType.Info);

                EditorGUILayout.HelpBox(
                    "Auto-generated blendshapes increase mesh data size.",
                    MessageType.None);
            }
        }

        private void DrawManualBlendshapes()
        {
            EditorGUILayout.LabelField("Blendshape Names (ordered)", EditorStyles.miniBoldLabel);

            for (int i = 0; i < config.positionBlendshapes.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{i + 1}.", GUILayout.Width(24));
                config.positionBlendshapes[i] = EditorGUILayout.TextField(
                    config.positionBlendshapes[i]);
                if (GUILayout.Button(RemoveGlyph, GUILayout.Width(20)))
                {
                    config.positionBlendshapes.RemoveAt(i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Blendshape", GUILayout.Width(130)))
            {
                config.positionBlendshapes.Add(
                    $"SPSBulge_Pos{config.positionBlendshapes.Count + 1}");
            }

            EditorGUILayout.HelpBox(
                "Enter blendshape names in order from start to end of the bulge travel path.",
                MessageType.None);
        }

        // --- Depth -> Weight Preview ---

        /// <summary>
        /// Draws a graph showing what each blendshape/bone weight looks like
        /// as depth goes from 0 to 1. This mirrors the actual blend tree output:
        /// X axis = depth (0->1), Y axis = weight.
        /// Each position gets its own colored line.
        /// </summary>
        private void DrawBellCurvePreview()
        {
            EditorGUILayout.LabelField("Depth \u2192 Effect Preview", EditorStyles.miniBoldLabel);
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(120));
            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

            float pad = 10f;
            float graphX = rect.x + pad;
            float graphW = rect.width - pad * 2f;
            float graphTop = rect.y + 16f;
            float graphBottom = rect.yMax - 14f;
            float graphH = graphBottom - graphTop;

            // Baseline
            Handles.color = new Color(1f, 1f, 1f, 0.15f);
            Handles.DrawLine(
                new Vector3(graphX, graphBottom),
                new Vector3(graphX + graphW, graphBottom));

            // 100% reference line (graph top = 150% to show overdrive headroom)
            float refY = graphBottom - graphH / 1.5f;
            Handles.color = new Color(1f, 1f, 1f, 0.08f);
            Handles.DrawLine(
                new Vector3(graphX, refY),
                new Vector3(graphX + graphW, refY));

            int posCount = config.PositionCount;
            if (posCount < 2) posCount = 3;

            // Build the same threshold entries that the blend tree uses
            // (temporary -- just for computing weights at each depth sample)
            float rangeStart = config.depthRangeStart;
            float rangeEnd = config.depthRangeEnd;
            float rangeSize = rangeEnd - rangeStart;
            float rampMargin = posCount > 1
                ? rangeSize / (posCount - 1) * 0.5f
                : rangeSize * 0.25f;
            float innerStart = rangeStart + rampMargin;
            float intensityScale = config.bulgeIntensity;

            // Compute position depth thresholds (matching BuildThresholdEntries)
            var posDepths = new float[posCount];
            for (int pos = 0; pos < posCount; pos++)
            {
                posDepths[pos] = posCount > 1
                    ? innerStart + pos * (rangeEnd - innerStart) / (posCount - 1)
                    : (rangeStart + rangeEnd) * 0.5f;
            }

            // For each position, compute its weight at each position-clip's depth
            // then simulate blend tree interpolation across the full 0->1 range
            int samples = 200;

            // Position colors (cycle through distinguishable hues)
            Color[] posColors = new Color[posCount];
            for (int p = 0; p < posCount; p++)
            {
                float hue = (float)p / posCount * 0.7f + 0.55f; // blue->green->yellow range
                posColors[p] = Color.HSVToRGB(hue % 1f, 0.6f, 0.9f);
            }

            // Draw a line per position showing its weight across depth
            for (int pos = 0; pos < posCount; pos++)
            {
                var points = new List<Vector3>();

                for (int s = 0; s <= samples; s++)
                {
                    float depth = (float)s / samples;
                    float xPixel = graphX + depth * graphW;

                    // Simulate: what weight does this position have at this depth?
                    // The blend tree interpolates between position clips.
                    // At each position's depth threshold, that position has its full weight.
                    // Between thresholds, weights blend linearly.
                    float weight = ComputePositionWeightAtDepth(
                        pos, depth, posDepths, posCount,
                        rangeStart, rangeEnd, intensityScale, config);

                    float yPixel = graphBottom - Mathf.Min(weight, 1.5f) / 1.5f * graphH;
                    points.Add(new Vector3(xPixel, yPixel));
                }

                Handles.color = new Color(posColors[pos].r, posColors[pos].g,
                    posColors[pos].b, 0.85f);
                if (points.Count >= 2)
                    Handles.DrawAAPolyLine(2f, points.ToArray());
            }

            // Draw position threshold markers on the baseline
            for (int pos = 0; pos < posCount; pos++)
            {
                float xPixel = graphX + posDepths[pos] * graphW;
                Handles.color = new Color(posColors[pos].r, posColors[pos].g,
                    posColors[pos].b, 0.5f);
                Handles.DrawLine(
                    new Vector3(xPixel, graphBottom),
                    new Vector3(xPixel, graphBottom + 6f));
            }

            // Depth range indicators
            Handles.color = new Color(1f, 1f, 1f, 0.2f);
            float rsX = graphX + rangeStart * graphW;
            float reX = graphX + rangeEnd * graphW;
            Handles.DrawLine(new Vector3(rsX, graphTop), new Vector3(rsX, graphBottom));
            Handles.DrawLine(new Vector3(reX, graphTop), new Vector3(reX, graphBottom));

            // Labels
            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(1f, 1f, 1f, 0.5f) },
                fontSize = 9
            };
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, 60, 14), "Weight", labelStyle);
            GUI.Label(new Rect(graphX + graphW - 40f, graphBottom, 40, 14), "Depth", labelStyle);
            GUI.Label(new Rect(graphX - 2f, graphBottom, 20, 14), "0", labelStyle);
            GUI.Label(new Rect(graphX + graphW - 8f, graphBottom, 20, 14), "1", labelStyle);

            var refLabelStyle = new GUIStyle(labelStyle)
            { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(graphX + graphW + 2f, refY - 7f, 35, 14), "100%", refLabelStyle);

            // Depth cursor -- follows the scene preview slider
            if (ScenePreviewManager.IsPreviewing || previewDepth > 0.001f)
            {
                float cursorX = graphX + previewDepth * graphW;
                Handles.color = new Color(1f, 1f, 1f, 0.7f);
                Handles.DrawLine(
                    new Vector3(cursorX, graphTop),
                    new Vector3(cursorX, graphBottom));

                // Depth value label at cursor
                var cursorStyle = new GUIStyle(labelStyle)
                {
                    normal = { textColor = Color.white },
                    alignment = TextAnchor.MiddleCenter
                };
                GUI.Label(new Rect(cursorX - 15f, graphTop - 14f, 30, 14),
                    previewDepth.ToString("F2"), cursorStyle);
            }
        }

        /// <summary>
        /// Simulates the blend tree to compute what weight a specific position
        /// has at a given depth value. Mirrors the actual threshold interpolation.
        /// </summary>
        private float ComputePositionWeightAtDepth(
            int pos, float depth, float[] posDepths, int posCount,
            float rangeStart, float rangeEnd,
            float intensityScale, BulgeConfig config)
        {
            if (depth <= rangeStart) return 0f;

            // Find which two position thresholds bracket this depth
            int lowerPos = -1;
            int upperPos = 0;
            float lowerDepth = rangeStart;
            float upperDepth = posDepths[0];

            for (int p = 0; p < posCount; p++)
            {
                if (depth >= posDepths[p])
                {
                    lowerPos = p;
                    lowerDepth = posDepths[p];
                    upperPos = p + 1;
                    upperDepth = (p + 1 < posCount) ? posDepths[p + 1] : 1f;
                }
            }

            float range = upperDepth - lowerDepth;
            float t = range > 0.0001f
                ? (depth - lowerDepth) / range
                : 0f;
            t = Mathf.Clamp01(t);

            float lowerWeight = (lowerPos >= 0)
                ? GetClipWeightForPosition(pos, lowerPos, intensityScale, config)
                : 0f;

            float upperWeight;
            if (upperPos < posCount)
                upperWeight = GetClipWeightForPosition(pos, upperPos, intensityScale, config);
            else if (depth > posDepths[posCount - 1])
            {
                // After last position: hold at last clip
                int offset = Mathf.Abs(pos - (posCount - 1));
                upperWeight = config.GetBellCurveWeight(offset) * intensityScale;
            }
            else
                upperWeight = 0f;

            return Mathf.Max(0f, Mathf.Lerp(lowerWeight, upperWeight, t));
        }

        private float GetClipWeightForPosition(
            int pos, int centerPos,
            float intensityScale, BulgeConfig config)
        {
            int offset = Mathf.Abs(pos - centerPos);
            return Mathf.Max(0f, config.GetBellCurveWeight(offset) * intensityScale);
        }

        // =====================================================================
        // Show Normals overlay (Advanced → Show Normals)
        // =====================================================================

        /// <summary>
        /// Draws baked vertex normals on the target mesh in the scene view.
        /// Yellow = a blendshape authored a normal delta at that vert;
        /// cyan = no delta. Skipped entirely when the toggle is off so
        /// the hook is free when users aren't inspecting.
        /// </summary>
        private void DrawNormalsOverlay(SceneView sceneView)
        {
            if (!_showNormals || targetRenderer == null || targetRenderer.sharedMesh == null)
                return;

            var sharedMesh = targetRenderer.sharedMesh;
            int vertCount = sharedMesh.vertexCount;
            int blendCount = sharedMesh.blendShapeCount;

            EnsureNormalsBuffers(sharedMesh, vertCount, blendCount);

            // Bake per-frame to reflect current skinning + blendshape weights.
            // _bakedNormalsMesh is held across frames; Unity just rewrites its
            // buffers in-place on BakeMesh.
            if (_bakedNormalsMesh == null)
                _bakedNormalsMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            targetRenderer.BakeMesh(_bakedNormalsMesh, true);

            _bakedVertsList.Clear();
            _bakedNormalsList.Clear();
            _bakedNormalsMesh.GetVertices(_bakedVertsList);
            _bakedNormalsMesh.GetNormals(_bakedNormalsList);
            if (_bakedVertsList.Count != vertCount || _bakedNormalsList.Count != vertCount)
                return;

            // Two segments per vert max (start + end). Worst case (highlight off)
            // packs every vert into its color class; oversize buffers up front
            // then use the filled prefix in DrawLines so we don't reallocate
            // mid-frame when the visible set grows.
            int maxPairs = vertCount * 2;
            if (_changedSegments == null || _changedSegments.Length < maxPairs)
                _changedSegments = new Vector3[maxPairs];
            if (_unchangedSegments == null || _unchangedSegments.Length < maxPairs)
                _unchangedSegments = new Vector3[maxPairs];

            var rendererTransform = targetRenderer.transform;
            int changedCount = 0;
            int unchangedCount = 0;

            for (int v = 0; v < vertCount; v++)
            {
                bool changed = _hasNormalDelta[v];
                if (_highlightChanged && !changed) continue;

                Vector3 worldPos = rendererTransform.TransformPoint(_bakedVertsList[v]);
                Vector3 worldNormal = rendererTransform.TransformDirection(_bakedNormalsList[v]);
                Vector3 tip = worldPos + worldNormal * _normalLength;

                if (changed)
                {
                    _changedSegments[changedCount++] = worldPos;
                    _changedSegments[changedCount++] = tip;
                }
                else
                {
                    _unchangedSegments[unchangedCount++] = worldPos;
                    _unchangedSegments[unchangedCount++] = tip;
                }
            }

            // One submission per color class. Handles.DrawLines(points, indices)
            // uses points[indices[2k]] -> points[indices[2k+1]] for segment k,
            // which lets us keep the points arrays oversized (grow-only) while
            // the indices arrays size exactly to the subset we filled.
            if (changedCount > 0)
            {
                Handles.color = new Color(1f, 0.85f, 0.2f, 0.9f);
                Handles.DrawLines(_changedSegments, GetIdentityIndices(ref _changedIndices, changedCount));
            }
            if (unchangedCount > 0)
            {
                Handles.color = new Color(0.35f, 0.8f, 1f, 0.25f);
                Handles.DrawLines(_unchangedSegments, GetIdentityIndices(ref _unchangedIndices, unchangedCount));
            }
        }

        private int[] _changedIndices;
        private int[] _unchangedIndices;
        // Returns an int[] of exactly `count` entries with value i at index i.
        // Reallocates only on size change - per-frame cost is zero for stable
        // visible-vertex counts (the normal case).
        private static int[] GetIdentityIndices(ref int[] cache, int count)
        {
            if (cache == null || cache.Length != count)
            {
                cache = new int[count];
                for (int i = 0; i < count; i++) cache[i] = i;
            }
            return cache;
        }

        /// <summary>
        /// Lazily (re)builds the authored-delta mask and the blend-frame read
        /// buffers. The mask is a function of the mesh asset and its blendshape
        /// layout - both stable across frames for a given avatar - so rebuild
        /// only when the identity or count changes.
        /// </summary>
        private void EnsureNormalsBuffers(Mesh sharedMesh, int vertCount, int blendCount)
        {
            if (_frameVertsBuf == null || _frameVertsBuf.Length != vertCount)
            {
                _frameVertsBuf = new Vector3[vertCount];
                _frameNormalsBuf = new Vector3[vertCount];
                _frameTangentsBuf = new Vector3[vertCount];
            }

            bool maskValid = _hasNormalDelta != null
                && _hasNormalDelta.Length == vertCount
                && _normalsMaskMesh == sharedMesh
                && _normalsMaskBlendCount == blendCount;
            if (maskValid) return;

            if (_hasNormalDelta == null || _hasNormalDelta.Length != vertCount)
                _hasNormalDelta = new bool[vertCount];
            else
                System.Array.Clear(_hasNormalDelta, 0, vertCount);

            for (int bs = 0; bs < blendCount; bs++)
            {
                int frameCount = sharedMesh.GetBlendShapeFrameCount(bs);
                for (int f = 0; f < frameCount; f++)
                {
                    sharedMesh.GetBlendShapeFrameVertices(
                        bs, f, _frameVertsBuf, _frameNormalsBuf, _frameTangentsBuf);
                    for (int v = 0; v < vertCount; v++)
                    {
                        if (_frameNormalsBuf[v].sqrMagnitude > 0.00001f)
                            _hasNormalDelta[v] = true;
                    }
                }
            }

            _normalsMaskMesh = sharedMesh;
            _normalsMaskBlendCount = blendCount;
        }
    }
}
