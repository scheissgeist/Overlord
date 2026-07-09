using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Overlord
{
    /// <summary>
    /// Manual JSON parser/serializer. No external dependencies.
    /// </summary>
    public static class JsonHelper
    {
        // --- Extraction (parse values from JSON string) ---

        public static string ExtractString(string json, string key)
        {
            string search = $"\"{key}\"";
            int keyIdx = 0;
            while (true)
            {
                keyIdx = json.IndexOf(search, keyIdx);
                if (keyIdx < 0) return null;
                // Ensure this is a standalone key (preceded by { , or whitespace, not another letter)
                if (keyIdx == 0 || json[keyIdx - 1] == '{' || json[keyIdx - 1] == ',' || json[keyIdx - 1] == ' ' || json[keyIdx - 1] == '\n' || json[keyIdx - 1] == '\t')
                    break;
                keyIdx += search.Length;
            }

            int colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
                start++;

            if (start >= json.Length || json[start] != '"')
                return null;

            // Handle escaped quotes
            var sb = new StringBuilder();
            int i = start + 1;
            while (i < json.Length)
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(next); break;
                    }
                    i += 2;
                }
                else if (json[i] == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(json[i]);
                    i++;
                }
            }

            return null;
        }

        public static string ExtractLastString(string json, string key)
        {
            string search = $"\"{key}\"";
            int keyIdx = json.LastIndexOf(search, StringComparison.Ordinal);
            while (keyIdx >= 0)
            {
                if (keyIdx == 0 || json[keyIdx - 1] == '{' || json[keyIdx - 1] == ',' || json[keyIdx - 1] == ' ' || json[keyIdx - 1] == '\n' || json[keyIdx - 1] == '\t')
                    break;
                keyIdx = json.LastIndexOf(search, keyIdx - 1, StringComparison.Ordinal);
            }

            if (keyIdx < 0)
                return null;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
                start++;

            if (start >= json.Length || json[start] != '"')
                return null;

            var sb = new StringBuilder();
            int i = start + 1;
            while (i < json.Length)
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(next); break;
                    }
                    i += 2;
                }
                else if (json[i] == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(json[i]);
                    i++;
                }
            }

            return null;
        }

        public static float ExtractFloat(string json, string key, float defaultValue = 0f)
        {
            string raw = ExtractRawValue(json, key);
            if (raw != null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                return result;
            return defaultValue;
        }

        public static int ExtractInt(string json, string key, int defaultValue = 0)
        {
            string raw = ExtractRawValue(json, key);
            if (raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                return result;
            return defaultValue;
        }

        public static bool ExtractBool(string json, string key, bool defaultValue = false)
        {
            string raw = ExtractRawValue(json, key);
            if (raw == "true") return true;
            if (raw == "false") return false;
            return defaultValue;
        }

        public static bool ExtractLastBool(string json, string key, bool defaultValue = false)
        {
            string raw = ExtractLastRawValue(json, key);
            if (raw == "true") return true;
            if (raw == "false") return false;
            return defaultValue;
        }

        private static string ExtractRawValue(string json, string key)
        {
            string search = $"\"{key}\"";
            int keyIdx = 0;
            while (true)
            {
                keyIdx = json.IndexOf(search, keyIdx);
                if (keyIdx < 0) return null;
                if (keyIdx == 0 || json[keyIdx - 1] == '{' || json[keyIdx - 1] == ',' || json[keyIdx - 1] == ' ' || json[keyIdx - 1] == '\n' || json[keyIdx - 1] == '\t')
                    break;
                keyIdx += search.Length;
            }

            int colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
                start++;

            if (start >= json.Length) return null;

            // String value — delegate to ExtractString
            if (json[start] == '"')
                return null;

            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' && !char.IsWhiteSpace(json[end]))
                end++;

            if (end > start)
                return json.Substring(start, end - start);

            return null;
        }

        private static string ExtractLastRawValue(string json, string key)
        {
            string search = $"\"{key}\"";
            int keyIdx = json.LastIndexOf(search, StringComparison.Ordinal);
            while (keyIdx >= 0)
            {
                if (keyIdx == 0 || json[keyIdx - 1] == '{' || json[keyIdx - 1] == ',' || json[keyIdx - 1] == ' ' || json[keyIdx - 1] == '\n' || json[keyIdx - 1] == '\t')
                    break;
                keyIdx = json.LastIndexOf(search, keyIdx - 1, StringComparison.Ordinal);
            }

            if (keyIdx < 0)
                return null;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
                start++;

            if (start >= json.Length) return null;
            if (json[start] == '"') return null;

            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' && !char.IsWhiteSpace(json[end]))
                end++;

            if (end > start)
                return json.Substring(start, end - start);

            return null;
        }

        // --- Raw JSON wrapper (for nested pre-serialized JSON) ---

        /// <summary>
        /// Wraps a pre-serialized JSON string so ToJson embeds it raw
        /// instead of escaping it as a quoted string.
        /// </summary>
        public class RawJson
        {
            public readonly string Json;
            public RawJson(string json) { Json = json; }
        }

        // --- Serialization (build JSON strings) ---

        public static string Escape(string text)
        {
            if (text == null) return "";
            return text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        public static string ToJson(Dictionary<string, object> dict)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kvp in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"');
                sb.Append(Escape(kvp.Key));
                sb.Append("\":");
                AppendValue(sb, kvp.Value);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
            }
            else if (value is RawJson raw)
            {
                sb.Append(raw.Json);
            }
            else if (value is string s)
            {
                sb.Append('"');
                sb.Append(Escape(s));
                sb.Append('"');
            }
            else if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
            }
            else if (value is int i)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
            }
            else if (value is float f)
            {
                sb.Append(f.ToString("G", CultureInfo.InvariantCulture));
            }
            else if (value is double d)
            {
                sb.Append(d.ToString("G", CultureInfo.InvariantCulture));
            }
            else if (value is Dictionary<string, object> dict)
            {
                sb.Append(ToJson(dict));
            }
            else if (value is List<object> list)
            {
                sb.Append('[');
                for (int idx = 0; idx < list.Count; idx++)
                {
                    if (idx > 0) sb.Append(',');
                    AppendValue(sb, list[idx]);
                }
                sb.Append(']');
            }
            else if (value is List<Dictionary<string, object>> dictList)
            {
                sb.Append('[');
                for (int idx = 0; idx < dictList.Count; idx++)
                {
                    if (idx > 0) sb.Append(',');
                    sb.Append(ToJson(dictList[idx]));
                }
                sb.Append(']');
            }
            else
            {
                sb.Append('"');
                sb.Append(Escape(value.ToString()));
                sb.Append('"');
            }
        }
    }
}
