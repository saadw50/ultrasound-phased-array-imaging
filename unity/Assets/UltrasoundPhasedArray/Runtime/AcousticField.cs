using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Huygens superposition of the eight transmit elements over a 2-D slice of the
    /// plane in front of the array. The array lies on z = 0, x is the array axis.
    /// </summary>
    /// <remarks>
    /// Two evaluations are provided. <see cref="EvaluateInstant"/> sums the finite tone burst
    /// at one instant, which shows the tilted wavefront and any grating-lobe wavefronts
    /// leaving the aperture. <see cref="EvaluateMagnitude"/> sums the steady-state complex
    /// pressure, which is the beam map. Both include 1/sqrt(r) cylindrical spreading and
    /// per-element directivity, and neither models absorption, reflection or the receiver.
    /// </remarks>
    public sealed class AcousticField
    {
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Field extent in millimetres. z is range, x is across the array.</summary>
        public float MinXmm, MaxXmm, MinZmm, MaxZmm;

        /// <summary>Signed instantaneous pressure, or magnitude, depending on the last call.</summary>
        public float[] Values { get; private set; }

        /// <summary>Largest absolute value written by the last evaluation.</summary>
        public float PeakValue { get; private set; }

        /// <summary>Near-field clamp so the singularity at r = 0 does not dominate the display.</summary>
        public float MinRadiusMm = 4f;

        public AcousticField(int width, int height)
        {
            Resize(width, height);
            MinXmm = -300f; MaxXmm = 300f;
            MinZmm = 0f; MaxZmm = 600f;
        }

        public void Resize(int width, int height)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            Values = new float[Width * Height];
        }

        public void SetExtent(float minXmm, float maxXmm, float minZmm, float maxZmm)
        {
            MinXmm = minXmm; MaxXmm = maxXmm;
            MinZmm = minZmm; MaxZmm = maxZmm;
        }

        /// <summary>Instantaneous pressure of the finite burst at <paramref name="timeUs"/>.</summary>
        public void EvaluateInstant(ArrayConfig config, BeamPlan plan, float[] weights,
                                    float timeUs, bool useEmittedDelays)
        {
            Evaluate(config, plan, weights, useEmittedDelays, true, timeUs);
        }

        /// <summary>Steady-state pressure magnitude, i.e. the continuous-wave beam map.</summary>
        public void EvaluateMagnitude(ArrayConfig config, BeamPlan plan, float[] weights,
                                      bool useEmittedDelays)
        {
            Evaluate(config, plan, weights, useEmittedDelays, false, 0f);
        }

        void Evaluate(ArrayConfig config, BeamPlan plan, float[] weights,
                      bool useEmittedDelays, bool instantaneous, float timeUs)
        {
            if (config == null) throw new ArgumentNullException("config");

            int elements = config.ElementCount;
            float[] delays = plan.EffectiveDelays(useEmittedDelays);
            if (delays == null || delays.Length < elements) return;

            float speed = Mathf.Max(1e-6f, config.soundSpeedMmPerUs);
            float frequencyPerUs = config.carrierHz * 1e-6f;
            float burstUs = config.BurstDurationUs;
            float wavelength = config.WavelengthMm;
            float halfWidth = 0.5f * Mathf.Max(0.1f, config.elementWidthMm);
            float minRadius = Mathf.Max(0.5f, MinRadiusMm);

            float[] positions = config.elementXmm;
            float[] values = Values;
            int width = Width, height = Height;

            float dx = width > 1 ? (MaxXmm - MinXmm) / (width - 1) : 0f;
            float dz = height > 1 ? (MaxZmm - MinZmm) / (height - 1) : 0f;
            float minX = MinXmm, minZ = MinZmm;

            var rowPeak = new float[height];

            Parallel.For(0, height, row =>
            {
                float z = minZ + row * dz;
                int offset = row * width;
                float peak = 0f;

                for (int column = 0; column < width; column++)
                {
                    float x = minX + column * dx;
                    float real = 0f, imaginary = 0f;

                    for (int i = 0; i < elements; i++)
                    {
                        float w = weights != null && i < weights.Length ? weights[i] : 1f;
                        if (w <= 0f) continue;

                        float ex = x - positions[i];
                        float radius = Mathf.Sqrt(ex * ex + z * z);
                        if (radius < minRadius) radius = minRadius;

                        // Element directivity: far-field pattern of a strip of width elementWidthMm.
                        float sine = ex / radius;
                        float argument = Mathf.PI * halfWidth * 2f * sine / wavelength;
                        float directivity = Mathf.Abs(argument) < 1e-4f
                            ? 1f
                            : Mathf.Sin(argument) / argument;

                        float amplitude = w * directivity / Mathf.Sqrt(radius);
                        float retarded = radius / speed;

                        if (instantaneous)
                        {
                            float u = timeUs - delays[i] - retarded;
                            if (u < 0f || u > burstUs) continue;
                            // Hann taper over the burst: the transducer is narrowband and does
                            // not reproduce the hard edges of the square drive.
                            float envelope = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * u / Mathf.Max(1e-6f, burstUs)));
                            real += amplitude * envelope * Mathf.Sin(2f * Mathf.PI * frequencyPerUs * u);
                        }
                        else
                        {
                            float phase = -2f * Mathf.PI * frequencyPerUs * (delays[i] + retarded);
                            real += amplitude * Mathf.Cos(phase);
                            imaginary += amplitude * Mathf.Sin(phase);
                        }
                    }

                    float value = instantaneous ? real : Mathf.Sqrt(real * real + imaginary * imaginary);
                    values[offset + column] = value;

                    float magnitude = value < 0f ? -value : value;
                    if (magnitude > peak) peak = magnitude;
                }

                rowPeak[row] = peak;
            });

            float overall = 0f;
            for (int row = 0; row < height; row++)
                if (rowPeak[row] > overall) overall = rowPeak[row];
            PeakValue = overall;
        }

        /// <summary>
        /// Time span that covers the burst travelling from the aperture to the far edge of
        /// the field, so an animation can loop over exactly one transmit event.
        /// </summary>
        public float SweepDurationUs(ArrayConfig config, BeamPlan plan)
        {
            float corner = Mathf.Sqrt(Mathf.Max(MaxXmm * MaxXmm, MinXmm * MinXmm) + MaxZmm * MaxZmm);
            return plan.maxDelayUs + config.BurstDurationUs +
                   corner / Mathf.Max(1e-6f, config.soundSpeedMmPerUs);
        }

        public float ValueAt(int column, int row)
        {
            if (column < 0 || column >= Width || row < 0 || row >= Height) return 0f;
            return Values[row * Width + column];
        }
    }
}
