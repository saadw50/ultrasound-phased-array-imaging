using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Per-channel transmit schedule: element position, commanded delay, the delay actually
    /// emitted on the ESP32 tick grid, and the relative amplitude of each channel.
    /// When a device is connected, its live per-channel echo readings are shown alongside.
    /// </summary>
    public sealed class ChannelPanel : PanelBase
    {
        public ChannelPanel()
        {
            Title = "Transmit channels";
            Subtitle = "delay schedule, µs";
        }

        public void Render(ArrayConfig config, BeamPlan plan, TxMode mode,
                           DeviceState device, int width, int height)
        {
            EnsureSize(width, height);
            Labels.Clear();
            Painter.Clear(Background);

            int elements = config.ElementCount;
            if (elements == 0) { DrawBorder(); Painter.Apply(); return; }

            float[] weights = config.ModeWeights(mode);
            float maxDelay = Mathf.Max(1e-3f, plan.maxDelayUs);

            const int left = 64, right = 96, top = 22, bottom = 20;
            int trackX = left;
            int trackWidth = Mathf.Max(16, width - left - right);
            int usableHeight = Mathf.Max(8, height - top - bottom);
            int rowHeight = Mathf.Max(6, usableHeight / elements);
            int barHeight = Mathf.Max(3, rowHeight - 6);

            // Delay axis.
            for (int i = 0; i <= 4; i++)
            {
                int column = trackX + Mathf.RoundToInt(trackWidth * i / 4f);
                Painter.VerticalLine(column, bottom, height - top, Grid);
                AddPixelLabel(column, bottom - 9,
                              (maxDelay * i / 4f).ToString("0.#", CultureInfo.InvariantCulture),
                              TextDim, TextAnchor.MiddleCenter, 10);
            }

            for (int i = 0; i < elements; i++)
            {
                // Element 1 at the top of the panel, matching the firmware numbering.
                int rowTop = height - top - i * rowHeight;
                int rowY = rowTop - barHeight;

                bool active = weights != null && i < weights.Length && weights[i] > 0f;
                Color32 barColor = active ? Accent : Axis;

                float delay = plan.delayUs != null && i < plan.delayUs.Length ? plan.delayUs[i] : 0f;
                float emitted = plan.emittedDelayUs != null && i < plan.emittedDelayUs.Length
                    ? plan.emittedDelayUs[i] : delay;

                int barWidth = Mathf.RoundToInt(trackWidth * Mathf.Clamp01(delay / maxDelay));
                Painter.FillRect(trackX, rowY, Mathf.Max(1, barWidth), barHeight, barColor);

                // Emitted delay tick: where the ESP32 tick grid actually places the edge.
                int emittedColumn = trackX + Mathf.RoundToInt(trackWidth * Mathf.Clamp01(emitted / maxDelay));
                Painter.VerticalLine(emittedColumn, rowY - 2, rowY + barHeight + 2, Trace);

                string label = string.Format(CultureInfo.InvariantCulture, "TX{0} {1,6:0.0}mm",
                                             i + 1, config.elementXmm[i]);
                AddPixelLabel(left - 6, rowY + barHeight * 0.5f, label,
                              active ? TextColor : TextDim, TextAnchor.MiddleRight, 10);

                string readout = string.Format(CultureInfo.InvariantCulture, "{0:0.00} µs", delay);
                if (weights != null && i < weights.Length && !Mathf.Approximately(weights[i], 1f))
                    readout += string.Format(CultureInfo.InvariantCulture, "  g={0:0.00}", weights[i]);

                ChannelReading? live = FindChannel(device, i + 1);
                if (live.HasValue)
                {
                    readout += live.Value.detected
                        ? string.Format(CultureInfo.InvariantCulture, "  peak {0:0}", live.Value.peakAdc)
                        : "  no echo";
                }

                AddPixelLabel(width - 6, rowY + barHeight * 0.5f, readout,
                              active ? TextColor : TextDim, TextAnchor.MiddleRight, 10);
            }

            DrawBorder();
            Painter.Apply();
        }

        static ChannelReading? FindChannel(DeviceState device, int id)
        {
            if (device == null || device.firmware != FirmwareKind.SequentialScanner) return null;
            foreach (ChannelReading reading in device.channels)
                if (reading.id == id) return reading;
            return null;
        }
    }
}
