using NUnit.Framework;
using UnityEngine;

namespace Ultrasound.PhasedArray.Tests
{
    /// <summary>
    /// Checks the beamforming core against values derived independently from the array
    /// geometry in <c>ultrasonic_shape_rudder.ino</c>.
    /// </summary>
    public class BeamPhysicsTests
    {
        ArrayConfig _config;

        [SetUp]
        public void SetUp()
        {
            _config = ArrayConfig.CreateDefault();
        }

        [TearDown]
        public void TearDown()
        {
            if (_config != null) Object.DestroyImmediate(_config);
        }

        [Test]
        public void GeometryMatchesTheHardware()
        {
            Assert.AreEqual(8, _config.ElementCount);
            Assert.AreEqual(8.66875f, _config.WavelengthMm, 1e-4f, "c / f at 40 kHz");
            Assert.AreEqual(17.0f, _config.PitchMm, 1e-4f, "uniform 17 mm pitch");
            Assert.AreEqual(119.0f, _config.ApertureMm, 1e-4f, "outer element centres");
            Assert.AreEqual(0.509926f, 1f / _config.PitchInWavelengths, 1e-4f, "lambda / d");
            Assert.AreEqual(408.392f, _config.RayleighDistanceMm, 0.05f, "D^2 / 4 lambda");
        }

        [Test]
        public void RangeConversionMatchesTheExportedCsv()
        {
            // The exports list range_cm 13.4366 against tof_us 775 and step the bins by 50 us.
            Assert.AreEqual(13.4366f, _config.RangeCmFromTofUs(775f), 1e-3f);
            Assert.AreEqual(0.866875f, _config.RangeCmFromTofUs(50f), 1e-5f);
            Assert.AreEqual(775f, _config.TofUsFromRangeCm(13.4366f), 1e-2f);
        }

        [Test]
        public void BroadsideNeedsNoDelays()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 0f);
            for (int i = 0; i < plan.ElementCount; i++)
                Assert.AreEqual(0f, plan.delayUs[i], 1e-6f);

            Assert.AreEqual(0f, plan.maxDelayUs, 1e-6f);
        }

        [Test]
        public void SteeredDelaysMatchTheFirmwareSchedule()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 15f);

            // x_i sin(15) / c, shifted so the earliest channel fires at zero.
            float[] expected = { 0f, 12.68904f, 25.37808f, 38.06711f,
                                 50.75615f, 63.44519f, 76.13423f, 88.82326f };

            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], plan.delayUs[i], 1e-3f, "channel " + (i + 1));

            Assert.AreEqual(44.41163f, plan.wavefrontReferenceDelayUs, 1e-3f);
            Assert.AreEqual(88.82326f, plan.maxDelayUs, 1e-3f);
        }

        [Test]
        public void NegativeSteeringMirrorsThePositiveSchedule()
        {
            BeamPlan positive = BeamPlan.Compute(_config, 15f);
            BeamPlan negative = BeamPlan.Compute(_config, -15f);

            int last = positive.ElementCount - 1;
            for (int i = 0; i <= last; i++)
                Assert.AreEqual(positive.delayUs[i], negative.delayUs[last - i], 1e-4f);
        }

        [Test]
        public void TickQuantisationIsNegligible()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 15f);

            // Half a tick at 240 MHz.
            Assert.LessOrEqual(plan.worstQuantisationUs, 1f / (2f * 240f) + 1e-6f);
            Assert.Less(plan.WorstQuantisationPhaseDeg(_config), 0.05f,
                        "delay quantisation is not what limits this array");

            float[] weights = _config.ModeWeights(TxMode.Full8);
            Assert.AreEqual(1f, ArrayFactor.Magnitude(_config, plan, weights, 15f, true), 1e-4f);
        }

        [Test]
        public void PatternPeaksAtTheCommandedAngle()
        {
            foreach (float steer in new[] { -15f, -7f, 0f, 5f, 15f })
            {
                BeamPlan plan = BeamPlan.Compute(_config, steer);
                float[] weights = _config.ModeWeights(TxMode.Full8);

                Assert.AreEqual(1f, ArrayFactor.Magnitude(_config, plan, weights, steer, false), 1e-4f,
                                "unit magnitude on axis at " + steer);

                float onAxis = ArrayFactor.MagnitudeDb(_config, plan, weights, steer, false);
                float offAxis = ArrayFactor.MagnitudeDb(_config, plan, weights, steer + 2f, false);
                Assert.Greater(onAxis, offAxis, "main lobe falls away at " + steer);
            }
        }

        [Test]
        public void GratingLobesFollowTheAnalyticFormula()
        {
            var broadside = ArrayFactor.GratingLobeAngles(_config, 0f);
            Assert.AreEqual(2, broadside.Count);
            Assert.AreEqual(-30.6589f, broadside[0], 1e-2f);
            Assert.AreEqual(30.6589f, broadside[1], 1e-2f);

            var steered = ArrayFactor.GratingLobeAngles(_config, 15f);
            CollectionAssert.IsNotEmpty(steered);
            Assert.AreEqual(-14.5431f, steered[0], 1e-2f);
            Assert.AreEqual(50.2414f, steered[steered.Count - 1], 1e-2f);
        }

        [Test]
        public void SteeringToTheSectorEdgePutsAGratingLobeInsideTheSector()
        {
            // The headline limitation of a 17 mm pitch at 40 kHz: steering to +15 degrees
            // places a full-amplitude second beam at -14.5 degrees, inside the scanned sector.
            var inside = ArrayFactor.GratingLobesInSector(_config, 15f, 15f);
            Assert.AreEqual(1, inside.Count);
            Assert.AreEqual(-14.5431f, inside[0], 1e-2f);

            BeamPlan plan = BeamPlan.Compute(_config, 15f);
            float[] weights = _config.ModeWeights(TxMode.Full8);
            float ghost = ArrayFactor.MagnitudeDb(_config, plan, weights, inside[0], false);
            Assert.Greater(ghost, -1f, "the ghost is as strong as the wanted beam");

            Assert.AreEqual(0, ArrayFactor.GratingLobesInSector(_config, 0f, 15f).Count,
                            "at broadside both grating lobes sit outside the sector");
        }

        [Test]
        public void BeamwidthMatchesTheUniformLineArrayFormula()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 0f);
            float[] weights = _config.ModeWeights(TxMode.Full8);

            float measured = ArrayFactor.MeasuredBeamwidthDeg(_config, plan, weights, false);
            Assert.AreEqual(3.28f, measured, 0.1f);

            float theoretical = ArrayFactor.TheoreticalBeamwidthDeg(_config, 0f, 8);
            Assert.AreEqual(theoretical, measured, 0.15f);
        }

        [Test]
        public void HalfTheApertureGivesRoughlyTwiceTheBeamwidth()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 0f);

            float full = ArrayFactor.MeasuredBeamwidthDeg(_config, plan,
                                                          _config.ModeWeights(TxMode.Full8), false);
            float left = ArrayFactor.MeasuredBeamwidthDeg(_config, plan,
                                                          _config.ModeWeights(TxMode.Left4), false);

            Assert.AreEqual(6.68f, left, 0.15f);
            Assert.Greater(left, full * 1.8f);
        }

        [Test]
        public void PeakSideLobeIsAsStrongAsTheMainLobe()
        {
            // With a pitch of about two wavelengths the grating lobe is not a side lobe that
            // can be tapered away: it carries the same energy as the main beam.
            BeamPlan plan = BeamPlan.Compute(_config, 0f);
            float[] weights = _config.ModeWeights(TxMode.Full8);

            float sideLobe = ArrayFactor.PeakSideLobeDb(_config, plan, weights, false);
            Assert.Greater(sideLobe, -1f);
        }

        [Test]
        public void DisablingAChannelRaisesTheNearSideLobes()
        {
            BeamPlan plan = BeamPlan.Compute(_config, 0f);
            float[] healthy = _config.ModeWeights(TxMode.Full8);

            _config.channelGain[3] = 0f;
            float[] damaged = _config.ModeWeights(TxMode.Full8);

            Assert.AreEqual(0f, damaged[3], 1e-6f);
            Assert.AreEqual(1f, ArrayFactor.Magnitude(_config, plan, damaged, 0f, false), 1e-4f,
                            "the pattern is still normalised by the live channels");

            // A gap in the aperture fills in the nulls either side of the main lobe, which is
            // how a weak or dead transmitter shows up in a measured pattern.
            float healthyLobe = ArrayFactor.MagnitudeDb(_config, plan, healthy, 6f, false);
            float damagedLobe = ArrayFactor.MagnitudeDb(_config, plan, damaged, 6f, false);
            Assert.Greater(damagedLobe, healthyLobe + 3f);
        }

        [Test]
        public void BurstLengthSetsTheAxialExtent()
        {
            _config.burstCycles = 24;
            Assert.AreEqual(600f, _config.BurstDurationUs, 1e-3f, "24 cycles at 40 kHz");
            Assert.AreEqual(10.4025f, _config.BurstRangeExtentCm, 1e-3f,
                            "a 600 us burst spans over 10 cm of range");
        }

        [Test]
        public void SubApertureWeightsSelectTheRightHalf()
        {
            float[] left = _config.ModeWeights(TxMode.Left4);
            float[] right = _config.ModeWeights(TxMode.Right4);

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(1f, left[i], 1e-6f);
                Assert.AreEqual(0f, right[i], 1e-6f);
                Assert.AreEqual(0f, left[i + 4], 1e-6f);
                Assert.AreEqual(1f, right[i + 4], 1e-6f);
            }
        }
    }
}
