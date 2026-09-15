// Mesh Text Baker — RichTextBlendTags.cs
// Layout-safe inline Photoshop-style blend scopes.
//   <blend=multiply>ink</blend>
// becomes a zero-width TMP link marker so pagination and geometry stay unchanged.

using System;
using System.Collections.Generic;
using System.Text;

namespace MeshTextBaker
{
    public static class RichTextBlendTags
    {
        public const string LinkPrefix = "<link=\"mtb-blend@";
        private const string LinkClose = "</link>";
        [ThreadStatic] private static StringBuilder s_Sb;

        private static StringBuilder Sb(int capacity)
        {
            if (s_Sb == null) s_Sb = new StringBuilder(Math.Max(256, capacity));
            s_Sb.Clear();
            return s_Sb;
        }

        public static bool HasBlendScopes(string text)
            => !string.IsNullOrEmpty(text) && text.IndexOf(LinkPrefix, StringComparison.Ordinal) >= 0;

        public static string Preprocess(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("<blend", StringComparison.Ordinal) < 0)
                return text;

            var sb = Sb(text.Length + 32);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    if (MatchAt(text, i, "<noparse>"))
                    {
                        int end = text.IndexOf("</noparse>", i + 9, StringComparison.Ordinal);
                        if (end < 0) { sb.Append(text, i, text.Length - i); break; }
                        end += 10;
                        sb.Append(text, i, end - i);
                        i = end;
                        continue;
                    }
                    if (MatchAt(text, i, "</blend>"))
                    {
                        sb.Append(LinkClose);
                        i += 8;
                        continue;
                    }
                    if (MatchAt(text, i, "<blend"))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 80)
                        {
                            string body = text.Substring(i + 6, close - i - 6).Trim();
                            if (body.StartsWith("=", StringComparison.Ordinal)) body = body.Substring(1).Trim();
                            else
                            {
                                int eq = body.IndexOf('=');
                                if (eq >= 0) body = body.Substring(eq + 1).Trim();
                            }
                            if (MeshTextBlendModeUtility.TryParse(body, out var mode))
                            {
                                sb.Append(LinkPrefix)
                                  .Append(MeshTextBlendModeUtility.ToTagName(mode))
                                  .Append("\">");
                                i = close + 1;
                                continue;
                            }
                        }
                    }
                }
                sb.Append(text[i++]);
            }
            return sb.ToString();
        }

        public static bool TryParseLink(string tag, out MeshTextBlendMode mode)
        {
            mode = MeshTextBlendMode.Normal;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith(LinkPrefix, StringComparison.Ordinal))
                return false;
            int start = LinkPrefix.Length;
            int quote = tag.IndexOf('"', start);
            if (quote < start) return false;
            return MeshTextBlendModeUtility.TryParse(tag.Substring(start, quote - start), out mode);
        }

        public static void GetVariants(string text, List<MeshTextBlendMode> variants)
        {
            variants.Clear();
            if (string.IsNullOrEmpty(text)) return;
            int i = 0;
            while ((i = text.IndexOf(LinkPrefix, i, StringComparison.Ordinal)) >= 0)
            {
                int close = text.IndexOf('>', i);
                if (close < 0) break;
                if (TryParseLink(text.Substring(i, close - i + 1), out var mode) && !variants.Contains(mode))
                    variants.Add(mode);
                i = close + 1;
            }
        }

        /// <summary>Shows text outside all blend scopes and hides scoped content.</summary>
        public static string BuildBaseMarkup(string text)
            => BuildMarkup(text, MeshTextBlendMode.Normal, showOutside: true);

        /// <summary>Shows only scopes matching <paramref name="variant"/>.</summary>
        public static string BuildVariantMarkup(string text, MeshTextBlendMode variant)
            => BuildMarkup(text, variant, showOutside: false);

        private static string BuildMarkup(string text, MeshTextBlendMode variant, bool showOutside)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = Sb(text.Length + 96);
            sb.Append(showOutside ? "<alpha=#FF>" : "<alpha=#00>");
            bool inBlend = false;
            bool visible = showOutside;
            int nestedLinkDepth = 0;
            int i = 0;

            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    if (MatchAt(text, i, LinkPrefix))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i)
                        {
                            string tag = text.Substring(i, close - i + 1);
                            bool matches = TryParseLink(tag, out var mode) && mode == variant;
                            visible = !showOutside && matches;
                            inBlend = true;
                            sb.Append(tag).Append(visible ? "<alpha=#FF>" : "<alpha=#00>");
                            i = close + 1;
                            continue;
                        }
                    }
                    if (inBlend && MatchAt(text, i, "<link="))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i)
                        {
                            nestedLinkDepth++;
                            sb.Append(text, i, close - i + 1);
                            i = close + 1;
                            continue;
                        }
                    }
                    if (inBlend && MatchAt(text, i, LinkClose))
                    {
                        sb.Append(LinkClose);
                        i += LinkClose.Length;
                        if (nestedLinkDepth > 0) { nestedLinkDepth--; continue; }
                        visible = showOutside;
                        sb.Append(visible ? "<alpha=#FF>" : "<alpha=#00>");
                        inBlend = false;
                        continue;
                    }

                    // TMP color tags reset vertex alpha; immediately re-assert our visibility.
                    if (MatchAt(text, i, "<color") || MatchAt(text, i, "</color>") || MatchAt(text, i, "<#"))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 64)
                        {
                            sb.Append(text, i, close - i + 1)
                              .Append(visible ? "<alpha=#FF>" : "<alpha=#00>");
                            i = close + 1;
                            continue;
                        }
                    }
                }
                sb.Append(text[i++]);
            }
            return sb.ToString();
        }

        private static bool MatchAt(string text, int pos, string token)
        {
            if (pos + token.Length > text.Length) return false;
            for (int i = 0; i < token.Length; i++)
                if (text[pos + i] != token[i]) return false;
            return true;
        }
    }
}
