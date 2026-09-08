using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

namespace Ultrasound.PhasedArray
{
    /// <summary>Which firmware sketch is answering on the other end.</summary>
    public enum FirmwareKind
    {
        Unknown = 0,
        /// <summary>html_logging.ino: sequential per-transmitter scanning, eight echo results.</summary>
        SequentialScanner = 1,
        /// <summary>ultrasonic_shape_rudder.ino: true-time-delay beam steering, one result per angle.</summary>
        BeamSteerer = 2,
    }

    /// <summary>One transmit channel as reported by the sequential scanner firmware.</summary>
    public struct ChannelReading
    {
        public int id;
        public bool detected;
        public float peakAdc;
        public float peakVolts;
        public float noiseAverage;
        public float threshold;
        public float tofUs;
        public float rawDistanceCm;
        public float distanceCm;
    }

    /// <summary>Snapshot of the device state, normalised across both firmware sketches.</summary>
    public sealed class DeviceState
    {
        public FirmwareKind firmware = FirmwareKind.Unknown;
        public int scanId;
        public bool scanning;
        public bool continuous;

        public float knownDistanceCm;
        public float soundCmPerUs;
        public int burstCycles;
        public float tofOffsetUs;

        /// <summary>Commanded steering angle. Only the beam-steering firmware reports one.</summary>
        public float beamAngleDeg;

        public float medianDistanceCm = float.NaN;
        public float medianTofUs = float.NaN;
        public float errorCm = float.NaN;
        public int detectedCount;

        public readonly List<ChannelReading> channels = new List<ChannelReading>();

        /// <summary>Peak amplitude per commanded angle, accumulated from beam-steering scans.</summary>
        public readonly SortedDictionary<float, float> angularProfile = new SortedDictionary<float, float>();

        public float lastUpdateTime;
        public string lastError = "";

        public bool HasData { get { return scanId > 0 || channels.Count > 0 || angularProfile.Count > 0; } }
    }

    /// <summary>
    /// Polls the ESP32 dashboard over HTTP and normalises both firmware payloads into a
    /// <see cref="DeviceState"/>.
    /// </summary>
    /// <remarks>
    /// Neither sketch streams a full angle-by-range image, so the live link cannot fill the
    /// sector display the way a recorded session does. The sequential scanner reports one
    /// echo per transmitter; the beam steerer reports one echo per commanded angle, which is
    /// accumulated here into an angular profile as a sweep proceeds.
    /// </remarks>
    [Serializable]
    public sealed class Esp32Link
    {
        [Tooltip("Dashboard address printed by the firmware on the serial monitor.")]
        public string host = "http://ultrasonic.local";

        [Tooltip("Seconds between polls of /api/data.")]
        public float pollInterval = 0.6f;

        [Tooltip("Request timeout in seconds. A scan blocks the device while it runs.")]
        public int timeoutSeconds = 8;

        public bool Connected { get; private set; }

        DeviceState _state;
        string _status;

        // Unity's serialiser can rebuild a serializable class without running its
        // constructor, so both of these are created on first use rather than in one.
        public DeviceState State
        {
            get { return _state ?? (_state = new DeviceState()); }
        }

        public string Status
        {
            get { return _status ?? "idle"; }
            private set { _status = value; }
        }

        public string BuildUrl(string path)
        {
            string root = (host ?? "").Trim().TrimEnd('/');
            if (root.Length == 0) root = "http://ultrasonic.local";
            if (!root.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !root.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                root = "http://" + root;
            return root + path;
        }

        /// <summary>Polls <c>/api/data</c> until <paramref name="keepRunning"/> returns false.</summary>
        public IEnumerator PollLoop(Func<bool> keepRunning)
        {
            while (keepRunning == null || keepRunning())
            {
                yield return Request("/api/data");
                yield return new WaitForSeconds(Mathf.Max(0.1f, pollInterval));
            }
        }

        /// <summary>Triggers one acquisition. The device blocks while it scans.</summary>
        public IEnumerator TriggerScan()
        {
            yield return Request("/api/scan");
        }

        /// <summary>Runs the built-in angle sweep. Beam-steering firmware only.</summary>
        public IEnumerator TriggerSweep()
        {
            State.angularProfile.Clear();
            yield return Request("/api/sweep");
        }

        public IEnumerator SetContinuous(bool enabled)
        {
            yield return Request("/api/continuous?on=" + (enabled ? "1" : "0"));
        }

        /// <summary>Commands a steering angle. Beam-steering firmware only.</summary>
        public IEnumerator SetAngle(float angleDeg)
        {
            yield return Request("/api/set?angle=" +
                                 angleDeg.ToString("0.##", CultureInfo.InvariantCulture));
        }

        public IEnumerator SetKnownDistance(float centimetres)
        {
            yield return Request("/api/set?known=" +
                                 centimetres.ToString("0.##", CultureInfo.InvariantCulture));
        }

        IEnumerator Request(string path)
        {
            string url = BuildUrl(path);
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.Max(1, timeoutSeconds);
                yield return request.SendWebRequest();

                if (IsError(request))
                {
                    Connected = false;
                    State.lastError = request.error;
                    Status = "offline: " + request.error;
                    yield break;
                }

                string body = request.downloadHandler != null ? request.downloadHandler.text : "";
                if (string.IsNullOrEmpty(body) || body[0] != '{')
                {
                    // /api/continuous answers with plain text.
                    Connected = true;
                    Status = "ok: " + body;
                    yield break;
                }

                if (Apply(body))
                {
                    Connected = true;
                    State.lastError = "";
                    Status = "connected";
                }
                else
                {
                    Status = "unparsed response";
                }
            }
        }

        static bool IsError(UnityWebRequest request)
        {
#if UNITY_2020_2_OR_NEWER
            return request.result != UnityWebRequest.Result.Success;
#else
            return request.isNetworkError || request.isHttpError;
#endif
        }

        /// <summary>Parses a dashboard payload from either firmware into <see cref="State"/>.</summary>
        public bool Apply(string json)
        {
            object root = SimpleJson.Parse(json);
            if (root == null) return false;

            var state = State;
            state.scanId = Mathf.RoundToInt(SimpleJson.GetNumber(root, "scanId", state.scanId));
            state.scanning = SimpleJson.GetBool(root, "scanning", false);
            state.continuous = SimpleJson.GetBool(root, "continuous", false);

            state.knownDistanceCm = SimpleJson.GetNumber(root, "settings.known", state.knownDistanceCm);
            state.soundCmPerUs = SimpleJson.GetNumber(root, "settings.sound", state.soundCmPerUs);
            state.burstCycles = Mathf.RoundToInt(SimpleJson.GetNumber(root, "settings.burst", state.burstCycles));
            state.tofOffsetUs = SimpleJson.GetNumber(root, "settings.offset", state.tofOffsetUs);
            state.beamAngleDeg = SimpleJson.GetNumber(root, "settings.angle", state.beamAngleDeg);

            var transmitters = SimpleJson.GetList(root, "tx");
            if (transmitters != null)
            {
                state.firmware = FirmwareKind.SequentialScanner;
                state.channels.Clear();
                foreach (object entry in transmitters)
                {
                    state.channels.Add(new ChannelReading
                    {
                        id = Mathf.RoundToInt(SimpleJson.GetNumber(entry, "id", 0f)),
                        detected = SimpleJson.GetBool(entry, "detected", false),
                        peakAdc = SimpleJson.GetNumber(entry, "peak", 0f),
                        peakVolts = SimpleJson.GetNumber(entry, "peakV", 0f),
                        noiseAverage = SimpleJson.GetNumber(entry, "noiseAvg", 0f),
                        threshold = SimpleJson.GetNumber(entry, "threshold", 0f),
                        tofUs = SimpleJson.GetNumber(entry, "tof", float.NaN),
                        rawDistanceCm = SimpleJson.GetNumber(entry, "rawDistance", float.NaN),
                        distanceCm = SimpleJson.GetNumber(entry, "distance", float.NaN),
                    });
                }

                state.medianDistanceCm = SimpleJson.GetNumber(root, "summary.median", float.NaN);
                state.medianTofUs = SimpleJson.GetNumber(root, "summary.medianTof", float.NaN);
                state.errorCm = SimpleJson.GetNumber(root, "summary.error", float.NaN);
                state.detectedCount = Mathf.RoundToInt(SimpleJson.GetNumber(root, "summary.detected", 0f));
            }

            object result = SimpleJson.Select(root, "result");
            if (result != null)
            {
                state.firmware = FirmwareKind.BeamSteerer;

                float angle = SimpleJson.GetNumber(root, "result.angle", state.beamAngleDeg);
                float peak = SimpleJson.GetNumber(root, "result.peak", 0f);
                state.angularProfile[angle] = peak;

                state.detectedCount = SimpleJson.GetBool(root, "result.detected", false) ? 1 : 0;
                state.medianDistanceCm = SimpleJson.GetNumber(root, "result.distance", float.NaN);
                state.medianTofUs = SimpleJson.GetNumber(root, "result.firstTof", float.NaN);

                state.channels.Clear();
                state.channels.Add(new ChannelReading
                {
                    id = 1,
                    detected = SimpleJson.GetBool(root, "result.detected", false),
                    peakAdc = peak,
                    peakVolts = SimpleJson.GetNumber(root, "result.peakV", 0f),
                    noiseAverage = SimpleJson.GetNumber(root, "result.noiseAvg", 0f),
                    threshold = SimpleJson.GetNumber(root, "result.threshold", 0f),
                    tofUs = SimpleJson.GetNumber(root, "result.firstTof", float.NaN),
                    rawDistanceCm = SimpleJson.GetNumber(root, "result.rawDistance", float.NaN),
                    distanceCm = SimpleJson.GetNumber(root, "result.distance", float.NaN),
                });
            }

            state.lastUpdateTime = Time.realtimeSinceStartup;
            return transmitters != null || result != null;
        }
    }
}
