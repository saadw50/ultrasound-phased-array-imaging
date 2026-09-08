using System;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Small CPU rasteriser over a <see cref="Color32"/> buffer.
    /// Panels draw into one of these and upload it once per rebuild, which keeps the whole
    /// tool free of materials, shaders and render-pipeline assumptions.
    /// Row 0 is the bottom of the texture, matching Unity's texture origin.
    /// </summary>
    public sealed class TexturePainter
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public Color32[] Pixels { get; private set; }

        Texture2D _texture;

        public TexturePainter(int width, int height)
        {
            Resize(width, height);
        }

        public Texture2D Texture
        {
            get
            {
                if (_texture == null)
                {
                    _texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
                    {
                        name = "UltrasoundPanel",
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                }
                return _texture;
            }
        }

        public bool Resize(int width, int height)
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            if (Width == width && Height == height && Pixels != null) return false;

            Width = width;
            Height = height;
            Pixels = new Color32[width * height];

            DestroyTexture();
            return true;
        }

        /// <summary>Pushes the pixel buffer to the GPU. Call once per rebuild, not per primitive.</summary>
        public void Apply()
        {
            Texture.SetPixels32(Pixels);
            Texture.Apply(false);
        }

        public void Release()
        {
            DestroyTexture();
        }

        void DestroyTexture()
        {
            if (_texture == null) return;

            if (Application.isPlaying) UnityEngine.Object.Destroy(_texture);
            else UnityEngine.Object.DestroyImmediate(_texture);

            _texture = null;
        }

        public void Clear(Color32 color)
        {
            var pixels = Pixels;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
        }

        public void SetPixel(int x, int y, Color32 color)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            Pixels[y * Width + x] = color;
        }

        public void BlendPixel(int x, int y, Color32 color, float alpha)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            if (alpha <= 0f) return;
            if (alpha >= 1f) { Pixels[y * Width + x] = color; return; }

            int index = y * Width + x;
            Color32 destination = Pixels[index];
            Pixels[index] = new Color32(
                (byte)(destination.r + (color.r - destination.r) * alpha),
                (byte)(destination.g + (color.g - destination.g) * alpha),
                (byte)(destination.b + (color.b - destination.b) * alpha),
                255);
        }

        public void HorizontalLine(int x0, int x1, int y, Color32 color)
        {
            if (y < 0 || y >= Height) return;
            if (x1 < x0) { int swap = x0; x0 = x1; x1 = swap; }
            x0 = Mathf.Max(0, x0);
            x1 = Mathf.Min(Width - 1, x1);
            int row = y * Width;
            for (int x = x0; x <= x1; x++) Pixels[row + x] = color;
        }

        public void VerticalLine(int x, int y0, int y1, Color32 color)
        {
            if (x < 0 || x >= Width) return;
            if (y1 < y0) { int swap = y0; y0 = y1; y1 = swap; }
            y0 = Mathf.Max(0, y0);
            y1 = Mathf.Min(Height - 1, y1);
            for (int y = y0; y <= y1; y++) Pixels[y * Width + x] = color;
        }

        public void Line(float x0, float y0, float x1, float y1, Color32 color)
        {
            float dx = x1 - x0, dy = y1 - y0;
            int steps = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)));
            if (steps <= 0) { SetPixel(Mathf.RoundToInt(x0), Mathf.RoundToInt(y0), color); return; }

            float stepX = dx / steps, stepY = dy / steps;
            float x = x0, y = y0;
            for (int i = 0; i <= steps; i++)
            {
                SetPixel(Mathf.RoundToInt(x), Mathf.RoundToInt(y), color);
                x += stepX;
                y += stepY;
            }
        }

        /// <summary>Dashed line, used for construction lines that must not read as data.</summary>
        public void DashedLine(float x0, float y0, float x1, float y1, Color32 color,
                               int onPixels = 5, int offPixels = 5)
        {
            float dx = x1 - x0, dy = y1 - y0;
            int steps = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)));
            if (steps <= 0) return;

            float stepX = dx / steps, stepY = dy / steps;
            float x = x0, y = y0;
            int period = Mathf.Max(1, onPixels + offPixels);
            for (int i = 0; i <= steps; i++)
            {
                if (i % period < onPixels)
                    SetPixel(Mathf.RoundToInt(x), Mathf.RoundToInt(y), color);
                x += stepX;
                y += stepY;
            }
        }

        public void Rect(int x, int y, int width, int height, Color32 color)
        {
            HorizontalLine(x, x + width - 1, y, color);
            HorizontalLine(x, x + width - 1, y + height - 1, color);
            VerticalLine(x, y, y + height - 1, color);
            VerticalLine(x + width - 1, y, y + height - 1, color);
        }

        public void FillRect(int x, int y, int width, int height, Color32 color)
        {
            for (int row = y; row < y + height; row++) HorizontalLine(x, x + width - 1, row, color);
        }

        public void Circle(float centreX, float centreY, float radius, Color32 color)
        {
            if (radius <= 0f) return;
            int steps = Mathf.Max(12, Mathf.CeilToInt(radius * 8f));
            float previousX = centreX + radius, previousY = centreY;
            for (int i = 1; i <= steps; i++)
            {
                float angle = 2f * Mathf.PI * i / steps;
                float x = centreX + radius * Mathf.Cos(angle);
                float y = centreY + radius * Mathf.Sin(angle);
                Line(previousX, previousY, x, y, color);
                previousX = x; previousY = y;
            }
        }

        public void Disc(float centreX, float centreY, float radius, Color32 color)
        {
            int minY = Mathf.Max(0, Mathf.FloorToInt(centreY - radius));
            int maxY = Mathf.Min(Height - 1, Mathf.CeilToInt(centreY + radius));
            float squared = radius * radius;

            for (int y = minY; y <= maxY; y++)
            {
                float dy = y - centreY;
                float half = squared - dy * dy;
                if (half < 0f) continue;
                half = Mathf.Sqrt(half);
                HorizontalLine(Mathf.RoundToInt(centreX - half), Mathf.RoundToInt(centreX + half), y, color);
            }
        }

        /// <summary>Arc of a circle between two angles measured in degrees from +x.</summary>
        public void Arc(float centreX, float centreY, float radius,
                        float fromDeg, float toDeg, Color32 color)
        {
            if (radius <= 0f) return;
            int steps = Mathf.Max(8, Mathf.CeilToInt(Mathf.Abs(toDeg - fromDeg) * radius * 0.02f) + 8);
            float previousX = 0f, previousY = 0f;
            for (int i = 0; i <= steps; i++)
            {
                float angle = Mathf.Lerp(fromDeg, toDeg, i / (float)steps) * Mathf.Deg2Rad;
                float x = centreX + radius * Mathf.Cos(angle);
                float y = centreY + radius * Mathf.Sin(angle);
                if (i > 0) Line(previousX, previousY, x, y, color);
                previousX = x; previousY = y;
            }
        }

        public void PolyLine(float[] xs, float[] ys, Color32 color)
        {
            if (xs == null || ys == null) return;
            int count = Mathf.Min(xs.Length, ys.Length);
            for (int i = 1; i < count; i++) Line(xs[i - 1], ys[i - 1], xs[i], ys[i], color);
        }

        /// <summary>Small filled cross marker, legible at any panel size.</summary>
        public void Marker(float x, float y, int size, Color32 color)
        {
            int cx = Mathf.RoundToInt(x), cy = Mathf.RoundToInt(y);
            HorizontalLine(cx - size, cx + size, cy, color);
            VerticalLine(cx, cy - size, cy + size, color);
        }
    }
}
