using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Reader and writer for the <c>.uscan</c> container produced by
    /// <c>unity/Tools/bake_scan.py</c>. The format is little-endian:
    /// <code>
    /// "USCN" int32 version int32 frameCount int32 metaLength utf8 meta
    /// per frame: int32 metaLength utf8 meta int32 angleCount int32 binCount
    ///            float32 angles[] float32 ranges[] float32 amplitude[angle * bin]
    /// </code>
    /// Metadata is newline separated <c>key=value</c> text so no JSON dependency is needed.
    /// </summary>
    public static class UscanFile
    {
        public const int Version = 1;
        static readonly byte[] Magic = { (byte)'U', (byte)'S', (byte)'C', (byte)'N' };

        public static ScanLibrary Read(byte[] bytes, string sourceName = "")
        {
            if (bytes == null || bytes.Length < 16)
                throw new InvalidDataException("uscan: file is too short");

            using (var stream = new MemoryStream(bytes, false))
                return Read(stream, sourceName);
        }

        public static ScanLibrary Read(Stream stream, string sourceName = "")
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                byte[] magic = reader.ReadBytes(4);
                if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] ||
                    magic[2] != Magic[2] || magic[3] != Magic[3])
                    throw new InvalidDataException("uscan: bad magic, not a .uscan file");

                int version = reader.ReadInt32();
                if (version != Version)
                    throw new InvalidDataException("uscan: unsupported version " + version);

                int frameCount = reader.ReadInt32();
                if (frameCount < 0 || frameCount > 4096)
                    throw new InvalidDataException("uscan: implausible frame count " + frameCount);

                var globalMeta = ReadMeta(reader);
                var library = new ScanLibrary
                {
                    label = Lookup(globalMeta, "label", sourceName),
                    source = Lookup(globalMeta, "source", sourceName),
                };

                for (int i = 0; i < frameCount; i++)
                    library.frames.Add(ReadFrame(reader, library.source));

                return library;
            }
        }

        static ScanFrame ReadFrame(BinaryReader reader, string source)
        {
            var meta = ReadMeta(reader);

            int angleCount = reader.ReadInt32();
            int binCount = reader.ReadInt32();
            if (angleCount <= 0 || binCount <= 0 || (long)angleCount * binCount > 64L * 1024 * 1024)
                throw new InvalidDataException("uscan: implausible frame dimensions");

            var frame = ScanFrame.Create(angleCount, binCount);
            for (int i = 0; i < angleCount; i++) frame.angleDeg[i] = reader.ReadSingle();
            for (int i = 0; i < binCount; i++) frame.rangeCm[i] = reader.ReadSingle();
            for (int i = 0; i < frame.amplitude.Length; i++) frame.amplitude[i] = reader.ReadSingle();

            frame.scene = Lookup(meta, "scene", "");
            frame.txMode = Lookup(meta, "tx_mode", "");
            frame.label = Lookup(meta, "label", "");
            frame.unit = Lookup(meta, "unit", "envelope_adc");
            frame.source = source;
            return frame;
        }

        static Dictionary<string, string> ReadMeta(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > 1 << 20)
                throw new InvalidDataException("uscan: implausible metadata length");

            var map = new Dictionary<string, string>();
            if (length == 0) return map;

            string text = Encoding.UTF8.GetString(reader.ReadBytes(length));
            foreach (string line in text.Split('\n'))
            {
                int split = line.IndexOf('=');
                if (split <= 0) continue;
                map[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
            }
            return map;
        }

        static string Lookup(Dictionary<string, string> meta, string key, string fallback)
        {
            string value;
            return meta.TryGetValue(key, out value) ? value : fallback;
        }

        /// <summary>Writes a library back out, mainly so round trips can be tested.</summary>
        public static void Write(Stream stream, ScanLibrary library)
        {
            if (library == null) throw new ArgumentNullException("library");

            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(library.frames.Count);

                WriteMeta(writer, new[]
                {
                    new KeyValuePair<string, string>("label", library.label),
                    new KeyValuePair<string, string>("source", library.source),
                });

                foreach (var frame in library.frames)
                {
                    WriteMeta(writer, new[]
                    {
                        new KeyValuePair<string, string>("scene", frame.scene),
                        new KeyValuePair<string, string>("tx_mode", frame.txMode),
                        new KeyValuePair<string, string>("label", frame.label),
                        new KeyValuePair<string, string>("unit", frame.unit),
                    });

                    writer.Write(frame.AngleCount);
                    writer.Write(frame.BinCount);
                    for (int i = 0; i < frame.AngleCount; i++) writer.Write(frame.angleDeg[i]);
                    for (int i = 0; i < frame.BinCount; i++) writer.Write(frame.rangeCm[i]);
                    for (int i = 0; i < frame.amplitude.Length; i++) writer.Write(frame.amplitude[i]);
                }
            }
        }

        static void WriteMeta(BinaryWriter writer, IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var builder = new StringBuilder();
            foreach (var pair in pairs)
            {
                if (string.IsNullOrEmpty(pair.Value)) continue;
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(pair.Key).Append('=')
                       .Append(pair.Value.Replace("\n", " ").Replace("=", "-"));
            }

            byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public static string FormatSummary(ScanLibrary library)
        {
            if (library == null) return "no library";
            var builder = new StringBuilder();
            builder.AppendFormat(CultureInfo.InvariantCulture, "{0} ({1} frames)",
                                 library.label, library.frames.Count);
            return builder.ToString();
        }
    }
}
