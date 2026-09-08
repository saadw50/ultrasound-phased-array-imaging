using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// B-mode style sector image built from a recorded or simulated
    /// <see cref="ScanFrame"/>, with the acquisition geometry drawn over it.
    /// </summary>
    public sealed class SectorPanel : PanelBase
    {
        readonly Colormap _colormap = new Colormap(ColormapKind.Grayscale);

        public ScanDisplaySettings display = ScanDisplaySettings.Default;
        public ColormapKind colormap = ColormapKind.Grayscale;
        public float ringSpacingCm = 20f;
        public bool markPeak = true;
        public float maxRangeOverrideCm;

        public SectorLayout Layout { get; private set; }
        public float PeakAngleDeg { get; private set; }
        public float PeakRangeCm { get; private set; }
        public float PeakValue { get; private set; }

        public SectorPanel()
        {
            Title = "Sector image";
            Subtitle = "envelope amplitude by steering angle and range";
        }

        public void Render(ScanFrame frame, ArrayConfig config, float steerDeg,
                           bool showGratingLobes, int width, int height)
        {
            EnsureSize(width, height);
            Labels.Clear();
            _colormap.Rebuild(colormap);

            Layout = ScanConverter.Render(frame, Painter, display, _colormap, Background,
                                          18, maxRangeOverrideCm);
            SectorLayout layout = Layout;

            ScanConverter.DrawGraticule(Painter, layout, ringSpacingCm, Grid, Axis);
            DrawRangeLabels(layout);
            DrawAngleLabels(layout);

            if (frame != null && frame.IsValid)
            {
                float angle, range, value;
                if (frame.TryFindPeak(out angle, out range, out value))
                {
                    PeakAngleDeg = angle;
                    PeakRangeCm = range;
                    PeakValue = value;

                    if (markPeak)
                    {
                        Vector2 pixel = layout.ToPixel(angle, range);
                        Painter.Circle(pixel.x, pixel.y, 7f, Accent);
                        AddPixelLabel(pixel.x + 10, pixel.y,
                                      string.Format(CultureInfo.InvariantCulture,
                                                    "peak {0:0.#}° {1:0.0} cm", angle, range),
                                      TextAccent, TextAnchor.MiddleLeft, 10);
                    }
                }
            }

            // The steering direction currently commanded in the simulation panels.
            Vector2 steerEnd = layout.ToPixel(steerDeg, layout.maxRangeCm);
            Painter.DashedLine(layout.apexX, layout.apexY, steerEnd.x, steerEnd.y, Accent, 10, 8);

            if (showGratingLobes && config != null)
            {
                foreach (float lobe in ArrayFactor.GratingLobesInSector(config, steerDeg,
                                                                       layout.halfAngleDeg))
                {
                    Vector2 end = layout.ToPixel(lobe, layout.maxRangeCm);
                    Painter.DashedLine(layout.apexX, layout.apexY, end.x, end.y, Warn, 3, 9);
                    AddPixelLabel(end.x, end.y + 10,
                                  "grating " + lobe.ToString("0.#", CultureInfo.InvariantCulture) + "°",
                                  TextWarn, TextAnchor.MiddleCenter, 10);
                }
            }

            DrawBorder();
            Painter.Apply();
        }

        void DrawRangeLabels(SectorLayout layout)
        {
            if (ringSpacingCm <= 0f) return;
            for (float range = ringSpacingCm; range <= layout.maxRangeCm + 0.01f; range += ringSpacingCm)
            {
                Vector2 pixel = layout.ToPixel(-layout.halfAngleDeg, range);
                AddPixelLabel(pixel.x - 4, pixel.y,
                              range.ToString("0", CultureInfo.InvariantCulture),
                              TextDim, TextAnchor.MiddleRight, 10);
            }
        }

        void DrawAngleLabels(SectorLayout layout)
        {
            for (float angle = -layout.halfAngleDeg; angle <= layout.halfAngleDeg + 0.01f; angle += 5f)
            {
                Vector2 pixel = layout.ToPixel(angle, layout.maxRangeCm);
                AddPixelLabel(pixel.x, pixel.y + 9,
                              angle.ToString("0", CultureInfo.InvariantCulture) + "°",
                              TextDim, TextAnchor.MiddleCenter, 10);
            }
        }
    }
}
