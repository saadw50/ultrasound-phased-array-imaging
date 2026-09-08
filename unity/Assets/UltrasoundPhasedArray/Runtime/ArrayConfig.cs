using System;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>Sub-aperture selection, matching the tx_mode column of the CSV exports.</summary>
    public enum TxMode
    {
        Full8 = 0,
        Left4 = 1,
        Right4 = 2,
    }

    /// <summary>
    /// Physical description of the 8-element transmit array.
    /// Defaults are taken from <c>ultrasonic_shape_rudder.ino</c> so that the simulated
    /// beam matches what the firmware actually fires.
    /// </summary>
    [CreateAssetMenu(fileName = "ArrayConfig", menuName = "Ultrasound/Phased Array Config")]
    public sealed class ArrayConfig : ScriptableObject
    {
        [Header("Geometry (firmware TX_X_MM, uniform 17 mm pitch)")]
        [Tooltip("Element centre positions along the array axis, in millimetres.")]
        public float[] elementXmm = { -59.5f, -42.5f, -25.5f, -8.5f, 8.5f, 25.5f, 42.5f, 59.5f };

        [Tooltip("Effective radiating width of one TCT40 element, used for element directivity.")]
        public float elementWidthMm = 16.0f;

        [Header("Per-channel calibration")]
        [Tooltip("Firmware CHANNEL_TRIM_US: extra delay added to each channel, in microseconds.")]
        public float[] channelTrimUs = new float[8];

        [Tooltip("Relative transmit amplitude per channel. 0 simulates a dead element.")]
        public float[] channelGain = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

        [Header("Acoustics (firmware SOUND_SPEED_MM_PER_US)")]
        public float soundSpeedMmPerUs = 0.34675f;
        public float carrierHz = 40000f;

        [Tooltip("Firmware PERIOD_US drive cycles per burst.")]
        [Range(1, 32)] public int burstCycles = 24;

        [Header("Scanning")]
        [Tooltip("Firmware clamps the commanded steer angle to this magnitude.")]
        public float maxSteerDeg = 15f;

        [Tooltip("ESP32 CPU MHz. Transmit delays are emitted on this tick grid.")]
        public int timerTicksPerUs = 240;

        [Header("Range calibration")]
        [Tooltip("Uncorrected range bias measured in the 50.0 cm raw-waveform audit, " +
                 "in centimetres. Subtracted from recorded ranges when correction is enabled.")]
        public float rangeBiasCm = 12.68f;

        public int ElementCount
        {
            get { return elementXmm != null ? elementXmm.Length : 0; }
        }

        /// <summary>Carrier wavelength in millimetres (c / f).</summary>
        public float WavelengthMm
        {
            get { return soundSpeedMmPerUs * 1e6f / Mathf.Max(1f, carrierHz); }
        }

        /// <summary>Mean centre-to-centre element spacing in millimetres.</summary>
        public float PitchMm
        {
            get
            {
                int n = ElementCount;
                if (n < 2) return 0f;
                return (elementXmm[n - 1] - elementXmm[0]) / (n - 1);
            }
        }

        /// <summary>Distance between the outermost element centres, in millimetres.</summary>
        public float ApertureMm
        {
            get
            {
                int n = ElementCount;
                if (n < 2) return 0f;
                float lo = elementXmm[0], hi = elementXmm[0];
                for (int i = 1; i < n; i++)
                {
                    if (elementXmm[i] < lo) lo = elementXmm[i];
                    if (elementXmm[i] > hi) hi = elementXmm[i];
                }
                return hi - lo;
            }
        }

        /// <summary>
        /// Rayleigh distance D^2 / (4 lambda) in millimetres. Targets closer than this are
        /// in the near field, where the far-field beam pattern does not apply.
        /// </summary>
        public float RayleighDistanceMm
        {
            get
            {
                float d = ApertureMm;
                return d * d / (4f * Mathf.Max(1e-6f, WavelengthMm));
            }
        }

        /// <summary>Pitch expressed in wavelengths. Above 0.5 the array can produce grating lobes.</summary>
        public float PitchInWavelengths
        {
            get { return PitchMm / Mathf.Max(1e-6f, WavelengthMm); }
        }

        /// <summary>Burst length in microseconds for the configured cycle count.</summary>
        public float BurstDurationUs
        {
            get { return burstCycles * 1e6f / Mathf.Max(1f, carrierHz); }
        }

        /// <summary>Range resolution set by the burst length, in centimetres (two-way).</summary>
        public float BurstRangeExtentCm
        {
            get { return BurstDurationUs * soundSpeedMmPerUs * 0.5f * 0.1f; }
        }

        /// <summary>Transmit weights for a sub-aperture, combined with the per-channel gains.</summary>
        public float[] ModeWeights(TxMode mode)
        {
            int n = ElementCount;
            var w = new float[n];
            int half = n / 2;
            for (int i = 0; i < n; i++)
            {
                bool on;
                switch (mode)
                {
                    case TxMode.Left4: on = i < half; break;
                    case TxMode.Right4: on = i >= half; break;
                    default: on = true; break;
                }
                float gain = (channelGain != null && i < channelGain.Length) ? channelGain[i] : 1f;
                w[i] = on ? Mathf.Max(0f, gain) : 0f;
            }
            return w;
        }

        public float TrimUs(int index)
        {
            if (channelTrimUs == null || index < 0 || index >= channelTrimUs.Length) return 0f;
            return channelTrimUs[index];
        }

        /// <summary>Two-way range in centimetres for a time of flight in microseconds.</summary>
        public float RangeCmFromTofUs(float tofUs)
        {
            return tofUs * soundSpeedMmPerUs * 0.5f * 0.1f;
        }

        public float TofUsFromRangeCm(float rangeCm)
        {
            return rangeCm * 10f * 2f / Mathf.Max(1e-6f, soundSpeedMmPerUs);
        }

        /// <summary>Fills in any array whose length drifted away from the element count.</summary>
        public void Normalise()
        {
            if (elementXmm == null || elementXmm.Length == 0)
                elementXmm = new float[] { -59.5f, -42.5f, -25.5f, -8.5f, 8.5f, 25.5f, 42.5f, 59.5f };

            int n = elementXmm.Length;
            channelTrimUs = Resize(channelTrimUs, n, 0f);
            channelGain = Resize(channelGain, n, 1f);
        }

        static float[] Resize(float[] source, int length, float fill)
        {
            if (source != null && source.Length == length) return source;
            var next = new float[length];
            for (int i = 0; i < length; i++)
                next[i] = (source != null && i < source.Length) ? source[i] : fill;
            return next;
        }

        /// <summary>A runtime instance with firmware defaults, for use without an asset.</summary>
        public static ArrayConfig CreateDefault()
        {
            var config = CreateInstance<ArrayConfig>();
            config.name = "ArrayConfig (firmware defaults)";
            config.Normalise();
            return config;
        }

        void OnValidate()
        {
            Normalise();
        }
    }
}
