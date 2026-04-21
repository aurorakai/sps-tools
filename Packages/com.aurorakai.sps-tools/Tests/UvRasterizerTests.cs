using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AuroraKai.SPSTools.Tests
{
    public class UvRasterizerTests
    {
        [Test]
        public void Rasterize_SingleFullTriangle_CoversExpectedPixelCount()
        {
            // Triangle covering the full UV square as two halves would overlap;
            // here we rasterize a single right triangle covering the lower-left
            // half of a 16x16 texture. Expected pixel count is roughly half the
            // texture: 16*16 / 2 ≈ 128 pixels, with edge cases possibly adding
            // a handful.
            var uvs = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
            };
            var tris = new[] { 0, 1, 2 };

            int count = 0;
            UvRasterizer.Rasterize(uvs, tris, 16, (px, py, w0, w1, w2, ti) => count++);

            // Half of 16*16 = 128. Allow ±8 for rasterization edge behaviour.
            Assert.Greater(count, 120);
            Assert.Less(count, 140);
        }

        [Test]
        public void Rasterize_DegenerateTriangle_ProducesNoPixels()
        {
            // Three collinear UVs → zero-area triangle, should be skipped.
            var uvs = new[]
            {
                new Vector2(0.1f, 0.1f),
                new Vector2(0.5f, 0.1f),
                new Vector2(0.9f, 0.1f),
            };
            var tris = new[] { 0, 1, 2 };

            int count = 0;
            UvRasterizer.Rasterize(uvs, tris, 64, (px, py, w0, w1, w2, ti) => count++);

            Assert.AreEqual(0, count);
        }

        [Test]
        public void Rasterize_BarycentricWeightsSumToOne()
        {
            var uvs = new[]
            {
                new Vector2(0.2f, 0.2f),
                new Vector2(0.8f, 0.3f),
                new Vector2(0.4f, 0.8f),
            };
            var tris = new[] { 0, 1, 2 };

            int checkedPixels = 0;
            UvRasterizer.Rasterize(uvs, tris, 128, (px, py, w0, w1, w2, ti) =>
            {
                float sum = w0 + w1 + w2;
                Assert.AreEqual(1f, sum, 0.001f,
                    $"barycentric weights at ({px},{py}) don't sum to 1 (got {sum})");
                checkedPixels++;
            });

            // Sanity: non-degenerate triangle should produce SOME pixels.
            Assert.Greater(checkedPixels, 100);
        }

        [Test]
        public void Rasterize_PixelsRemainInResolutionBounds()
        {
            // Triangle with UVs around the corners — check that no pixel index
            // escapes [0, resolution-1] even when UV triangle extends to edges.
            var uvs = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
            };
            var tris = new[] { 0, 1, 2 };

            int count = 0;
            UvRasterizer.Rasterize(uvs, tris, 32, (px, py, w0, w1, w2, ti) =>
            {
                Assert.GreaterOrEqual(px, 0);
                Assert.Less(px, 32);
                Assert.GreaterOrEqual(py, 0);
                Assert.Less(py, 32);
                count++;
            });

            Assert.Greater(count, 0);
        }

        [Test]
        public void Rasterize_TriIndexMatchesInputOrder()
        {
            // Two triangles; the callback should receive triIndex 0 for the
            // first and triIndex 1 for the second.
            var uvs = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0.4f, 0f),
                new Vector2(0f, 0.4f),

                new Vector2(0.6f, 0.6f),
                new Vector2(1f, 0.6f),
                new Vector2(0.6f, 1f),
            };
            var tris = new[] { 0, 1, 2, 3, 4, 5 };

            var seenIndices = new HashSet<int>();
            UvRasterizer.Rasterize(uvs, tris, 32, (px, py, w0, w1, w2, ti) =>
            {
                seenIndices.Add(ti);
            });

            Assert.IsTrue(seenIndices.Contains(0));
            Assert.IsTrue(seenIndices.Contains(1));
            Assert.AreEqual(2, seenIndices.Count);
        }

        [Test]
        public void Rasterize_NullInputs_NoException()
        {
            int count = 0;
            UvRasterizer.PerPixel cb = (px, py, w0, w1, w2, ti) => count++;

            Assert.DoesNotThrow(() => UvRasterizer.Rasterize(null, new[] { 0, 1, 2 }, 16, cb));
            Assert.DoesNotThrow(() => UvRasterizer.Rasterize(new Vector2[3], null, 16, cb));
            Assert.DoesNotThrow(() => UvRasterizer.Rasterize(new Vector2[3], new[] { 0, 1, 2 }, 16, null));
            Assert.AreEqual(0, count);
        }

        [Test]
        public void Rasterize_ZeroResolution_NoException()
        {
            var uvs = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
            };
            var tris = new[] { 0, 1, 2 };

            int count = 0;
            Assert.DoesNotThrow(() =>
                UvRasterizer.Rasterize(uvs, tris, 0, (px, py, w0, w1, w2, ti) => count++));
            Assert.AreEqual(0, count);
        }
    }
}
