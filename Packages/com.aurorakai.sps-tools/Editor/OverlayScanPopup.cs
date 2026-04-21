using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Modal-ish popup showing candidate overlay meshes with coverage percentages.
    /// User picks which to add via checkboxes; strong matches are pre-checked.
    /// </summary>
    public class OverlayScanPopup : EditorWindow
    {
        private List<OverlayMeshDetector.Candidate> candidates;
        private bool[] selected;
        private Action<List<OverlayMeshDetector.Candidate>> onConfirm;
        private Vector2 scroll;

        private static GUIStyle s_linkBoldStyle;

        public static void Show(
            List<OverlayMeshDetector.Candidate> candidates,
            Action<List<OverlayMeshDetector.Candidate>> onConfirm)
        {
            var w = CreateInstance<OverlayScanPopup>();
            w.candidates = candidates;
            w.selected = new bool[candidates.Count];   // none pre-selected - user picks
            w.onConfirm = onConfirm;
            w.titleContent = new GUIContent("Overlay Mesh Scan");
            w.minSize = new Vector2(420, 300);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            // State is lost on domain reload (script recompile while popup is open);
            // close cleanly instead of NRE-ing in the row loop.
            if (candidates == null || selected == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField("Candidate Overlay Meshes", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each candidate's count is the number of its vertices that fall inside the " +
                "path's affected region. Click a row's name to highlight the mesh in the scene.",
                MessageType.None);
            EditorGUILayout.Space(4);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                selected[i] = EditorGUILayout.Toggle(selected[i], GUILayout.Width(18));

                EditorGUILayout.BeginVertical();
                EditorGUILayout.BeginHorizontal();
                string nameLabel = c.renderer != null ? c.renderer.gameObject.name : "(missing)";
                bool hidden = c.renderer != null && !c.renderer.gameObject.activeInHierarchy;
                if (hidden) nameLabel += "  (hidden)";
                if (s_linkBoldStyle == null)
                    s_linkBoldStyle = new GUIStyle(EditorStyles.linkLabel) { fontStyle = FontStyle.Bold };
                if (GUILayout.Button(nameLabel, s_linkBoldStyle))
                {
                    if (c.renderer != null)
                    {
                        Selection.activeGameObject = c.renderer.gameObject;
                        EditorGUIUtility.PingObject(c.renderer.gameObject);
                    }
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                string strength;
                if (c.vertsInRange >= OverlayMeshDetector.StrongMatchMinVerts) strength = "strong";
                else if (c.vertsInRange >= OverlayMeshDetector.SuggestedMatchMinVerts) strength = "suggested";
                else strength = "minimal";

                string densityHint;
                if (c.densityVsPrimary <= 0f)        densityHint = "";
                else if (c.densityVsPrimary < 0.85f) densityHint = $"~{(c.densityVsPrimary * 100f):F0}% as dense as primary";
                else if (c.densityVsPrimary < 1.15f) densityHint = "similar density to primary";
                else                                  densityHint = $"~{c.densityVsPrimary:F1}\u00D7 denser than primary";

                EditorGUILayout.LabelField(
                    $"{c.vertsInRange} verts in bulge area  ·  {strength}" +
                    (string.IsNullOrEmpty(densityHint) ? "" : $"  ·  {densityHint}"),
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"({c.totalVerts} total verts in mesh)",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();

                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select All"))
            {
                for (int i = 0; i < candidates.Count; i++) selected[i] = true;
            }
            if (GUILayout.Button("Clear"))
            {
                for (int i = 0; i < candidates.Count; i++) selected[i] = false;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel", GUILayout.Height(28)))
                Close();
            if (GUILayout.Button("Add Selected", GUILayout.Height(28)))
            {
                var picked = new List<OverlayMeshDetector.Candidate>();
                for (int i = 0; i < candidates.Count; i++)
                    if (selected[i]) picked.Add(candidates[i]);
                onConfirm?.Invoke(picked);
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
