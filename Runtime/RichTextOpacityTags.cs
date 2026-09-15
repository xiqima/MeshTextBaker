// Mesh Text Baker — RichTextOpacityTags.cs
// Layout-safe <opacity=0..1> scopes. Preprocess stores values in zero-width TMP links;
// Apply converts them to alpha state immediately before TMP layout/rendering.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace MeshTextBaker
{
    public static class RichTextOpacityTags
    {
        public const string LinkPrefix = "<link=\"mtb-opacity@";
        private const string LinkClose = "</link>";
        [ThreadStatic] private static StringBuilder s_Sb;

        private struct LinkState
        {
            public bool opacityLink;
            public float previousOpacity;
        }

        private static StringBuilder Sb(int capacity)
        {
            if (s_Sb == null) s_Sb = new StringBuilder(Math.Max(256, capacity));
            s_Sb.Clear();
            return s_Sb;
        }

        public static bool HasOpacityScopes(string text)
            => !string.IsNullOrEmpty(text) && text.IndexOf(LinkPrefix, StringComparison.Ordinal) >= 0;

        public static string Preprocess(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("<opacity", StringComparison.Ordinal) < 0)
                return text;
            StringBuilder sb = Sb(text.Length + 32);
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
                        sb.Append(text, i, end - i); i = end; continue;
                    }
                    if (MatchAt(text, i, "</opacity>"))
                    {
                        sb.Append(LinkClose); i += 10; continue;
                    }
                    if (MatchAt(text, i, "<opacity"))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 80)
                        {
                            string body = text.Substring(i + 8, close - i - 8).Trim();
                            if (body.StartsWith("=", StringComparison.Ordinal)) body = body.Substring(1).Trim();
                            else
                            {
                                int eq = body.IndexOf('=');
                                if (eq >= 0) body = body.Substring(eq + 1).Trim();
                            }
                            if (float.TryParse(body.Trim('"'), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out float value))
                            {
                                sb.Append(LinkPrefix)
                                  .Append(Mathf.Clamp01(value).ToString("0.###", CultureInfo.InvariantCulture))
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

        public static bool TryParseLink(string tag, out float opacity)
        {
            opacity = 1f;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith(LinkPrefix, StringComparison.Ordinal))
                return false;
            int start = LinkPrefix.Length;
            int quote = tag.IndexOf('"', start);
            if (quote <= start) return false;
            if (!float.TryParse(tag.Substring(start, quote - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out opacity)) return false;
            opacity = Mathf.Clamp01(opacity);
            return true;
        }

        /// <summary>Injects effective TMP alpha states while preserving all zero-width links.</summary>
        public static string Apply(string text)
        {
            if (!HasOpacityScopes(text)) return text;
            StringBuilder sb = Sb(text.Length + 64);
            var links = new Stack<LinkState>(4);
            float opacity = 1f;
            float baseAlpha = 1f; // alpha requested by PBR visibility markup / native <alpha>
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    if (MatchAt(text, i, "<link="))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i)
                        {
                            string tag = text.Substring(i, close - i + 1);
                            bool isOpacity = TryParseLink(tag, out float value);
                            links.Push(new LinkState { opacityLink = isOpacity, previousOpacity = opacity });
                            sb.Append(tag);
                            if (isOpacity)
                            {
                                opacity *= value;
                                AppendAlpha(sb, baseAlpha * opacity);
                            }
                            i = close + 1;
                            continue;
                        }
                    }
                    if (MatchAt(text, i, LinkClose))
                    {
                        sb.Append(LinkClose);
                        if (links.Count > 0)
                        {
                            LinkState state = links.Pop();
                            if (state.opacityLink)
                            {
                                opacity = state.previousOpacity;
                                AppendAlpha(sb, baseAlpha * opacity);
                            }
                        }
                        i += LinkClose.Length;
                        continue;
                    }
                    if (MatchAt(text, i, "<alpha="))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 32)
                        {
                            if (TryParseAlpha(text.Substring(i, close - i + 1), out float parsed))
                                baseAlpha = parsed;
                            AppendAlpha(sb, baseAlpha * opacity);
                            i = close + 1;
                            continue;
                        }
                    }
                    if (MatchAt(text, i, "<color") || MatchAt(text, i, "</color>") || MatchAt(text, i, "<#"))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 80)
                        {
                            sb.Append(text, i, close - i + 1);
                            AppendAlpha(sb, baseAlpha * opacity);
                            i = close + 1;
                            continue;
                        }
                    }
                }
                sb.Append(text[i++]);
            }
            return sb.ToString();
        }

        private static bool TryParseAlpha(string tag, out float alpha)
        {
            alpha = 1f;
            int hash = tag.IndexOf('#');
            int end = tag.IndexOf('>', hash + 1);
            if (hash < 0 || end <= hash) return false;
            string hex = tag.Substring(hash + 1, end - hash - 1);
            if (hex.Length != 2 || !byte.TryParse(hex, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out byte value)) return false;
            alpha = value / 255f;
            return true;
        }

        private static void AppendAlpha(StringBuilder sb, float alpha)
        {
            byte value = (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255);
            sb.Append("<alpha=#").Append(value.ToString("X2")).Append('>');
        }

        private static bool MatchAt(string text, int pos, string token)
        {
            if (pos + token.Length > text.Length) return false;
            for (int i = 0; i < token.Length; i++) if (text[pos + i] != token[i]) return false;
            return true;
        }
    }
}
