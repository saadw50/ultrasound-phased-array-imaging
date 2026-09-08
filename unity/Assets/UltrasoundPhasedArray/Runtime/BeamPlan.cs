using System;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Transmit delay schedule for one steering angle.
    /// This is a direct port of <c>prepareBeam()</c> in <c>ultrasonic_shape_rudder.ino</c>:
    /// the raw geometric delay per element is computed, then the whole set is shifted so the
    /// earliest channel fires at t = 0, because the hardware cannot fire before the trigger.
    /// </summary>
    public struct BeamPlan
    {
        /// <summary>Commanded steering angle in degrees, positive toward +x.</summary>
        public float steerDeg;

        /// <summary>Ideal delay per channel in microseconds, relative to the first channel.</summary>
        public float[] delayUs;

        /// <summary>Delays after rounding onto the ESP32 CPU tick grid, as actually emitted.</summary>
        public float[] emittedDelayUs;

        /// <summary>Firmware <c>wavefrontReferenceDelayUs</c>: the shift applied to reach t = 0.</summary>
        public float wavefrontReferenceDelayUs;

        /// <summary>Largest channel delay, i.e. the ramp-up time of the aperture.</summary>
        public float maxDelayUs;

        /// <summary>Worst-case rounding error introduced by the tick grid, in microseconds.</summary>
        public float worstQuantisationUs;

        public int ElementCount
        {
            get { return delayUs != null ? delayUs.Length : 0; }
        }

        /// <summary>Worst-case tick-grid rounding expressed as carrier phase error, in degrees.</summary>
        public float WorstQuantisationPhaseDeg(ArrayConfig config)
        {
            return worstQuantisationUs * config.carrierHz * 1e-6f * 360f;
        }

        /// <summary>
        /// Builds the delay schedule for <paramref name="steerDeg"/>.
        /// Mirrors the firmware including the per-channel trim and the tick quantisation.
        /// </summary>
        public static BeamPlan Compute(ArrayConfig config, float steerDeg)
        {
            if (config == null) throw new ArgumentNullException("config");
            config.Normalise();

            int n = config.ElementCount;
            var plan = new BeamPlan
            {
                steerDeg = steerDeg,
                delayUs = new float[n],
                emittedDelayUs = new float[n],
            };
            if (n == 0) return plan;

            float sine = Mathf.Sin(steerDeg * Mathf.Deg2Rad);
            float speed = Mathf.Max(1e-6f, config.soundSpeedMmPerUs);

            var raw = new float[n];
            float minimum = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                raw[i] = config.elementXmm[i] * sine / speed + config.TrimUs(i);
                if (raw[i] < minimum) minimum = raw[i];
            }

            plan.wavefrontReferenceDelayUs = -minimum;

            int ticksPerUs = Mathf.Max(1, config.timerTicksPerUs);
            for (int i = 0; i < n; i++)
            {
                float ideal = raw[i] - minimum;
                plan.delayUs[i] = ideal;
                plan.emittedDelayUs[i] = QuantiseUs(ideal, ticksPerUs);

                float error = Mathf.Abs(plan.emittedDelayUs[i] - ideal);
                if (error > plan.worstQuantisationUs) plan.worstQuantisationUs = error;
                if (ideal > plan.maxDelayUs) plan.maxDelayUs = ideal;
            }

            return plan;
        }

        /// <summary>Firmware <c>usToTicks</c> followed by the reverse conversion.</summary>
        public static float QuantiseUs(float microseconds, int ticksPerUs)
        {
            double ticks = Math.Round((double)microseconds * ticksPerUs, MidpointRounding.AwayFromZero);
            return (float)(ticks / ticksPerUs);
        }

        /// <summary>Delays actually used for a simulation, honouring the quantisation toggle.</summary>
        public float[] EffectiveDelays(bool useEmitted)
        {
            return useEmitted ? emittedDelayUs : delayUs;
        }

        /// <summary>
        /// Total transmit occupancy: the aperture ramp plus the burst itself.
        /// The firmware uses the same sum to size its acquisition guard.
        /// </summary>
        public float TransmitDurationUs(ArrayConfig config)
        {
            return maxDelayUs + config.BurstDurationUs;
        }
    }
}
