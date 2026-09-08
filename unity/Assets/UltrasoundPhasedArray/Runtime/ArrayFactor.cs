using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Far-field transmit beam pattern of the array for a given <see cref="BeamPlan"/>.
    /// </summary>
    /// <remarks>
    /// For observation angle theta the round-trip delay from element i is
    /// tau_i - x_i sin(theta) / c, so the complex far-field sum is
    /// sum_i w_i exp(j 2 pi f (x_i sin(theta) / c - tau_i)).
    /// This is transmit-only: the platform has a single receiver, so there is no
    /// receive beamforming to fold in.
    /// </remarks>
    public static class ArrayFactor
    {
        /// <summary>Normalised pattern magnitude in the range 0..1 at one observation angle.</summary>
        public static float Magnitude(ArrayConfig config, BeamPlan plan, float[] weights,
                                      float observeDeg, bool useEmittedDelays)
        {
            int n = config.ElementCount;
            if (n == 0) return 0f;

            float[] delays = plan.EffectiveDelays(useEmittedDelays);
            float frequencyPerUs = config.carrierHz * 1e-6f;
            float speed = Mathf.Max(1e-6f, config.soundSpeedMmPerUs);
            float sine = Mathf.Sin(observeDeg * Mathf.Deg2Rad);

            float real = 0f, imaginary = 0f, total = 0f;
            for (int i = 0; i < n; i++)
            {
                float w = weights != null && i < weights.Length ? weights[i] : 1f;
                if (w <= 0f) continue;

                float phase = 2f * Mathf.PI * frequencyPerUs *
                              (config.elementXmm[i] * sine / speed - delays[i]);
                real += w * Mathf.Cos(phase);
                imaginary += w * Mathf.Sin(phase);
                total += w;
            }

            if (total <= 0f) return 0f;
            return Mathf.Sqrt(real * real + imaginary * imaginary) / total;
        }

        /// <summary>Pattern magnitude in decibels, floored at <paramref name="floorDb"/>.</summary>
        public static float MagnitudeDb(ArrayConfig config, BeamPlan plan, float[] weights,
                                        float observeDeg, bool useEmittedDelays, float floorDb = -60f)
        {
            float magnitude = Magnitude(config, plan, weights, observeDeg, useEmittedDelays);
            if (magnitude <= 0f) return floorDb;
            return Mathf.Max(floorDb, 20f * Mathf.Log10(magnitude));
        }

        /// <summary>Samples the pattern in dB across a span, writing <paramref name="destination"/>.</summary>
        public static void SampleDb(ArrayConfig config, BeamPlan plan, float[] weights,
                                    float fromDeg, float toDeg, float[] destination,
                                    bool useEmittedDelays, float floorDb = -60f)
        {
            if (destination == null || destination.Length == 0) return;
            int count = destination.Length;
            float step = count > 1 ? (toDeg - fromDeg) / (count - 1) : 0f;
            for (int i = 0; i < count; i++)
                destination[i] = MagnitudeDb(config, plan, weights, fromDeg + i * step,
                                             useEmittedDelays, floorDb);
        }

        /// <summary>
        /// Analytic grating-lobe directions: sin(theta_g) = sin(theta_0) + n * lambda / pitch.
        /// A visible entry means a second full-amplitude beam exists at that angle.
        /// </summary>
        public static List<float> GratingLobeAngles(ArrayConfig config, float steerDeg, int maxOrder = 3)
        {
            var angles = new List<float>();
            float ratio = config.WavelengthMm / Mathf.Max(1e-6f, Mathf.Abs(config.PitchMm));
            float sine0 = Mathf.Sin(steerDeg * Mathf.Deg2Rad);

            for (int order = -maxOrder; order <= maxOrder; order++)
            {
                if (order == 0) continue;
                float sine = sine0 + order * ratio;
                if (sine < -1f || sine > 1f) continue;
                angles.Add(Mathf.Asin(sine) * Mathf.Rad2Deg);
            }
            angles.Sort();
            return angles;
        }

        /// <summary>
        /// Textbook -3 dB beamwidth of a uniform line array, 0.886 lambda / (N d cos theta),
        /// returned in degrees. Used as a cross-check on the sampled pattern.
        /// </summary>
        public static float TheoreticalBeamwidthDeg(ArrayConfig config, float steerDeg, int activeElements)
        {
            float aperture = Mathf.Max(1e-6f, activeElements * Mathf.Abs(config.PitchMm));
            float cosine = Mathf.Max(0.05f, Mathf.Cos(steerDeg * Mathf.Deg2Rad));
            return 0.886f * config.WavelengthMm / (aperture * cosine) * Mathf.Rad2Deg;
        }

        /// <summary>Measures the -3 dB width of the main lobe by walking outward from the steer angle.</summary>
        public static float MeasuredBeamwidthDeg(ArrayConfig config, BeamPlan plan, float[] weights,
                                                 bool useEmittedDelays, float stepDeg = 0.02f)
        {
            float centre = plan.steerDeg;
            float lower = WalkToEdge(config, plan, weights, centre, -stepDeg, useEmittedDelays);
            float upper = WalkToEdge(config, plan, weights, centre, stepDeg, useEmittedDelays);
            return upper - lower;
        }

        static float WalkToEdge(ArrayConfig config, BeamPlan plan, float[] weights,
                                float centre, float step, bool useEmittedDelays)
        {
            float angle = centre;
            for (int i = 0; i < 20000; i++)
            {
                float next = angle + step;
                if (next < -90f || next > 90f) return angle;
                if (MagnitudeDb(config, plan, weights, next, useEmittedDelays) < -3f) return next;
                angle = next;
            }
            return angle;
        }

        /// <summary>
        /// Highest pattern level outside the main lobe, in dB. Sampled rather than analytic so
        /// that dead channels and per-channel trims are included.
        /// </summary>
        public static float PeakSideLobeDb(ArrayConfig config, BeamPlan plan, float[] weights,
                                           bool useEmittedDelays, float guardDeg = 0f)
        {
            if (guardDeg <= 0f)
                guardDeg = MeasuredBeamwidthDeg(config, plan, weights, useEmittedDelays);

            float peak = -120f;
            const int samples = 3601;
            for (int i = 0; i < samples; i++)
            {
                float angle = -90f + 180f * i / (samples - 1);
                if (Mathf.Abs(angle - plan.steerDeg) < guardDeg) continue;
                float level = MagnitudeDb(config, plan, weights, angle, useEmittedDelays, -120f);
                if (level > peak) peak = level;
            }
            return peak;
        }

        /// <summary>Grating lobes that fall inside the scanned sector, where they alias as real targets.</summary>
        public static List<float> GratingLobesInSector(ArrayConfig config, float steerDeg, float sectorHalfWidthDeg)
        {
            var inside = new List<float>();
            foreach (float angle in GratingLobeAngles(config, steerDeg))
                if (Mathf.Abs(angle) <= sectorHalfWidthDeg) inside.Add(angle);
            return inside;
        }
    }
}
