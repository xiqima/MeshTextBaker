// Mesh Text Baker — MarkdownParser.cs
// Lightweight markdown → TMP rich text converter. No external libraries.
//
// Supported markdown:
//   **bold**        → <b>bold</b>          (may span line breaks)
//   *italic*        → <i>italic</i>        (may span line breaks)
//   `code`          → <noparse>code</noparse>  (literal tags/examples survive pagination)
//   # heading       → <size=150%><b>heading</b></size>\n
//   ## heading      → <size=130%><b>heading</b></size>\n
//   - item          → • item
//   TMP rich-text tags pass through when whitelisted: <b>, <i>, <u>, <s>, <color>, <#hex>,
//   <size>, <br>, <sprite>, <font>, <align>, <alpha>, <mark>, <sub>, <sup>, <space>, <newpage>.
//   A lone '<' in normal text (e.g. "a < b") is emitted verbatim, not treated as a tag.
//
// Color: use TMP-native <color=#hex>…</color> (the legacy [color=…][/color] markdown syntax
// was removed — one syntax, one behavior).

using System;
using System.Collections.Generic;
using System.Text;

namespace MeshTextBaker
{
    /// <summary>
    /// Converts a lightweight markdown subset into TextMeshPro rich-text format.
    /// </summary>
    public static class MarkdownParser
    {
        // Reusable per-thread StringBuilder to avoid per-call allocations.
        [System.ThreadStatic] private static StringBuilder s_Sb;

        // Whitelisted TMP tag names allowed to pass through unchanged.
        private static readonly string[] AllowedTags =
        {
            "b", "i", "u", "s", "color", "size", "br", "sprite", "font", "align",
            "alpha", "mark", "sub", "sup", "space", "newpage", "nobr", "indent",
            "line-height", "cspace", "voffset", "link", "image", "noparse", "bookmark", "pos",
            // Mesh Text Baker custom scopes (converted to zero-width TMP links):
            "emission", "surface", "blend", "opacity"
        };

        public static string ConvertToRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // Convert GitHub-style pipe tables first. The generated <pos=%> columns are normal
            // TMP markup and the existing inline markdown pass can still style cell contents.
            input = ConvertPipeTables(input);

            if (s_Sb == null) s_Sb = new StringBuilder(2048);
            s_Sb.Clear();
            var sb = s_Sb;

            int i = 0;
            int len = input.Length;

            while (i < len)
            {
                // Inline code span `...` → <noparse>...</noparse>.
                // Critical for docs/demos that SHOW tags like `<color>` / `<emission>` as
                // examples: without this, bare example tags open real rich-text scopes and
                // RichTextTagRepair reopens them on later pages (visible "<color" garbage).
                if (input[i] == '`')
                {
                    int close = input.IndexOf('`', i + 1);
                    // Same-line only (match bold/italic policy); unclosed → literal backtick.
                    int lineEnd = LineEnd(input, i);
                    if (close > i && close < lineEnd)
                    {
                        sb.Append("<noparse>");
                        sb.Append(input, i + 1, close - i - 1);
                        sb.Append("</noparse>");
                        i = close + 1;
                        continue;
                    }
                }

                // Heading (# or ##) at line start
                if (IsLineStart(input, i) && i < len && input[i] == '#')
                {
                    int level = 0;
                    while (i < len && input[i] == '#') { level++; i++; }
                    if (i < len && input[i] == ' ') i++;

                    string sizeTag = level == 1 ? "<size=150%>" : "<size=130%>";
                    int start = i;
                    while (i < len && input[i] != '\n') i++;
                    string headingText = input.Substring(start, i - start).TrimEnd();

                    sb.Append(sizeTag);
                    sb.Append("<b>");
                    sb.Append(headingText);
                    sb.Append("</b></size>");
                    if (i < len && input[i] == '\n') { sb.Append('\n'); i++; }
                    continue;
                }

                // Unordered list item "- " or "* " at line start
                if (IsLineStart(input, i) && i + 1 < len &&
                    (input[i] == '-' || input[i] == '*') && input[i + 1] == ' ')
                {
                    i += 2;
                    sb.Append("\u2022 ");
                    continue;
                }

                // Bold **text** — selection wrappers may span multiple paragraphs.
                if (i + 1 < len && input[i] == '*' && input[i + 1] == '*')
                {
                    int end = IndexOfWithin(input, "**", i + 2, len);
                    if (end >= 0)
                    {
                        sb.Append("<b>");
                        sb.Append(input, i + 2, end - i - 2);
                        sb.Append("</b>");
                        i = end + 2;
                        continue;
                    }
                }

                // Italic *text* (but not **) — may span line breaks.
                if (i < len && input[i] == '*' && (i + 1 >= len || input[i + 1] != '*'))
                {
                    int end = FindClosingItalic(input, i + 1, len);
                    if (end >= 0)
                    {
                        sb.Append("<i>");
                        sb.Append(input, i + 1, end - i - 1);
                        sb.Append("</i>");
                        i = end + 1;
                        continue;
                    }
                }

                // Pass-through TMP tags — only if the tag name is whitelisted; otherwise the
                // '<' is ordinary text ("2 < 3" must not swallow the rest of the string).
                if (i < len && input[i] == '<')
                {
                    int endTag = input.IndexOf('>', i);
                    if (endTag >= 0 && endTag - i < 64 && IsWhitelistedTag(input, i, endTag))
                    {
                        sb.Append(input, i, endTag - i + 1);
                        i = endTag + 1;
                        continue;
                    }
                }

                sb.Append(input[i]);
                i++;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Simple Markdown tables: header + separator + rows. Cells are one physical TMP line
        /// (<nobr>), so a row is never split by vertical pagination. Column starts are computed
        /// from the longest visible cell in each column and emitted as percentages.
        /// </summary>
        private static string ConvertPipeTables(string input)
        {
            if (input.IndexOf('|') < 0) return input;
            string[] lines = input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var output = new StringBuilder(input.Length + 128);
            int i = 0;
            while (i < lines.Length)
            {
                if (i + 1 < lines.Length && TryParseTableRow(lines[i], out var header) &&
                    IsTableSeparator(lines[i + 1], header.Count))
                {
                    var rows = new List<List<string>> { header };
                    int j = i + 2;
                    while (j < lines.Length && TryParseTableRow(lines[j], out var row) &&
                           row.Count == header.Count)
                    {
                        rows.Add(row);
                        j++;
                    }

                    int[] widths = new int[header.Count];
                    for (int r = 0; r < rows.Count; r++)
                        for (int c = 0; c < widths.Length; c++)
                            widths[c] = Math.Max(widths[c], Math.Max(1, VisibleLength(rows[r][c])) + 2);
                    int total = 0;
                    for (int c = 0; c < widths.Length; c++) total += Math.Max(4, widths[c]);

                    int maxRowVisible = 0;
                    for (int r = 0; r < rows.Count; r++)
                    {
                        int rowVis = 0;
                        for (int c = 0; c < widths.Length; c++) rowVis += VisibleLength(rows[r][c]);
                        rowVis += Math.Max(0, widths.Length - 1) * 2;
                        if (rowVis > maxRowVisible) maxRowVisible = rowVis;
                    }
                    int budgetForScale = Math.Max(total, maxRowVisible);

                    // Tables have no independent cell containers. The whole row is rendered as
                    // one TMP line with <pos=%> columns. To keep a row atomic we avoid regular
                    // spaces inside cells (NBSP = no break opportunity) and slightly shrink the
                    // row from its character budget so a long trailing column overflows to the
                    // right instead of wrapping to column 0 on the next line.
                    float tableScale = Math.Max(20f, Math.Min(100f, 4000f / Math.Max(1, budgetForScale)));
                    string scaleText = tableScale.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

                    for (int r = 0; r < rows.Count; r++)
                    {
                        output.Append("<size=").Append(scaleText).Append("%><nobr>");
                        if (r == 0) output.Append("<b>");
                        int consumed = 0;
                        for (int c = 0; c < widths.Length; c++)
                        {
                            float pct = total > 0 ? consumed * 100f / total : 0f;
                            string raw = rows[r][c];
                            string ell = EllipsizeCell(raw, 44);
                            string safe = MakeNonBreaking(ell);
                            output.Append("<pos=")
                                  .Append(pct.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                                  .Append("%>")
                                  .Append(safe);
                            consumed += Math.Max(4, widths[c]);
                        }
                        if (r == 0) output.Append("</b>");
                        output.Append("</nobr></size>");
                        if (j < lines.Length || r < rows.Count - 1) output.Append('\n');
                    }
                    i = j;
                    continue;
                }

                output.Append(lines[i]);
                if (i < lines.Length - 1) output.Append('\n');
                i++;
            }
            return output.ToString();
        }

        private static bool TryParseTableRow(string line, out List<string> cells)
        {
            cells = null;
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf('|') < 0) return false;
            string body = line.Trim();
            if (body.StartsWith("|", StringComparison.Ordinal)) body = body.Substring(1);
            if (body.EndsWith("|", StringComparison.Ordinal)) body = body.Substring(0, body.Length - 1);
            string[] split = body.Split('|');
            if (split.Length < 2) return false;
            cells = new List<string>(split.Length);
            for (int i = 0; i < split.Length; i++) cells.Add(split[i].Trim());
            return true;
        }

        private static bool IsTableSeparator(string line, int expectedColumns)
        {
            if (!TryParseTableRow(line, out var cells) || cells.Count != expectedColumns) return false;
            for (int i = 0; i < cells.Count; i++)
            {
                string s = cells[i].Trim();
                if (s.StartsWith(":", StringComparison.Ordinal)) s = s.Substring(1);
                if (s.EndsWith(":", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
                if (s.Length < 3) return false;
                for (int k = 0; k < s.Length; k++) if (s[k] != '-') return false;
            }
            return true;
        }

        private static int VisibleLength(string cell)
        {
            int n = 0;
            bool inTag = false;
            for (int i = 0; i < cell.Length; i++)
            {
                if (cell[i] == '<') { inTag = true; continue; }
                if (inTag) { if (cell[i] == '>') inTag = false; continue; }
                if (cell[i] == '*' || cell[i] == '`') continue;
                n++;
            }
            return n;
        }

        private static string EllipsizeCell(string cell, int maxVisible)
        {
            if (VisibleLength(cell) <= maxVisible) return cell;
            // Simple tables deliberately do not wrap. Keep markup-safe behavior by truncating
            // plain long cells only; tagged cells are left intact rather than cut mid-tag.
            if (cell.IndexOf('<') >= 0 || cell.IndexOf('*') >= 0 || cell.IndexOf('`') >= 0)
                return cell;
            return cell.Substring(0, Math.Min(cell.Length, maxVisible - 1)).TrimEnd() + "…";
        }

        private static string MakeNonBreaking(string cell)
        {
            if (string.IsNullOrEmpty(cell)) return cell;
            // Prevent TMP word-wrap inside table cells: regular spaces become NBSP so a row
            // cannot break in the middle and the remainder cannot wrap to column 0.
            var sb = new StringBuilder(cell.Length);
            bool inTag = false;
            for (int i = 0; i < cell.Length; i++)
            {
                char ch = cell[i];
                if (ch == '<') { inTag = true; sb.Append(ch); continue; }
                if (inTag)
                {
                    sb.Append(ch);
                    if (ch == '>') inTag = false;
                    continue;
                }
                sb.Append(ch == ' ' ? '\u00A0' : ch);
            }
            return sb.ToString();
        }

        private static bool IsLineStart(string input, int pos)
        {
            if (pos == 0) return true;
            return pos > 0 && input[pos - 1] == '\n';
        }

        /// <summary>Index of the current line's terminating '\n' (or input length).</summary>
        private static int LineEnd(string input, int pos)
        {
            int nl = input.IndexOf('\n', pos);
            return nl < 0 ? input.Length : nl;
        }

        private static int IndexOfWithin(string input, string token, int start, int limit)
        {
            if (start >= limit) return -1;
            int idx = input.IndexOf(token, start, limit - start, System.StringComparison.Ordinal);
            return idx;
        }

        private static int FindClosingItalic(string input, int start, int limit)
        {
            for (int j = start; j < limit; j++)
            {
                if (input[j] == '*')
                {
                    if (j + 1 < limit && input[j + 1] == '*') { j++; continue; }
                    return j;
                }
            }
            return -1;
        }

        private static bool IsWhitelistedTag(string input, int open, int close)
        {
            int p = open + 1;
            if (p < close && input[p] == '/') p++;           // closing tag
            if (p < close && input[p] == '#') return true;   // TMP color shorthand <#RRGGBB>

            int nameStart = p;
            while (p < close)
            {
                char c = input[p];
                if (char.IsLetter(c) || c == '-') p++;
                else break;
            }
            int nameLen = p - nameStart;
            if (nameLen == 0) return false;

            // After the name only '=', space or the end of the tag are valid.
            if (p < close && input[p] != '=' && input[p] != ' ') return false;

            for (int t = 0; t < AllowedTags.Length; t++)
            {
                string tag = AllowedTags[t];
                if (tag.Length != nameLen) continue;
                bool match = true;
                for (int k = 0; k < nameLen; k++)
                {
                    if (char.ToLowerInvariant(input[nameStart + k]) != tag[k]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }
}
