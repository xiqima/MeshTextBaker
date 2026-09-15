// Mesh Text Baker — InlineImages.cs
// Custom <image=...> tag: bakes a texture INSIDE the text flow.
//
// Syntax:
//   <image=name>                      — image from the library, height = 100% of font size
//   <image=res:Path/To/Tex>           — texture loaded from a Resources folder (localizers
//                                       can reference their own images without touching the
//                                       Image Library; per-locale text = per-locale images)
//   <image=name h=150>                — height = 150% of font size (width auto from aspect)
//   <image=name h=120 w=200>          — explicit width (percent of font size)
//   <image=name displace=false>       — OVERLAY: text does not move
//   <image=name under=true>           — draw UNDER the glyphs (text on top of the image)
//   <image=name pivot=c>              — anchor point: bl (default) | br | tl | tr | c
//   <image=name blend=multiply opacity=0.5> — Photoshop blend + opacity
//
// HOW IT WORKS (anchor trick):
//   The tag is replaced by a ZERO-WIDTH SPACE (U+200B) — a real character TMP lays out,
//   so it has a precise position in textInfo — followed by <space=N> when the image
//   displaces text. N is CALIBRATED: TextLayoutEngine measures the real advance TMP
//   produces per requested unit for the current font/size and corrects the value, so
//   the shift equals the image width regardless of TMP's internal scale conventions. After layout the anchor character is located by its string index and
//   the image rect is computed from its origin/baseline (shifted by the pivot).
//
// BOOK SUPPORT: measuring maps fit indices from the processed string back to the SOURCE
// string via segment mapping (see ProcessWithMap/MapProcessedToSource), so page slicing
// operates on original text with intact tags. A page never splits mid-tag: a fit landing
// inside a replaced region snaps to the tag start (the image moves to the next page).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace MeshTextBaker
{
    /// <summary>A named texture usable via the &lt;image=name&gt; tag.</summary>
    [Serializable]
    public class NamedImage
    {
        [Tooltip("Name used in the tag: <image=thisName>")]
        public string name;
        public Texture2D texture;
    }

    /// <summary>PBR scope an inline image sits inside (from surrounding glow/shine tags).</summary>
    public enum ImagePbrScope { None = 0, Glow = 1, Shine = 2 }

    /// <summary>One image draw request produced during layout (mesh-local pixel space).</summary>
    public struct InlineImageDraw
    {
        public Texture2D texture;
        public Rect localRect;  // in the zone mesh's local space (same space as glyph vertices)
        public bool underText;  // draw before the glyphs (image behind the text)
        public ImagePbrScope scope;    // PBR scope the tag sits inside
        public string scopeColorHex;   // glow scope color (null = zone emission color)
        public float scopeIntensity;   // glow i= multiplier (1 when unset)
        public float scopeMetallic;    // surface m= (-1 = unchanged)
        public float scopeSmoothness;  // surface s= (-1 = unchanged)
        public float scopeHeight;      // surface h= (NaN = unset)
        public bool hasBlendMode;      // inherited <blend> or direct image blend=
        public MeshTextBlendMode blendMode;
        public float opacity;          // inherited/direct opacity multiplier
    }

    /// <summary>Parsed request before layout: anchor char index in the OUTPUT string.</summary>
    public struct InlineImageRequest
    {
        public int anchorIndex;     // index of the U+200B anchor in the processed string
        public Texture2D texture;
        public float heightPercent; // of font size
        public float widthPercent;  // of font size; <= 0 → auto from texture aspect
        public bool displace;       // true → text shifted by <space>; false → overlay
        public bool underText;      // true → drawn under the glyphs
        public Vector2 pivot;       // 0..1; (0,0) = bottom-left on the baseline (default)
        public ImagePbrScope scope;    // PBR scope the tag sits inside (glow/shine/none)
        public string scopeColorHex;   // glow scope color (null = zone default)
        public float scopeIntensity;   // glow i= (1 when unset)
        public float scopeMetallic;    // surface m= (-1 = unchanged)
        public float scopeSmoothness;  // surface s= (-1 = unchanged)
        public float scopeHeight;      // surface h= (NaN = unset)
        public bool hasBlendMode;
        public MeshTextBlendMode blendMode;
        public float opacity;
    }

    /// <summary>Maps a processed-string region back to its source-tag region.</summary>
    public struct ImageTagSegment
    {
        public int srcStart;  // tag start in the source string
        public int srcLen;    // tag length in the source string
        public int procStart; // replacement start in the processed string
        public int procLen;   // replacement length in the processed string
    }

    public static class InlineImageParser
    {
        private struct InheritedScopeState
        {
            public ImagePbrScope pbr;
            public string color;
            public float intensity;
            public float metallic;
            public float smoothness;
            public float height;
            public bool hasBlend;
            public MeshTextBlendMode blend;
            public float opacity;
        }

        public const char AnchorChar = '\u200B'; // zero-width space: laid out, zero advance
        public const string ResourcesPrefix = "res:";
        public const string FilePrefix = "file:";

        // Loose-file texture cache: path → (texture, file write time). The write time lets a
        // translator overwrite the PNG and simply re-bake — the change is picked up without
        // restarting anything.
        private static readonly Dictionary<string, CachedFileImage> s_FileCache =
            new Dictionary<string, CachedFileImage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hard cap for loose-file images (bytes). Prevents OOM on huge PNGs.</summary>
        public const int MaxFileImageBytes = 16 * 1024 * 1024;

        private struct CachedFileImage
        {
            public Texture2D texture;
            public long writeTimeTicks;
        }

        [ThreadStatic] private static StringBuilder s_Sb;

        public static bool HasImageTags(string text)
            => !string.IsNullOrEmpty(text) &&
               text.IndexOf("<image=", StringComparison.Ordinal) >= 0;

        /// <summary>Process without index mapping (draw pass).</summary>
        public static string Process(string text, float fontSizePx, IList<NamedImage> library,
            List<InlineImageRequest> requests, List<string> warnings, float spacePixelFactor = 1f)
            => ProcessWithMap(text, fontSizePx, library, requests, warnings, null, spacePixelFactor);

        /// <summary>
        /// Replaces &lt;image=...&gt; tags with anchor (+ spacing) markup. When
        /// <paramref name="segments"/> is non-null, records processed↔source region mapping
        /// (book measuring). Unknown images strip the tag and warn; flow stays intact.
        /// </summary>
        public static string ProcessWithMap(string text, float fontSizePx, IList<NamedImage> library,
            List<InlineImageRequest> requests, List<string> warnings, List<ImageTagSegment> segments,
            float spacePixelFactor = 1f)
        {
            if (!HasImageTags(text)) return text;

            if (s_Sb == null) s_Sb = new StringBuilder(text.Length + 64);
            s_Sb.Clear();
            var sb = s_Sb;

            int i = 0;
            int len = text.Length;

            // Images inherit nested PBR/blend scopes. Save the complete state for every custom
            // link because all TMP links share the same </link> closer.
            var inherited = new InheritedScopeState
            {
                pbr = ImagePbrScope.None,
                intensity = 1f,
                metallic = -1f,
                smoothness = -1f,
                height = float.NaN,
                blend = MeshTextBlendMode.Normal,
                opacity = 1f
            };
            var scopeStack = new Stack<InheritedScopeState>(4);

            while (i < len)
            {
                // Markdown code spans become <noparse>; image examples inside must remain literal.
                if (text[i] == '<' && MatchAt(text, i, "<noparse>"))
                {
                    int end = text.IndexOf("</noparse>", i + 9, StringComparison.Ordinal);
                    if (end < 0) { sb.Append(text, i, len - i); break; }
                    int after = end + 10;
                    sb.Append(text, i, after - i);
                    i = after;
                    continue;
                }
                if (text[i] == '<' && MatchAt(text, i, "<link=\"mtb-"))
                {
                    int lc = text.IndexOf('>', i);
                    if (lc > i)
                    {
                        string ltag = text.Substring(i, lc - i + 1);
                        scopeStack.Push(inherited);
                        if (ltag.StartsWith(RichTextPbrTags.GlowLinkPrefix, StringComparison.Ordinal))
                        {
                            inherited.pbr = ImagePbrScope.Glow;
                            RichTextPbrTags.ParseGlowParams(ltag, out inherited.color, out inherited.intensity);
                        }
                        else if (ltag.StartsWith(RichTextPbrTags.ShineLinkPrefix, StringComparison.Ordinal))
                        {
                            inherited.pbr = ImagePbrScope.Shine;
                            RichTextPbrTags.ParseSurfaceParams(ltag, out inherited.metallic,
                                out inherited.smoothness, out inherited.height);
                        }
                        else if (RichTextBlendTags.TryParseLink(ltag, out var blend))
                        {
                            inherited.hasBlend = true;
                            inherited.blend = blend;
                        }
                        else if (RichTextOpacityTags.TryParseLink(ltag, out float opacity))
                        {
                            inherited.opacity *= opacity;
                        }
                        sb.Append(ltag);
                        i = lc + 1;
                        continue;
                    }
                }
                if (text[i] == '<' && MatchAt(text, i, "</link>"))
                {
                    if (scopeStack.Count > 0) inherited = scopeStack.Pop();
                    sb.Append("</link>");
                    i += 7;
                    continue;
                }

                if (text[i] == '<' && MatchAt(text, i, "<image="))
                {
                    int close = text.IndexOf('>', i);
                    if (close > i && close - i < 200)
                    {
                        string body = text.Substring(i + 7, close - i - 7);
                        ParseBody(body, out string name, out float hPct, out float wPct,
                            out bool displace, out bool under, out Vector2 pivot,
                            out bool hasDirectBlend, out MeshTextBlendMode directBlend,
                            out float directOpacity);

                        Texture2D tex = Resolve(library, name, warnings);
                        int procStart = sb.Length;

                        if (tex != null)
                        {
                            float hPx = fontSizePx * (hPct / 100f);
                            float wPx = wPct > 0f
                                ? fontSizePx * (wPct / 100f)
                                : (tex.height > 0 ? hPx * ((float)tex.width / tex.height) : hPx);

                            requests?.Add(new InlineImageRequest
                            {
                                anchorIndex = sb.Length,
                                texture = tex,
                                heightPercent = hPct,
                                widthPercent = wPct,
                                displace = displace,
                                underText = under,
                                pivot = pivot,
                                scope = inherited.pbr,
                                scopeColorHex = inherited.color,
                                scopeIntensity = inherited.intensity,
                                scopeMetallic = inherited.metallic,
                                scopeSmoothness = inherited.smoothness,
                                scopeHeight = inherited.height,
                                hasBlendMode = hasDirectBlend || inherited.hasBlend,
                                blendMode = hasDirectBlend ? directBlend : inherited.blend,
                                opacity = Mathf.Clamp01(inherited.opacity * directOpacity)
                            });

                            sb.Append(AnchorChar);
                            if (displace)
                            {
                                // The pixel meaning of <space=N> depends on TMP's internal
                                // component scale (both raw px and em gave wrong shifts).
                                // spacePixelFactor is CALIBRATED by the layout engine: the
                                // actual advance produced per requested unit, measured once
                                // per (font, size). Emitting wPx/factor lands exactly wPx.
                                float shift = wPx / Mathf.Max(0.0001f, spacePixelFactor);
                                sb.Append("<space=")
                                  .Append(shift.ToString("0.#", CultureInfo.InvariantCulture))
                                  .Append('>');
                            }
                        }
                        // tex == null → tag dropped entirely (warned inside Resolve)

                        segments?.Add(new ImageTagSegment
                        {
                            srcStart = i,
                            srcLen = close - i + 1,
                            procStart = procStart,
                            procLen = sb.Length - procStart
                        });

                        i = close + 1;
                        continue;
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps an index in the processed string back to the source string. Indices inside
        /// a replaced region snap to the region's tag START (a page break never lands
        /// mid-tag — the whole image moves to the next page).
        /// </summary>
        public static int MapProcessedToSource(int procIndex, List<ImageTagSegment> segments)
        {
            if (segments == null || segments.Count == 0) return procIndex;

            int procOffset = 0; // running (source - processed) delta before current segment
            for (int s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                if (procIndex < seg.procStart)
                    return procIndex + procOffset;
                if (procIndex < seg.procStart + seg.procLen)
                    return seg.srcStart; // inside the replacement → snap to tag start
                procOffset += seg.srcLen - seg.procLen;
            }
            return procIndex + procOffset;
        }

        /// <summary>Pixel size for rect computation after layout.</summary>
        public static void GetPixelSize(InlineImageRequest req, float fontSizePx,
            out float wPx, out float hPx)
        {
            hPx = fontSizePx * (req.heightPercent / 100f);
            wPx = req.widthPercent > 0f
                ? fontSizePx * (req.widthPercent / 100f)
                : (req.texture != null && req.texture.height > 0
                    ? hPx * ((float)req.texture.width / req.texture.height)
                    : hPx);
        }

        private static void ParseBody(string body, out string name, out float hPct, out float wPct,
            out bool displace, out bool under, out Vector2 pivot,
            out bool hasBlend, out MeshTextBlendMode blendMode, out float opacity)
        {
            hPct = 100f;
            wPct = -1f;
            displace = true;
            under = false;
            pivot = Vector2.zero; // bottom-left on the baseline
            hasBlend = false;
            blendMode = MeshTextBlendMode.Normal;
            opacity = 1f;

            string[] parts = body.Split(' ');
            name = parts.Length > 0 ? parts[0].Trim().Trim('"') : "";

            for (int p = 1; p < parts.Length; p++)
            {
                string part = parts[p].Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).ToLowerInvariant();
                string val = part.Substring(eq + 1);

                if (key == "h" && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float h))
                    hPct = Mathf.Clamp(h, 1f, 2000f);
                else if (key == "w" && float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
                    wPct = Mathf.Clamp(w, 1f, 2000f);
                else if (key == "displace")
                    displace = !val.Equals("false", StringComparison.OrdinalIgnoreCase) && val != "0";
                else if (key == "under")
                    under = val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1";
                else if (key == "pivot")
                    pivot = ParsePivot(val);
                else if (key == "blend" && MeshTextBlendModeUtility.TryParse(val, out var parsedBlend))
                {
                    hasBlend = true;
                    blendMode = parsedBlend;
                }
                else if (key == "opacity" && float.TryParse(val, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float parsedOpacity))
                    opacity = Mathf.Clamp01(parsedOpacity);
            }
        }

        private static Vector2 ParsePivot(string val)
        {
            switch (val.ToLowerInvariant())
            {
                case "br": return new Vector2(1f, 0f);
                case "tl": return new Vector2(0f, 1f);
                case "tr": return new Vector2(1f, 1f);
                case "c":
                case "center": return new Vector2(0.5f, 0.5f);
                case "bl":
                default: return Vector2.zero;
            }
        }

        /// <summary>
        /// Resolves the image source:
        ///   "res:Path"  — Resources folder (content shipped inside the build);
        ///   "file:Path" — loose PNG/JPG on disk (fan translators: no project needed —
        ///                 drop the file next to the game and reference it from the text);
        ///   otherwise   — the Image Library (name → Texture2D).
        /// </summary>
        private static Texture2D Resolve(IList<NamedImage> library, string name, List<string> warnings)
        {
            if (string.IsNullOrEmpty(name)) return null;

            ILocalizedContentResolver content = MeshTextLocalizationRuntime.ContentResolver;
            if (content != null && content.TryResolveFile(LocalizedContentKind.Image, name,
                MeshTextLocalizationRuntime.CurrentLocale, out string localizedPath))
            {
                Texture2D localized = LoadResolvedFileImage(localizedPath, name, warnings);
                if (localized != null) return localized;
            }

            if (name.StartsWith(ResourcesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = name.Substring(ResourcesPrefix.Length);
                var loaded = Resources.Load<Texture2D>(path);
                if (loaded == null)
                    warnings?.Add($"<image={name}>: Resources.Load found no Texture2D at '{path}' — tag skipped.");
                return loaded;
            }

            if (name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = name.Substring(FilePrefix.Length);
                Texture2D file = LoadFileImage(relative, null);
                if (file != null) return file;
                // A translated file tag can still fall back to the embedded original by basename:
                // file:images/seal.png → Image Library alias "seal".
                string alias = System.IO.Path.GetFileNameWithoutExtension(relative);
                Texture2D original = FindLibraryTexture(library, alias);
                if (original != null) return original;
                warnings?.Add($"<image={name}>: no localized/global file or original alias '{alias}'.");
                return null;
            }

            Texture2D libraryTexture = FindLibraryTexture(library, name);
            if (libraryTexture != null) return libraryTexture;
            warnings?.Add($"<image={name}>: no external override or Image Library entry — tag skipped.");
            return null;
        }

        private static Texture2D FindLibraryTexture(IList<NamedImage> library, string alias)
        {
            if (library == null || string.IsNullOrEmpty(alias)) return null;
            for (int i = 0; i < library.Count; i++)
            {
                NamedImage item = library[i];
                if (item != null && item.texture != null &&
                    string.Equals(item.name, alias, StringComparison.OrdinalIgnoreCase))
                    return item.texture;
            }
            return null;
        }

        // ── Loose-file source (fan-translation friendly) ─────────────────────────────
        //
        // Search order for <image=file:ru/stamp.png>:
        //   1. {persistentDataPath}/ru/stamp.png
        //        — user-writable on every platform, survives game updates, no admin rights.
        //          A translator overrides ANY image here without touching the install dir.
        //   2. {StreamingAssets}/ru/stamp.png
        //        — visible folder inside the shipped build ("Game_Data/StreamingAssets/…"):
        //          the natural place for translation packs distributed with the game.
        //   In the EDITOR, StreamingAssets == Assets/StreamingAssets — the same tag works
        //   identically while authoring and in the built game.
        //
        // Relative paths may include subfolders ("ru/stamp.png"). Absolute paths are
        // rejected (portability + safety). ".." is rejected (no escaping the sandbox).

        private static Texture2D LoadFileImage(string relPath, List<string> warnings)
        {
            relPath = relPath.Trim().Trim('"').Replace('\\', '/');
            if (string.IsNullOrEmpty(relPath))
                return null;
            if (System.IO.Path.IsPathRooted(relPath) || relPath.Contains(".."))
            {
                warnings?.Add($"<image=file:{relPath}>: absolute paths and '..' are not allowed — tag skipped.");
                return null;
            }

            string resolved = null;
            string p1 = System.IO.Path.Combine(Application.persistentDataPath, relPath);
            string p2 = System.IO.Path.Combine(Application.streamingAssetsPath, relPath);
            if (System.IO.File.Exists(p1)) resolved = p1;
            else if (System.IO.File.Exists(p2)) resolved = p2;

            if (resolved == null)
            {
                warnings?.Add($"<image=file:{relPath}>: file not found. Looked in " +
                              $"'{p1}' and '{p2}'. Tag skipped.");
                return null;
            }

            return LoadResolvedFileImage(resolved, "file:" + relPath, warnings);
        }

        private static Texture2D LoadResolvedFileImage(string resolved, string displayName,
            List<string> warnings)
        {
            try
            {
                resolved = System.IO.Path.GetFullPath(resolved);
                long writeTicks = System.IO.File.GetLastWriteTimeUtc(resolved).Ticks;
                if (s_FileCache.TryGetValue(resolved, out var cached) &&
                    cached.texture != null && cached.writeTimeTicks == writeTicks)
                    return cached.texture;

                var info = new System.IO.FileInfo(resolved);
                if (info.Length > MaxFileImageBytes)
                {
                    warnings?.Add($"<image={displayName}>: file is {info.Length / (1024 * 1024.0):0.#} MB " +
                                  $"(limit {MaxFileImageBytes / (1024 * 1024)} MB) — tag skipped.");
                    return null;
                }

                byte[] bytes = System.IO.File.ReadAllBytes(resolved);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
                {
                    name = "MTB_File_" + System.IO.Path.GetFileName(resolved),
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.DontSave
                };
                if (!tex.LoadImage(bytes))
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
                    else UnityEngine.Object.DestroyImmediate(tex);
                    warnings?.Add($"<image={displayName}>: not a valid PNG/JPG — tag skipped.");
                    return null;
                }

                if (s_FileCache.TryGetValue(resolved, out var stale) && stale.texture != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(stale.texture);
                    else UnityEngine.Object.DestroyImmediate(stale.texture);
                }
                s_FileCache[resolved] = new CachedFileImage { texture = tex, writeTimeTicks = writeTicks };
                return tex;
            }
            catch (Exception e)
            {
                warnings?.Add($"<image={displayName}>: load failed ({e.Message}) — tag skipped.");
                return null;
            }
        }

        /// <summary>Releases all loose-file textures (domain reload / shutdown).</summary>
        public static void ReleaseFileCache()
        {
            foreach (var kv in s_FileCache)
            {
                if (kv.Value.texture == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(kv.Value.texture);
                else UnityEngine.Object.DestroyImmediate(kv.Value.texture);
            }
            s_FileCache.Clear();
        }

        private static bool MatchAt(string text, int pos, string token)
        {
            if (pos + token.Length > text.Length) return false;
            for (int k = 0; k < token.Length; k++)
                if (text[pos + k] != token[k]) return false;
            return true;
        }
    }
}
