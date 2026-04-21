using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Manual texture-slot picker for non-Poiyomi materials. Lists the shader's
    /// texture properties; user chooses which slot receives the baked map.
    /// No animation wiring — intensity animation on non-Poiyomi shaders is the
    /// user's responsibility.
    /// </summary>
    public class NormalMapApplyPopup : EditorWindow
    {
        /// <summary>One material + its renderers + the texture to assign.</summary>
        public struct Entry
        {
            public Material material;
            public List<string> rendererPaths;
            public Texture2D targetTexture;
        }

        private List<Entry> _entries;
        private int[] _selectedPropertyIndex;
        private string[][] _propertyNamesPerEntry;
        private bool[] _skip;
        private Vector2 _scroll;

        /// <summary>Opens the popup over the given entries.</summary>
        public static void Open(List<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return;
            var win = GetWindow<NormalMapApplyPopup>("Normal Map — Manual Apply");
            win._entries = entries;
            win.BuildPropertyDropdowns();
            win.minSize = new Vector2(460, 360);
            win.ShowUtility();
        }

        private void BuildPropertyDropdowns()
        {
            int n = _entries.Count;
            _selectedPropertyIndex = new int[n];
            _propertyNamesPerEntry = new string[n][];
            _skip = new bool[n];

            for (int i = 0; i < n; i++)
            {
                var mat = _entries[i].material;
                var names = new List<string>();
                if (mat != null && mat.shader != null)
                {
                    int count = ShaderUtil.GetPropertyCount(mat.shader);
                    for (int p = 0; p < count; p++)
                    {
                        if (ShaderUtil.GetPropertyType(mat.shader, p) != ShaderUtil.ShaderPropertyType.TexEnv)
                            continue;
                        string propName = ShaderUtil.GetPropertyName(mat.shader, p);
                        if (propName.Equals("_MainTex", System.StringComparison.Ordinal)) continue;
                        names.Add(propName);
                    }
                }
                _propertyNamesPerEntry[i] = names.ToArray();

                // Best-effort default: pick any slot whose name mentions normal/bump/detail.
                int preferred = -1;
                for (int k = 0; k < names.Count; k++)
                {
                    string low = names[k].ToLowerInvariant();
                    if (low.Contains("detailnormal") || low.Contains("detail_normal")
                        || low.Contains("bump") || low.Contains("normal"))
                    {
                        preferred = k;
                        break;
                    }
                }
                _selectedPropertyIndex[i] = preferred >= 0 ? preferred : 0;
            }
        }

        private void OnGUI()
        {
            if (_entries == null || _entries.Count == 0)
            {
                EditorGUILayout.LabelField("No materials to apply.");
                if (GUILayout.Button("Close")) Close();
                return;
            }

            EditorGUILayout.HelpBox(
                "Manual apply for non-Poiyomi materials. Pick a texture slot " +
                "on each material to receive the baked normal map. Animation " +
                "of the slot's blend/intensity is your responsibility on " +
                "non-Poiyomi shaders.",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _entries.Count; i++)
                DrawEntry(i);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel")) Close();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Apply", GUILayout.Width(120)))
            {
                ApplySelections();
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntry(int i)
        {
            var e = _entries[i];
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            _skip[i] = !EditorGUILayout.Toggle(!_skip[i], GUILayout.Width(18));
            EditorGUILayout.ObjectField(e.material, typeof(Material), false);
            EditorGUILayout.EndHorizontal();

            if (e.rendererPaths != null && e.rendererPaths.Count > 1)
            {
                EditorGUILayout.HelpBox(
                    $"Material is used by {e.rendererPaths.Count} renderers. " +
                    "Applying here will modify shading on all of them.",
                    MessageType.Warning);
            }

            if (!_skip[i])
            {
                var names = _propertyNamesPerEntry[i];
                if (names.Length == 0)
                {
                    EditorGUILayout.LabelField(
                        "  (shader has no texture properties other than _MainTex)",
                        EditorStyles.miniLabel);
                }
                else
                {
                    _selectedPropertyIndex[i] = EditorGUILayout.Popup(
                        "Target Slot", _selectedPropertyIndex[i], names);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void ApplySelections()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_skip[i]) continue;
                var names = _propertyNamesPerEntry[i];
                if (names.Length == 0) continue;

                var e = _entries[i];
                if (e.material == null || e.targetTexture == null) continue;

                string propName = names[_selectedPropertyIndex[i]];

                Undo.RecordObject(e.material, "Apply Baked Normal Map");
                e.material.SetTexture(propName, e.targetTexture);
                EditorUtility.SetDirty(e.material);
            }
            AssetDatabase.SaveAssets();
        }
    }
}
