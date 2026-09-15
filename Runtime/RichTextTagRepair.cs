// Mesh Text Baker — RichTextTagRepair.cs
// Repairs TMP rich-text tag scopes when text is sliced (page boundaries, overflow chains,
// clipping). Tracks the stack of open tags so a slice can be closed properly at its end
// (</b></color>) and reopened at the start of the next slice (<b><color=#hex>).
//
// Handles: normal open/close tags (<b>...</b>, <color=#hex>...</color>), the TMP color
// shorthand (<#RRGGBB> ... </color>), self-closing / void tags (<br>, <sprite=...>),
// and <noparse>...</noparse> regions (content is treated as plain text — not scanned
// for scopes). Markdown code spans `...` compile to noparse so demo tags stay literal.

using System.Collections.Generic;
using System.Text;

namespace MeshTextBaker
{
    /// <summary>One open rich-text tag: normalized name + the full original tag text.</summary>
    public struct OpenRichTag
    {
        public string name;     // normalized, lowercase ("color" for <#hex> shorthand)
        public string fullTag;  // e.g. "<color=#ff0000>" — used verbatim to reopen
    }

    public static class RichTextTagRepair
    {
        // Tags that never have a matching closing tag.
        private static readonly HashSet<string> VoidTags = new HashSet<string>
        {
            "br", "sprite", "space", "page", "newpage", "image", "bookmark", "pos",
            "alpha" // state switch, no closing tag in TMP
        };

        private const int MaxTagLength = 128;

        [System.ThreadStatic] private static List<OpenRichTag> s_Stack;
        [System.ThreadStatic] private static StringBuilder s_Sb;

        private static List<OpenRichTag> Stack
        {
            get { return s_Stack ?? (s_Stack = new List<OpenRichTag>(8)); }
        }

        private static StringBuilder Sb
        {
            get
            {
                if (s_Sb == null) s_Sb = new StringBuilder(128);
                s_Sb.Clear();
                return s_Sb;
            }
        }

        /// <summary>
        /// Scans <paramref name="text"/> in [start, end) and updates <paramref name="stack"/>
        /// with the tags still open at the end of the range.
        /// </summary>
        public static void ScanOpenTags(string text, int start, int end, List<OpenRichTag> stack)
        {
            if (string.IsNullOrEmpty(text)) return;
            end = System.Math.Min(end, text.Length);

            bool inNoparse = false;

            for (int i = System.Math.Max(0, start); i < end; i++)
            {
                if (text[i] != '<') continue;

                int close = text.IndexOf('>', i + 1);
                if (close < 0 || close >= end || close - i > MaxTagLength) continue;

                // <noparse> ... </noparse>: skip ALL tag parsing inside (demo/docs text).
                if (MatchTagAt(text, i, close, "noparse", closing: false))
                {
                    inNoparse = true;
                    i = close;
                    continue;
                }
                if (MatchTagAt(text, i, close, "noparse", closing: true))
                {
                    inNoparse = false;
                    i = close;
                    continue;
                }
                if (inNoparse) { i = close; continue; }

                bool closing = i + 1 < close && text[i + 1] == '/';
                int nameStart = closing ? i + 2 : i + 1;

                string name = ParseTagName(text, nameStart, close);
                if (string.IsNullOrEmpty(name)) { i = close; continue; }

                if (closing)
                {
                    // Pop the innermost matching open tag.
                    for (int k = stack.Count - 1; k >= 0; k--)
                    {
                        if (stack[k].name == name) { stack.RemoveAt(k); break; }
                    }
                }
                else if (!VoidTags.Contains(name))
                {
                    string fullTag = text.Substring(i, close - i + 1);
                    // Bare "<color>" without attributes is almost always a docs example.
                    if (IsAttributeLessExampleTag(name, fullTag))
                    {
                        i = close;
                        continue;
                    }
                    stack.Add(new OpenRichTag
                    {
                        name = name,
                        fullTag = fullTag
                    });
                }

                i = close;
            }
        }

        private static bool MatchTagAt(string text, int lt, int gt, string name, bool closing)
        {
            int p = lt + 1;
            if (closing)
            {
                if (p >= gt || text[p] != '/') return false;
                p++;
            }
            if (p + name.Length > gt) return false;
            for (int k = 0; k < name.Length; k++)
            {
                if (char.ToLowerInvariant(text[p + k]) != name[k]) return false;
            }
            int after = p + name.Length;
            // name must end at '>' or whitespace / attributes
            if (after == gt) return true;
            char c = text[after];
            return c == ' ' || c == '\t' || c == '/' || c == '=';
        }


        /// <summary>Closing tags for the stack, innermost first: "&lt;/b&gt;&lt;/color&gt;".</summary>
        public static string CloseTags(List<OpenRichTag> stack)
        {
            if (stack == null || stack.Count == 0) return "";
            var sb = Sb;
            for (int i = stack.Count - 1; i >= 0; i--)
                sb.Append("</").Append(stack[i].name).Append('>');
            return sb.ToString();
        }

        /// <summary>Reopening tags in original order: "&lt;b&gt;&lt;color=#hex&gt;".</summary>
        public static string ReopenTags(List<OpenRichTag> stack)
        {
            if (stack == null || stack.Count == 0) return "";
            var sb = Sb;
            for (int i = 0; i < stack.Count; i++)
                sb.Append(stack[i].fullTag);
            return sb.ToString();
        }

        /// <summary>
        /// Prefix that reopens all tags still open at <paramref name="position"/> in
        /// <paramref name="text"/> (scans [0, position)). Empty string if none.
        /// </summary>
        public static string GetReopenPrefix(string text, int position)
        {
            if (string.IsNullOrEmpty(text) || position <= 0) return "";
            var stack = Stack;
            stack.Clear();
            ScanOpenTags(text, 0, position, stack);
            return stack.Count == 0 ? "" : ReopenTags(stack);
        }

        /// <summary>Suffix that closes all tags left open in <paramref name="text"/>.</summary>
        public static string GetClosingSuffix(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var stack = Stack;
            stack.Clear();
            ScanOpenTags(text, 0, text.Length, stack);
            return stack.Count == 0 ? "" : CloseTags(stack);
        }

        /// <summary>
        /// Splits rich text at <paramref name="index"/> and repairs the boundary:
        /// <paramref name="head"/> ends with closing tags for its open scopes, and
        /// <paramref name="tail"/> starts with the same scopes reopened.
        /// </summary>
        public static void Split(string text, int index, out string head, out string tail)
        {
            if (string.IsNullOrEmpty(text)) { head = ""; tail = ""; return; }
            index = UnityEngine.Mathf.Clamp(index, 0, text.Length);

            var stack = Stack;
            stack.Clear();
            ScanOpenTags(text, 0, index, stack);

            string headRaw = text.Substring(0, index);
            string tailRaw = index < text.Length ? text.Substring(index) : "";

            if (stack.Count == 0)
            {
                head = headRaw;
                tail = tailRaw;
                return;
            }

            head = headRaw + CloseTags(stack);
            tail = string.IsNullOrEmpty(tailRaw) ? "" : ReopenTags(stack) + tailRaw;
        }

        /// <summary>
        /// Tags that require attributes to be meaningful. A bare &lt;color&gt; with no
        /// value is treated as non-scope (docs/examples), not as an open rich-text span.
        /// </summary>
        private static bool IsAttributeLessExampleTag(string name, string fullTag)
        {
            if (name != "color" && name != "font" && name != "size" && name != "align" &&
                name != "material" && name != "sprite" && name != "link")
                return false;
            // Real tags look like <color=#ff0>, <color=red>, <size=120%>, <#rrggbb>…
            // Bare <color> / <size> with only optional spaces before '>' is an example.
            int lt = fullTag.IndexOf('<');
            int gt = fullTag.LastIndexOf('>');
            if (lt < 0 || gt <= lt) return false;
            string inner = fullTag.Substring(lt + 1, gt - lt - 1).Trim();
            if (inner.StartsWith("/")) return false;
            // TMP shorthand starts with the value itself (<#RGB>, <#RRGGBB>, ...), not
            // the normalized parsed name "color". It is always a real opening scope.
            if (inner.StartsWith("#")) return false;
            // strip name
            if (inner.Length == name.Length) return true; // exact "color"
            if (inner.Length > name.Length)
            {
                char c = inner[name.Length];
                // attributes begin with '=' or space then key — those are real tags
                if (c == '=' || c == ' ' || c == '#') return false;
            }
            return true;
        }

        private static string ParseTagName(string text, int start, int end)
        {
            if (start >= end) return null;

            // TMP color shorthand: <#RRGGBB> (closes with </color>).
            if (text[start] == '#') return "color";

            int q = start;
            while (q < end)
            {
                char c = text[q];
                if (char.IsLetter(c) || c == '-') q++;
                else break;
            }
            if (q == start) return null;

            // Attribute or value may follow (=, space); that's fine — name is enough.
            return text.Substring(start, q - start).ToLowerInvariant();
        }
    }
}
