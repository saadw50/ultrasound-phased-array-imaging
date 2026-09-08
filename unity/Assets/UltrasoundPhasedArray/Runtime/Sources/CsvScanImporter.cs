using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Imports the angle/range CSV exports under <c>results/distance/</c> directly, so a
    /// session can be inspected without running the Python bake step first.
    /// </summary>
    /// <remarks>
    /// The exports store one row per (capture, range bin). Rows are grouped by
    /// (scene, tx_mode) and the <c>envelope_adc</c> column is averaged over every repeat of a
    /// given steering angle, which is exactly what <c>unity/Tools/bake_scan.py</c> does.
    /// The largest export is over half a million rows, so the work runs on a background
    /// thread and reports progress.
    /// </remarks>
    public sealed class CsvScanImporter
    {
        /// <summary>Drop range bins beyond this distance. Zero keeps every bin.</summary>
        public float maxRangeCm;

        public volatile bool IsDone;
        public volatile string Status = "idle";

        public ScanLibrary Result { get; private set; }
        public Exception Error { get; private set; }

        float _progress;
        volatile bool _cancelled;

        public float Progress { get { return _progress; } }

        public void Cancel() { _cancelled = true; }

        /// <summary>Parses on a background thread. Poll <see cref="IsDone"/> from a coroutine.</summary>
        public void BeginImport(string path)
        {
            IsDone = false;
            Error = null;
            Result = null;
            _progress = 0f;
            _cancelled = false;
            Status = "reading " + Path.GetFileName(path);

            var thread = new Thread(() =>
            {
                try
                {
                    Result = Import(path);
                    Status = Result != null
                        ? string.Format(CultureInfo.InvariantCulture, "loaded {0} frames", Result.frames.Count)
                        : "cancelled";
                }
                catch (Exception exception)
                {
                    Error = exception;
                    Status = "error: " + exception.Message;
                }
                finally
                {
                    IsDone = true;
                }
            });

            thread.IsBackground = true;
            thread.Name = "CsvScanImporter";
            thread.Start();
        }

        /// <summary>Synchronous import, also used by the edit-mode tests.</summary>
        public ScanLibrary Import(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream))
            {
                long length = stream.Length;
                var library = Import(reader, Path.GetFileName(path),
                                     () => length > 0 ? (float)stream.Position / length : 0f);
                return library;
            }
        }

        /// <summary>Core parser. <paramref name="progress"/> may be null.</summary>
        public ScanLibrary Import(TextReader reader, string sourceName, Func<float> progress)
        {
            string header = reader.ReadLine();
            if (header == null) throw new InvalidDataException("empty CSV");

            var columns = IndexColumns(header);
            int sceneColumn = Require(columns, "scene", sourceName);
            int modeColumn = Require(columns, "tx_mode", sourceName);
            int angleColumn = Require(columns, "angle_deg", sourceName);
            int binColumn = Require(columns, "bin_index", sourceName);
            int rangeColumn = Require(columns, "range_cm", sourceName);
            int valueColumn = Require(columns, "envelope_adc", sourceName);

            int widest = Math.Max(Math.Max(sceneColumn, modeColumn),
                         Math.Max(Math.Max(angleColumn, binColumn),
                                  Math.Max(rangeColumn, valueColumn)));

            var groups = new Dictionary<string, Accumulator>();
            var binRange = new Dictionary<int, float>();

            string line;
            long rows = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if (_cancelled) return null;

                if ((++rows & 0x3FFF) == 0)
                {
                    if (progress != null) _progress = Math.Min(0.98f, progress());
                    Status = string.Format(CultureInfo.InvariantCulture, "parsed {0:N0} rows", rows);
                }

                if (line.Length == 0) continue;
                string[] fields = line.Split(',');
                if (fields.Length <= widest) continue;

                float angle, range, value;
                int bin;
                if (!TryParseFloat(fields[angleColumn], out angle)) continue;
                if (!TryParseFloat(fields[rangeColumn], out range)) continue;
                if (!TryParseFloat(fields[valueColumn], out value)) continue;
                if (!int.TryParse(fields[binColumn], NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out bin)) continue;

                if (maxRangeCm > 0f && range > maxRangeCm) continue;

                binRange[bin] = range;

                string key = fields[sceneColumn] + "|" + fields[modeColumn];
                Accumulator accumulator;
                if (!groups.TryGetValue(key, out accumulator))
                {
                    accumulator = new Accumulator
                    {
                        scene = fields[sceneColumn],
                        txMode = fields[modeColumn],
                    };
                    groups[key] = accumulator;
                }

                accumulator.Add(angle, bin, value);
            }

            _progress = 1f;
            return Build(groups, binRange, sourceName);
        }

        static ScanLibrary Build(Dictionary<string, Accumulator> groups,
                                 Dictionary<int, float> binRange, string sourceName)
        {
            var bins = new List<int>(binRange.Keys);
            bins.Sort();

            var binSlot = new Dictionary<int, int>(bins.Count);
            for (int i = 0; i < bins.Count; i++) binSlot[bins[i]] = i;

            var library = new ScanLibrary { label = sourceName, source = sourceName };

            var keys = new List<string>(groups.Keys);
            keys.Sort(StringComparer.Ordinal);

            foreach (string key in keys)
            {
                Accumulator accumulator = groups[key];
                var angles = new List<int>(accumulator.angleKeys);
                angles.Sort();
                if (angles.Count == 0 || bins.Count == 0) continue;

                var frame = ScanFrame.Create(angles.Count, bins.Count);
                for (int i = 0; i < bins.Count; i++) frame.rangeCm[i] = binRange[bins[i]];

                for (int a = 0; a < angles.Count; a++)
                {
                    frame.angleDeg[a] = angles[a] / 1000f;
                    for (int b = 0; b < bins.Count; b++)
                    {
                        double sum; int count;
                        if (!accumulator.TryGet(angles[a], bins[b], out sum, out count) || count == 0)
                            continue;
                        frame[a, b] = (float)(sum / count);
                    }
                }

                frame.scene = accumulator.scene;
                frame.txMode = accumulator.txMode;
                frame.source = sourceName;
                frame.unit = "envelope_adc_counts_mean";
                frame.label = string.Format("{0} - {1} {2}", sourceName, frame.scene, frame.txMode);
                library.frames.Add(frame);
            }

            return library;
        }

        static Dictionary<string, int> IndexColumns(string header)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string[] names = header.Split(',');
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i].Trim().Trim('\uFEFF');
                if (name.Length > 0 && !map.ContainsKey(name)) map[name] = i;
            }
            return map;
        }

        static int Require(Dictionary<string, int> columns, string name, string source)
        {
            int index;
            if (!columns.TryGetValue(name, out index))
                throw new InvalidDataException(source + ": missing column '" + name + "'");
            return index;
        }

        static bool TryParseFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Running sum and count per (angle, range bin) for one scene and tx mode.</summary>
        sealed class Accumulator
        {
            public string scene = "";
            public string txMode = "";

            readonly Dictionary<long, double> _sums = new Dictionary<long, double>();
            readonly Dictionary<long, int> _counts = new Dictionary<long, int>();
            readonly HashSet<int> _angleKeys = new HashSet<int>();

            public IEnumerable<int> angleKeys { get { return _angleKeys; } }

            public void Add(float angleDeg, int bin, float value)
            {
                int angleKey = AngleKey(angleDeg);
                _angleKeys.Add(angleKey);

                long key = Pack(angleKey, bin);
                double sum;
                _sums[key] = _sums.TryGetValue(key, out sum) ? sum + value : value;

                int count;
                _counts[key] = _counts.TryGetValue(key, out count) ? count + 1 : 1;
            }

            public bool TryGet(int angleKey, int bin, out double sum, out int count)
            {
                long key = Pack(angleKey, bin);
                count = 0;
                return _sums.TryGetValue(key, out sum) & _counts.TryGetValue(key, out count);
            }

            // Angles are held in millidegrees so float noise never splits one steering step
            // into two rows.
            static int AngleKey(float angleDeg)
            {
                return (int)Math.Round(angleDeg * 1000.0);
            }

            // The bin index occupies the low 24 bits, which the shifted angle never touches.
            static long Pack(int angleKey, int bin)
            {
                return ((long)angleKey << 24) | (uint)(bin & 0xFFFFFF);
            }
        }
    }
}
