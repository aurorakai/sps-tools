using System;
using System.Collections.Generic;
using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// Which side of the body an SPS guided path is projected onto.
    /// Directions are relative to the avatar root.
    /// </summary>
    public enum GuidedPathProjection
    {
        Front,
        Back,
        Left,
        Right,
        Nearest
    }

    /// <summary>
    /// Turns an SPS2 guided path, which runs through the inside of the body,
    /// into bulge path waypoints on the target mesh's surface.
    ///
    /// Each path sample casts a ray in the chosen direction and takes the
    /// first surface it leaves the mesh through. Samples that aren't inside the
    /// mesh (no hit, or the first hit is a surface facing the sample) are
    /// trimmed from both ends of the path, and the depth range is narrowed to
    /// match, so depth fraction still lines up with distance along the path.
    /// </summary>
    public static class GuidedPathSurfaceProjector
    {
        // Path samples used to find where the path is inside the mesh.
        private const int CoverageSamples = 41;

        public class Result
        {
            public List<PathWaypoint> waypoints = new List<PathWaypoint>();

            /// <summary>Fraction of the guided path where the waypoints start.</summary>
            public float startFraction;

            /// <summary>Fraction of the guided path where the waypoints end.</summary>
            public float endFraction = 1f;

            /// <summary>Waypoints that fell back to the nearest vertex.</summary>
            public int nearestFallbacks;
        }

        /// <summary>
        /// Projects <paramref name="path"/> onto <paramref name="renderer"/>'s
        /// posed surface. Returns null if no part of the path projects onto the
        /// mesh.
        /// </summary>
        public static Result Project(
            SpsGuidedPath path,
            Renderer renderer,
            Transform avatarRoot,
            GuidedPathProjection projection,
            int waypointCount,
            float radius,
            float aspectRatio)
        {
            if (path == null || renderer == null || avatarRoot == null) return null;
            if (!TryGetWorldSurface(renderer, out var vertices, out var normals, out var triangles))
                return null;

            Vector3 direction = GetDirection(avatarRoot, projection);
            var surface = new Surface(vertices, normals, triangles);

            var result = FindCoveredRange(path, surface, direction);
            if (result == null) return null;

            waypointCount = Mathf.Max(2, waypointCount);
            for (int i = 0; i < waypointCount; i++)
            {
                float fraction = Mathf.Lerp(result.startFraction, result.endFraction,
                    i / (float)(waypointCount - 1));
                Vector3 sample = path.Evaluate(fraction);

                if (!surface.TryProject(sample, direction, out Vector3 point, out Vector3 normal))
                {
                    surface.Nearest(sample, out point, out normal);
                    result.nearestFallbacks++;
                }

                result.waypoints.Add(new PathWaypoint
                {
                    localPosition = avatarRoot.InverseTransformPoint(point),
                    localNormal = avatarRoot.InverseTransformDirection(normal).normalized,
                    radius = radius,
                    aspectRatio = aspectRatio
                });
            }

            return result;
        }

        /// <summary>
        /// Finds the part of the path between the first and last samples that
        /// project onto the surface. Returns null if none do.
        /// </summary>
        internal static Result FindCoveredRange(
            SpsGuidedPath path, Surface surface, Vector3 direction)
        {
            int first = -1, last = -1;
            for (int i = 0; i < CoverageSamples; i++)
            {
                Vector3 sample = path.Evaluate(i / (float)(CoverageSamples - 1));
                if (!surface.TryProject(sample, direction, out _, out _)) continue;
                if (first < 0) first = i;
                last = i;
            }

            if (first < 0 || first == last) return null;
            return new Result
            {
                startFraction = first / (float)(CoverageSamples - 1),
                endFraction = last / (float)(CoverageSamples - 1)
            };
        }

        internal static Vector3 GetDirection(Transform avatarRoot, GuidedPathProjection projection)
        {
            switch (projection)
            {
                case GuidedPathProjection.Front: return avatarRoot.forward;
                case GuidedPathProjection.Back: return -avatarRoot.forward;
                case GuidedPathProjection.Left: return -avatarRoot.right;
                case GuidedPathProjection.Right: return avatarRoot.right;
                default: return Vector3.zero;
            }
        }

        /// <summary>
        /// World-space triangles of the renderer as currently posed.
        /// </summary>
        private static bool TryGetWorldSurface(
            Renderer renderer, out Vector3[] vertices, out Vector3[] normals, out int[] triangles)
        {
            vertices = null;
            normals = null;
            triangles = null;

            Mesh source = null;
            if (renderer is SkinnedMeshRenderer smr)
            {
                source = smr.sharedMesh;
            }
            else
            {
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter != null) source = meshFilter.sharedMesh;
            }
            if (source == null) return false;

            var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                Mesh posed = source;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.BakeMesh(baked, true);
                    posed = baked;
                }

                var transform = renderer.transform;
                vertices = posed.vertices;
                normals = posed.normals;
                for (int v = 0; v < vertices.Length; v++)
                    vertices[v] = transform.TransformPoint(vertices[v]);
                if (normals.Length == vertices.Length)
                {
                    for (int v = 0; v < normals.Length; v++)
                        normals[v] = transform.TransformDirection(normals[v]).normalized;
                }
                else
                {
                    normals = null;
                }
                triangles = source.triangles;
                return vertices.Length > 0 && triangles.Length >= 3;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }

        /// <summary>
        /// A triangle mesh in world space that path samples are projected onto.
        /// </summary>
        internal class Surface
        {
            private readonly Vector3[] vertices;
            private readonly Vector3[] normals;
            private readonly int[] triangles;
            private KDTreeNearest nearestTree;

            public Surface(Vector3[] vertices, Vector3[] normals, int[] triangles)
            {
                this.vertices = vertices;
                this.normals = normals;
                this.triangles = triangles;
            }

            /// <summary>
            /// Casts from <paramref name="origin"/> along <paramref name="direction"/>
            /// and returns the first surface hit if the ray is leaving the mesh
            /// there, meaning the origin is inside. With a zero direction, uses
            /// the nearest vertex instead.
            /// </summary>
            public bool TryProject(
                Vector3 origin, Vector3 direction, out Vector3 point, out Vector3 normal)
            {
                if (direction == Vector3.zero)
                {
                    Nearest(origin, out point, out normal);
                    return true;
                }

                point = origin;
                normal = -direction;
                if (!Raycast(origin, direction.normalized,
                        out float distance, out int tri, out float u, out float v))
                {
                    return false;
                }

                point = origin + direction.normalized * distance;
                normal = GetNormal(tri, u, v);
                return Vector3.Dot(normal, direction) > 0f;
            }

            public void Nearest(Vector3 origin, out Vector3 point, out Vector3 normal)
            {
                if (nearestTree == null) nearestTree = new KDTreeNearest(vertices);
                int index = nearestTree.FindNearest(origin);
                point = vertices[index];
                normal = normals != null ? normals[index] : (point - origin).normalized;
            }

            private Vector3 GetNormal(int tri, float u, float v)
            {
                int a = triangles[tri], b = triangles[tri + 1], c = triangles[tri + 2];
                if (normals != null)
                {
                    Vector3 n = normals[a] * (1f - u - v) + normals[b] * u + normals[c] * v;
                    if (n.sqrMagnitude > 1e-12f) return n.normalized;
                }
                // Unity's front faces wind clockwise, so this is the outward normal.
                return Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized;
            }

            /// <summary>
            /// Two-sided Möller–Trumbore against every triangle; nearest hit wins.
            /// </summary>
            private bool Raycast(
                Vector3 origin, Vector3 direction,
                out float bestDistance, out int bestTri, out float bestU, out float bestV)
            {
                const float epsilon = 1e-8f;
                bestDistance = float.MaxValue;
                bestTri = -1;
                bestU = bestV = 0f;

                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    Vector3 a = vertices[triangles[t]];
                    Vector3 edge1 = vertices[triangles[t + 1]] - a;
                    Vector3 edge2 = vertices[triangles[t + 2]] - a;

                    Vector3 p = Vector3.Cross(direction, edge2);
                    float det = Vector3.Dot(edge1, p);
                    if (Math.Abs(det) < epsilon) continue;
                    float invDet = 1f / det;

                    Vector3 s = origin - a;
                    float u = Vector3.Dot(s, p) * invDet;
                    if (u < 0f || u > 1f) continue;

                    Vector3 q = Vector3.Cross(s, edge1);
                    float v = Vector3.Dot(direction, q) * invDet;
                    if (v < 0f || u + v > 1f) continue;

                    float distance = Vector3.Dot(edge2, q) * invDet;
                    if (distance <= 0f || distance >= bestDistance) continue;

                    bestDistance = distance;
                    bestTri = t;
                    bestU = u;
                    bestV = v;
                }

                return bestTri >= 0;
            }
        }
    }
}
