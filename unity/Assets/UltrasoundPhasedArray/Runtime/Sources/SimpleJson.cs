using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Minimal JSON reader for the ESP32 dashboard responses.
    /// UnityEngine.JsonUtility cannot represent the firmware's <c>null</c> numeric fields
    /// (an undetected echo reports <c>"tof":null</c>), so the payload is parsed by hand into
    /// dictionaries and looked up by path.
    /// </summary>
    public static class SimpleJson
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int index = 0;
            try
            {
                object value = ParseValue(text, ref index);
                return value;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>Looks up a dotted path such as <c>settings.angle</c>.</summary>
        public static object Select(object root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return root;

            object current = root;
            foreach (string key in path.Split('.'))
            {
                var map = current as Dictionary<string, object>;
                if (map == null || !map.TryGetValue(key, out current)) return null;
            }
            return current;
        }

        public static bool TryGetNumber(object root, string path, out float value)
        {
            value = 0f;
            object node = Select(root, path);
            if (node is double) { value = (float)(double)node; return true; }
            if (node is bool) { value = (bool)node ? 1f : 0f; return true; }

            var text = node as string;
            if (text != null)
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

            return false;
        }

        public static float GetNumber(object root, string path, float fallback)
        {
            float value;
            return TryGetNumber(root, path, out value) ? value : fallback;
        }

        public static bool GetBool(object root, string path, bool fallback)
        {
            object node = Select(root, path);
            if (node is bool) return (bool)node;
            if (node is double) return (double)node != 0.0;
            return fallback;
        }

        public static string GetString(object root, string path, string fallback)
        {
            object node = Select(root, path);
            if (node is string) return (string)node;
            if (node is double) return ((double)node).ToString(CultureInfo.InvariantCulture);
            if (node is bool) return (bool)node ? "true" : "false";
            return fallback;
        }

        public static List<object> GetList(object root, string path)
        {
            return Select(root, path) as List<object>;
        }

        // ------------------------------------------------------------------

        static object ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new FormatException("unexpected end of JSON");

            char c = text[index];
            switch (c)
            {
                case '{': return ParseObject(text, ref index);
                case '[': return ParseArray(text, ref index);
                case '"': return ParseString(text, ref index);
                case 't': Expect(text, ref index, "true"); return true;
                case 'f': Expect(text, ref index, "false"); return false;
                case 'n': Expect(text, ref index, "null"); return null;
                default: return ParseNumber(text, ref index);
            }
        }

        static Dictionary<string, object> ParseObject(string text, ref int index)
        {
            var map = new Dictionary<string, object>();
            index++; // '{'
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == '}') { index++; return map; }

            while (index < text.Length)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"') throw new FormatException("expected key");
                string key = ParseString(text, ref index);

                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':') throw new FormatException("expected ':'");
                index++;

                map[key] = ParseValue(text, ref index);

                SkipWhitespace(text, ref index);
                if (index >= text.Length) break;
                if (text[index] == ',') { index++; continue; }
                if (text[index] == '}') { index++; return map; }
                throw new FormatException("expected ',' or '}'");
            }

            throw new FormatException("unterminated object");
        }

        static List<object> ParseArray(string text, ref int index)
        {
            var list = new List<object>();
            index++; // '['
            SkipWhitespace(text, ref index);

            if (index < text.Length && text[index] == ']') { index++; return list; }

            while (index < text.Length)
            {
                list.Add(ParseValue(text, ref index));

                SkipWhitespace(text, ref index);
                if (index >= text.Length) break;
                if (text[index] == ',') { index++; continue; }
                if (text[index] == ']') { index++; return list; }
                throw new FormatException("expected ',' or ']'");
            }

            throw new FormatException("unterminated array");
        }

        static string ParseString(string text, ref int index)
        {
            var builder = new StringBuilder();
            index++; // opening quote

            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return builder.ToString();

                if (c != '\\') { builder.Append(c); continue; }
                if (index >= text.Length) break;

                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > text.Length) throw new FormatException("bad \\u escape");
                        builder.Append((char)Convert.ToInt32(text.Substring(index, 4), 16));
                        index += 4;
                        break;
                    default: throw new FormatException("bad escape");
                }
            }

            throw new FormatException("unterminated string");
        }

        static object ParseNumber(string text, ref int index)
        {
            int start = index;
            // Letters are consumed too so an Arduino "inf" or "nan" literal is swallowed
            // whole rather than leaving the parser stuck on a stray character.
            while (index < text.Length &&
                   (char.IsLetterOrDigit(text[index]) || "+-.".IndexOf(text[index]) >= 0)) index++;

            string slice = text.Substring(start, index - start);
            double value;
            if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                // The firmware prints NaN and inf directly for undetected results.
                if (slice.IndexOf("nan", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                if (slice.IndexOf("inf", StringComparison.OrdinalIgnoreCase) >= 0) return null;
                throw new FormatException("bad number '" + slice + "'");
            }
            return value;
        }

        static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                throw new FormatException("expected '" + literal + "'");
            index += literal.Length;
        }

        static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }
    }
}
