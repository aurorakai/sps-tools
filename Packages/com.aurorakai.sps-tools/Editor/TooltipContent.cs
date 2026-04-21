using UnityEngine;

namespace AuroraKai.SPSTools
{
    public static class TooltipContent
    {
        public static readonly GUIContent PathRadius = new GUIContent(
            "Path Radius", "Controls which vertices are included in the blendshape region. Larger radius = more vertices affected.");
        public static readonly GUIContent Displacement = new GUIContent(
            "Displacement (mm)", "How far vertices push outward at blendshape weight 100.");
        public static readonly GUIContent SmoothingPasses = new GUIContent(
            "Smoothing Passes", "Laplacian smoothing iterations. Higher = smoother but softer deformation. 0 = raw displacement.");
        public static readonly GUIContent SubdivideRegion = new GUIContent(
            "Subdivide Region", "Adds geometry in the deformation zone for smoother results on low-poly meshes.");
        public static readonly GUIContent SubdivisionPasses = new GUIContent(
            "Subdivision Passes", "Number of subdivision iterations. Each pass quadruples triangle count in the region.");
        public static readonly GUIContent RecalculateNormals = new GUIContent(
            "Recalculate Normals", "Recomputes normals on the deformed blendshape so lighting updates with the bulge. Recommended for correct shading.");
        public static readonly GUIContent NormalFalloffSoftness = new GUIContent(
            "Falloff Softness", "Tunes the boundary falloff on recomputed normals. 1 = baseline smoothstep. Higher (2-3) widens the transition to hide triangle edges at the affected region's boundary. Lower (<1) tightens it.");
        public static readonly GUIContent NormalSmoothingPasses = new GUIContent(
            "Smoothing Passes", "Laplacian blur iterations on the normal deltas, bounded to affected verts. 0 = off (baseline). 1-3 smooths inner-region faceting. Higher = more blur, longer generation.");
        public static readonly GUIContent NormalBoundaryRings = new GUIContent(
            "Boundary Blend Rings", "How many rings of unmoved verts outside the affected region receive a partial normal delta. 1 = baseline (tight boundary, 1 tri wide). 2-4 = wider blend for low-poly meshes where the transition is visible as a crease.");
        public static readonly GUIContent BulgeIntensity = new GUIContent(
            "Bulge Intensity", "How much of the blendshape displacement is used. 0% = off, 100% = full, 200% = overdrive.");
        public static readonly GUIContent BulgeWidth = new GUIContent(
            "Bulge Width", "How many neighboring positions are affected by each peak. Higher = wider, more gradual shape.");
        public static readonly GUIContent DepthRange = new GUIContent(
            "Depth Range", "The SPS depth range over which the effect travels.");
        public static readonly GUIContent DepthRangeStart = new GUIContent(
            "Start", "Depth parameter value where the effect begins.");
        public static readonly GUIContent DepthRangeEnd = new GUIContent(
            "End", "Depth parameter value where the effect reaches its deepest position. Lower this to match your socket's FX Float saturation point when it can't reach 1.0 at full insertion.");
        public static readonly GUIContent SnapToVertices = new GUIContent(
            "Snap to Vertices", "Snap path waypoints to the nearest mesh vertex for precise placement.");
        public static readonly GUIContent ConfigName = new GUIContent(
            "Configuration Name", "Name for this configuration. Used in file paths and VRCFury component naming.");
        public static readonly GUIContent DepthParameter = new GUIContent(
            "Depth Parameter", "The FX Float parameter driven by the SPS Socket's depth animations.");
    }
}
