using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Transmit beam pattern in decibels against observation angle.
    /// The scanned sector is shaded so that grating lobes falling inside it, where they
    /// alias as real targets, are immediately visible.
    /// </summary>
    public sealed class BeamPatternPanel : PanelBase
    {
        public float floorDb = -40f;
        public float spanDeg = 90f;

        float[] _current;
        float[] _nominal;

        public float MeasuredBeamwidthDeg { get; private set; }
        public float PeakSideLobeDb { get; private set; }
        public List<float> GratingLobes { get; private set; }
        public List<float> GratingLobesInSector { get; private set; }

        public BeamPatternPanel()
        {
            Title = "Transmit beam pattern";
            Subtitle = "far-field array factor, one way";
            GratingLobes = new List<float>();
            GratingLobesInSector = new List<float>();
        }

        public void Render(ArrayConfig config, BeamPlan plan, TxMode mode,
                           bool useEmittedDelays, float sectorHalfWidthDeg,
                           int width, int height)
        {
            EnsureSize(width, height);
            Labels.Clear();
            Painter.Clear(Background);

            int samples = Mathf.Clamp(width * 2, 256, 4096);
            if (_current == null || _current.Length != samples)
            {
                _current = new float[samples];
                _nominal = new float[samples];
            }

            float[] weights = config.ModeWeights(mode);
            ArrayFactor.SampleDb(config, plan, weights, -spanDeg, spanDeg, _current,
                                 useEmittedDelays, floorDb);

            // Reference trace: all eight elements at full gain with ideal delays, so any
            // loss from a sub-aperture or a weak channel is visible as a gap.
            var uniform = new float[config.ElementCount];
            for (int i = 0; i < uniform.Length; i++) uniform[i] = 1f;
            ArrayFactor.SampleDb(config, plan, uniform, -spanDeg, spanDeg, _nominal, false, floorDb);

            MeasuredBeamwidthDeg = ArrayFactor.MeasuredBeamwidthDeg(config, plan, weights, useEmittedDelays);
            PeakSideLobeDb = ArrayFactor.PeakSideLobeDb(config, plan, weights, useEmittedDelays);
            GratingLobes = ArrayFactor.GratingLobeAngles(config, plan.steerDeg);
            GratingLobesInSector = ArrayFactor.GratingLobesInSector(config, plan.steerDeg, sectorHalfWidthDeg);

            const int left = 44, right = 12, top = 26, bottom = 26;
            int plotX = left, plotY = bottom;
            int plotWidth = Mathf.Max(8, width - left - right);
            int plotHeight = Mathf.Max(8, height - top - bottom);

            ShadeSector(sectorHalfWidthDeg, plotX, plotY, plotWidth, plotHeight);
            DrawGrid(plotX, plotY, plotWidth, plotHeight);
            DrawTrace(_nominal, plotX, plotY, plotWidth, plotHeight, TraceFaint);
            DrawTrace(_current, plotX, plotY, plotWidth, plotHeight, Trace);
            DrawLobeMarkers(plan.steerDeg, sectorHalfWidthDeg, plotX, plotY, plotWidth, plotHeight);

            Painter.Rect(plotX, plotY, plotWidth, plotHeight, Axis);
            DrawBorder();
            Painter.Apply();
        }

        void ShadeSector(float halfWidthDeg, int x, int y, int width, int height)
        {
            int from = Mathf.RoundToInt(x + AngleToNormalised(-halfWidthDeg) * width);
            int to = Mathf.RoundToInt(x + AngleToNormalised(halfWidthDeg) * width);
            Painter.FillRect(from, y, Mathf.Max(1, to - from), height, Sector);
        }

        void DrawGrid(int x, int y, int width, int height)
        {
            for (float db = 0f; db >= floorDb; db -= 10f)
            {
                int row = y + Mathf.RoundToInt(LevelToNormalised(db) * height);
                Painter.HorizontalLine(x, x + width - 1, row, Mathf.Approximately(db, 0f) ? GridStrong : Grid);
                AddPixelLabel(x - 6, row, db.ToString("0", CultureInfo.InvariantCulture) + " dB",
                              TextDim, TextAnchor.MiddleRight, 10);
            }

            for (float angle = -90f; angle <= 90f; angle += 15f)
            {
                if (Mathf.Abs(angle) > spanDeg) continue;
                int column = x + Mathf.RoundToInt(AngleToNormalised(angle) * width);
                Painter.VerticalLine(column, y, y + height - 1, Mathf.Approximately(angle, 0f) ? GridStrong : Grid);
                AddPixelLabel(column, y - 12, angle.ToString("0", CultureInfo.InvariantCulture) + "°",
                              TextDim, TextAnchor.MiddleCenter, 10);
            }
        }

        void DrawTrace(float[] samples, int x, int y, int width, int height, Color32 color)
        {
            if (samples == null) return;
            float previousX = 0f, previousY = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                float fraction = i / (float)(samples.Length - 1);
                float pixelX = x + fraction * width;
                float pixelY = y + LevelToNormalised(samples[i]) * height;
                if (i > 0) Painter.Line(previousX, previousY, pixelX, pixelY, color);
                previousX = pixelX;
                previousY = pixelY;
            }
        }

        void DrawLobeMarkers(float steerDeg, float sectorHalfWidthDeg,
                             int x, int y, int width, int height)
        {
            int steerColumn = x + Mathf.RoundToInt(AngleToNormalised(steerDeg) * width);
            Painter.VerticalLine(steerColumn, y, y + height - 1, Accent);
            AddPixelLabel(steerColumn, y + height + 8,
                          "main " + steerDeg.ToString("0.#", CultureInfo.InvariantCulture) + "°",
                          TextAccent, TextAnchor.MiddleCenter, 10);

            foreach (float angle in GratingLobes)
            {
                if (Mathf.Abs(angle) > spanDeg) continue;
                int column = x + Mathf.RoundToInt(AngleToNormalised(angle) * width);
                Painter.DashedLine(column, y, column, y + height - 1, Warn, 4, 6);

                // Grating labels sit inside the plot so they never collide with the main
                // lobe label on the axis below.
                bool inSector = Mathf.Abs(angle) <= sectorHalfWidthDeg;
                AddPixelLabel(column, y + height - 10,
                              (inSector ? "! " : "") +
                              angle.ToString("0.#", CultureInfo.InvariantCulture) + "°",
                              inSector ? TextWarn : TextDim, TextAnchor.MiddleCenter, 10);
            }
        }

        float AngleToNormalised(float angleDeg)
        {
            return Mathf.Clamp01((angleDeg + spanDeg) / (2f * spanDeg));
        }

        float LevelToNormalised(float db)
        {
            return Mathf.Clamp01((db - floorDb) / -floorDb);
        }
    }
}
