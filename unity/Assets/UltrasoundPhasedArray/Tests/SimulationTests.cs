using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Ultrasound.PhasedArray.Tests
{
    /// <summary>Behaviour of the forward model and the acoustic field.</summary>
    public class SimulationTests
    {
        ArrayConfig _config;
        SyntheticScanSimulator _simulator;

        [SetUp]
        public void SetUp()
        {
            _config = ArrayConfig.CreateDefault();
            _simulator = new SyntheticScanSimulator
            {
                noiseFloor = 0f,
                noiseAmplitude = 0f,
                applyRangeBias = false,
            };
        }

        [TearDown]
        public void TearDown()
        {
            if (_config != null) Object.DestroyImmediate(_config);
        }

        [Test]
        public void ASingleTargetLandsAtItsOwnBearingAndRange()
        {
            var targets = new List<AcousticTarget> { new AcousticTarget(0f, 60f, 1f) };
            ScanFrame frame = _simulator.Simulate(_config, TxMode.Full8, targets);

            Assert.IsTrue(frame.IsValid);

            float angle, range, value;
            Assert.IsTrue(frame.TryFindPeak(out angle, out range, out value));
            Assert.AreEqual(0f, angle, 1.0f);
            Assert.AreEqual(60f, range, _config.BurstRangeExtentCm * 0.5f);
            Assert.Greater(value, 0f);
        }

        [Test]
        public void TheRangeBiasMovesTheEchoFurtherAway()
        {
            var targets = new List<AcousticTarget> { new AcousticTarget(0f, 50f, 1f) };

            ScanFrame unbiased = _simulator.Simulate(_config, TxMode.Full8, targets);
            _simulator.applyRangeBias = true;
            ScanFrame biased = _simulator.Simulate(_config, TxMode.Full8, targets);

            float angle, unbiasedRange, biasedRange, value;
            unbiased.TryFindPeak(out angle, out unbiasedRange, out value);
            biased.TryFindPeak(out angle, out biasedRange, out value);

            Assert.AreEqual(_config.rangeBiasCm, biasedRange - unbiasedRange,
                            _simulator.RangeStepCm(_config) * 1.5f);
        }

        [Test]
        public void ATargetOutsideTheSectorAppearsInsideItAtAFalseBearing()
        {
            // A reflector at +20 degrees is never lit by the main lobe, because the sweep only
            // reaches +15. It is lit by a grating lobe: sin(g) = sin(20) - lambda / pitch puts
            // the responsible steering angle at about -9.7 degrees, so the sector image shows a
            // strong echo on the opposite side of the array from the real object.
            var targets = new List<AcousticTarget> { new AcousticTarget(20f, 70f, 1f) };
            ScanFrame frame = _simulator.Simulate(_config, TxMode.Full8, targets);

            float peakAngle, peakRange, peakValue;
            Assert.IsTrue(frame.TryFindPeak(out peakAngle, out peakRange, out peakValue));

            float expected = Mathf.Asin(Mathf.Sin(20f * Mathf.Deg2Rad) -
                                        1f / _config.PitchInWavelengths) * Mathf.Rad2Deg;
            Assert.AreEqual(expected, peakAngle, 1.5f,
                            "the image peaks where the grating lobe points, not at the target");
            Assert.AreEqual(70f, peakRange, _config.BurstRangeExtentCm * 0.5f,
                            "the range is still correct, only the bearing is wrong");

            // Nothing comparable appears at the sector edge nearest the real object.
            float[] profile = frame.AngularProfile();
            Assert.Less(profile[frame.AngleCount - 1], 0.5f * peakValue);
        }

        [Test]
        public void AHalfApertureSpreadsTheTargetOverMoreBearings()
        {
            var targets = new List<AcousticTarget> { new AcousticTarget(0f, 60f, 1f) };

            ScanFrame full = _simulator.Simulate(_config, TxMode.Full8, targets);
            ScanFrame left = _simulator.Simulate(_config, TxMode.Left4, targets);

            // Half the aperture is a broader, lower-resolution beam: the peak amplitude is
            // normalised, so compare how many bearings see the target instead.
            Assert.Greater(CountAbove(left, 0.5f * left.MaxAmplitude()),
                           CountAbove(full, 0.5f * full.MaxAmplitude()));
        }

        [Test]
        public void AnEmptySceneProducesOnlyTheNoiseFloor()
        {
            _simulator.noiseFloor = 3f;
            _simulator.noiseAmplitude = 0f;

            ScanFrame frame = _simulator.Simulate(_config, TxMode.Full8, new List<AcousticTarget>());

            Assert.IsTrue(frame.IsValid);
            foreach (float value in frame.amplitude) Assert.AreEqual(3f, value, 1e-4f);
        }

        [Test]
        public void TheFieldIsBrightestAlongTheSteeredDirection()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 10f);
            float[] weights = _config.ModeWeights(TxMode.Full8);

            var field = new AcousticField(96, 96);
            field.SetExtent(-300f, 300f, 0f, 600f);
            field.EvaluateMagnitude(_config, plan, weights, false);

            Assert.Greater(field.PeakValue, 0f);

            // At 500 mm range the steered beam should be stronger than 10 degrees off it.
            float onBeam = SampleField(field, 500f * Mathf.Sin(10f * Mathf.Deg2Rad),
                                       500f * Mathf.Cos(10f * Mathf.Deg2Rad));
            float offBeam = SampleField(field, 500f * Mathf.Sin(0f), 500f * Mathf.Cos(0f));
            Assert.Greater(onBeam, offBeam);
        }

        [Test]
        public void TheBurstTravelsOutwardsAtTheSpeedOfSound()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 0f);
            float[] weights = _config.ModeWeights(TxMode.Full8);

            var field = new AcousticField(48, 48);
            field.SetExtent(-200f, 200f, 0f, 400f);

            field.EvaluateInstant(_config, plan, weights, 0f, false);
            Assert.AreEqual(0f, field.PeakValue, 1e-6f, "nothing has radiated at t = 0");

            field.EvaluateInstant(_config, plan, weights, 300f, false);
            Assert.Greater(field.PeakValue, 0f, "the burst is in flight at t = 300 us");
            Assert.AreEqual(0f, SampleField(field, 0f, 380f), 1e-6f,
                            "380 mm needs about 1100 us, so that point is still silent");

            // 380 mm plus the aperture offset, well inside the 600 us burst.
            float arrivalUs = 400f / _config.soundSpeedMmPerUs;
            field.EvaluateInstant(_config, plan, weights, arrivalUs + 200f, false);
            Assert.Greater(field.PeakValue, 0f);
        }

        static int CountAbove(ScanFrame frame, float threshold)
        {
            int count = 0;
            float[] profile = frame.AngularProfile();
            foreach (float value in profile) if (value >= threshold) count++;
            return count;
        }

        static float SampleField(AcousticField field, float xMm, float zMm)
        {
            int column = Mathf.RoundToInt((xMm - field.MinXmm) /
                                          (field.MaxXmm - field.MinXmm) * (field.Width - 1));
            int row = Mathf.RoundToInt((zMm - field.MinZmm) /
                                       (field.MaxZmm - field.MinZmm) * (field.Height - 1));
            return field.ValueAt(column, row);
        }
    }
}
