using UnityEngine;

namespace AuroraKai.SPSTools
{
    /// <summary>
    /// CPU UV-space triangle rasterizer. Invokes a callback per covered pixel
    /// with barycentric coordinates. Pixels slightly outside the triangle due
    /// to edge-fp-jitter are included (eps = 1e-5) so adjacent triangles both
    /// claim edge pixels — callers should use a last-writer-wins or
    /// deviation-based tiebreak.
    /// </summary>
    public static class UvRasterizer
    {
        /// <summary>Per-pixel callback with barycentric weights (clamped [0,1], sum ≈ 1).</summary>
        public delegate void PerPixel(int px, int py, float w0, float w1, float w2, int triIndex);

        /// <summary>Rasterizes each UV triangle at the given resolution.</summary>
        public static void Rasterize(
            Vector2[] uvs, int[] triangles, int resolution, PerPixel callback)
        {
            if (uvs == null || triangles == null || callback == null) return;
            if (resolution <= 0) return;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i];
                int i1 = triangles[i + 1];
                int i2 = triangles[i + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;

                RasterizeTriangle(uvs[i0], uvs[i1], uvs[i2], resolution, i / 3, callback);
            }
        }

        private static void RasterizeTriangle(
            Vector2 uv0, Vector2 uv1, Vector2 uv2,
            int resolution, int triIndex, PerPixel callback)
        {
            // UV [0,1] → pixel space; pixel centres at (x+0.5, y+0.5).
            Vector2 p0 = new Vector2(uv0.x * resolution, uv0.y * resolution);
            Vector2 p1 = new Vector2(uv1.x * resolution, uv1.y * resolution);
            Vector2 p2 = new Vector2(uv2.x * resolution, uv2.y * resolution);

            // 2× signed area. Sign tells winding; skip degenerate tris.
            float doubleArea = (p1.x - p0.x) * (p2.y - p0.y) - (p1.y - p0.y) * (p2.x - p0.x);
            if (Mathf.Abs(doubleArea) < 1e-6f) return;

            float invDoubleArea = 1f / doubleArea;

            int xMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, p1.x, p2.x)));
            int yMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, p1.y, p2.y)));
            int xMax = Mathf.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(p0.x, p1.x, p2.x)));
            int yMax = Mathf.Min(resolution - 1, Mathf.CeilToInt(Mathf.Max(p0.y, p1.y, p2.y)));

            if (xMax < xMin || yMax < yMin) return;

            // Edge function e(A,B)(P) = (B.x-A.x)(P.y-A.y) - (B.y-A.y)(P.x-A.x).
            // Dividing by doubleArea gives the barycentric weight for the vertex
            // opposite edge (A,B). Works for both CCW and CW windings since sign
            // cancels in the division.
            for (int y = yMin; y <= yMax; y++)
            {
                float py = y + 0.5f;
                for (int x = xMin; x <= xMax; x++)
                {
                    float px = x + 0.5f;

                    float e12 = (p2.x - p1.x) * (py - p1.y) - (p2.y - p1.y) * (px - p1.x);
                    float e20 = (p0.x - p2.x) * (py - p2.y) - (p0.y - p2.y) * (px - p2.x);
                    float e01 = (p1.x - p0.x) * (py - p0.y) - (p1.y - p0.y) * (px - p0.x);

                    float w0 = e12 * invDoubleArea;
                    float w1 = e20 * invDoubleArea;
                    float w2 = e01 * invDoubleArea;

                    const float eps = -1e-5f;
                    if (w0 < eps || w1 < eps || w2 < eps) continue;

                    w0 = Mathf.Clamp01(w0);
                    w1 = Mathf.Clamp01(w1);
                    w2 = Mathf.Clamp01(w2);

                    callback(x, y, w0, w1, w2, triIndex);
                }
            }
        }
    }
}
