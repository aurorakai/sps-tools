using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    // Floating utility popup shown the first time a user opens an effect
    // window. EditorPrefs flag is set in OnDestroy so any close path (X
    // button, Got it button, domain reload) counts as "dismissed".
    internal class EffectWelcomeWindow : EditorWindow
    {
        private const float WindowWidth = 540f;
        // Sized to the current six-step Bulge content; FlexibleSpace below the
        // scroll view absorbs small variance and overflow falls back to scroll.
        private const float WindowHeight = 525f;
        private const float HorizontalPadding = 24f;
        private const float VerticalPadding = 20f;
        private const float AccentBarHeight = 3f;

        // Unicode circled-digit glyphs for steps 1-9; rare to need more.
        private static readonly string[] s_circledNumbers =
            { "①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨" };

        // Singleton guard — re-Show calls focus the existing window instead
        // of stacking duplicates. Cleared in OnDestroy.
        private static EffectWelcomeWindow s_instance;

        // Set by beforeAssemblyReload so OnDestroy can tell a domain-reload
        // teardown apart from a real user dismiss.
        private static bool s_assemblyReloading;

        private string _intro;
        private string[] _stepHeadlines;
        private string[] _stepBodies;
        private Color _accent;
        private string _editorPrefsKey;

        // Lazy-built styles (EditorStyles isn't ready in OnEnable).
        private GUIStyle _introStyle;
        private GUIStyle _stepNumberStyle;
        private GUIStyle _stepHeadlineStyle;
        private GUIStyle _stepBodyStyle;
        private GUIStyle _footerStyle;
        private Vector2 _scroll;

        // Single source of truth for the EditorPrefs key shape. Used by
        // BaseEffectWindow to gate first-run display and persist dismissal.
        internal static string KeyFor(Type effectWindowType) =>
            string.Format("SPSPop.Welcome.{0}.Shown", effectWindowType.Name);

        public static void Show(
            string title,
            string intro,
            IReadOnlyList<(string headline, string body)> steps,
            Color accent,
            string editorPrefsKey)
        {
            if (s_instance != null)
            {
                s_instance.Focus();
                return;
            }

            var w = CreateInstance<EffectWelcomeWindow>();
            w.titleContent = new GUIContent(title);
            w._intro = intro ?? "";
            w._accent = accent;
            w._editorPrefsKey = editorPrefsKey;

            int n = steps?.Count ?? 0;
            w._stepHeadlines = new string[n];
            w._stepBodies = new string[n];
            for (int i = 0; i < n; i++)
            {
                w._stepHeadlines[i] = steps[i].headline ?? "";
                w._stepBodies[i] = steps[i].body ?? "";
            }

            // Center over the main editor window.
            var screen = EditorGUIUtility.GetMainWindowPosition();
            float x = screen.x + (screen.width - WindowWidth) * 0.5f;
            float y = screen.y + (screen.height - WindowHeight) * 0.5f;
            w.minSize = new Vector2(WindowWidth, WindowHeight);
            w.maxSize = new Vector2(WindowWidth, WindowHeight);
            w.position = new Rect(x, y, WindowWidth, WindowHeight);

            s_instance = w;
            w.ShowUtility();
        }

        private void OnGUI()
        {
            EnsureStyles();

            // Theme-color accent strip along the top edge.
            EditorGUI.DrawRect(new Rect(0, 0, position.width, AccentBarHeight), _accent);

            GUILayout.Space(VerticalPadding + AccentBarHeight);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(HorizontalPadding);
            EditorGUILayout.BeginVertical();

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUIStyle.none, GUIStyle.none);

            bool hasIntro = !string.IsNullOrEmpty(_intro);
            if (hasIntro)
            {
                DrawIntro();
                DrawSeparator();
            }
            DrawSteps();
            DrawSeparator();
            DrawFooter();

            EditorGUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            DrawDismissButton();

            EditorGUILayout.EndVertical();
            GUILayout.Space(HorizontalPadding);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(VerticalPadding);
        }

        private void DrawIntro()
        {
            if (string.IsNullOrEmpty(_intro)) return;
            EditorGUILayout.LabelField(_intro, _introStyle);
        }

        private void DrawSeparator()
        {
            GUILayout.Space(14f);
            Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(1f), GUILayout.ExpandWidth(true));
            Color sep = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.08f)
                : new Color(0f, 0f, 0f, 0.12f);
            EditorGUI.DrawRect(r, sep);
            GUILayout.Space(14f);
        }

        private void DrawSteps()
        {
            int count = _stepHeadlines?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                EditorGUILayout.BeginHorizontal();

                string num = i < s_circledNumbers.Length
                    ? s_circledNumbers[i]
                    : $"{i + 1}.";
                GUILayout.Label(num, _stepNumberStyle, GUILayout.Width(34f));

                EditorGUILayout.BeginVertical();
                GUILayout.Space(2f); // align baseline with the number glyph
                EditorGUILayout.LabelField(_stepHeadlines[i], _stepHeadlineStyle);
                EditorGUILayout.LabelField(_stepBodies[i], _stepBodyStyle);
                EditorGUILayout.EndVertical();

                EditorGUILayout.EndHorizontal();

                if (i < count - 1) GUILayout.Space(12f);
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.LabelField(
                "Hover any field for tooltips. Expand foldouts for extra controls.",
                _footerStyle);
        }

        private void DrawDismissButton()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Continue", GUILayout.Height(28f), GUILayout.Width(100f)))
                Close();
            EditorGUILayout.EndHorizontal();
        }

        private void OnEnable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
        }

        private static void OnBeforeAssemblyReload() => s_assemblyReloading = true;

        private void OnDestroy()
        {
            // A domain-reload teardown isn't a user dismiss — leave the
            // EditorPrefs flag alone so the popup re-appears next session.
            bool userDismissed = !s_assemblyReloading;
            if (userDismissed && !string.IsNullOrEmpty(_editorPrefsKey))
                EditorPrefs.SetBool(_editorPrefsKey, true);
            if (s_instance == this) s_instance = null;
        }

        private void EnsureStyles()
        {
            if (_introStyle != null) return;

            _introStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                fontSize = 13,
                richText = false,
            };

            _stepNumberStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 20,
                alignment = TextAnchor.UpperLeft,
            };
            _stepNumberStyle.normal.textColor = _accent;

            _stepHeadlineStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                wordWrap = true,
            };

            _stepBodyStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 12,
                wordWrap = true,
            };

            _footerStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
            };
        }
    }
}
