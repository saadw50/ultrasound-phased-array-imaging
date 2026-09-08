using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ultrasound.PhasedArray
{
    /// <summary>A virtual reflector for the forward model.</summary>
    [Serializable]
    public struct AcousticTarget
    {
        [Tooltip("Bearing from the array normal, in degrees.")]
        public float angleDeg;

        [Tooltip("True distance from the array, in centimetres.")]
        public float rangeCm;

        [Tooltip("Relative echo strength, 1 for a strong flat reflector.")]
        public float reflectivity;

        public AcousticTarget(float angleDeg, float rangeCm, float reflectivity)
        {
            this.angleDeg = angleDeg;
            this.rangeCm = rangeCm;
            this.reflectivity = reflectivity;
        }
    }

    /// <summary>
    /// Forward model that produces the angle/range frame the platform would record for a set
    /// of virtual targets.
    /// </summary>
    /// <remarks>
    /// This is transmit beamforming into a single wide-beam receiver, matching the hardware:
    /// the angular response at steering angle s for a target at bearing t is the transmit
    /// array factor evaluated at t, not at s. That is why a target can appear at the wrong
    /// bearing whenever a grating lobe points at it, and reproducing that artefact is the
    /// main reason this simulator exists.
    ///
    /// Absorption, multipath, the receive electronics and the envelope detector are not
    /// modelled, so amplitudes are relative and not calibrated ADC counts.
    /// </remarks>
    [Serializable]
    public sealed class SyntheticScanSimulator
    {
        [Header("Scan grid")]
        public float minAngleDeg = -15f;
        public float maxAngleDeg = 15f;
        public float angleStepDeg = 1f;

        public float minRangeCm = 12.57f;
        public float maxRangeCm = 160f;

        [Header("Signal")]
        [Tooltip("Amplitude of a unit reflector at the reference range, in display units.")]
        public float fullScale = 220f;

        [Tooltip("Range at which a unit reflector produces fullScale, in centimetres.")]
        public float referenceRangeCm = 50f;

        [Tooltip("Mean background level added to every cell.")]
        public float noiseFloor = 2.5f;

        [Tooltip("Peak-to-peak noise added on top of the floor.")]
        public float noiseAmplitude = 1.5f;

        [Tooltip("Add the measured uncorrected range bias, so echoes land where the hardware puts them.")]
        public bool applyRangeBias = true;

        [Tooltip("Use the tick-quantised delays the firmware actually emits.")]
        public bool useEmittedDelays = true;

        public int seed = 12345;

        /// <summary>Number of range bins for the configured grid and sound speed.</summary>
        public int BinCount(ArrayConfig config)
        {
            float step = RangeStepCm(config);
            return Mathf.Max(2, Mathf.FloorToInt((maxRangeCm - minRangeCm) / step) + 1);
        }

        /// <summary>
        /// Range bin width. The exports sample the envelope every 50 microseconds, which is
        /// 0.867 cm two-way at the firmware sound speed.
        /// </summary>
        public float RangeStepCm(ArrayConfig config)
        {
            return Mathf.Max(0.05f, config.RangeCmFromTofUs(50f));
        }

        public ScanFrame Simulate(ArrayConfig config, TxMode mode, IList<AcousticTarget> targets)
        {
            if (config == null) throw new ArgumentNullException("config");
            config.Normalise();

            float angleStep = Mathf.Max(0.1f, angleStepDeg);
            int angleCount = Mathf.Max(1, Mathf.FloorToInt((maxAngleDeg - minAngleDeg) / angleStep) + 1);
            float rangeStep = RangeStepCm(config);
            int binCount = BinCount(config);

            var frame = ScanFrame.Create(angleCount, binCount);
            for (int a = 0; a < angleCount; a++) frame.angleDeg[a] = minAngleDeg + a * angleStep;
            for (int b = 0; b < binCount; b++) frame.rangeCm[b] = minRangeCm + b * rangeStep;

            float[] weights = config.ModeWeights(mode);
            float axialExtent = Mathf.Max(rangeStep, config.BurstRangeExtentCm);
            float bias = applyRangeBias ? config.rangeBiasCm : 0f;

            for (int a = 0; a < angleCount; a++)
            {
                BeamPlan plan = BeamPlan.Compute(config, frame.angleDeg[a]);

                if (targets != null)
                {
                    for (int t = 0; t < targets.Count; t++)
                    {
                        AcousticTarget target = targets[t];
                        if (target.reflectivity <= 0f) continue;

                        float transmit = ArrayFactor.Magnitude(config, plan, weights,
                                                               target.angleDeg, useEmittedDelays);
                        if (transmit <= 1e-4f) continue;

                        // The single receiver has no beamforming, only its own element pattern.
                        float receive = ElementDirectivity(config, target.angleDeg);
                        float range = Mathf.Max(1f, target.rangeCm);
                        float spreading = (referenceRangeCm / range) * (referenceRangeCm / range);

                        float peak = fullScale * target.reflectivity * transmit *
                                     Mathf.Abs(receive) * spreading;
                        if (peak <= 0.01f) continue;

                        DepositEcho(frame, a, target.rangeCm + bias, axialExtent, peak);
                    }
                }
            }

            AddNoise(frame);
            return frame;
        }

        /// <summary>Spreads one echo over the range bins covered by the transmit burst.</summary>
        static void DepositEcho(ScanFrame frame, int angleIndex, float centreCm,
                                float extentCm, float peak)
        {
            float half = 0.5f * extentCm;
            for (int b = 0; b < frame.BinCount; b++)
            {
                float offset = frame.rangeCm[b] - centreCm;
                if (offset < -half || offset > half) continue;

                // Raised cosine along range: the envelope of a tone burst, not a hard edge.
                float envelope = 0.5f * (1f + Mathf.Cos(Mathf.PI * offset / half));
                float value = peak * envelope;
                if (value > frame[angleIndex, b]) frame[angleIndex, b] = value;
            }
        }

        void AddNoise(ScanFrame frame)
        {
            if (noiseFloor <= 0f && noiseAmplitude <= 0f) return;

            var random = new System.Random(seed);
            for (int i = 0; i < frame.amplitude.Length; i++)
            {
                float noise = noiseFloor + noiseAmplitude * (float)random.NextDouble();
                frame.amplitude[i] += noise;
            }
        }

        /// <summary>Far-field pattern of a single element, shared by the transmitters and the receiver.</summary>
        public static float ElementDirectivity(ArrayConfig config, float angleDeg)
        {
            float argument = Mathf.PI * config.elementWidthMm *
                             Mathf.Sin(angleDeg * Mathf.Deg2Rad) / config.WavelengthMm;
            if (Mathf.Abs(argument) < 1e-4f) return 1f;
            return Mathf.Sin(argument) / argument;
        }

        /// <summary>A default scene: one strong target ahead and a weaker one off to the side.</summary>
        public static List<AcousticTarget> DefaultScene()
        {
            return new List<AcousticTarget>
            {
                new AcousticTarget(0f, 50f, 1.0f),
                new AcousticTarget(9f, 84.5f, 0.8f),
                new AcousticTarget(-11f, 120f, 0.6f),
            };
        }
    }
}
