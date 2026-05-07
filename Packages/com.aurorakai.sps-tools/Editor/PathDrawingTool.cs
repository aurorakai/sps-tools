using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Scene view path drawing tool for defining blendshape generation regions.
    /// Used by the Bulge configurator.
    /// </summary>
    public static class PathDrawingTool
    {
        public static bool IsDrawing { get; private set; }

        // Drawing state
        private static GameObject s_targetMesh;
        private static Transform s_avatarRoot;
        private static Color s_color;
        private static List<PathWaypoint> s_waypoints;
        private static Action<List<PathWaypoint>> s_onConfirm;
        private static Action s_onCancel;

        // Single uniform radius for the entire path
        private static float s_pathRadius = 0.03f;

        // Ellipse stretch: 1 = circle, >1 elongated along path, <1 wider across path
        private static float s_pathAspect = 1f;

        // Snapping
        private static bool s_snapToVerts;

        // Draggable panel state
        private static Vector2 s_panelPos = new Vector2(12f, -1f); // -1 = needs init
        private static bool s_isDraggingPanel;
        private static Vector2 s_dragOffset;
        private const float kPanelWidth = 210f;
        private const float kPanelHeight = 190f;
        private const float kPanelTitleHeight = 22f;
        private const float kHintBarHeight = 22f;

        // Cached GUIStyles (avoid allocations every frame)
        private static GUIStyle s_panelTitleStyle;
        private static GUIStyle s_panelLabelStyle;
        private static GUIStyle s_panelValueStyle;
        private static GUIStyle s_panelStatsStyle;
        private static GUIStyle s_hintBarStyle;
        private static GUIStyle s_waypointNumStyle;
        private static GUIStyle s_overlayLabelStyle;
        private static bool s_stylesInitialized;

        // Cached mesh data for snap (avoid copying arrays every drag)
        private static Vector3[] s_snapVertices;
        private static Vector3[] s_snapNormals;
        private static Mesh s_snapCachedMesh;
        // KDTree over object-space snap vertices; avoids O(N) linear scan per drag.
        private static KDTreeNearest s_snapTree;

        // Reusable per-frame world-space waypoint cache.
        private static readonly List<Vector3> s_tempPositions = new List<Vector3>();
        private static readonly List<Vector3> s_tempNormals = new List<Vector3>();
        private static readonly List<Vector3> s_auxPositions = new List<Vector3>();
        // Reusable point buffer for the batched spline polyline.
        private static Vector3[] s_splinePoints;
        private static bool s_waypointWorldCacheDirty = true;

        // Vertex highlight cache
        private static Vector3[] s_cachedAffectedVerts;
        private static float[] s_cachedAffectedWeights;
        private static int s_cachedAffectedCount;
        private static double s_lastVertexCalcTime;
        private static Material s_vertexMaterial;
        private static bool s_affectedVertexCacheDirty = true;
        // Cached mesh.vertices for affected-vertex calc (avoid array copy every 100 ms tick)
        private static Mesh s_affectedCachedMesh;
        private static Vector3[] s_affectedCachedVertices;

        // Reflection cache
        private static MethodInfo s_intersectRayMeshMethod;

        // ─────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────

        public static void BeginDrawing(
            GameObject targetMesh,
            Transform avatarRoot,
            Color themeColor,
            List<PathWaypoint> existingPath,
            Action<List<PathWaypoint>> onConfirm,
            Action onCancel)
        {
            // Re-entry guard. A second BeginDrawing without a confirm/cancel
            // (e.g. user hits "Redraw Path" twice before closing the previous
            // session) would otherwise double-subscribe OnSceneGUI and
            // PumpRepaint, running every per-frame cost twice and losing the
            // first session's onCancel callback.
            if (IsDrawing) StopDrawing();

            s_targetMesh = targetMesh;
            s_avatarRoot = avatarRoot;
            s_color = themeColor;
            s_waypoints = existingPath != null
                ? new List<PathWaypoint>(existingPath)
                : new List<PathWaypoint>();
            s_onConfirm = onConfirm;
            s_onCancel = onCancel;

            // Init radius + aspect from existing path or keep current
            if (s_waypoints.Count > 0)
            {
                s_pathRadius = s_waypoints[0].radius;
                s_pathAspect = s_waypoints[0].aspectRatio;
            }

            // Seed the sync-change detectors to match the initial slider values.
            // Without this, s_lastSyncedRadius/Aspect carry zero from static-
            // init (or from a prior session), so the very first OnSceneGUI tick
            // sees "radius changed" and overwrites every waypoint's per-vertex
            // radius/aspect with the slider value, silently flattening any
            // variation the user had authored in the loaded path.
            s_lastSyncedRadius = s_pathRadius;
            s_lastSyncedAspect = s_pathAspect;
            s_tempPositions.Clear();
            s_tempNormals.Clear();
            s_waypointWorldCacheDirty = true;
            s_affectedVertexCacheDirty = true;
            s_lastVertexCalcTime = 0d;
            s_lastPumpTime = 0d;

            IsDrawing = true;
            SceneView.duringSceneGui += OnSceneGUI;
            // Drive cache-refresh repaints from a 10 Hz editor tick instead of
            // forcing a SceneView.Repaint() at the end of every OnSceneGUI pass.
            // Handles interactions repaint themselves; we only need to pump the
            // affected-vertex visualization forward on our own cadence.
            EditorApplication.update += PumpRepaint;
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Draws a stored path as a read-only overlay (used when the editor window is open
        /// but path drawing mode is not active).
        /// </summary>
        public static void DrawPathOverlay(
            List<PathWaypoint> waypoints, Transform avatarRoot, Color color)
        {
            if (waypoints == null || waypoints.Count == 0 || avatarRoot == null) return;

            // Centerline
            if (waypoints.Count >= 2)
                DrawSpline(waypoints, avatarRoot, color, 0.4f, 2.5f);

            // Waypoint dots with radius ellipses
            for (int i = 0; i < waypoints.Count; i++)
            {
                var wp = waypoints[i];
                Vector3 worldPos = avatarRoot.TransformPoint(wp.localPosition);
                float sz = HandleUtility.GetHandleSize(worldPos) * 0.14f;

                // Radius ellipse (aspect-stretched along path tangent)
                Handles.color = new Color(color.r, color.g, color.b, 0.18f);
                DrawWaypointEllipse(waypoints, i, avatarRoot);

                // Dot
                Handles.color = new Color(color.r, color.g, color.b, 0.8f);
                Handles.SphereHandleCap(0, worldPos, Quaternion.identity, sz, EventType.Repaint);

                InitStyles();
                s_overlayLabelStyle.normal.textColor = color;
                Handles.Label(worldPos + Vector3.up * sz * 1.5f,
                    (i + 1).ToString(), s_overlayLabelStyle);
            }
        }

        // ─────────────────────────────────────────────
        // Main scene GUI
        // ─────────────────────────────────────────────

        public static void CancelDrawing()
        {
            if (!IsDrawing) return;
            s_onCancel?.Invoke();
            StopDrawing();
        }

        private static void StopDrawing()
        {
            IsDrawing = false;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= PumpRepaint;
            SceneView.RepaintAll();
        }

        private static void InvalidatePathCaches(bool repaint = true)
        {
            s_waypointWorldCacheDirty = true;
            InvalidateAffectedVertexCache(repaint);
        }

        private static void InvalidateAffectedVertexCache(bool repaint = true)
        {
            s_affectedVertexCacheDirty = true;
            if (repaint && IsDrawing)
                SceneView.RepaintAll();
        }

        private static void UpdateWaypointWorldCache()
        {
            if (!s_waypointWorldCacheDirty
                && s_waypoints != null
                && s_tempPositions.Count == s_waypoints.Count
                && s_tempNormals.Count == s_waypoints.Count)
            {
                return;
            }

            s_waypointWorldCacheDirty = false;
            s_tempPositions.Clear();
            s_tempNormals.Clear();

            if (s_waypoints == null || s_avatarRoot == null)
                return;

            if (s_tempPositions.Capacity < s_waypoints.Count)
                s_tempPositions.Capacity = s_waypoints.Count;
            if (s_tempNormals.Capacity < s_waypoints.Count)
                s_tempNormals.Capacity = s_waypoints.Count;

            foreach (var wp in s_waypoints)
            {
                s_tempPositions.Add(s_avatarRoot.TransformPoint(wp.localPosition));
                s_tempNormals.Add(s_avatarRoot.TransformDirection(wp.localNormal).normalized);
            }
        }

        // Runs while drawing is active. Repaints scene views at most every
        // 100 ms so the affected-vertex cache visualization stays fresh,
        // without burning CPU on continuous OnSceneGUI -> Repaint cycles.
        private static double s_lastPumpTime;
        private static void PumpRepaint()
        {
            if (!s_affectedVertexCacheDirty) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - s_lastPumpTime < 0.1) return;
            s_lastPumpTime = now;
            SceneView.RepaintAll();
        }

        private static void Confirm()
        {
            if (s_waypoints.Count < 2) return;
            SyncRadiusToAll();
            s_onConfirm?.Invoke(new List<PathWaypoint>(s_waypoints));
            StopDrawing();
        }

        private static void Cancel()
        {
            s_onCancel?.Invoke();
            StopDrawing();
        }

        private static void InitStyles()
        {
            if (s_stylesInitialized) return;
            s_stylesInitialized = true;

            s_panelTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft
            };
            s_panelLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.75f, 0.75f, 0.75f) }
            };
            s_panelValueStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleRight,
                fontSize = 10
            };
            s_panelStatsStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                fontSize = 10
            };
            s_hintBarStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) },
                fontSize = 10
            };
            s_waypointNumStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14
            };
            s_overlayLabelStyle = new GUIStyle(EditorStyles.miniLabel);
        }

        private static float s_lastSyncedRadius;
        private static float s_lastSyncedAspect = 1f;

        /// <summary>Sets all waypoint radii and aspect to the current slider values, only when changed.</summary>
        private static void SyncRadiusToAll()
        {
            bool radiusChanged = !Mathf.Approximately(s_pathRadius, s_lastSyncedRadius);
            bool aspectChanged = !Mathf.Approximately(s_pathAspect, s_lastSyncedAspect);
            if (!radiusChanged && !aspectChanged) return;

            s_lastSyncedRadius = s_pathRadius;
            s_lastSyncedAspect = s_pathAspect;
            foreach (var wp in s_waypoints)
            {
                wp.radius = s_pathRadius;
                wp.aspectRatio = s_pathAspect;
            }

            InvalidatePathCaches();
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!IsDrawing) return;

            InitStyles();
            var evt = Event.current;
            float sceneW = sceneView.position.width;
            float sceneH = sceneView.position.height;
            // `panelRect` here reflects s_panelPos from the end of the previous
            // frame. s_panelPos is mutated only inside DrawFloatingPanel's drag
            // handler (during MouseDrag events), so a MouseDown on this frame
            // sees a stable rect - no one-frame lag to guard against.
            Rect panelRect = GetFloatingPanelRect(sceneW, sceneH);
            Rect hintBarRect = GetBottomHintBarRect(sceneW, sceneH);
            bool blockSceneInput = IsPointerOverOverlay(evt.mousePosition, panelRect, hintBarRect);

            // Sync radius slider → all waypoints only if changed
            SyncRadiusToAll();

            // ── Draw 3D elements first (handles, tube, vertices) ──
            DrawWaypointHandles(evt, !blockSceneInput);
            DrawPathVisualization();
            UpdateAndDrawVertexHighlight();

            // ── Handle click-to-add ──
            if (!blockSceneInput)
                HandleClickToAdd(evt);

            // ── Keyboard shortcuts ──
            // Enter/Escape are tool-level and fire regardless of pointer
            // position: the user should be able to confirm/cancel the draw
            // without having to move the mouse off the floating panel first.
            // Ctrl+Z is gated on `!blockSceneInput` so the panel area passes
            // the keystroke through to Unity's normal scene-undo (which is
            // what the user expects from Ctrl+Z when hovering over UI).
            if (evt.type == EventType.KeyDown)
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                { Confirm(); evt.Use(); return; }
                if (evt.keyCode == KeyCode.Escape)
                { Cancel(); evt.Use(); return; }
                if (!blockSceneInput
                    && evt.keyCode == KeyCode.Z && evt.control
                    && s_waypoints.Count > 0)
                {
                    s_waypoints.RemoveAt(s_waypoints.Count - 1);
                    InvalidatePathCaches();
                    evt.Use();
                    return;
                }
            }

            // ── Draw 2D overlays (panel + hint bar) ──
            Handles.BeginGUI();
            DrawFloatingPanel(panelRect, evt);
            DrawBottomHintBar(hintBarRect);
            Handles.EndGUI();

            // Prevent scene selection while drawing. Handle interactions repaint
            // automatically; the 10 Hz EditorApplication.update pump handles the
            // vertex-cache visualization refresh. Forcing a repaint here used to
            // create a continuous 60 Hz loop that multiplied every per-frame cost
            // with waypoint count - the main cause of the draw lag.
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
        }

        // ─────────────────────────────────────────────
        // Floating panel (draggable, bottom-left default)
        // ─────────────────────────────────────────────

        private static Rect GetFloatingPanelRect(float sceneW, float sceneH)
        {
            if (s_panelPos.y < 0f)
                s_panelPos = new Vector2(12f, sceneH - kPanelHeight - 34f);

            s_panelPos.x = Mathf.Clamp(s_panelPos.x, 0f, sceneW - kPanelWidth);
            s_panelPos.y = Mathf.Clamp(s_panelPos.y, 0f, sceneH - kPanelHeight);

            return new Rect(s_panelPos.x, s_panelPos.y, kPanelWidth, kPanelHeight);
        }

        private static Rect GetBottomHintBarRect(float sceneW, float sceneH)
        {
            return new Rect(0f, sceneH - kHintBarHeight, sceneW, kHintBarHeight);
        }

        private static bool IsPointerOverOverlay(
            Vector2 mousePosition, Rect panelRect, Rect hintBarRect)
        {
            return panelRect.Contains(mousePosition) || hintBarRect.Contains(mousePosition);
        }

        private static void DrawFloatingPanel(Rect panelRect, Event evt)
        {
            float panelW = panelRect.width;

            // Drag handling on title bar
            float titleH = kPanelTitleHeight;
            var titleRect = new Rect(panelRect.x, panelRect.y, panelW, titleH);

            if (evt.type == EventType.MouseDown && titleRect.Contains(evt.mousePosition))
            { s_isDraggingPanel = true; s_dragOffset = evt.mousePosition - s_panelPos; evt.Use(); }
            if (s_isDraggingPanel)
            {
                if (evt.type == EventType.MouseDrag)
                { s_panelPos = evt.mousePosition - s_dragOffset; evt.Use(); }
                if (evt.type == EventType.MouseUp)
                { s_isDraggingPanel = false; evt.Use(); }
            }

            panelRect = new Rect(s_panelPos.x, s_panelPos.y, panelRect.width, panelRect.height);
            titleRect = new Rect(panelRect.x, panelRect.y, panelW, titleH);

            // Background
            EditorGUI.DrawRect(panelRect, new Color(0.22f, 0.22f, 0.22f, 0.97f));
            EditorGUI.DrawRect(titleRect, new Color(0.19f, 0.19f, 0.19f));

            float pad = 8f;
            float x = panelRect.x + pad;
            float w = panelW - pad * 2;
            float y = panelRect.y + 4f;

            // Title
            s_panelTitleStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            GUI.Label(new Rect(x, y, w, 16f), "Path Editor", s_panelTitleStyle);
            y += titleH;

            // Path Radius
            GUI.Label(new Rect(x, y, 68f, 16f), "Path Radius", s_panelLabelStyle);
            s_pathRadius = GUI.HorizontalSlider(
                new Rect(x + 70f, y + 4f, w - 104f, 12f),
                s_pathRadius, 0.005f, 0.15f);
            GUI.Label(new Rect(x + w - 32f, y, 32f, 16f),
                s_pathRadius.ToString("F3"), s_panelValueStyle);
            y += 20f;

            // Aspect - stretch along path vs across path
            GUI.Label(new Rect(x, y, 68f, 16f), "Aspect", s_panelLabelStyle);
            s_pathAspect = GUI.HorizontalSlider(
                new Rect(x + 70f, y + 4f, w - 104f, 12f),
                s_pathAspect, 0.3f, 3f);
            GUI.Label(new Rect(x + w - 32f, y, 32f, 16f),
                s_pathAspect.ToString("F2"), s_panelValueStyle);
            y += 20f;

            // Snap toggle
            s_snapToVerts = GUI.Toggle(new Rect(x, y, w, 18f),
                s_snapToVerts, "Snap to Vertices", EditorStyles.miniButton);
            y += 22f;

            // Stats
            GUI.Label(new Rect(x, y, w, 14f),
                $"{s_waypoints.Count} pts  \u00B7  ~{s_cachedAffectedCount} verts",
                s_panelStatsStyle);
            y += 16f;

            // Buttons
            float btnH = 22f;
            float halfW = (w - 4f) * 0.5f;
            bool canConfirm = s_waypoints.Count >= 2;

            GUI.enabled = canConfirm;
            if (GUI.Button(new Rect(x, y, halfW, btnH), "Confirm"))
            { Confirm(); GUI.enabled = true; return; }

            GUI.enabled = true;
            if (GUI.Button(new Rect(x + halfW + 4f, y, halfW, btnH), "Cancel"))
            { Cancel(); return; }
            y += btnH + 3f;

            GUI.enabled = s_waypoints.Count > 0;
            if (GUI.Button(new Rect(x, y, halfW, btnH), "Undo Last"))
            {
                s_waypoints.RemoveAt(s_waypoints.Count - 1);
                InvalidatePathCaches();
            }
            if (GUI.Button(new Rect(x + halfW + 4f, y, halfW, btnH), "Clear All"))
            {
                s_waypoints.Clear();
                InvalidatePathCaches();
            }
            GUI.enabled = true;
        }

        private static void DrawBottomHintBar(Rect barRect)
        {
            EditorGUI.DrawRect(barRect, new Color(0.1f, 0.1f, 0.12f, 0.85f));

            GUI.Label(barRect,
                "Click mesh to add points  \u2022  Drag to move  \u2022  Right-click to remove  \u2022  Ctrl+Z to undo",
                s_hintBarStyle);
        }


        // ─────────────────────────────────────────────
        // 3D handle drawing
        // ─────────────────────────────────────────────

        private static void DrawWaypointHandles(Event evt, bool allowInteraction)
        {
            UpdateWaypointWorldCache();

            for (int i = 0; i < s_waypoints.Count; i++)
            {
                var wp = s_waypoints[i];
                Vector3 worldPos = s_tempPositions[i];
                float handleSize = HandleUtility.GetHandleSize(worldPos) * 0.22f;

                // Radius ellipse (aspect-stretched along path tangent)
                Handles.color = new Color(s_color.r, s_color.g, s_color.b, 0.25f);
                DrawWaypointEllipse(s_waypoints, i, s_avatarRoot, s_tempPositions, s_tempNormals);

                // Sphere handle (draggable)
                Handles.color = s_color;
                Vector3 newPos = worldPos;
                if (allowInteraction)
                {
                    newPos = Handles.FreeMoveHandle(
                        worldPos, handleSize, Vector3.zero, Handles.SphereHandleCap);
                }
                else
                {
                    Handles.SphereHandleCap(
                        0, worldPos, Quaternion.identity, handleSize, EventType.Repaint);
                }

                // Number label
                s_waypointNumStyle.normal.textColor = s_color;
                Handles.Label(worldPos + Vector3.up * handleSize * 1.6f,
                    (i + 1).ToString(), s_waypointNumStyle);

                // Snap dragged handle to mesh surface
                if (allowInteraction && (newPos - worldPos).sqrMagnitude > 0.000001f)
                {
                    var cam = SceneView.lastActiveSceneView?.camera;
                    if (cam != null)
                    {
                        Ray dragRay = new Ray(cam.transform.position,
                            (newPos - cam.transform.position).normalized);
                        if (RaycastToMesh(dragRay, out Vector3 snapPos, out Vector3 snapNormal))
                        {
                            if (s_snapToVerts)
                                SnapToNearestVertex(ref snapPos, ref snapNormal);
                            wp.localPosition = s_avatarRoot.InverseTransformPoint(snapPos);
                            wp.localNormal = s_avatarRoot.InverseTransformDirection(snapNormal);
                            s_tempPositions[i] = snapPos;
                            s_tempNormals[i] = snapNormal.normalized;
                            InvalidateAffectedVertexCache(false);
                        }
                    }
                }

                // Right-click to remove
                if (allowInteraction && evt.type == EventType.MouseDown && evt.button == 1)
                {
                    if (HandleUtility.DistanceToCircle(worldPos, handleSize) < 10f)
                    {
                        s_waypoints.RemoveAt(i);
                        InvalidatePathCaches(false);
                        evt.Use();
                        return;
                    }
                }
            }
        }

        private static void HandleClickToAdd(Event evt)
        {
            if (evt.type != EventType.MouseDown || evt.button != 0 || evt.alt) return;
            UpdateWaypointWorldCache();

            // Don't add if clicking on an existing handle
            for (int i = 0; i < s_waypoints.Count; i++)
            {
                Vector3 wp = s_tempPositions[i];
                float handleSize = HandleUtility.GetHandleSize(wp) * 0.22f;
                if (HandleUtility.DistanceToCircle(wp, handleSize) < 15f)
                    return;
            }

            Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
            if (RaycastToMesh(ray, out Vector3 hitPos, out Vector3 hitNormal))
            {
                if (s_snapToVerts)
                    SnapToNearestVertex(ref hitPos, ref hitNormal);

                s_waypoints.Add(new PathWaypoint
                {
                    localPosition = s_avatarRoot.InverseTransformPoint(hitPos),
                    localNormal = s_avatarRoot.InverseTransformDirection(hitNormal),
                    radius = s_pathRadius,
                    aspectRatio = s_pathAspect
                });
                InvalidatePathCaches(false);
                evt.Use();
            }
        }

        // ─────────────────────────────────────────────
        // Path + tube visualization
        // ─────────────────────────────────────────────

        private static void DrawPathVisualization()
        {
            if (s_waypoints.Count < 2) return;
            UpdateWaypointWorldCache();

            // Thick smooth centerline. Populates s_splinePoints as a side effect,
            // which we reuse below for the tip arrow instead of a fresh
            // ToPolyline allocation.
            DrawSpline(s_waypoints, s_avatarRoot, s_color, 0.8f, 3.5f);

            // Direction arrow at end, pulled straight from the last two samples
            // of the polyline we just drew.
            if (s_splinePoints != null && s_splinePoints.Length >= 2)
            {
                Vector3 tip = s_splinePoints[s_splinePoints.Length - 1];
                Vector3 prev = s_splinePoints[s_splinePoints.Length - 2];
                Vector3 dir = (tip - prev).normalized;
                float arrowSize = HandleUtility.GetHandleSize(tip) * 0.12f;
                Handles.color = s_color;
                Handles.ConeHandleCap(0, tip + dir * arrowSize,
                    Quaternion.LookRotation(dir), arrowSize, EventType.Repaint);
            }
        }

        /// <summary>
        /// Draws the radius ellipse at a waypoint. Axes align with the path
        /// tangent and its cross product with the surface normal so the ellipse
        /// is flat on the surface and stretched along the path.
        /// </summary>
        private static void DrawWaypointEllipse(
            List<PathWaypoint> waypoints, int index, Transform avatarRoot)
        {
            DrawWaypointEllipse(waypoints, index, avatarRoot, null, null);
        }

        private static void DrawWaypointEllipse(
            List<PathWaypoint> waypoints, int index, Transform avatarRoot,
            List<Vector3> worldPositions, List<Vector3> worldNormals)
        {
            var wp = waypoints[index];
            bool hasCachedPositions = worldPositions != null && worldPositions.Count == waypoints.Count;
            bool hasCachedNormals = worldNormals != null && worldNormals.Count == waypoints.Count;
            Vector3 worldPos = hasCachedPositions
                ? worldPositions[index]
                : avatarRoot.TransformPoint(wp.localPosition);
            Vector3 worldNormal = hasCachedNormals
                ? worldNormals[index]
                : avatarRoot.TransformDirection(wp.localNormal).normalized;

            // Path tangent at this waypoint (neighbor differences)
            Vector3 tangent;
            if (waypoints.Count < 2)
            {
                tangent = Vector3.Cross(worldNormal, Vector3.up);
                if (tangent.sqrMagnitude < 0.01f)
                    tangent = Vector3.Cross(worldNormal, Vector3.forward);
                tangent = tangent.normalized;
            }
            else if (index == 0)
            {
                Vector3 next = hasCachedPositions
                    ? worldPositions[1]
                    : avatarRoot.TransformPoint(waypoints[1].localPosition);
                tangent = (next - worldPos).normalized;
            }
            else if (index == waypoints.Count - 1)
            {
                Vector3 prev = hasCachedPositions
                    ? worldPositions[index - 1]
                    : avatarRoot.TransformPoint(waypoints[index - 1].localPosition);
                tangent = (worldPos - prev).normalized;
            }
            else
            {
                Vector3 prev = hasCachedPositions
                    ? worldPositions[index - 1]
                    : avatarRoot.TransformPoint(waypoints[index - 1].localPosition);
                Vector3 next = hasCachedPositions
                    ? worldPositions[index + 1]
                    : avatarRoot.TransformPoint(waypoints[index + 1].localPosition);
                tangent = (next - prev).normalized;
            }

            // Project tangent onto surface plane, then cross for perpendicular
            Vector3 alongAxis = (tangent - Vector3.Dot(tangent, worldNormal) * worldNormal).normalized;
            if (alongAxis.sqrMagnitude < 0.01f) alongAxis = tangent; // fallback
            Vector3 acrossAxis = Vector3.Cross(worldNormal, alongAxis).normalized;

            float aspect = Mathf.Max(0.1f, wp.aspectRatio);
            float alongRadius = wp.radius * aspect;
            float acrossRadius = wp.radius / aspect;

            const int segments = 48;
            Vector3 prevPt = worldPos + alongAxis * alongRadius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * (2f * Mathf.PI / segments);
                Vector3 pt = worldPos
                    + alongAxis * (alongRadius * Mathf.Cos(angle))
                    + acrossAxis * (acrossRadius * Mathf.Sin(angle));
                Handles.DrawLine(prevPt, pt);
                prevPt = pt;
            }
        }

        /// <summary>
        /// Draws the spline centerline as a single batched antialiased polyline.
        ///
        /// Earlier versions issued one <c>Handles.DrawAAPolyLine</c> per
        /// sub-segment to pulse brightness at each waypoint - pretty, but that
        /// turned into 20·N DrawAAPolyLine calls per scene repaint (each is a
        /// separate GPU submission), and an O(N²) inner brightness loop on top.
        /// At 4-5 waypoints × 60 Hz the editor starts crawling.
        ///
        /// One batched call with a point array is orders of magnitude cheaper
        /// and visually indistinguishable at normal zoom.
        /// </summary>
        private static void DrawSpline(
            List<PathWaypoint> waypoints, Transform avatarRoot,
            Color color, float lineAlpha, float lineWidth)
        {
            List<Vector3> worldPositions;
            if (ReferenceEquals(waypoints, s_waypoints) && avatarRoot == s_avatarRoot)
            {
                UpdateWaypointWorldCache();
                worldPositions = s_tempPositions;
            }
            else
            {
                s_auxPositions.Clear();
                if (s_auxPositions.Capacity < waypoints.Count)
                    s_auxPositions.Capacity = waypoints.Count;
                foreach (var wp in waypoints)
                    s_auxPositions.Add(avatarRoot.TransformPoint(wp.localPosition));
                worldPositions = s_auxPositions;
            }

            // 10 samples per waypoint is plenty smooth; floor of 40 keeps short
            // paths readable. Half the old per-waypoint cost.
            int segmentsPerSpan = 10;
            int floor = 40;
            int spanCount = Mathf.Max(waypoints.Count - 1, 1);
            int sampleCount = Mathf.Max(spanCount * segmentsPerSpan, floor) + 1;

            // Reuse the static buffer when its size already matches, otherwise
            // reallocate - avoids per-frame GC pressure during active drawing.
            if (s_splinePoints == null || s_splinePoints.Length != sampleCount)
                s_splinePoints = new Vector3[sampleCount];

            int last = sampleCount - 1;
            for (int i = 0; i <= last; i++)
            {
                float t = (float)i / last;
                s_splinePoints[i] = CatmullRomSpline.Evaluate(worldPositions, t);
            }

            Handles.color = new Color(color.r, color.g, color.b, lineAlpha);
            Handles.DrawAAPolyLine(lineWidth, s_splinePoints);
        }

        // ─────────────────────────────────────────────
        // Vertex highlighting
        // ─────────────────────────────────────────────

        private static void UpdateAndDrawVertexHighlight()
        {
            UpdateAffectedVertexCache();

            if (s_cachedAffectedCount <= 0 || Event.current.type != EventType.Repaint)
                return;

            if (s_vertexMaterial == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                s_vertexMaterial = new Material(shader);
                s_vertexMaterial.hideFlags = HideFlags.HideAndDontSave;
                s_vertexMaterial.SetInt("_ZTest",
                    (int)UnityEngine.Rendering.CompareFunction.Always);
                s_vertexMaterial.SetInt("_ZWrite", 0);
            }

            s_vertexMaterial.SetPass(0);
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.QUADS);

            float pointSize = 0.004f;
            var cam = SceneView.lastActiveSceneView?.camera;
            if (cam == null) { GL.End(); GL.PopMatrix(); return; }

            Vector3 camRight = cam.transform.right;
            Vector3 camUp = cam.transform.up;

            for (int i = 0; i < s_cachedAffectedCount; i++)
            {
                float alpha = Mathf.Lerp(0.3f, 1f, s_cachedAffectedWeights[i]);
                GL.Color(new Color(s_color.r, s_color.g, s_color.b, alpha));

                Vector3 p = s_cachedAffectedVerts[i];
                float size = pointSize * HandleUtility.GetHandleSize(p);
                Vector3 r = camRight * size;
                Vector3 u = camUp * size;

                GL.Vertex(p - r - u);
                GL.Vertex(p - r + u);
                GL.Vertex(p + r + u);
                GL.Vertex(p + r - u);
            }

            GL.End();
            GL.PopMatrix();
        }

        private static void UpdateAffectedVertexCache()
        {
            if (!s_affectedVertexCacheDirty)
                return;
            if (EditorApplication.timeSinceStartup - s_lastVertexCalcTime < 0.1)
                return;
            s_lastVertexCalcTime = EditorApplication.timeSinceStartup;
            s_affectedVertexCacheDirty = false;

            if (s_targetMesh == null || s_waypoints.Count == 0)
            { s_cachedAffectedCount = 0; return; }

            var smr = s_targetMesh.GetComponent<SkinnedMeshRenderer>();
            if (smr == null || smr.sharedMesh == null)
            { s_cachedAffectedCount = 0; return; }

            var mesh = smr.sharedMesh;
            // Cache mesh.vertices across ticks - the getter copies the full array,
            // which was wasted work at our 100 ms cadence on 50k+ vert meshes.
            if (s_affectedCachedMesh != mesh)
            {
                s_affectedCachedMesh = mesh;
                s_affectedCachedVertices = mesh.vertices;
            }
            var vertices = s_affectedCachedVertices;
            int vertCount = vertices.Length;
            const int previewVertexBudget = 16000;
            int stride = Mathf.Max(1, Mathf.CeilToInt(vertCount / (float)previewVertexBudget));

            int maxResults = vertCount / stride + 1;
            if (s_cachedAffectedVerts == null || s_cachedAffectedVerts.Length < maxResults)
            {
                s_cachedAffectedVerts = new Vector3[maxResults];
                s_cachedAffectedWeights = new float[maxResults];
            }

            s_cachedAffectedCount = 0;

            int segments = Mathf.Clamp((s_waypoints.Count - 1) * 6, 12, 48);
            var tube = CatmullRomSpline.BuildTube(s_waypoints, segments);
            Matrix4x4 meshToWorld = s_targetMesh.transform.localToWorldMatrix;
            Matrix4x4 meshToAvatar = s_avatarRoot.worldToLocalMatrix * meshToWorld;

            for (int v = 0; v < vertCount; v += stride)
            {
                Vector3 localVert = meshToAvatar.MultiplyPoint3x4(vertices[v]);

                float dist = tube.DistanceToTube(localVert, out float radiusAtClosest);
                if (dist < radiusAtClosest)
                {
                    float w = 1f - (dist / radiusAtClosest);
                    w = w * w * (3f - 2f * w); // smoothstep
                    if (w > 0.001f)
                    {
                        Vector3 worldVert = meshToWorld.MultiplyPoint3x4(vertices[v]);
                        s_cachedAffectedVerts[s_cachedAffectedCount] = worldVert;
                        s_cachedAffectedWeights[s_cachedAffectedCount] = w;
                        s_cachedAffectedCount++;
                    }
                }
            }
        }

        // ─────────────────────────────────────────────
        // Utilities
        // ─────────────────────────────────────────────

        private static float GetPathLength()
        {
            if (s_waypoints.Count < 2 || s_avatarRoot == null) return 0f;
            var positions = new List<Vector3>(s_waypoints.Count);
            foreach (var wp in s_waypoints)
                positions.Add(s_avatarRoot.TransformPoint(wp.localPosition));
            return CatmullRomSpline.ArcLength(positions);
        }

        /// <summary>
        /// Snaps a world-space position to the nearest mesh vertex.
        /// Also outputs the smoothed normal at that vertex.
        /// </summary>
        private static void SnapToNearestVertex(ref Vector3 worldPos, ref Vector3 worldNormal)
        {
            if (s_targetMesh == null) return;

            var smr = s_targetMesh.GetComponent<SkinnedMeshRenderer>();
            Mesh mesh = smr != null ? smr.sharedMesh : null;
            if (mesh == null)
            {
                var mf = s_targetMesh.GetComponent<MeshFilter>();
                mesh = mf != null ? mf.sharedMesh : null;
            }
            if (mesh == null) return;

            // Cache vertex/normal arrays and a KDTree over object-space positions.
            // Querying in object space lets the tree survive avatar moves - we only
            // rebuild when the mesh asset changes.
            if (s_snapCachedMesh != mesh)
            {
                s_snapCachedMesh = mesh;
                // On a SkinnedMeshRenderer the user clicks against the posed (skinned)
                // surface, so the snap candidates have to be the posed positions too -
                // mesh.vertices is bind-pose and can be cm off on a posed avatar.
                if (smr != null)
                {
                    var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    try
                    {
                        smr.BakeMesh(baked, true);
                        s_snapVertices = baked.vertices;
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(baked);
                    }
                }
                else
                {
                    s_snapVertices = mesh.vertices;
                }
                s_snapNormals = mesh.normals;
                s_snapTree = new KDTreeNearest(s_snapVertices);
            }
            var vertices = s_snapVertices;
            var normals = s_snapNormals;
            var meshTransform = s_targetMesh.transform;

            int bestIdx = s_snapTree.FindNearest(meshTransform.InverseTransformPoint(worldPos));
            if (bestIdx >= 0)
            {
                worldPos = meshTransform.TransformPoint(vertices[bestIdx]);
                if (bestIdx < normals.Length)
                    worldNormal = meshTransform.TransformDirection(normals[bestIdx]).normalized;
            }
        }

        private static bool RaycastToMesh(Ray ray, out Vector3 hitPos, out Vector3 hitNormal)
        {
            hitPos = Vector3.zero;
            hitNormal = Vector3.up;

            if (s_targetMesh == null) return false;

            var skinnedRenderer = s_targetMesh.GetComponent<SkinnedMeshRenderer>();
            var meshFilter = s_targetMesh.GetComponent<MeshFilter>();

            Mesh mesh = null;
            if (skinnedRenderer != null)
                mesh = skinnedRenderer.sharedMesh;
            else if (meshFilter != null)
                mesh = meshFilter.sharedMesh;

            if (mesh == null) return false;

            if (s_intersectRayMeshMethod == null)
            {
                s_intersectRayMeshMethod = typeof(HandleUtility).GetMethod(
                    "IntersectRayMesh",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(Ray), typeof(Mesh), typeof(Matrix4x4),
                        typeof(RaycastHit).MakeByRefType() },
                    null);
            }

            if (s_intersectRayMeshMethod != null)
            {
                var parameters = new object[]
                    { ray, mesh, s_targetMesh.transform.localToWorldMatrix, null };
                bool result = (bool)s_intersectRayMeshMethod.Invoke(null, parameters);
                if (result)
                {
                    var hit = (RaycastHit)parameters[3];
                    hitPos = hit.point;
                    hitNormal = hit.normal;
                    return true;
                }
            }
            else
            {
                var tempCollider = s_targetMesh.AddComponent<MeshCollider>();
                tempCollider.sharedMesh = mesh;
                try
                {
                    if (Physics.Raycast(ray, out RaycastHit hit) &&
                        hit.collider == tempCollider)
                    {
                        hitPos = hit.point;
                        hitNormal = hit.normal;
                        return true;
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tempCollider);
                }
            }

            return false;
        }
    }
}
