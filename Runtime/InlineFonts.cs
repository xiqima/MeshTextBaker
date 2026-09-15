// Mesh Text Baker — InlineFonts.cs
// Resolves <font=alias|res:path|file:path.ttf> into TMP-native font tags.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;

namespace MeshTextBaker
{
    [Serializable]
    public class NamedFont
    {
        [Tooltip("Alias used by <font=thisName>...</font>.")]
        public string name;
        public TMP_FontAsset font;
    }

    public static class InlineFontResolver
    {
        public const string ResourcesPrefix = "res:";
        public const string FilePrefix = "file:";
        public const int MaxFileFontBytes = 32 * 1024 * 1024;

        private struct CachedFont
        {
            public Font source;
            public TMP_FontAsset asset;
            public long writeTicks;
        }

        private static readonly Dictionary<string, CachedFont> s_FileCache =
            new Dictionary<string, CachedFont>(StringComparer.OrdinalIgnoreCase);
        [ThreadStatic] private static StringBuilder s_Sb;

        public static bool HasFontTags(string text)
            => !string.IsNullOrEmpty(text) && text.IndexOf("<font=", StringComparison.OrdinalIgnoreCase) >= 0;

        public static string Process(string text, IList<NamedFont> library, List<string> warnings = null)
        {
            if (!HasFontTags(text)) return text;
            if (s_Sb == null) s_Sb = new StringBuilder(text.Length + 32);
            s_Sb.Clear();
            int i = 0;
            while (i < text.Length)
            {
                // Markdown code spans are wrapped in <noparse>; font examples stay literal.
                if (text[i] == '<' && StartsWithIgnoreCase(text, i, "<noparse>"))
                {
                    int end = text.IndexOf("</noparse>", i + 9, StringComparison.OrdinalIgnoreCase);
                    if (end < 0) { s_Sb.Append(text, i, text.Length - i); break; }
                    int after = end + 10;
                    s_Sb.Append(text, i, after - i);
                    i = after;
                    continue;
                }
                if (text[i] == '<' && StartsWithIgnoreCase(text, i, "<font="))
                {
                    int close = text.IndexOf('>', i);
                    if (close > i && close - i < 260)
                    {
                        string value = ExtractValue(text.Substring(i + 6, close - i - 6));
                        TMP_FontAsset asset = Resolve(value, library, warnings);
                        if (asset != null)
                        {
                            Register(asset);
                            s_Sb.Append("<font=\"").Append(asset.name).Append("\">");
                            i = close + 1;
                            continue;
                        }
                    }
                }
                s_Sb.Append(text[i++]);
            }
            return s_Sb.ToString();
        }

        private static string ExtractValue(string body)
        {
            body = body.Trim();
            if (body.Length == 0) return "";
            if (body[0] == '"')
            {
                int q = body.IndexOf('"', 1);
                return q > 0 ? body.Substring(1, q - 1) : body.Trim('"');
            }
            int space = body.IndexOf(' ');
            return (space >= 0 ? body.Substring(0, space) : body).Trim('"');
        }

        private static TMP_FontAsset Resolve(string value, IList<NamedFont> library, List<string> warnings)
        {
            if (string.IsNullOrEmpty(value)) return null;

            ILocalizedContentResolver content = MeshTextLocalizationRuntime.ContentResolver;
            if (content != null && content.TryResolveFile(LocalizedContentKind.Font, value,
                MeshTextLocalizationRuntime.CurrentLocale, out string localizedPath))
            {
                TMP_FontAsset localized = LoadResolvedFile(localizedPath, value, warnings);
                if (localized != null) return localized;
            }

            if (value.StartsWith(ResourcesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string path = value.Substring(ResourcesPrefix.Length);
                var tmp = Resources.Load<TMP_FontAsset>(path);
                if (tmp != null) return tmp;
                var source = Resources.Load<Font>(path);
                if (source != null) return CreateDynamic(source, "MTB_Res_" + SafeName(path), warnings);
                warnings?.Add($"<font={value}>: no TMP_FontAsset or Font at Resources/'{path}'.");
                return null;
            }
            if (value.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string relative = value.Substring(FilePrefix.Length);
                TMP_FontAsset file = LoadFile(relative, null);
                if (file != null) return file;
                string alias = Path.GetFileNameWithoutExtension(relative);
                TMP_FontAsset original = FindLibraryFont(library, alias);
                if (original != null) return original;
                warnings?.Add($"<font={value}>: no localized/global file or original alias '{alias}'.");
                return null;
            }

            TMP_FontAsset libraryFont = FindLibraryFont(library, value);
            if (libraryFont != null) return libraryFont;

            // Leave unknown native TMP names untouched. They may live in TMP's configured
            // Resources/Fonts path and will be resolved by TMP itself.
            return null;
        }

        private static TMP_FontAsset FindLibraryFont(IList<NamedFont> library, string alias)
        {
            if (library == null || string.IsNullOrEmpty(alias)) return null;
            for (int i = 0; i < library.Count; i++)
            {
                NamedFont item = library[i];
                if (item != null && item.font != null &&
                    string.Equals(item.name, alias, StringComparison.OrdinalIgnoreCase))
                    return item.font;
            }
            return null;
        }

        private static TMP_FontAsset LoadFile(string relativePath, List<string> warnings)
        {
            relativePath = relativePath.Trim().Trim('"').Replace('\\', '/');
            if (string.IsNullOrEmpty(relativePath)) return null;
            if (Path.IsPathRooted(relativePath) || relativePath.Contains(".."))
            {
                warnings?.Add($"<font=file:{relativePath}>: absolute paths and '..' are not allowed.");
                return null;
            }
            string p1 = Path.Combine(Application.persistentDataPath, relativePath);
            string p2 = Path.Combine(Application.streamingAssetsPath, relativePath);
            string resolved = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);
            if (resolved == null)
            {
                warnings?.Add($"<font=file:{relativePath}>: file not found in '{p1}' or '{p2}'.");
                return null;
            }
            return LoadResolvedFile(resolved, "file:" + relativePath, warnings);
        }

        private static TMP_FontAsset LoadResolvedFile(string resolved, string displayName,
            List<string> warnings)
        {
            try
            {
                resolved = Path.GetFullPath(resolved);
                string ext = Path.GetExtension(resolved).ToLowerInvariant();
                if (ext != ".ttf" && ext != ".otf")
                {
                    warnings?.Add($"<font={displayName}>: only .ttf and .otf are supported.");
                    return null;
                }
                var info = new FileInfo(resolved);
                if (info.Length > MaxFileFontBytes)
                {
                    warnings?.Add($"<font={displayName}>: {info.Length / (1024 * 1024.0):0.#} MB " +
                                  $"exceeds the {MaxFileFontBytes / (1024 * 1024)} MB limit.");
                    return null;
                }
                long ticks = info.LastWriteTimeUtc.Ticks;
                if (s_FileCache.TryGetValue(resolved, out var cached) &&
                    cached.asset != null && cached.writeTicks == ticks)
                    return cached.asset;

                var source = new Font(resolved) { hideFlags = HideFlags.DontSave };
                string uniqueName = "MTB_File_" + SafeName(Path.GetFileNameWithoutExtension(resolved)) +
                                    "_" + StableHash(resolved + ticks).ToString("x8");
                TMP_FontAsset asset = CreateDynamic(source, uniqueName, warnings);
                if (asset == null) { Destroy(source); return null; }

                if (s_FileCache.TryGetValue(resolved, out var stale)) Destroy(stale);
                s_FileCache[resolved] = new CachedFont
                {
                    source = source,
                    asset = asset,
                    writeTicks = ticks
                };
                return asset;
            }
            catch (Exception e)
            {
                warnings?.Add($"<font={displayName}>: load failed ({e.Message}).");
                return null;
            }
        }

        private static TMP_FontAsset CreateDynamic(Font source, string name, List<string> warnings)
        {
            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(source);
            if (asset == null)
            {
                warnings?.Add($"Unable to create a dynamic TMP font asset from '{source.name}'.");
                return null;
            }
            asset.name = name;
            asset.hideFlags = HideFlags.DontSave;
            // CreateFontAsset initializes lookup hashes before we assign our stable runtime name.
            // Rebuild so TMP's <font="name"> hash matches MaterialReferenceManager registration.
            asset.ReadFontAssetDefinition();
            if (asset.material != null)
            {
                asset.material.name = name + " Material";
                asset.material.hideFlags = HideFlags.DontSave;
            }
            Register(asset);
            return asset;
        }

        private static void Register(TMP_FontAsset asset)
        {
            if (asset == null) return;
            // TMP's <font="name"> parser resolves through this runtime registry.
            MaterialReferenceManager.AddFontAsset(asset);
        }

        public static void ReleaseFileCache()
        {
            foreach (var item in s_FileCache.Values) Destroy(item);
            s_FileCache.Clear();
        }

        private static void Destroy(CachedFont item)
        {
            if (item.asset != null)
            {
                if (item.asset.material != null) Destroy(item.asset.material);
                if (item.asset.atlasTextures != null)
                    for (int i = 0; i < item.asset.atlasTextures.Length; i++) Destroy(item.asset.atlasTextures[i]);
                Destroy(item.asset);
            }
            Destroy(item.source);
        }

        private static void Destroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }

        private static string SafeName(string value)
        {
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
                sb.Append(char.IsLetterOrDigit(value[i]) || value[i] == '_' ? value[i] : '_');
            return sb.ToString();
        }

        private static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; i++) hash = (hash ^ value[i]) * 16777619;
                return hash;
            }
        }

        private static bool StartsWithIgnoreCase(string text, int index, string value)
        {
            if (index + value.Length > text.Length) return false;
            return string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }
    }
}
