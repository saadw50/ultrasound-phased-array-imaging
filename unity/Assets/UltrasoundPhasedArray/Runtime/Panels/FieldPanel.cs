using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    public enum FieldMode
    {
        /// <summary>Instantaneous pressure of the tone burst, animated over time.</summary>
        Wavefront = 0,
        /// <summary>Steady-state pressure magnitude: the beam map.</summary>
        BeamMap = 1,
    }

    /// <summary>
    /// Two-dimensional acoustic field in front of the aperture.
    /// In wavefront mode the tilted main wavefront and the separate grating-lobe wavefronts
    /// can be seen leaving the array; in beam-map mode the same lobes appear as standing beams.
    /// </summary>
    public sealed class FieldPanel : PanelBase
    {
        readonly Colormap _signed = new Colormap(ColormapKind.Diverging);
        readonly Colormap _magnitude = new Colormap(ColormapKind.Inferno);

        AcousticField _field;

        /// <summary>Field half-width in millimetres. Range depth is derived from the aspect.</summary>
        public float halfWidthMm = 400f;
        public float depthMm = 700f;

        /// <summary>Resolution divisor: 1 computes every pixel, 2 computes every other.</summary>
        public int downsample = 2;

        public float dynamicRangeDb = 34f;

        public FieldPanel()
        {
            Title = "Acoustic field";
            Subtitle = "Huygens sum of the eight elements";
        }

        public AcousticField Field { get { return _field; } }

        public void Render(ArrayConfig config, BeamPlan plan, TxMode mode, FieldMode fieldMode,
                           float timeUs, bool useEmittedDelays, IList<AcousticTarget> targets,
                           int width, int height)
        {
            EnsureSize(width, height);
            Labels.Clear();

            int step = Mathf.Clamp(downsample, 1, 8);
            int fieldWidth = Mathf.Max(16, width / step);
            int fieldHeight = Mathf.Max(16, height / step);

            if (_field == null || _field.Width != fieldWidth || _field.Height != fieldHeight)
                _field = new AcousticField(fieldWidth, fieldHeight);

            // Keep the field square in world units so the beam angles are not sheared.
            float aspect = height / (float)Mathf.Max(1, width);
            float halfWidth = Mathf.Max(20f, halfWidthMm);
            _field.SetExtent(-halfWidth, halfWidth, 0f, 2f * halfWidth * aspect);
            depthMm = _field.MaxZmm;

            float[] weights = config.ModeWeights(mode);
            if (fieldMode == FieldMode.Wavefront)
                _field.EvaluateInstant(config, plan, weights, timeUs, useEmittedDelays);
            else
                _field.EvaluateMagnitude(config, plan, weights, useEmittedDelays);

            Blit(fieldMode, step);
            DrawGeometry(config, plan, mode, targets);
            DrawBorder();
            Painter.Apply();
        }

        void Blit(FieldMode fieldMode, int step)
        {
            float peak = Mathf.Max(1e-9f, _field.PeakValue);
            var pixels = Painter.Pixels;
            int width = Painter.Width, height = Painter.Height;

            for (int y = 0; y < height; y++)
            {
                int sourceRow = Mathf.Min(_field.Height - 1, y / step);
                int destinationRow = y * width;

                for (int x = 0; x < width; x++)
                {
                    int sourceColumn = Mathf.Min(_field.Width - 1, x / step);
                    float value = _field.Values[sourceRow * _field.Width + sourceColumn] / peak;

                    if (fieldMode == FieldMode.Wavefront)
                    {
                        // Signed pressure through a diverging map: dark is still, bright is loud.
                        pixels[destinationRow + x] = _signed.EvaluateSigned(SignedCompress(value));
                    }
                    else
                    {
                        float magnitude = Mathf.Abs(value);
                        float db = 20f * Mathf.Log10(Mathf.Max(magnitude, 1e-6f));
                        float normalised = Mathf.Clamp01((db + dynamicRangeDb) / dynamicRangeDb);
                        pixels[destinationRow + x] = _magnitude.Evaluate(normalised);
                    }
                }
            }
        }

        /// <summary>Compresses a signed value so low-level lobes stay visible next to the main beam.</summary>
        static float SignedCompress(float value)
        {
            float magnitude = Mathf.Abs(value);
            if (magnitude < 1e-7f) return 0f;
            float compressed = Mathf.Pow(magnitude, 0.55f);
            return value < 0f ? -compressed : compressed;
        }

        void DrawGeometry(ArrayConfig config, BeamPlan plan, TxMode mode, IList<AcousticTarget> targets)
        {
            float[] weights = config.ModeWeights(mode);

            // Range rings every 10 cm, measured from the centre of the aperture.
            for (float centimetres = 10f; centimetres * 10f <= _field.MaxZmm; centimetres += 10f)
            {
                float radius = centimetres * 10f * PixelsPerMm;
                Painter.Arc(ApertureX, ApertureY, radius, 20f, 160f, Grid);
                if (Mathf.Approximately(centimetres % 50f, 0f))
                    AddPixelLabel(ApertureX + 4, ApertureY + radius,
                                  centimetres.ToString("0", CultureInfo.InvariantCulture) + " cm",
                                  TextDim, TextAnchor.MiddleLeft, 10);
            }

            // Steering ray and grating-lobe rays.
            DrawRay(plan.steerDeg, Accent);
            foreach (float angle in ArrayFactor.GratingLobeAngles(config, plan.steerDeg))
                DrawRay(angle, Warn, true);

            // Elements, coloured by whether they are firing.
            for (int i = 0; i < config.ElementCount; i++)
            {
                float x = ApertureX + config.elementXmm[i] * PixelsPerMm;
                bool active = weights != null && i < weights.Length && weights[i] > 0f;
                Painter.Disc(x, ApertureY + 1f, 2.5f, active ? Accent : Axis);
            }
            Painter.HorizontalLine(
                Mathf.RoundToInt(ApertureX - config.ApertureMm * 0.5f * PixelsPerMm),
                Mathf.RoundToInt(ApertureX + config.ApertureMm * 0.5f * PixelsPerMm),
                Mathf.RoundToInt(ApertureY), Axis);

            // The far-field boundary: inside it the beam pattern does not yet apply.
            float rayleigh = config.RayleighDistanceMm * PixelsPerMm;
            if (rayleigh > 6f && rayleigh < Painter.Height)
            {
                Painter.Arc(ApertureX, ApertureY, rayleigh, 15f, 165f, GridStrong);
                AddPixelLabel(ApertureX, ApertureY + rayleigh + 8,
                              "far field beyond " +
                              (config.RayleighDistanceMm * 0.1f).ToString("0.0", CultureInfo.InvariantCulture) + " cm",
                              TextDim, TextAnchor.MiddleCenter, 10);
            }

            if (targets != null)
            {
                foreach (AcousticTarget target in targets)
                {
                    if (target.reflectivity <= 0f) continue;
                    float radians = target.angleDeg * Mathf.Deg2Rad;
                    float x = ApertureX + target.rangeCm * 10f * Mathf.Sin(radians) * PixelsPerMm;
                    float y = ApertureY + target.rangeCm * 10f * Mathf.Cos(radians) * PixelsPerMm;
                    if (y > Painter.Height) continue;
                    Painter.Circle(x, y, 5f, TraceFaint);
                    Painter.Marker(x, y, 3, Trace);
                }
            }
        }

        void DrawRay(float angleDeg, Color32 color, bool dashed = false)
        {
            float radians = angleDeg * Mathf.Deg2Rad;
            float length = Painter.Height * 2f;
            float x = ApertureX + length * Mathf.Sin(radians);
            float y = ApertureY + length * Mathf.Cos(radians);

            if (dashed) Painter.DashedLine(ApertureX, ApertureY, x, y, color, 3, 9);
            else Painter.DashedLine(ApertureX, ApertureY, x, y, color, 12, 6);
        }

        float PixelsPerMm
        {
            get
            {
                float span = _field.MaxXmm - _field.MinXmm;
                return span > 0f ? Painter.Width / span : 1f;
            }
        }

        float ApertureX { get { return Painter.Width * 0.5f; } }
        float ApertureY { get { return 0f; } }
    }
}
