// Mesh Text Baker — FlatJsonLocalizationParser.cs
// Dependency-free parser for flat { "key": "value" } localization files.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MeshTextBaker
{
    internal static class FlatJsonLocalizationParser
    {
        public static bool TryParse(string json, Dictionary<string, string> output, out string error)
        {
            error = null;
            if (output == null) { error = "Output dictionary is null."; return false; }
            if (string.IsNullOrWhiteSpace(json)) return true;

            // Parse into a copy and commit only after the entire input is valid. Callers never
            // observe a half-populated catalog when a later key or trailing token is malformed.
            var parsed = new Dictionary<string, string>(output, output.Comparer);
            int i = 0;
            SkipWs(json, ref i);
            if (!Take(json, ref i, '{')) { error = "Expected '{'."; return false; }
            SkipWs(json, ref i);
            if (Take(json, ref i, '}'))
                return Finish(json, ref i, parsed, output, out error);

            while (i < json.Length)
            {
                if (!TryReadString(json, ref i, out string key, out error)) return false;
                SkipWs(json, ref i);
                if (!Take(json, ref i, ':')) { error = $"Expected ':' after '{key}'."; return false; }
                SkipWs(json, ref i);
                if (!TryReadString(json, ref i, out string value, out error))
                {
                    error = $"Value for '{key}' must be a JSON string. {error}";
                    return false;
                }
                parsed[key] = value;
                SkipWs(json, ref i);
                if (Take(json, ref i, '}'))
                    return Finish(json, ref i, parsed, output, out error);
                if (!Take(json, ref i, ',')) { error = "Expected ',' or '}'."; return false; }
                SkipWs(json, ref i);
            }
            error = "Unexpected end of JSON.";
            return false;
        }

        private static bool Finish(string json, ref int i, Dictionary<string, string> parsed,
            Dictionary<string, string> output, out string error)
        {
            SkipWs(json, ref i);
            if (i != json.Length)
            {
                error = "Unexpected content after the JSON object.";
                return false;
            }

            output.Clear();
            foreach (var pair in parsed) output[pair.Key] = pair.Value;
            error = null;
            return true;
        }

        private static bool TryReadString(string s, ref int i, out string value, out string error)
        {
            value = null; error = null;
            SkipWs(s, ref i);
            if (!Take(s, ref i, '"')) { error = "Expected string."; return false; }
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') { value = sb.ToString(); return true; }
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) { error = "Incomplete escape sequence."; return false; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length || !ushort.TryParse(s.Substring(i, 4),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                        { error = "Invalid unicode escape."; return false; }
                        sb.Append((char)code); i += 4; break;
                    default: error = $"Unsupported escape '\\{e}'."; return false;
                }
            }
            error = "Unterminated string.";
            return false;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static bool Take(string s, ref int i, char expected)
        {
            if (i >= s.Length || s[i] != expected) return false;
            i++; return true;
        }
    }
}
