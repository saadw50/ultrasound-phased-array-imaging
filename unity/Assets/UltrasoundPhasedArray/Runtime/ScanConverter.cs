using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>Brightness mapping applied to a <see cref="ScanFrame"/> before display.</summary>
    [System.Serializable]
    public struct ScanDisplaySettings
    {
        [Tooltip("Gain applied to the raw amplitude before compression, in decibels.")]
        public float gainDb;

        [Tooltip("Display window below the reference level, in decibels.")]
        public float dynamicRangeDb;

        [Tooltip("Logarithmic compression, as used by clinical B-mode displays.")]
        public bool logCompression;

        [Tooltip("Reference amplitude for 0 dB. Zero auto-scales to the frame peak.")]
        public float referenceAmplitude;

        public static ScanDisplaySettings Default
        {
            get
            {
                return new ScanDisplaySettings
                {
                    gainDb = 0f,
                    dynamicRangeDb = 30f,
                    logCompression = true,
                    referenceAmplitude = 0f,
                };
            }
        }

        /// <summary>Maps a raw amplitude to 0..1 display brightness.</summary>
        public float Normalise(float amplitude, float reference)
        {
            if (referenceAmplitude > 0f) reference = referenceAmplitude;
            if (reference <= 0f) return 0f;

            float value = amplitude * Mathf.Pow(10f, gainDb / 20f);
            if (value <= 0f) return 0f;

            if (!logCompression) return Mathf.Clamp01(value / reference);

            float range = Mathf.Max(1f, dynamicRangeDb);
            float db = 20f * Mathf.Log10(value / reference);
            return Mathf.Clamp01((db + range) / range);
        }
    }

    /// <summary>
    /// Pixel mapping of a sector image. The array sits at the apex, range grows upward,
    /// and the steering angle opens symmetrically about the vertical.
    /// </summary>
    public struct SectorLayout
    {
        public float apexX;
        public float apexY;
        public float pixelsPerCm;
        public float maxRangeCm;
        public float minRangeCm;
        public float halfAngleDeg;

        public Vector2 ToPixel(float angleDeg, float rangeCm)
        {
            float radians = angleDeg * Mathf.Deg2Rad;
            return new Vector2(
                apexX + rangeCm * Mathf.Sin(radians) * pixelsPerCm,
                apexY + rangeCm * Mathf.Cos(radians) * pixelsPerCm);
        }

        /// <summary>Inverse mapping. Returns false for pixels outside the acquired sector.</summary>
        public bool ToPolar(float pixelX, float pixelY, out float angleDeg, out float rangeCm)
        {
            float dx = (pixelX - apexX) / pixelsPerCm;
            float dz = (pixelY - apexY) / pixelsPerCm;

            rangeCm = Mathf.Sqrt(dx * dx + dz * dz);
            angleDeg = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;

            return dz >= 0f && rangeCm >= minRangeCm && rangeCm <= maxRangeCm &&
                   Mathf.Abs(angleDeg) <= halfAngleDeg;
        }
    }

    /// <summary>Converts angle/range frames into a cartesian sector image.</summary>
    public static class ScanConverter
    {
        /// <summary>Fits the sector into the painter, leaving room for the apex and labels.</summary>
        public static SectorLayout ComputeLayout(ScanFrame frame, int width, int height,
                                                 int marginPixels, float maxRangeOverrideCm)
        {
            float halfAngle = frame != null && frame.AngleCount > 0
                ? Mathf.Max(Mathf.Abs(frame.MinAngleDeg), Mathf.Abs(frame.MaxAngleDeg))
                : 15f;
            halfAngle = Mathf.Clamp(halfAngle, 1f, 89f);

            float maxRange = maxRangeOverrideCm > 0f
                ? maxRangeOverrideCm
                : (frame != null && frame.BinCount > 0 ? frame.MaxRangeCm : 100f);
            maxRange = Mathf.Max(1f, maxRange);

            float usableHeight = Mathf.Max(8, height - 2 * marginPixels);
            float usableHalfWidth = Mathf.Max(8, width * 0.5f - marginPixels);

            float halfWidthCm = Mathf.Max(0.05f, maxRange * Mathf.Sin(halfAngle * Mathf.Deg2Rad));
            float scale = Mathf.Min(usableHeight / maxRange, usableHalfWidth / halfWidthCm);

            return new SectorLayout
            {
                apexX = width * 0.5f,
                apexY = marginPixels,
                pixelsPerCm = scale,
                maxRangeCm = maxRange,
                minRangeCm = frame != null && frame.BinCount > 0 ? frame.MinRangeCm : 0f,
                halfAngleDeg = halfAngle,
            };
        }

        /// <summary>
        /// Rasterises <paramref name="frame"/> into <paramref name="painter"/>.
        /// Pixels outside the acquired sector keep <paramref name="background"/>: the array
        /// never measured them, and filling them in would invent data.
        /// </summary>
        public static SectorLayout Render(ScanFrame frame, TexturePainter painter,
                                          ScanDisplaySettings settings, Colormap colormap,
                                          Color32 background, int marginPixels = 16,
                                          float maxRangeOverrideCm = 0f)
        {
            painter.Clear(background);
            var layout = ComputeLayout(frame, painter.Width, painter.Height,
                                       marginPixels, maxRangeOverrideCm);
            if (frame == null || !frame.IsValid) return layout;

            float reference = frame.MaxAmplitude();
            if (reference <= 0f) return layout;

            int width = painter.Width, height = painter.Height;
            var pixels = painter.Pixels;

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    float angle, range;
                    if (!layout.ToPolar(x + 0.5f, y + 0.5f, out angle, out range)) continue;

                    float amplitude = frame.Sample(angle, range);
                    float brightness = settings.Normalise(amplitude, reference);
                    if (brightness <= 0f) continue;

                    pixels[row + x] = colormap.Evaluate(brightness);
                }
            }

            return layout;
        }

        /// <summary>Draws the sector outline, range arcs and angle spokes over a rendered frame.</summary>
        public static void DrawGraticule(TexturePainter painter, SectorLayout layout,
                                         float ringSpacingCm, Color32 grid, Color32 outline)
        {
            if (ringSpacingCm > 0f)
            {
                for (float range = ringSpacingCm; range <= layout.maxRangeCm + 0.01f; range += ringSpacingCm)
                {
                    // Arc angles are measured from +x, and the sector opens about +y.
                    painter.Arc(layout.apexX, layout.apexY, range * layout.pixelsPerCm,
                                90f - layout.halfAngleDeg, 90f + layout.halfAngleDeg, grid);
                }
            }

            for (float angle = -layout.halfAngleDeg; angle <= layout.halfAngleDeg + 0.01f; angle += 5f)
            {
                Vector2 outer = layout.ToPixel(angle, layout.maxRangeCm);
                bool edge = Mathf.Abs(Mathf.Abs(angle) - layout.halfAngleDeg) < 0.01f;
                painter.Line(layout.apexX, layout.apexY, outer.x, outer.y, edge ? outline : grid);
            }

            painter.Arc(layout.apexX, layout.apexY, layout.maxRangeCm * layout.pixelsPerCm,
                        90f - layout.halfAngleDeg, 90f + layout.halfAngleDeg, outline);
        }
    }
}
