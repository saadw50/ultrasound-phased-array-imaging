using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    public enum ConsoleMode
    {
        /// <summary>Forward model: what the platform would record for virtual targets.</summary>
        Simulation = 0,
        /// <summary>Playback of a baked session from results/distance.</summary>
        Recording = 1,
        /// <summary>Live telemetry from an ESP32 running one of the firmware sketches.</summary>
        Live = 2,
    }

    /// <summary>
    /// The whole instrument. Add this component to an empty GameObject and press play:
    /// it builds its own textures, needs no scene assets, materials or shaders, and renders
    /// through IMGUI so it behaves the same in every render pipeline.
    /// </summary>
    [AddComponentMenu("Ultrasound/Phased Array Console")]
    [DisallowMultipleComponent]
    public sealed partial class PhasedArrayConsole : MonoBehaviour
    {
        [Header("Array")]
        [Tooltip("Leave empty to use the firmware defaults at runtime.")]
        public ArrayConfig config;

        [Header("Beam")]
        [Range(-30f, 30f)] public float steerDeg;
        public TxMode txMode = TxMode.Full8;

        [Tooltip("Sweep the steering angle back and forth across the sector.")]
        public bool sweepSteering;
        public float sweepDegreesPerSecond = 6f;

        [Tooltip("Simulate with the tick-quantised delays the firmware emits, not ideal ones.")]
        public bool useEmittedDelays = true;

        [Header("Field")]
        public FieldMode fieldMode = FieldMode.Wavefront;
        public bool animateWavefront = true;

        [Tooltip("Acoustic microseconds advanced per real second. Lower is slower motion.")]
        public float microsecondsPerSecond = 400f;

        [Tooltip("1 evaluates every pixel, 2 every other pixel, and so on.")]
        [Range(1, 6)] public int fieldDownsample = 2;

        [Header("Mode")]
        public ConsoleMode mode = ConsoleMode.Simulation;

        [Header("Simulation")]
        public SyntheticScanSimulator simulator = new SyntheticScanSimulator();
        public List<AcousticTarget> targets = new List<AcousticTarget>();

        [Header("Advanced")]
        [Tooltip("Work on a copy of the config asset so play-mode edits do not modify it on disk.")]
        public bool cloneConfigAtRuntime = true;

        [Header("Recording")]
        [Tooltip("Subtract the matching background acquisition from the target frame.")]
        public bool subtractBackground;

        [Tooltip("Remove the measured range bias so echoes sit at their true distance.")]
        public bool correctRangeBias;

        [Header("Live device")]
        public Esp32Link device = new Esp32Link();

        // ------------------------------------------------------------------

        readonly FieldPanel _fieldPanel = new FieldPanel();
        readonly BeamPatternPanel _beamPanel = new BeamPatternPanel();
        readonly SectorPanel _sectorPanel = new SectorPanel();
        readonly ChannelPanel _channelPanel = new ChannelPanel();

        readonly Dictionary<long, GUIStyle> _styles = new Dictionary<long, GUIStyle>();

        BeamPlan _plan;
        ScanFrame _displayFrame;
        ScanLibrary _library;
        List<string> _datasets = new List<string>();
        int _datasetIndex;
        int _frameIndex;
        string _statusMessage = "";
        string _exportMessage = "";

        float _timeUs;
        int _lastStateHash = -1;
        bool _polling;
        Vector2 _controlScroll;

        // IMGUI runs a layout pass and a repaint pass over the same controls, so the target
        // list is only ever mutated from Update, never from inside OnGUI.
        int _pendingTargetRemoval = -1;
        bool _pendingAddTarget;
        bool _pendingResetScene;

        Rect _controlRect, _fieldRect, _beamRect, _sectorRect, _channelRect;

        /// <summary>Set when this component created the config and is responsible for it.</summary>
        ArrayConfig _ownedConfig;

        const float ControlWidth = 316f;
        const float Gap = 6f;

        // ------------------------------------------------------------------

        void OnEnable()
        {
            if (config == null)
            {
                config = ArrayConfig.CreateDefault();
                _ownedConfig = config;
            }
            else if (cloneConfigAtRuntime && Application.isPlaying && _ownedConfig != config)
            {
                config = Instantiate(config);
                _ownedConfig = config;
            }

            config.Normalise();

            if (targets == null || targets.Count == 0)
                targets = SyntheticScanSimulator.DefaultScene();

            _plan = BeamPlan.Compute(config, steerDeg);
            StartCoroutine(LoadDatasetIndex());
        }

        void OnDisable()
        {
            _fieldPanel.Dispose();
            _beamPanel.Dispose();
            _sectorPanel.Dispose();
            _channelPanel.Dispose();
            _styles.Clear();

            if (_ownedConfig != null)
            {
                if (Application.isPlaying) Destroy(_ownedConfig);
                else DestroyImmediate(_ownedConfig);
                _ownedConfig = null;
            }
        }

        void Update()
        {
            if (config == null) return;
            ApplyPendingEdits();
            config.Normalise();
            _fieldPanel.downsample = fieldDownsample;

            if (sweepSteering)
            {
                float limit = Mathf.Abs(config.maxSteerDeg);
                float phase = Time.time * sweepDegreesPerSecond / Mathf.Max(1f, limit);
                steerDeg = limit * Mathf.Sin(phase);
            }
            steerDeg = Mathf.Clamp(steerDeg, -Mathf.Abs(config.maxSteerDeg), Mathf.Abs(config.maxSteerDeg));

            _plan = BeamPlan.Compute(config, steerDeg);

            if (animateWavefront && fieldMode == FieldMode.Wavefront)
            {
                _timeUs += Time.deltaTime * Mathf.Max(1f, microsecondsPerSecond);
                float sweep = _fieldPanel.Field != null
                    ? _fieldPanel.Field.SweepDurationUs(config, _plan)
                    : 3000f;
                if (_timeUs > sweep) _timeUs = 0f;
            }

            ComputeLayout();

            int hash = StateHash();
            bool structural = hash != _lastStateHash;
            if (structural)
            {
                _lastStateHash = hash;
                RebuildFrame();
            }

            bool animating = animateWavefront && fieldMode == FieldMode.Wavefront;
            if (structural || animating)
                _fieldPanel.Render(config, _plan, txMode, fieldMode, _timeUs, useEmittedDelays,
                                   mode == ConsoleMode.Simulation ? targets : null,
                                   (int)_fieldRect.width, (int)_fieldRect.height);

            if (structural)
            {
                float sectorHalf = _displayFrame != null && _displayFrame.IsValid
                    ? Mathf.Max(Mathf.Abs(_displayFrame.MinAngleDeg), Mathf.Abs(_displayFrame.MaxAngleDeg))
                    : Mathf.Abs(config.maxSteerDeg);

                _beamPanel.Render(config, _plan, txMode, useEmittedDelays, sectorHalf,
                                  (int)_beamRect.width, (int)_beamRect.height);
                _sectorPanel.Render(_displayFrame, config, steerDeg, true,
                                    (int)_sectorRect.width, (int)_sectorRect.height);
                _channelPanel.Render(config, _plan, txMode, device.State,
                                     (int)_channelRect.width, (int)_channelRect.height);
            }
        }

        void ApplyPendingEdits()
        {
            if (_pendingResetScene)
            {
                targets = SyntheticScanSimulator.DefaultScene();
                _pendingResetScene = false;
            }

            if (_pendingAddTarget)
            {
                targets.Add(new AcousticTarget(0f, 60f, 0.9f));
                _pendingAddTarget = false;
            }

            if (_pendingTargetRemoval >= 0)
            {
                if (_pendingTargetRemoval < targets.Count) targets.RemoveAt(_pendingTargetRemoval);
                _pendingTargetRemoval = -1;
            }
        }

        /// <summary>
        /// Everything that changes what the static panels show. The field panel animates
        /// every frame, but rebuilding the other three only when something moves keeps the
        /// console cheap enough to leave running.
        /// </summary>
        int StateHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Mathf.RoundToInt(steerDeg * 100f);
                hash = hash * 31 + (int)txMode;
                hash = hash * 31 + (int)fieldMode;
                hash = hash * 31 + (int)mode;
                hash = hash * 31 + (useEmittedDelays ? 1 : 0);
                hash = hash * 31 + (subtractBackground ? 1 : 0);
                hash = hash * 31 + (correctRangeBias ? 1 : 0);
                hash = hash * 31 + _datasetIndex * 7919 + _frameIndex;
                hash = hash * 31 + config.burstCycles;
                hash = hash * 31 + Mathf.RoundToInt(config.rangeBiasCm * 100f);
                hash = hash * 31 + Mathf.RoundToInt(_sectorPanel.display.gainDb * 10f);
                hash = hash * 31 + Mathf.RoundToInt(_sectorPanel.display.dynamicRangeDb * 10f);
                hash = hash * 31 + (_sectorPanel.display.logCompression ? 1 : 0);
                hash = hash * 31 + (int)_sectorPanel.colormap;
                hash = hash * 31 + Mathf.RoundToInt(_fieldPanel.halfWidthMm);
                hash = hash * 31 + fieldDownsample;
                hash = hash * 31 + device.State.scanId;
                hash = hash * 31 + device.State.channels.Count;

                for (int i = 0; i < config.ElementCount; i++)
                    hash = hash * 31 + Mathf.RoundToInt(config.channelGain[i] * 100f);

                foreach (AcousticTarget target in targets)
                    hash = hash * 31 + Mathf.RoundToInt(target.angleDeg * 10f)
                                     + Mathf.RoundToInt(target.rangeCm * 10f) * 13
                                     + Mathf.RoundToInt(target.reflectivity * 100f) * 131;

                hash = hash * 31 + Mathf.RoundToInt(_fieldRect.width) * 31
                                 + Mathf.RoundToInt(_fieldRect.height);
                return hash;
            }
        }

        void RebuildFrame()
        {
            switch (mode)
            {
                case ConsoleMode.Simulation:
                    _displayFrame = simulator.Simulate(config, txMode, targets);
                    _statusMessage = string.Format(CultureInfo.InvariantCulture,
                        "forward model, {0} target(s)", targets.Count);
                    break;

                case ConsoleMode.Recording:
                    _displayFrame = BuildRecordedFrame();
                    break;

                case ConsoleMode.Live:
                    _displayFrame = BuildLiveFrame();
                    break;
            }
        }

        ScanFrame BuildRecordedFrame()
        {
            if (_library == null || _library.frames.Count == 0)
            {
                _statusMessage = "no dataset loaded";
                return null;
            }

            _frameIndex = Mathf.Clamp(_frameIndex, 0, _library.frames.Count - 1);
            ScanFrame frame = _library.frames[_frameIndex];

            if (subtractBackground)
            {
                ScanFrame background = _library.Find("BG", frame.txMode);
                if (background != null && !ReferenceEquals(background, frame))
                    frame = ScanFrame.Subtract(frame, background);
            }

            if (correctRangeBias) frame = frame.WithRangeOffset(-config.rangeBiasCm);

            _statusMessage = frame.DisplayName;
            return frame;
        }

        /// <summary>
        /// Builds a one-column frame from the live angular profile.
        /// Neither firmware streams an angle-by-range image, so the sector display stays
        /// sparse until a sweep has visited several angles.
        /// </summary>
        ScanFrame BuildLiveFrame()
        {
            DeviceState state = device.State;
            if (state.angularProfile.Count == 0)
            {
                _statusMessage = device.Connected
                    ? "connected, no beam sweep recorded yet"
                    : "not connected";
                return null;
            }

            var angles = new List<float>(state.angularProfile.Keys);
            float step = Mathf.Max(0.05f, config.RangeCmFromTofUs(50f));
            float minRange = simulator.minRangeCm;
            int bins = Mathf.Max(2, Mathf.FloorToInt((simulator.maxRangeCm - minRange) / step) + 1);

            var frame = ScanFrame.Create(angles.Count, bins);
            for (int b = 0; b < bins; b++) frame.rangeCm[b] = minRange + b * step;

            float extent = Mathf.Max(step, config.BurstRangeExtentCm);
            float bias = correctRangeBias ? -config.rangeBiasCm : 0f;

            for (int a = 0; a < angles.Count; a++)
            {
                frame.angleDeg[a] = angles[a];
                float peak = state.angularProfile[angles[a]];
                float range = float.IsNaN(state.medianDistanceCm) ? 0f : state.medianDistanceCm;
                if (range <= 0f || peak <= 0f) continue;

                float centre = range + bias;
                float half = 0.5f * extent;
                for (int b = 0; b < bins; b++)
                {
                    float offset = frame.rangeCm[b] - centre;
                    if (offset < -half || offset > half) continue;
                    frame[a, b] = peak * 0.5f * (1f + Mathf.Cos(Mathf.PI * offset / half));
                }
            }

            frame.label = "live " + state.firmware;
            frame.unit = "peak_adc";
            _statusMessage = string.Format(CultureInfo.InvariantCulture,
                "live: {0} angle(s) recorded", angles.Count);
            return frame;
        }

        // ------------------------------------------------------------------

        IEnumerator LoadDatasetIndex()
        {
            yield return ScanAssetLoader.LoadManifest(
                names => _datasets = names,
                error => _statusMessage = error);

            if (_datasets.Count > 0) yield return LoadDataset(0);
        }

        IEnumerator LoadDataset(int index)
        {
            if (index < 0 || index >= _datasets.Count) yield break;
            _datasetIndex = index;
            _statusMessage = "loading " + _datasets[index];

            yield return ScanAssetLoader.LoadLibrary(_datasets[index],
                library =>
                {
                    _library = library;
                    _frameIndex = PreferredFrame(library);
                    _lastStateHash = -1;
                },
                error => _statusMessage = error);
        }

        /// <summary>Picks the full-aperture target acquisition when the session has one.</summary>
        static int PreferredFrame(ScanLibrary library)
        {
            for (int i = 0; i < library.frames.Count; i++)
                if (library.frames[i].scene == "TARGET" && library.frames[i].txMode == "FULL8")
                    return i;
            return 0;
        }

        IEnumerator PollDevice()
        {
            _polling = true;
            yield return device.PollLoop(() => _polling && mode == ConsoleMode.Live);
            _polling = false;
        }

        // ------------------------------------------------------------------

        void ComputeLayout()
        {
            float width = Screen.width, height = Screen.height;
            _controlRect = new Rect(Gap, Gap, ControlWidth, height - 2 * Gap);

            float x = ControlWidth + 2 * Gap;
            float panelWidth = Mathf.Max(120f, (width - x - Gap) * 0.5f - Gap * 0.5f);
            float panelHeight = Mathf.Max(120f, (height - 3 * Gap) * 0.5f);

            _fieldRect = new Rect(x, Gap, panelWidth, panelHeight);
            _beamRect = new Rect(x + panelWidth + Gap, Gap, panelWidth, panelHeight);
            _sectorRect = new Rect(x, panelHeight + 2 * Gap, panelWidth, panelHeight);
            _channelRect = new Rect(x + panelWidth + Gap, panelHeight + 2 * Gap, panelWidth, panelHeight);
        }

        void OnGUI()
        {
            if (config == null) return;

            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture,
                            ScaleMode.StretchToFill, false, 0f,
                            new Color(0.031f, 0.043f, 0.055f, 1f), 0f, 0f);

            DrawPanel(_fieldPanel, _fieldRect);
            DrawPanel(_beamPanel, _beamRect);
            DrawPanel(_sectorPanel, _sectorRect);
            DrawPanel(_channelPanel, _channelRect);

            DrawControls();
        }

        void DrawPanel(PanelBase panel, Rect rect)
        {
            if (panel.Texture == null) return;

            GUI.DrawTexture(rect, panel.Texture, ScaleMode.StretchToFill, false);

            GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 16), panel.Title,
                      Style(TextAnchor.MiddleLeft, 12, PanelBase.TextColor));
            if (!string.IsNullOrEmpty(panel.Subtitle))
                GUI.Label(new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 16), panel.Subtitle,
                          Style(TextAnchor.MiddleRight, 10, PanelBase.TextDim));

            foreach (PanelLabel label in panel.Labels)
            {
                float px = rect.x + label.normalised.x * rect.width;
                float py = rect.yMax - label.normalised.y * rect.height;
                int size = label.fontSize > 0 ? label.fontSize : 10;

                Rect box;
                switch (label.anchor)
                {
                    case TextAnchor.MiddleRight: box = new Rect(px - 160f, py - 8f, 160f, 16f); break;
                    case TextAnchor.MiddleCenter: box = new Rect(px - 80f, py - 8f, 160f, 16f); break;
                    default: box = new Rect(px, py - 8f, 180f, 16f); break;
                }

                GUI.Label(box, label.text, Style(label.anchor, size, label.color));
            }
        }

        GUIStyle Style(TextAnchor anchor, int fontSize, Color color)
        {
            long key = ((long)anchor << 40) ^ ((long)fontSize << 32) ^ (uint)color.GetHashCode();
            GUIStyle style;
            if (_styles.TryGetValue(key, out style) && style != null) return style;

            style = new GUIStyle(GUI.skin.label)
            {
                alignment = anchor,
                fontSize = fontSize,
                wordWrap = false,
                clipping = TextClipping.Overflow,
            };
            style.normal.textColor = color;
            _styles[key] = style;
            return style;
        }
    }
}
