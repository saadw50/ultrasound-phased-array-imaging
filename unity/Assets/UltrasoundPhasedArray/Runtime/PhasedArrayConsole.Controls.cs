using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>IMGUI control column for <see cref="PhasedArrayConsole"/>.</summary>
    public sealed partial class PhasedArrayConsole
    {
        static readonly string[] ModeNames = { "Simulate", "Recording", "Live" };
        static readonly string[] TxModeNames = { "FULL8", "LEFT4", "RIGHT4" };
        static readonly string[] FieldModeNames = { "Wavefront", "Beam map" };
        static readonly string[] ColormapNames = { "Gray", "Inferno", "Viridis", "Amber" };

        void DrawControls()
        {
            GUI.Box(_controlRect, GUIContent.none);
            GUILayout.BeginArea(new Rect(_controlRect.x + 8, _controlRect.y + 6,
                                         _controlRect.width - 16, _controlRect.height - 12));
            _controlScroll = GUILayout.BeginScrollView(_controlScroll);

            GUILayout.Label("8-TX phased array console",
                            Style(TextAnchor.MiddleLeft, 14, PanelBase.TextColor));
            GUILayout.Label("transmit beamforming into one receiver",
                            Style(TextAnchor.MiddleLeft, 10, PanelBase.TextDim));

            GUILayout.Space(6);
            mode = (ConsoleMode)GUILayout.Toolbar((int)mode, ModeNames);

            DrawBeamControls();
            DrawFieldControls();

            switch (mode)
            {
                case ConsoleMode.Simulation: DrawSimulationControls(); break;
                case ConsoleMode.Recording: DrawRecordingControls(); break;
                case ConsoleMode.Live: DrawLiveControls(); break;
            }

            DrawMetrics();
            DrawExport();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void SectionHeader(string text)
        {
            GUILayout.Space(8);
            GUILayout.Label(text, Style(TextAnchor.MiddleLeft, 11, PanelBase.TextAccent));
        }

        void Readout(string label, string value, Color? color = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Style(TextAnchor.MiddleLeft, 10, PanelBase.TextDim),
                            GUILayout.Width(140f));
            GUILayout.Label(value, Style(TextAnchor.MiddleRight, 10,
                                         color ?? PanelBase.TextColor));
            GUILayout.EndHorizontal();
        }

        float Slider(string label, float value, float minimum, float maximum, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, Style(TextAnchor.MiddleLeft, 10, PanelBase.TextDim),
                            GUILayout.Width(112f));
            GUILayout.Label(value.ToString(format, CultureInfo.InvariantCulture),
                            Style(TextAnchor.MiddleRight, 10, PanelBase.TextColor),
                            GUILayout.Width(56f));
            GUILayout.EndHorizontal();
            return GUILayout.HorizontalSlider(value, minimum, maximum);
        }

        void DrawBeamControls()
        {
            SectionHeader("BEAM");

            float limit = Mathf.Abs(config.maxSteerDeg);
            GUI.enabled = !sweepSteering;
            steerDeg = Slider("Steer angle", steerDeg, -limit, limit, "0.0°");
            GUI.enabled = true;

            sweepSteering = GUILayout.Toggle(sweepSteering, " sweep the sector");
            txMode = (TxMode)GUILayout.Toolbar((int)txMode, TxModeNames);

            GUILayout.Space(4);
            GUILayout.Label("Active elements", Style(TextAnchor.MiddleLeft, 10, PanelBase.TextDim));
            DrawElementToggles();

            useEmittedDelays = GUILayout.Toggle(useEmittedDelays, " use tick-quantised delays");

            int cycles = Mathf.RoundToInt(Slider("Burst cycles", config.burstCycles, 1f, 32f, "0"));
            if (cycles != config.burstCycles) config.burstCycles = cycles;
        }

        void DrawElementToggles()
        {
            int elements = config.ElementCount;
            for (int row = 0; row < 2; row++)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < 4; column++)
                {
                    int index = row * 4 + column;
                    if (index >= elements) break;

                    bool on = config.channelGain[index] > 0f;
                    bool next = GUILayout.Toggle(on, (index + 1).ToString(), GUI.skin.button,
                                                 GUILayout.Height(20f));
                    if (next != on) config.channelGain[index] = next ? 1f : 0f;
                }
                GUILayout.EndHorizontal();
            }
        }

        void DrawFieldControls()
        {
            SectionHeader("FIELD");
            fieldMode = (FieldMode)GUILayout.Toolbar((int)fieldMode, FieldModeNames);

            if (fieldMode == FieldMode.Wavefront)
            {
                animateWavefront = GUILayout.Toggle(animateWavefront, " animate");
                microsecondsPerSecond = Slider("Playback", microsecondsPerSecond, 40f, 4000f, "0 µs/s");
                if (!animateWavefront)
                {
                    float sweep = _fieldPanel.Field != null
                        ? _fieldPanel.Field.SweepDurationUs(config, _plan) : 3000f;
                    _timeUs = Slider("Time", _timeUs, 0f, sweep, "0 µs");
                }
            }

            _fieldPanel.halfWidthMm = Slider("Half width", _fieldPanel.halfWidthMm, 100f, 1200f, "0 mm");
            fieldDownsample = Mathf.RoundToInt(Slider("Pixel step", fieldDownsample, 1f, 6f, "0"));
        }

        void DrawSimulationControls()
        {
            SectionHeader("TARGETS");
            simulator.applyRangeBias = GUILayout.Toggle(simulator.applyRangeBias,
                                                        " add the measured range bias");

            for (int i = 0; i < targets.Count; i++)
            {
                AcousticTarget target = targets[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                                              "{0}. {1,5:0.0}°  {2,5:0.0} cm", i + 1,
                                              target.angleDeg, target.rangeCm),
                                Style(TextAnchor.MiddleLeft, 10, PanelBase.TextColor));
                if (GUILayout.Button("x", GUILayout.Width(22f))) _pendingTargetRemoval = i;
                GUILayout.EndHorizontal();

                target.angleDeg = Slider("   bearing", target.angleDeg, -40f, 40f, "0.0°");
                target.rangeCm = Slider("   range", target.rangeCm, 15f, simulator.maxRangeCm, "0.0 cm");
                targets[i] = target;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add target")) _pendingAddTarget = true;
            if (GUILayout.Button("Reset scene")) _pendingResetScene = true;
            GUILayout.EndHorizontal();

            DrawDisplayControls();
        }

        void DrawRecordingControls()
        {
            SectionHeader("DATASET");

            if (_datasets.Count == 0)
            {
                GUILayout.Label("No .uscan files in StreamingAssets/ultrasound.\n" +
                                "Bake one with unity/Tools/bake_scan.py.",
                                Style(TextAnchor.MiddleLeft, 10, PanelBase.TextWarn));
            }

            for (int i = 0; i < _datasets.Count; i++)
            {
                bool selected = i == _datasetIndex && _library != null;
                if (GUILayout.Toggle(selected, " " + _datasets[i], GUI.skin.button) && !selected)
                    StartCoroutine(LoadDataset(i));
            }

            if (_library != null && _library.frames.Count > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label("Acquisition", Style(TextAnchor.MiddleLeft, 10, PanelBase.TextDim));

                for (int i = 0; i < _library.frames.Count; i++)
                {
                    ScanFrame frame = _library.frames[i];
                    string name = string.Format("{0} {1}", frame.scene, frame.txMode).Trim();
                    bool selected = i == _frameIndex;
                    if (GUILayout.Toggle(selected, " " + name, GUI.skin.button) && !selected)
                        _frameIndex = i;
                }

                subtractBackground = GUILayout.Toggle(subtractBackground, " subtract BG acquisition");
                correctRangeBias = GUILayout.Toggle(correctRangeBias,
                    string.Format(CultureInfo.InvariantCulture,
                                  " remove {0:0.00} cm range bias", config.rangeBiasCm));
            }

            DrawDisplayControls();
        }

        void DrawLiveControls()
        {
            SectionHeader("DEVICE");
            GUILayout.Label("Dashboard address", Style(TextAnchor.MiddleLeft, 10, PanelBase.TextDim));
            device.host = GUILayout.TextField(device.host ?? "");

            GUILayout.BeginHorizontal();
            bool poll = GUILayout.Toggle(_polling, _polling ? " polling" : " poll", GUI.skin.button);
            if (poll != _polling)
            {
                if (poll) StartCoroutine(PollDevice());
                else _polling = false;
            }
            if (GUILayout.Button("Scan")) StartCoroutine(device.TriggerScan());
            if (GUILayout.Button("Sweep")) StartCoroutine(device.TriggerSweep());
            GUILayout.EndHorizontal();

            DeviceState state = device.State;
            Readout("link", device.Status, device.Connected ? PanelBase.TextAccent : PanelBase.TextWarn);
            Readout("firmware", state.firmware.ToString());
            Readout("scan id", state.scanId.ToString(CultureInfo.InvariantCulture));
            Readout("channels detected", state.detectedCount.ToString(CultureInfo.InvariantCulture));

            if (!float.IsNaN(state.medianDistanceCm))
                Readout("device distance",
                        state.medianDistanceCm.ToString("0.0", CultureInfo.InvariantCulture) + " cm");

            if (state.firmware == FirmwareKind.BeamSteerer)
            {
                GUILayout.Space(4);
                if (GUILayout.Button("Command current steer angle"))
                    StartCoroutine(device.SetAngle(steerDeg));
                Readout("angles recorded",
                        state.angularProfile.Count.ToString(CultureInfo.InvariantCulture));
            }

            GUILayout.Label("Neither sketch streams a full angle/range image, so the sector "
                            + "panel stays sparse on a live link.",
                            Style(TextAnchor.UpperLeft, 10, PanelBase.TextDim));

            DrawDisplayControls();
        }

        void DrawDisplayControls()
        {
            SectionHeader("DISPLAY");

            ScanDisplaySettings display = _sectorPanel.display;
            display.gainDb = Slider("Gain", display.gainDb, -20f, 40f, "0.0 dB");
            display.dynamicRangeDb = Slider("Dynamic range", display.dynamicRangeDb, 6f, 60f, "0 dB");
            display.logCompression = GUILayout.Toggle(display.logCompression, " log compression");
            _sectorPanel.display = display;

            int colormapIndex = Mathf.Clamp((int)_sectorPanel.colormap, 0, ColormapNames.Length - 1);
            _sectorPanel.colormap = (ColormapKind)GUILayout.Toolbar(colormapIndex, ColormapNames);
        }

        void DrawMetrics()
        {
            SectionHeader("ARRAY");
            Readout("wavelength",
                    config.WavelengthMm.ToString("0.00", CultureInfo.InvariantCulture) + " mm");
            Readout("pitch",
                    config.PitchMm.ToString("0.0", CultureInfo.InvariantCulture) + " mm = " +
                    config.PitchInWavelengths.ToString("0.00", CultureInfo.InvariantCulture) + " λ",
                    config.PitchInWavelengths > 0.5f ? PanelBase.TextWarn : PanelBase.TextColor);
            Readout("aperture",
                    config.ApertureMm.ToString("0", CultureInfo.InvariantCulture) + " mm");
            Readout("far field beyond",
                    (config.RayleighDistanceMm * 0.1f).ToString("0.0", CultureInfo.InvariantCulture) + " cm");
            Readout("burst",
                    config.BurstDurationUs.ToString("0", CultureInfo.InvariantCulture) + " µs = " +
                    config.BurstRangeExtentCm.ToString("0.0", CultureInfo.InvariantCulture) + " cm");

            SectionHeader("BEAM METRICS");
            Readout("-3 dB width",
                    _beamPanel.MeasuredBeamwidthDeg.ToString("0.00", CultureInfo.InvariantCulture) + "°");
            Readout("peak side lobe",
                    _beamPanel.PeakSideLobeDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB",
                    _beamPanel.PeakSideLobeDb > -6f ? PanelBase.TextWarn : PanelBase.TextColor);
            Readout("aperture ramp",
                    _plan.maxDelayUs.ToString("0.0", CultureInfo.InvariantCulture) + " µs");
            Readout("tick error",
                    _plan.worstQuantisationUs.ToString("0.0000", CultureInfo.InvariantCulture) + " µs = " +
                    _plan.WorstQuantisationPhaseDeg(config).ToString("0.00", CultureInfo.InvariantCulture) + "°");

            var lobes = _beamPanel.GratingLobes;
            Readout("grating lobes", lobes.Count == 0 ? "none" : FormatAngles(lobes));

            var inSector = _beamPanel.GratingLobesInSector;
            if (inSector.Count > 0)
            {
                Readout("inside the sector", FormatAngles(inSector), PanelBase.TextWarn);
                GUILayout.Label("A grating lobe inside the scanned sector puts a full-strength "
                                + "copy of the target at the wrong bearing.",
                                Style(TextAnchor.UpperLeft, 10, PanelBase.TextWarn));
            }

            SectionHeader("FRAME");
            Readout("source", _statusMessage);
            if (_displayFrame != null && _displayFrame.IsValid)
            {
                Readout("peak",
                        string.Format(CultureInfo.InvariantCulture, "{0:0.#}° {1:0.0} cm",
                                      _sectorPanel.PeakAngleDeg, _sectorPanel.PeakRangeCm));
                Readout("units", _displayFrame.unit);
            }
        }

        static string FormatAngles(List<float> angles)
        {
            var parts = new List<string>(angles.Count);
            foreach (float angle in angles)
                parts.Add(angle.ToString("0.#", CultureInfo.InvariantCulture) + "°");
            return string.Join(", ", parts.ToArray());
        }

        void DrawExport()
        {
            GUILayout.Space(10);
            if (GUILayout.Button("Export panels as PNG")) ExportPanels();
            if (!string.IsNullOrEmpty(_exportMessage))
                GUILayout.Label(_exportMessage, Style(TextAnchor.UpperLeft, 10, PanelBase.TextDim));
        }

        void ExportPanels()
        {
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, "ultrasound-export");
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

                WritePng(folder, stamp, "field", _fieldPanel);
                WritePng(folder, stamp, "beam", _beamPanel);
                WritePng(folder, stamp, "sector", _sectorPanel);
                WritePng(folder, stamp, "channels", _channelPanel);

                _exportMessage = "written to " + folder;
                Debug.Log("[PhasedArrayConsole] exported panels to " + folder);
            }
            catch (Exception exception)
            {
                _exportMessage = "export failed: " + exception.Message;
                Debug.LogWarning("[PhasedArrayConsole] " + _exportMessage);
            }
        }

        static void WritePng(string folder, string stamp, string name, PanelBase panel)
        {
            if (panel.Texture == null) return;
            byte[] png = panel.Texture.EncodeToPNG();
            if (png == null) return;
            File.WriteAllBytes(Path.Combine(folder, stamp + "_" + name + ".png"), png);
        }
    }
}
