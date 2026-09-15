// Mesh Text Baker — RichTextPbrTags.cs
// Per-word PBR control via custom rich-text tags. Canonical names:
//   <emission>word</emission>               — word glows (zone's Emission Color)
//   <emission=#00ffcc>…                     — custom color
//   <emission=#00ffcc i=3>…                 — custom color, 3× brighter
//   <surface>word</surface>                 — layout-only/no-op PBR scope (neutral by default)
//   <surface m=1 s=0.9>…                    — per-scope surface properties
//   <surface h=1>raised</surface>            — signed height (-1 engraved .. +1 raised)
//     (m = metallic 0..1, s = smoothness 0..1, h = normalized signed height)
//
// HOW IT WORKS (layout-identity trick):
//   1. Preprocess() converts the custom tags into TMP-native <link="mtb-glow[#hex]"> tags.
//      (Wire format keeps mtb-glow / mtb-shine ids for back-compat with saved book caches.)
//      Link tags are ZERO-WIDTH: they do not affect glyph layout, measuring, pagination or
//      overflow slicing, and RichTextTagRepair reopens them across page boundaries like any
//      other tag. The albedo pass renders the text with the links in place — no visual change.
//   2. For the emission (or mask coverage) pass the SAME final string is transformed by
//      BuildGlowMarkup()/BuildShineMarkup(): segments outside the tagged scopes are wrapped
//      in <alpha=#00> (invisible), segments inside get <alpha=#FF> (+ <color=#hex> for glow).
//      Alpha/color tags are also zero-width, so the glyph geometry matches the albedo pass
//      EXACTLY — the glow/mask lands precisely under the visible letters.
//
// Uses UnityEngine only for Vector2/Mathf in shine-variant helpers (EditMode tests OK).

using System;
using System.Text;

namespace MeshTextBaker
{
    public static class RichTextPbrTags
    {
        public const string GlowLinkPrefix = "<link=\"mtb-glow";
        public const string ShineLinkPrefix = "<link=\"mtb-shine";
        private const string LinkClose = "</link>";

        [ThreadStatic] private static StringBuilder s_Sb;

        private static StringBuilder Sb(int capacity)
        {
            if (s_Sb == null) s_Sb = new StringBuilder(Math.Max(256, capacity));
            s_Sb.Clear();
            return s_Sb;
        }

        /// <summary>Fast check: does the (preprocessed) text contain glow scopes?</summary>
        public static bool HasGlow(string text)
            => !string.IsNullOrEmpty(text) &&
               text.IndexOf(GlowLinkPrefix, StringComparison.Ordinal) >= 0;

        /// <summary>True when a surface scope explicitly changes metallic or smoothness.</summary>
        public static bool HasShine(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int i = 0;
            while ((i = text.IndexOf(ShineLinkPrefix, i, StringComparison.Ordinal)) >= 0)
            {
                int close = text.IndexOf('>', i);
                if (close < 0) break;
                ParseSurfaceParams(text.Substring(i, close - i + 1),
                    out float m, out float s, out _);
                if (m >= 0f || s >= 0f) return true;
                i = close + 1;
            }
            return false;
        }

        /// <summary>True when a surface scope explicitly carries a non-zero h= value.</summary>
        public static bool HasHeight(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int i = 0;
            while ((i = text.IndexOf(ShineLinkPrefix, i, StringComparison.Ordinal)) >= 0)
            {
                int close = text.IndexOf('>', i);
                if (close < 0) break;
                ParseSurfaceParams(text.Substring(i, close - i + 1), out _, out _, out float h);
                if (!float.IsNaN(h) && !UnityEngine.Mathf.Approximately(h, 0f)) return true;
                i = close + 1;
            }
            return false;
        }

        /// <summary>
        /// Converts &lt;glow&gt;/&lt;glow=#hex&gt;/&lt;shine&gt; (and closers) into TMP link
        /// tags. Anything else passes through untouched. Tags are lowercase-only by design.
        /// </summary>
        public static string Preprocess(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (text.IndexOf("<emission", StringComparison.Ordinal) < 0 &&
                text.IndexOf("<surface", StringComparison.Ordinal) < 0)
                return text;

            var sb = Sb(text.Length + 64);
            int i = 0;
            int len = text.Length;

            while (i < len)
            {
                if (text[i] == '<')
                {
                    // Pass <noparse>…</noparse> through untouched (and do not convert
                    // emission/surface tags that live inside as documentation examples).
                    if (MatchAt(text, i, "<noparse>"))
                    {
                        int endNp = text.IndexOf("</noparse>", i + 9, System.StringComparison.Ordinal);
                        if (endNp < 0)
                        {
                            sb.Append(text, i, len - i);
                            break;
                        }
                        int endAfter = endNp + 10; // len("</noparse>")
                        sb.Append(text, i, endAfter - i);
                        i = endAfter;
                        continue;
                    }

                    if (MatchAt(text, i, "</emission>"))
                    {
                        sb.Append(LinkClose);
                        i += 11;
                        continue;
                    }
                    if (MatchAt(text, i, "</surface>"))
                    {
                        sb.Append(LinkClose);
                        i += 10;
                        continue;
                    }

                    // surface
                    int shineNameLen = MatchAt(text, i, "<surface") ? 8 : 0;
                    if (shineNameLen > 0)
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 96)
                        {
                            // Wire format: mtb-shine[@m,s,h]. Empty fields mean "unchanged".
                            // A bare <surface> intentionally remains a visual PBR no-op.
                            string attrs = text.Substring(i + shineNameLen, close - i - shineNameLen).Trim();
                            ParseSurfaceAttributes(attrs, out float m, out float sm, out float h);
                            sb.Append("<link=\"mtb-shine");
                            if (m >= 0f || sm >= 0f || !float.IsNaN(h))
                            {
                                sb.Append('@').Append(F(m)).Append(',').Append(F(sm));
                                if (!float.IsNaN(h)) sb.Append(',').Append(FormatFloat(h));
                            }
                            sb.Append("\">");
                            i = close + 1;
                            continue;
                        }
                    }

                    // emission
                    int glowNameLen = MatchAt(text, i, "<emission") ? 9 : 0;
                    if (glowNameLen > 0)
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 96)
                        {
                            // "<emission>", "<emission=#hex>", "<emission=#hex i=2>", "<emission i=2>"
                            string inner = text.Substring(i + glowNameLen, close - i - glowNameLen).Trim();
                            string col = null;
                            float intensity = -1f;

                            if (inner.StartsWith("=", StringComparison.Ordinal))
                            {
                                string rest = inner.Substring(1).TrimStart();
                                int sp = rest.IndexOf(' ');
                                string colPart = sp >= 0 ? rest.Substring(0, sp) : rest;
                                if (colPart.StartsWith("#", StringComparison.Ordinal)) colPart = colPart.Substring(1);
                                if (IsHex(colPart)) col = colPart;
                                inner = sp >= 0 ? rest.Substring(sp + 1) : "";
                            }
                            ParseKeyFloats(inner, ref intensity, ref intensity, out bool hasI);

                            sb.Append("<link=\"mtb-glow");
                            if (col != null) sb.Append('#').Append(col);
                            if (hasI && intensity > 0f) sb.Append('@').Append(F(intensity));
                            sb.Append("\">");
                            i = close + 1;
                            continue;
                        }
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Scales an RRGGBB hex color by k (0..1).</summary>
        private static string DimHex(string hex, float k)
        {
            if (hex.Length < 6) return hex;
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            r = (int)(r * k); g = (int)(g * k); b = (int)(b * k);
            return r.ToString("x2") + g.ToString("x2") + b.ToString("x2");
        }

        private static string F(float v)
            => v < 0f ? "" : FormatFloat(v);

        private static string FormatFloat(float v)
            => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private static void ParseSurfaceAttributes(string attrs, out float metallic,
            out float smoothness, out float height)
        {
            metallic = -1f;
            smoothness = -1f;
            height = float.NaN;
            if (string.IsNullOrEmpty(attrs)) return;
            string[] parts = attrs.Split(' ');
            for (int p = 0; p < parts.Length; p++)
            {
                string part = parts[p].Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!float.TryParse(part.Substring(eq + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v)) continue;
                string key = part.Substring(0, eq).ToLowerInvariant();
                if (key == "m") metallic = UnityEngine.Mathf.Clamp01(v);
                else if (key == "s") smoothness = UnityEngine.Mathf.Clamp01(v);
                else if (key == "h") height = UnityEngine.Mathf.Clamp(v, -1f, 1f);
            }
        }

        /// <summary>Parses "m=X s=Y" / "i=N" attribute lists (space-separated).</summary>
        private static void ParseKeyFloats(string attrs, ref float mOrI, ref float s, out bool hasI)
        {
            hasI = false;
            if (string.IsNullOrEmpty(attrs)) return;
            string[] parts = attrs.Split(' ');
            for (int p = 0; p < parts.Length; p++)
            {
                string part = parts[p].Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).ToLowerInvariant();
                if (!float.TryParse(part.Substring(eq + 1),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v)) continue;
                if (key == "m" || key == "i") { mOrI = v; if (key == "i") hasI = true; }
                else if (key == "s") s = v;
            }
        }

        /// <summary>
        /// Emission-pass version of the final text: glyphs outside glow scopes get alpha 0,
        /// inside — alpha FF (+ per-tag color). Layout is identical to the albedo pass.
        /// </summary>
        public static string BuildGlowMarkup(string text)
            => BuildGlowMarkup(text, 1f);

        /// <summary>
        /// Emission markup with per-scope intensity support: each scope's LDR color is
        /// scaled by (scopeIntensity / maxIntensity); the material carries maxIntensity.
        /// Relative brightness between words is preserved inside the LDR emission map.
        /// </summary>
        public static string BuildGlowMarkup(string text, float maxIntensity)
            => BuildScopedMarkup(text, GlowLinkPrefix, true, Math.Max(1f, maxIntensity));

        /// <summary>Largest i= multiplier among glow scopes (>= 1). Material intensity = zone × this.</summary>
        public static float GetMaxGlowIntensity(string text)
        {
            float max = 1f;
            if (string.IsNullOrEmpty(text)) return max;
            int i = 0;
            while ((i = text.IndexOf(GlowLinkPrefix, i, StringComparison.Ordinal)) >= 0)
            {
                int close = text.IndexOf('>', i);
                if (close < 0) break;
                float scopeI = ParseScopeIntensity(text.Substring(i, close - i + 1));
                if (scopeI > max) max = scopeI;
                i = close + 1;
            }
            return max;
        }

        private static float ParseScopeIntensity(string tag)
        {
            int at = tag.IndexOf('@');
            if (at < 0) return 1f;
            int endQ = tag.IndexOf('"', at);
            if (endQ < 0) return 1f;
            string num = tag.Substring(at + 1, endQ - at - 1);
            // shine scopes use "@m,s" — a comma means this is not a glow intensity
            if (num.IndexOf(',') >= 0) return 1f;
            return float.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) && v > 0f ? v : 1f;
        }

        /// <summary>
        /// Collects distinct explicit (metallic, smoothness) variants among surface scopes.
        /// Bare and height-only scopes are skipped, so they cannot alter the material mask.
        /// </summary>
        public static void GetShineVariants(string text, System.Collections.Generic.List<UnityEngine.Vector2> variants)
        {
            variants.Clear();
            if (string.IsNullOrEmpty(text)) return;
            int i = 0;
            while ((i = text.IndexOf(ShineLinkPrefix, i, StringComparison.Ordinal)) >= 0)
            {
                int close = text.IndexOf('>', i);
                if (close < 0) break;
                ParseSurfaceParams(text.Substring(i, close - i + 1), out float m, out float sm, out _);
                // Bare <surface> and height-only scopes must not create a metallic mask.
                if (m >= 0f || sm >= 0f)
                {
                    var v = new UnityEngine.Vector2(m, sm);
                    if (!variants.Contains(v)) variants.Add(v);
                }
                i = close + 1;
            }
        }

        /// <summary>
        /// Shine markup showing ONLY scopes whose (m,s) matches the given variant — used to
        /// composite each metallic/smoothness combination as its own mask pass.
        /// </summary>
        public static string BuildShineMarkupForVariant(string text, UnityEngine.Vector2 variant)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = Sb(text.Length + 128);
            sb.Append("<alpha=#00>");

            int i = 0;
            int len = text.Length;
            bool inScope = false;
            bool scopeVisible = false;
            int nestedLinkDepth = 0;

            while (i < len)
            {
                if (text[i] == '<')
                {
                    if (MatchAt(text, i, ShineLinkPrefix))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i)
                        {
                            string tag = text.Substring(i, close - i + 1);
                            ParseSurfaceParams(tag, out float m, out float sm, out _);
                            scopeVisible = UnityEngine.Mathf.Approximately(m, variant.x) &&
                                           UnityEngine.Mathf.Approximately(sm, variant.y);
                            sb.Append(tag);
                            if (scopeVisible) sb.Append("<alpha=#FF>");
                            inScope = true;
                            i = close + 1;
                            continue;
                        }
                    }

                    if (inScope && MatchAt(text, i, "<link="))
                    {
                        int nestedClose = text.IndexOf('>', i);
                        if (nestedClose > i)
                        {
                            nestedLinkDepth++;
                            sb.Append(text, i, nestedClose - i + 1);
                            i = nestedClose + 1;
                            continue;
                        }
                    }
                    if (inScope && MatchAt(text, i, LinkClose))
                    {
                        sb.Append(LinkClose);
                        i += LinkClose.Length;
                        if (nestedLinkDepth > 0) { nestedLinkDepth--; continue; }
                        if (scopeVisible) sb.Append("<alpha=#00>");
                        inScope = false;
                        scopeVisible = false;
                        continue;
                    }

                    // Same alpha-reset guard as the glow markup (see BuildScopedMarkup).
                    if (MatchAt(text, i, "<color") || MatchAt(text, i, "</color>") ||
                        (MatchAt(text, i, "<#") && text.IndexOf('>', i) > i))
                    {
                        int closeTag = text.IndexOf('>', i);
                        if (closeTag > i && closeTag - i < 64)
                        {
                            sb.Append(text, i, closeTag - i + 1);
                            sb.Append(inScope && scopeVisible ? "<alpha=#FF>" : "<alpha=#00>");
                            i = closeTag + 1;
                            continue;
                        }
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Glow scope params from a link tag: color hex (null = zone default) + i=.</summary>
        public static void ParseGlowParams(string tag, out string colorHex, out float intensity)
        {
            colorHex = null;
            intensity = ParseScopeIntensity(tag);
            int hash = tag.IndexOf('#');
            if (hash < 0) return;
            int end = tag.IndexOfAny(new[] { '@', '"' }, hash);
            if (end <= hash) return;
            string candidate = tag.Substring(hash + 1, end - hash - 1);
            if (IsHex(candidate)) colorHex = candidate;
        }

        /// <summary>Per-scope "m,s" from a surface link tag; -1 where unspecified.</summary>
        public static void ParseShineParams(string tag, out float metallic, out float smoothness)
            => ParseSurfaceParams(tag, out metallic, out smoothness, out _);

        /// <summary>Per-scope m/s/h values; m/s=-1 and h=NaN mean unspecified.</summary>
        public static void ParseSurfaceParams(string tag, out float metallic,
            out float smoothness, out float height)
        {
            metallic = -1f;
            smoothness = -1f;
            height = float.NaN;
            int at = tag.IndexOf('@');
            if (at < 0) return;
            int endQ = tag.IndexOf('"', at);
            if (endQ < 0) return;
            string[] values = tag.Substring(at + 1, endQ - at - 1).Split(',');
            if (values.Length > 0 && !string.IsNullOrEmpty(values[0]))
                float.TryParse(values[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out metallic);
            if (values.Length > 1 && !string.IsNullOrEmpty(values[1]) &&
                !float.TryParse(values[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out smoothness)) smoothness = -1f;
            if (values.Length > 2 && !string.IsNullOrEmpty(values[2]) &&
                float.TryParse(values[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float parsedHeight))
                height = UnityEngine.Mathf.Clamp(parsedHeight, -1f, 1f);
        }

        public static void GetHeightVariants(string text, System.Collections.Generic.List<float> variants)
        {
            variants.Clear();
            if (string.IsNullOrEmpty(text)) return;
            int i = 0;
            while ((i = text.IndexOf(ShineLinkPrefix, i, StringComparison.Ordinal)) >= 0)
            {
                int close = text.IndexOf('>', i);
                if (close < 0) break;
                ParseSurfaceParams(text.Substring(i, close - i + 1), out _, out _, out float h);
                if (!float.IsNaN(h) && !UnityEngine.Mathf.Approximately(h, 0f) && !variants.Contains(h))
                    variants.Add(h);
                i = close + 1;
            }
        }

        public static string BuildHeightMarkupForVariant(string text, float variant)
            => BuildSurfaceMarkupForHeight(text, variant);

        private static string BuildSurfaceMarkupForHeight(string text, float variant)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = Sb(text.Length + 96);
            sb.Append("<alpha=#00>");
            bool inScope = false;
            bool visible = false;
            int nestedLinkDepth = 0;
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    if (MatchAt(text, i, ShineLinkPrefix))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i)
                        {
                            string tag = text.Substring(i, close - i + 1);
                            ParseSurfaceParams(tag, out _, out _, out float h);
                            visible = !float.IsNaN(h) && UnityEngine.Mathf.Approximately(h, variant);
                            inScope = true;
                            sb.Append(tag).Append(visible ? "<alpha=#FF>" : "<alpha=#00>");
                            i = close + 1;
                            continue;
                        }
                    }
                    if (inScope && MatchAt(text, i, "<link="))
                    {
                        int nestedClose = text.IndexOf('>', i);
                        if (nestedClose > i)
                        {
                            nestedLinkDepth++;
                            sb.Append(text, i, nestedClose - i + 1);
                            i = nestedClose + 1;
                            continue;
                        }
                    }
                    if (inScope && MatchAt(text, i, LinkClose))
                    {
                        sb.Append(LinkClose);
                        i += LinkClose.Length;
                        if (nestedLinkDepth > 0) { nestedLinkDepth--; continue; }
                        sb.Append("<alpha=#00>");
                        inScope = false; visible = false;
                        continue;
                    }
                    if (MatchAt(text, i, "<color") || MatchAt(text, i, "</color>") || MatchAt(text, i, "<#"))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i && close - i < 64)
                        {
                            sb.Append(text, i, close - i + 1)
                              .Append(inScope && visible ? "<alpha=#FF>" : "<alpha=#00>");
                            i = close + 1;
                            continue;
                        }
                    }
                }
                sb.Append(text[i++]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Mask-coverage version: glyphs outside shine scopes get alpha 0, inside — alpha FF.
        /// Rendered white (vertex override) into the coverage buffer.
        /// </summary>
        public static string BuildShineMarkup(string text)
            => BuildScopedMarkup(text, ShineLinkPrefix, false, 1f);

        private static string BuildScopedMarkup(string text, string scopePrefix, bool emitScopeColor,
            float maxIntensity)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = Sb(text.Length + 128);
            sb.Append("<alpha=#00>");

            int i = 0;
            int len = text.Length;
            bool inScope = false;
            bool scopeHasColor = false;
            int nestedLinkDepth = 0;

            while (i < len)
            {
                if (text[i] == '<')
                {
                    if (MatchAt(text, i, scopePrefix))
                    {
                        int close = text.IndexOf('>', i);
                        if (close > i)
                        {
                            string tag = text.Substring(i, close - i + 1);
                            sb.Append(tag);        // keep the link tag (zero-width)
                            sb.Append("<alpha=#FF>");

                            scopeHasColor = false;
                            if (emitScopeColor)
                            {
                                // tag: <link="mtb-glow[#hex][@i]">
                                string col = null;
                                int hash = tag.IndexOf('#');
                                if (hash >= 0)
                                {
                                    int colEnd = tag.IndexOfAny(new[] { '@', '"' }, hash);
                                    if (colEnd > hash)
                                    {
                                        string candidate = tag.Substring(hash + 1, colEnd - hash - 1);
                                        if (IsHex(candidate)) col = candidate;
                                    }
                                }
                                float scopeI = ParseScopeIntensity(tag);
                                float dim = maxIntensity > 0f ? Math.Min(1f, scopeI / maxIntensity) : 1f;

                                if (col == null && dim < 0.999f) col = "ffffff"; // dim needs a color to scale
                                if (col != null)
                                {
                                    if (dim < 0.999f) col = DimHex(col, dim);
                                    sb.Append("<color=#").Append(col).Append('>');
                                    scopeHasColor = true;
                                }
                            }
                            inScope = true;
                            i = close + 1;
                            continue;
                        }
                    }

                    if (inScope && MatchAt(text, i, "<link="))
                    {
                        int nestedClose = text.IndexOf('>', i);
                        if (nestedClose > i)
                        {
                            nestedLinkDepth++;
                            sb.Append(text, i, nestedClose - i + 1);
                            i = nestedClose + 1;
                            continue;
                        }
                    }
                    if (inScope && MatchAt(text, i, LinkClose))
                    {
                        if (nestedLinkDepth > 0)
                        {
                            nestedLinkDepth--;
                            sb.Append(LinkClose);
                            i += LinkClose.Length;
                            continue;
                        }
                        if (scopeHasColor) { sb.Append("</color>"); scopeHasColor = false; }
                        sb.Append(LinkClose);
                        sb.Append("<alpha=#00>");
                        inScope = false;
                        i += LinkClose.Length;
                        continue;
                    }

                    // USER color tags reset TMP's vertex alpha to FF, overriding our alpha
                    // masking ("everything after a <color> became emissive" bug). Copy the
                    // tag, then RE-ISSUE the alpha that must currently be in effect.
                    if (MatchAt(text, i, "<color") || MatchAt(text, i, "</color>") || MatchAt(text, i, "<#"))
                    {
                        int closeTag = text.IndexOf('>', i);
                        if (closeTag > i && closeTag - i < 64)
                        {
                            sb.Append(text, i, closeTag - i + 1);
                            sb.Append(inScope ? "<alpha=#FF>" : "<alpha=#00>");
                            i = closeTag + 1;
                            continue;
                        }
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            return sb.ToString();
        }

        private static bool MatchAt(string text, int pos, string token)
        {
            if (pos + token.Length > text.Length) return false;
            for (int k = 0; k < token.Length; k++)
                if (text[pos + k] != token[k]) return false;
            return true;
        }

        private static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s) || (s.Length != 6 && s.Length != 8)) return false;
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }
    }
}
