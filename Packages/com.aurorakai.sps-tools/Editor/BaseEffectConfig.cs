using System;
using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Feature gates. Flip to <c>true</c> to surface work-in-progress UI.
    /// </summary>
    public static class FeatureFlags
    {
        // Bone-chain / bone-scale deformation path. Hidden until testing is complete;
        // underlying code (generators, detectors, serialized fields) is intentionally
        // preserved so flipping this flag restores full functionality.
        public const bool BoneChainEnabled = false;

        // Developer-only UI (blend-tree dump button, verbose logs on generate).
        // Leave false for shipped builds.
        public const bool DebugUiEnabled = false;
    }

    public enum DeformationMode
    {
        BoneScale,
        Blendshape
    }

    [Serializable]
    public class PathWaypoint
    {
        public Vector3 localPosition;
        public Vector3 localNormal;
        public float radius = 0.03f;
        // Ellipse stretch factor: 1.0 = circle, >1 elongated along path, <1 squished
        public float aspectRatio = 1f;
    }

    /// <summary>
    /// Tracks an additional SkinnedMeshRenderer that should receive matching
    /// blendshapes alongside the primary mesh (e.g. a clothing overlay).
    /// Stores both the live reference and an asset path so the entry survives
    /// domain reloads.
    /// </summary>
    [Serializable]
    public class TrackedMesh
    {
        public string rendererPath;
        public Mesh originalMesh;
        public string originalMeshPath = "";
        public string originalMeshGuid = "";
        public Mesh generatedMesh;
        public string generatedMeshPath = "";
        public string generatedMeshGuid = "";
    }

    public abstract class BaseEffectConfig : ScriptableObject
    {
        // Hidden in Inspector because scene references can't be serialized in assets.
        // The editor window reconnects via avatarRootName at runtime.
        [HideInInspector] public GameObject avatarRoot;
        public string avatarRootName = "";     // for re-finding avatar after reload
        public string depthParameter = "SPS_Depth";
        public int selectedSocketIndex = -1;
        public List<int> enabledSocketIndices = new List<int>();
        public DeformationMode deformationMode = DeformationMode.Blendshape;

        /// <summary>
        /// The deformation mode the system should act on. When
        /// <see cref="FeatureFlags.BoneChainEnabled"/> is off, BoneScale is
        /// coerced to Blendshape at read time so WIP data never reaches
        /// generation or preview. Read this anywhere behavior branches on
        /// mode; the raw <see cref="deformationMode"/> field is only for
        /// rendering the mode-picker toolbar.
        /// </summary>
        public DeformationMode EffectiveDeformationMode =>
            FeatureFlags.BoneChainEnabled ? deformationMode : DeformationMode.Blendshape;

        // Bone scale mode
        public string targetBonePath = "";
        public bool scaleX = true;
        public bool scaleY = true;
        public bool scaleZ;

        // Blendshape mode
        public string rendererPath = "";
        public string blendshapeNamingPattern = ""; // e.g. "TummyBulge{0}" - leave empty for default

        // Path data (shared by both for auto-generation)
        public List<PathWaypoint> pathWaypoints = new List<PathWaypoint>();

        // Blendshape displacement in meters (how far vertices push outward at weight 100)
        public float blendshapeDisplacement = 0.015f;

        // Blendshape smoothing (Laplacian passes)
        public int smoothingPasses = 3;

        // Dynamic subdivision of affected mesh region before blendshape generation
        public bool subdivideAffectedRegion;
        public int subdivisionPasses = 1;

        // Recalculate blendshape normal deltas so lighting updates with the deformation
        public bool recalculateNormals = true;

        // Advanced normal-delta tuning. Defaults preserve prior behavior.
        // Softness >1 widens the boundary transition (hides tri edges); <1 tightens it.
        // Smoothing passes run a 1-ring Laplacian blur on the computed normal deltas,
        // bounded to affected verts — targets inner-region faceting.
        public float normalFalloffSoftness = 1f;
        public int normalSmoothingPasses = 0;
        public int normalBoundaryRings = 1;

        // Non-destructive mesh handling (primary mesh)
        public Mesh originalMesh;    // pre-modification mesh (for restore)
        public string originalMeshPath = "";
        public string originalMeshGuid = "";
        public Mesh generatedMesh;   // generated mesh with added blendshapes
        public string generatedMeshPath = "";
        public string generatedMeshGuid = "";

        // Additional meshes that receive matching blendshapes (e.g. clothing overlays)
        public List<TrackedMesh> additionalMeshes = new List<TrackedMesh>();

        // Configuration naming (supports multiple configs per avatar)
        public string configurationName = "Default";

        [HideInInspector] public string stableConfigId = "";

        public abstract bool IsValid();

        public void EnsureStableConfigId()
        {
            if (string.IsNullOrEmpty(stableConfigId))
                stableConfigId = Guid.NewGuid().ToString("N");
        }

        public void ResetStableConfigId()
        {
            stableConfigId = "";
        }

        /// <summary>
        /// Short identifier for the concrete effect type (e.g. "Bulge").
        /// Used as a path segment so configs of different types coexist under
        /// the same avatar folder without their generated assets colliding.
        /// </summary>
        public abstract string EffectTypeName { get; }

        /// <summary>
        /// Returns the sanitized folder path for this config - the single
        /// location that holds the config asset itself, its generated mesh,
        /// clips, controller, and the NormalMap subfolder. Co-locating config
        /// and generated output makes it trivial to back up or delete all
        /// artifacts for a given (avatar, effect, config) triple.
        /// </summary>
        public string GetConfigFolder()
        {
            if (avatarRoot == null) return null;
            string avatarName = SanitizeFileName(avatarRoot.name);
            string effect = SanitizeFileName(EffectTypeName);
            string configName = SanitizeFileName(
                string.IsNullOrWhiteSpace(configurationName)
                    ? "Default"
                    : configurationName);
            return $"Assets/SPSTools/{avatarName}/{effect}/{configName}";
        }

        /// <summary>
        /// Alias for <see cref="GetConfigFolder"/>. Generated assets and the
        /// config share one folder; callers use this name at sites that write
        /// output so intent is obvious locally.
        /// </summary>
        public string GetOutputFolder() => GetConfigFolder();

        /// <summary>
        /// Returns the VRCFury child GameObject name for this configuration.
        /// </summary>
        public string GetGameObjectName(string effectPrefix)
        {
            return $"[{effectPrefix} - {configurationName}]";
        }

        /// <summary>
        /// Returns the blendshape name for a given index.
        /// Uses custom pattern if set (e.g. "TummyBulge{0}" → "TummyBulge1"),
        /// otherwise falls back to defaultPrefix + index (e.g. "SPSBulge_Pos1").
        /// </summary>
        public string GetBlendshapeName(int index, string defaultPrefix)
        {
            if (!string.IsNullOrEmpty(blendshapeNamingPattern) &&
                blendshapeNamingPattern.Contains("{0}"))
            {
                return string.Format(blendshapeNamingPattern, index);
            }
            return $"{defaultPrefix}{index}";
        }

        public static string GetRelativePath(Transform root, Transform target)
        {
            var parts = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts);
        }

        public static SkinnedMeshRenderer ResolveRenderer(
            GameObject avatarRoot, string rendererPath)
        {
            if (avatarRoot == null || string.IsNullOrEmpty(rendererPath)) return null;
            var t = avatarRoot.transform.Find(rendererPath);
            return t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
        }

        /// <summary>
        /// Replaces every character in <see cref="System.IO.Path.GetInvalidFileNameChars"/>
        /// with '_'. Safe on every platform (the invalid-char set differs between
        /// Windows and Unix — we always split on the superset for the current host).
        /// </summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid));
        }
    }
}
