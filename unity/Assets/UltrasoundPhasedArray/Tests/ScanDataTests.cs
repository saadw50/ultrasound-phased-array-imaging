using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Ultrasound.PhasedArray.Tests
{
    /// <summary>Round trips and parsing for the data path: CSV, .uscan, JSON telemetry.</summary>
    public class ScanDataTests
    {
        static ScanFrame MakeFrame(string scene, string txMode, float scale)
        {
            var frame = ScanFrame.Create(3, 4);
            frame.angleDeg = new[] { -1f, 0f, 1f };
            frame.rangeCm = new[] { 10f, 11f, 12f, 13f };
            for (int a = 0; a < 3; a++)
                for (int r = 0; r < 4; r++)
                    frame[a, r] = scale * (a * 4 + r);

            frame.scene = scene;
            frame.txMode = txMode;
            frame.label = scene + " " + txMode;
            return frame;
        }

        [Test]
        public void UscanSurvivesARoundTrip()
        {
            var library = new ScanLibrary { label = "unit test", source = "memory" };
            library.frames.Add(MakeFrame("TARGET", "FULL8", 1f));
            library.frames.Add(MakeFrame("BG", "FULL8", 0.25f));

            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                UscanFile.Write(stream, library);
                bytes = stream.ToArray();
            }

            ScanLibrary restored = UscanFile.Read(bytes, "memory");

            Assert.AreEqual(2, restored.frames.Count);
            Assert.AreEqual("unit test", restored.label);

            ScanFrame target = restored.Find("TARGET", "FULL8");
            Assert.IsNotNull(target);
            Assert.AreEqual(3, target.AngleCount);
            Assert.AreEqual(4, target.BinCount);
            Assert.AreEqual(11f, target[2, 3], 1e-5f);
            Assert.AreEqual(1f, target.angleDeg[2], 1e-5f);
            Assert.AreEqual(13f, target.rangeCm[3], 1e-5f);
        }

        [Test]
        public void ReadingRubbishBytesFails()
        {
            Assert.Throws<InvalidDataException>(() => UscanFile.Read(new byte[32]));
        }

        [Test]
        public void BackgroundSubtractionClampsAtZero()
        {
            ScanFrame target = MakeFrame("TARGET", "FULL8", 1f);
            ScanFrame background = MakeFrame("BG", "FULL8", 0.25f);

            ScanFrame difference = ScanFrame.Subtract(target, background);

            Assert.AreEqual(0.75f * 11f, difference[2, 3], 1e-4f);
            Assert.AreEqual(0f, difference[0, 0], 1e-6f);
            foreach (float value in difference.amplitude) Assert.GreaterOrEqual(value, 0f);
        }

        [Test]
        public void RangeOffsetShiftsTheAxisNotTheData()
        {
            ScanFrame frame = MakeFrame("TARGET", "FULL8", 1f);
            ScanFrame shifted = frame.WithRangeOffset(-12.68f);

            Assert.AreEqual(10f - 12.68f, shifted.rangeCm[0], 1e-4f);
            Assert.AreEqual(frame[1, 2], shifted[1, 2], 1e-6f);
            Assert.AreEqual(frame.rangeCm[0], 10f, 1e-6f, "the original is not modified");
        }

        [Test]
        public void SamplingIsBilinearInsideTheSectorAndZeroOutside()
        {
            ScanFrame frame = MakeFrame("TARGET", "FULL8", 1f);

            Assert.AreEqual(frame[1, 1], frame.Sample(0f, 11f), 1e-4f);
            Assert.AreEqual(0.5f * (frame[1, 1] + frame[1, 2]), frame.Sample(0f, 11.5f), 1e-4f);
            Assert.AreEqual(0f, frame.Sample(45f, 11f), 1e-6f, "outside the swept angles");
            Assert.AreEqual(0f, frame.Sample(0f, 200f), 1e-6f, "beyond the recorded range");
        }

        [Test]
        public void PeakSearchReportsAngleAndRange()
        {
            ScanFrame frame = MakeFrame("TARGET", "FULL8", 1f);
            float angle, range, value;

            Assert.IsTrue(frame.TryFindPeak(out angle, out range, out value));
            Assert.AreEqual(1f, angle, 1e-5f);
            Assert.AreEqual(13f, range, 1e-5f);
            Assert.AreEqual(11f, value, 1e-5f);
        }

        [Test]
        public void CsvImporterAveragesRepeatsPerAngle()
        {
            const string csv =
                "scene,scan_id,capture_id,angle_deg,tx_mode,repeat,noise_avg,noise_max," +
                "threshold,detected,peak_adc,first_cross_us,distance_cm,bin_index,tof_us," +
                "range_cm,envelope_adc\n" +
                "TARGET,1,1,-1.000,FULL8,1,0,0,10,1,50,3342,50.0,15,775.000,13.4366,10\n" +
                "TARGET,1,1,-1.000,FULL8,1,0,0,10,1,50,3342,50.0,16,825.000,14.3034,20\n" +
                "TARGET,1,2,-1.000,FULL8,2,0,0,10,1,50,3342,50.0,15,775.000,13.4366,30\n" +
                "TARGET,1,2,-1.000,FULL8,2,0,0,10,1,50,3342,50.0,16,825.000,14.3034,40\n" +
                "TARGET,1,3,0.000,FULL8,1,0,0,10,1,50,3342,50.0,15,775.000,13.4366,7\n" +
                "TARGET,1,3,0.000,FULL8,1,0,0,10,1,50,3342,50.0,16,825.000,14.3034,9\n" +
                "BG,1,4,-1.000,FULL8,1,0,0,10,0,2,0,-1.0,15,775.000,13.4366,1\n" +
                "BG,1,4,-1.000,FULL8,1,0,0,10,0,2,0,-1.0,16,825.000,14.3034,2\n";

            var importer = new CsvScanImporter();
            ScanLibrary library;
            using (var reader = new StringReader(csv))
                library = importer.Import(reader, "unit.csv", null);

            Assert.AreEqual(2, library.frames.Count, "one frame per scene and tx mode");

            ScanFrame target = library.Find("TARGET", "FULL8");
            Assert.IsNotNull(target);
            Assert.AreEqual(2, target.AngleCount);
            Assert.AreEqual(2, target.BinCount);

            Assert.AreEqual(-1f, target.angleDeg[0], 1e-4f);
            Assert.AreEqual(13.4366f, target.rangeCm[0], 1e-3f);

            Assert.AreEqual(20f, target[0, 0], 1e-4f, "mean of 10 and 30");
            Assert.AreEqual(30f, target[0, 1], 1e-4f, "mean of 20 and 40");
            Assert.AreEqual(7f, target[1, 0], 1e-4f, "single repeat kept as is");

            ScanFrame background = library.Find("BG", "FULL8");
            Assert.IsNotNull(background);
            Assert.AreEqual(1f, background[0, 0], 1e-4f);
        }

        [Test]
        public void CsvImporterRejectsAFileWithoutTheExpectedColumns()
        {
            var importer = new CsvScanImporter();
            using (var reader = new StringReader("a,b,c\n1,2,3\n"))
                Assert.Throws<InvalidDataException>(() => importer.Import(reader, "bad.csv", null));
        }

        [Test]
        public void JsonParserHandlesTheFirmwarePayload()
        {
            const string json = "{\"scanId\":7,\"scanning\":false,\"settings\":{\"known\":50.00," +
                                "\"sound\":0.034675},\"tx\":[{\"id\":1,\"detected\":true,\"tof\":3342}," +
                                "{\"id\":2,\"detected\":false,\"tof\":null}]}";

            object root = SimpleJson.Parse(json);
            Assert.IsNotNull(root);

            Assert.AreEqual(7f, SimpleJson.GetNumber(root, "scanId", -1f), 1e-4f);
            Assert.IsFalse(SimpleJson.GetBool(root, "scanning", true));
            Assert.AreEqual(50f, SimpleJson.GetNumber(root, "settings.known", -1f), 1e-4f);

            List<object> transmitters = SimpleJson.GetList(root, "tx");
            Assert.AreEqual(2, transmitters.Count);
            Assert.AreEqual(3342f, SimpleJson.GetNumber(transmitters[0], "tof", -1f), 1e-3f);

            // A missing echo is reported as null and must fall through to the default.
            Assert.AreEqual(-1f, SimpleJson.GetNumber(transmitters[1], "tof", -1f), 1e-4f);
            Assert.AreEqual(-1f, SimpleJson.GetNumber(root, "settings.missing", -1f), 1e-4f);
        }

        [Test]
        public void JsonParserReturnsNullOnMalformedInput()
        {
            Assert.IsNull(SimpleJson.Parse("{\"a\":"));
            Assert.IsNull(SimpleJson.Parse("not json"));
            Assert.IsNull(SimpleJson.Parse(""));
        }

        [Test]
        public void LinkRecognisesTheSequentialScannerFirmware()
        {
            var link = new Esp32Link();
            const string json = "{\"scanId\":3,\"scanning\":false,\"continuous\":false," +
                                "\"settings\":{\"known\":50.00,\"burst\":8,\"sound\":0.034300,\"offset\":0}," +
                                "\"summary\":{\"median\":62.68,\"medianTof\":3654.0,\"error\":12.68,\"detected\":6}," +
                                "\"tx\":[{\"id\":1,\"detected\":true,\"peak\":180,\"peakV\":0.58,\"tof\":3654," +
                                "\"rawDistance\":62.68,\"distance\":62.68}]}";

            Assert.IsTrue(link.Apply(json));
            Assert.AreEqual(FirmwareKind.SequentialScanner, link.State.firmware);
            Assert.AreEqual(3, link.State.scanId);
            Assert.AreEqual(6, link.State.detectedCount);
            Assert.AreEqual(12.68f, link.State.errorCm, 1e-3f);
            Assert.AreEqual(1, link.State.channels.Count);
            Assert.AreEqual(180f, link.State.channels[0].peakAdc, 1e-3f);
        }

        [Test]
        public void LinkRecognisesTheBeamSteeringFirmware()
        {
            var link = new Esp32Link();
            const string first = "{\"scanId\":1,\"settings\":{\"angle\":-5.0,\"burst\":32}," +
                                 "\"result\":{\"angle\":-5.0,\"detected\":true,\"peak\":140,\"peakV\":0.45," +
                                 "\"firstTof\":2900,\"rawDistance\":49.7,\"distance\":49.7}}";
            const string second = "{\"scanId\":2,\"settings\":{\"angle\":5.0,\"burst\":32}," +
                                  "\"result\":{\"angle\":5.0,\"detected\":true,\"peak\":95,\"peakV\":0.31," +
                                  "\"firstTof\":2900,\"rawDistance\":49.7,\"distance\":49.7}}";

            Assert.IsTrue(link.Apply(first));
            Assert.IsTrue(link.Apply(second));

            Assert.AreEqual(FirmwareKind.BeamSteerer, link.State.firmware);
            Assert.AreEqual(2, link.State.angularProfile.Count, "a sweep accumulates one entry per angle");
            Assert.AreEqual(140f, link.State.angularProfile[-5f], 1e-3f);
            Assert.AreEqual(95f, link.State.angularProfile[5f], 1e-3f);
        }

        [Test]
        public void LinkBuildsUsableUrls()
        {
            var link = new Esp32Link { host = "192.168.1.50" };
            Assert.AreEqual("http://192.168.1.50/api/data", link.BuildUrl("/api/data"));

            link.host = "http://ultrasonic.local/";
            Assert.AreEqual("http://ultrasonic.local/api/scan", link.BuildUrl("/api/scan"));
        }
    }
}
