// Mesh Text Baker — BookmarkUtility.cs
// <bookmark=id> markers in book text: zero-width for layout, collectible for navigation.
//
// Syntax (any of):
//   <bookmark=chapter1>
//   <bookmark=chapter1 />
//   <bookmark id="chapter1">
//   <bookmark name="chapter1">
//
// Process() strips the tags from the display string (so TMP never draws them) and
// returns a map id → character index in the STRIPPED string (insertion point).

using System;
using System.Collections.Generic;
using System.Text;

namespace MeshTextBaker
{
    public static class BookmarkUtility
    {
        public const string TagName = "bookmark";

        [ThreadStatic] private static StringBuilder s_Sb;

        public static bool HasBookmarkTags(string text)
            => !string.IsNullOrEmpty(text) &&
               text.IndexOf("<bookmark", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Removes all &lt;bookmark…&gt; tags from <paramref name="text"/>.
        /// When <paramref name="map"/> is non-null, records each id → char index
        /// in the returned (stripped) string.
        /// </summary>
        public static string Process(string text, Dictionary<string, int> map)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (!HasBookmarkTags(text)) return text;

            if (s_Sb == null) s_Sb = new StringBuilder(text.Length);
            s_Sb.Clear();
            var sb = s_Sb;

            int i = 0;
            int len = text.Length;
            bool inNoparse = false;
            while (i < len)
            {
                // Skip bookmark extraction inside <noparse>…</noparse>
                if (!inNoparse && text[i] == '<' && MatchAt(text, i, "<noparse>"))
                {
                    inNoparse = true;
                    // copy the opening tag verbatim
                    int gt = text.IndexOf('>', i);
                    if (gt < 0) gt = i + "<noparse>".Length - 1;
                    sb.Append(text, i, gt - i + 1);
                    i = gt + 1;
                    continue;
                }
                if (inNoparse && text[i] == '<' && MatchAt(text, i, "</noparse>"))
                {
                    int gt = text.IndexOf('>', i);
                    if (gt < 0) gt = i + "</noparse>".Length - 1;
                    sb.Append(text, i, gt - i + 1);
                    i = gt + 1;
                    inNoparse = false;
                    continue;
                }
                if (inNoparse)
                {
                    sb.Append(text[i]);
                    i++;
                    continue;
                }

                if (text[i] == '<' && MatchAt(text, i, "<bookmark"))
                {
                    int gt = text.IndexOf('>', i);
                    if (gt > i && gt - i < 96)
                    {
                        string tag = text.Substring(i, gt - i + 1);
                        if (TryParseId(tag, out string id) && map != null && !map.ContainsKey(id))
                            map[id] = sb.Length; // insertion point in stripped text
                        i = gt + 1;
                        continue;
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            return sb.ToString();
        }

        public static bool TryParseId(string tag, out string id)
        {
            id = null;
            if (string.IsNullOrEmpty(tag)) return false;
            int eq = tag.IndexOf('=');
            if (eq < 0) return false;
            string rest = tag.Substring(eq + 1).Trim().TrimEnd('>').Trim();
            // <bookmark id="x"> → after first '=' we may get 'id="x"' if tag is weird;
            // standard forms: <bookmark=x> or <bookmark id="x"> (first '=' after id/name key)
            // For <bookmark id="x">, eq points at id's '=', rest = "\"x\"" or "\"x\" /"
            // For <bookmark=x>, rest = "x" or "x /"
            if (rest.StartsWith("\"") || rest.StartsWith("'"))
            {
                char q = rest[0];
                int end = rest.IndexOf(q, 1);
                if (end > 1) id = rest.Substring(1, end - 1);
            }
            else
            {
                // strip trailing junk
                int cut = -1;
                for (int k = 0; k < rest.Length; k++)
                {
                    char c = rest[k];
                    if (c == ' ' || c == '/' || c == '>') { cut = k; break; }
                }
                id = (cut >= 0 ? rest.Substring(0, cut) : rest).Trim();
                // <bookmark id=x> without quotes → rest is "x" already if first eq was id's
            }
            if (string.IsNullOrEmpty(id)) return false;
            // sanitize: no spaces
            if (id.IndexOf(' ') >= 0) id = id.Replace(" ", "");
            return id.Length > 0;
        }

        private static bool MatchAt(string text, int pos, string token)
        {
            if (pos + token.Length > text.Length) return false;
            for (int k = 0; k < token.Length; k++)
            {
                if (char.ToLowerInvariant(text[pos + k]) != char.ToLowerInvariant(token[k]))
                    return false;
            }
            return true;
        }
    }
}
