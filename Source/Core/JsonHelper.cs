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

        /// <summary>
        /// Index of the last occurrence of <paramref name="key"/> used as a key at the
        /// TOP LEVEL of the message object (brace depth 1), skipping nested
        /// objects/arrays and string contents. Returns -1 if there is none.
        ///
        /// SECURITY: identity fields (username, source, adminCommand) are read with a
        /// last-occurrence scan. The old scan tracked no brace depth, so a key the
        /// viewer nested inside their own sub-object could win the LastIndexOf and
        /// impersonate another viewer or claim admin. The relay now pins those keys
        /// last, but the host must not DEPEND on the relay's key order — the embedded
        /// local server accepts viewer JSON directly, and an older relay may be running.
        /// </summary>
        private static int FindLastTopLevelKey(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
                return -1;

            string search = "\"" + key + "\"";
            int depth = 0;
            int found = -1;
            bool inString = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    if (c == '\\') { i++; continue; }   // skip the escaped char
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"')
                {
                    // A key only counts at depth 1 and only when a ':' follows the
                    // closing quote — otherwise it is a VALUE that happens to match.
                    if (depth == 1 && string.CompareOrdinal(json, i, search, 0, search.Length) == 0)
                    {
                        int after = i + search.Length;
                        while (after < json.Length && char.IsWhiteSpace(json[after]))
                            after++;
                        if (after < json.Length && json[after] == ':')
                        {
                            found = i;
                            i = after;                  // resume past the key
                            continue;
                        }
                    }
                    inString = true;
                    continue;
                }

                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }

            return found;
        }

        public static string ExtractLastString(string json, string key)
        {
            int keyIdx = FindLastTopLevelKey(json, key);
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
            // Top-level only — this is the path that reads adminCommand, so a nested
            // {"adminCommand":true} must never win. See FindLastTopLevelKey.
            int keyIdx = FindLastTopLevelKey(json, key);
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
