using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// One angle-by-range envelope image: the same shape as the angle/range CSV exports
    /// under <c>results/distance/</c>, and the input to the sector display.
    /// </summary>
    [Serializable]
    public sealed class ScanFrame
    {
        public string label = "";
        public string scene = "";
        public string txMode = "";
        public string source = "";
        public string unit = "envelope_adc";

        /// <summary>Steering angles in degrees, ascending.</summary>
        public float[] angleDeg = new float[0];

        /// <summary>Range bin centres in centimetres, ascending.</summary>
        public float[] rangeCm = new float[0];

        /// <summary>Amplitude per (angle, range), angle-major.</summary>
        public float[] amplitude = new float[0];

        public int AngleCount { get { return angleDeg != null ? angleDeg.Length : 0; } }
        public int BinCount { get { return rangeCm != null ? rangeCm.Length : 0; } }

        public bool IsValid
        {
            get
            {
                return AngleCount > 0 && BinCount > 0 &&
                       amplitude != null && amplitude.Length == AngleCount * BinCount;
            }
        }

        public float this[int angleIndex, int binIndex]
        {
            get { return amplitude[angleIndex * BinCount + binIndex]; }
            set { amplitude[angleIndex * BinCount + binIndex] = value; }
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(label)) return label;
                return string.Format("{0} {1}", scene, txMode).Trim();
            }
        }

        public static ScanFrame Create(int angleCount, int binCount)
        {
            return new ScanFrame
            {
                angleDeg = new float[angleCount],
                rangeCm = new float[binCount],
                amplitude = new float[angleCount * binCount],
            };
        }

        public float MaxAmplitude()
        {
            float peak = 0f;
            if (amplitude == null) return 0f;
            for (int i = 0; i < amplitude.Length; i++)
                if (amplitude[i] > peak) peak = amplitude[i];
            return peak;
        }

        public float MinAngleDeg { get { return AngleCount > 0 ? angleDeg[0] : 0f; } }
        public float MaxAngleDeg { get { return AngleCount > 0 ? angleDeg[AngleCount - 1] : 0f; } }
        public float MinRangeCm { get { return BinCount > 0 ? rangeCm[0] : 0f; } }
        public float MaxRangeCm { get { return BinCount > 0 ? rangeCm[BinCount - 1] : 0f; } }

        /// <summary>Angle and range of the strongest cell, useful as a readout.</summary>
        public bool TryFindPeak(out float peakAngleDeg, out float peakRangeCm, out float peakValue)
        {
            peakAngleDeg = 0f; peakRangeCm = 0f; peakValue = 0f;
            if (!IsValid) return false;

            int best = 0;
            for (int i = 1; i < amplitude.Length; i++)
                if (amplitude[i] > amplitude[best]) best = i;

            peakValue = amplitude[best];
            peakAngleDeg = angleDeg[best / BinCount];
            peakRangeCm = rangeCm[best % BinCount];
            return true;
        }

        /// <summary>
        /// Clutter removal by subtracting a background acquisition, clamped at zero.
        /// Both frames must share their angle and range axes.
        /// </summary>
        public static ScanFrame Subtract(ScanFrame target, ScanFrame background, bool clampToZero = true)
        {
            if (target == null || background == null) return target;
            if (!target.IsValid || !background.IsValid) return target;
            if (target.AngleCount != background.AngleCount || target.BinCount != background.BinCount)
                return target;

            var result = Create(target.AngleCount, target.BinCount);
            Array.Copy(target.angleDeg, result.angleDeg, target.AngleCount);
            Array.Copy(target.rangeCm, result.rangeCm, target.BinCount);

            for (int i = 0; i < result.amplitude.Length; i++)
            {
                float value = target.amplitude[i] - background.amplitude[i];
                result.amplitude[i] = clampToZero && value < 0f ? 0f : value;
            }

            result.label = target.DisplayName + " - background";
            result.scene = target.scene;
            result.txMode = target.txMode;
            result.source = target.source;
            result.unit = target.unit;
            return result;
        }

        /// <summary>
        /// Returns a copy with every range shifted by <paramref name="offsetCm"/>.
        /// Pass the negative of the measured bias to place echoes at their true range.
        /// </summary>
        public ScanFrame WithRangeOffset(float offsetCm)
        {
            if (Mathf.Approximately(offsetCm, 0f) || !IsValid) return this;

            var shifted = Create(AngleCount, BinCount);
            Array.Copy(angleDeg, shifted.angleDeg, AngleCount);
            Array.Copy(amplitude, shifted.amplitude, amplitude.Length);
            for (int i = 0; i < BinCount; i++) shifted.rangeCm[i] = rangeCm[i] + offsetCm;

            shifted.label = label;
            shifted.scene = scene;
            shifted.txMode = txMode;
            shifted.source = source;
            shifted.unit = unit;
            return shifted;
        }

        /// <summary>
        /// Bilinear sample in (angle, range) space. Returns 0 outside the acquired sector so
        /// the scan converter leaves unmeasured screen area black rather than extrapolating.
        /// </summary>
        public float Sample(float angle, float range)
        {
            if (!IsValid) return 0f;
            if (angle < angleDeg[0] || angle > angleDeg[AngleCount - 1]) return 0f;
            if (range < rangeCm[0] || range > rangeCm[BinCount - 1]) return 0f;

            int a0 = LowerBound(angleDeg, angle);
            int r0 = LowerBound(rangeCm, range);
            int a1 = Mathf.Min(a0 + 1, AngleCount - 1);
            int r1 = Mathf.Min(r0 + 1, BinCount - 1);

            float af = a1 > a0 ? (angle - angleDeg[a0]) / (angleDeg[a1] - angleDeg[a0]) : 0f;
            float rf = r1 > r0 ? (range - rangeCm[r0]) / (rangeCm[r1] - rangeCm[r0]) : 0f;

            float top = Mathf.Lerp(this[a0, r0], this[a1, r0], af);
            float bottom = Mathf.Lerp(this[a0, r1], this[a1, r1], af);
            return Mathf.Lerp(top, bottom, rf);
        }

        static int LowerBound(float[] values, float target)
        {
            int low = 0, high = values.Length - 1;
            while (low < high - 1)
            {
                int mid = (low + high) / 2;
                if (values[mid] <= target) low = mid; else high = mid;
            }
            return low;
        }

        /// <summary>Peak amplitude per angle, i.e. the angular response used for beam checks.</summary>
        public float[] AngularProfile()
        {
            var profile = new float[AngleCount];
            for (int a = 0; a < AngleCount; a++)
            {
                float peak = 0f;
                for (int r = 0; r < BinCount; r++)
                    if (this[a, r] > peak) peak = this[a, r];
                profile[a] = peak;
            }
            return profile;
        }

        /// <summary>Peak amplitude per range bin, summed across the sector.</summary>
        public float[] RangeProfile()
        {
            var profile = new float[BinCount];
            for (int r = 0; r < BinCount; r++)
            {
                float peak = 0f;
                for (int a = 0; a < AngleCount; a++)
                    if (this[a, r] > peak) peak = this[a, r];
                profile[r] = peak;
            }
            return profile;
        }
    }

    /// <summary>A named collection of frames, as produced by one baked capture session.</summary>
    public sealed class ScanLibrary
    {
        public string label = "";
        public string source = "";
        public readonly List<ScanFrame> frames = new List<ScanFrame>();

        public ScanFrame Find(string scene, string txMode)
        {
            foreach (var frame in frames)
                if (frame.scene == scene && frame.txMode == txMode) return frame;
            return null;
        }

        public ScanFrame FrameAt(int index)
        {
            if (index < 0 || index >= frames.Count) return null;
            return frames[index];
        }
    }
}
