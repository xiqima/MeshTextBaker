// Mesh Text Baker — ExternalLocalizationStore.cs
// GameRoot/Localization/<locale> loose-file overrides for fan translations and mods.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace MeshTextBaker
{
    public sealed class ExternalLocalizationStore : ILocalizedContentResolver
    {
        private sealed class LocaleCatalog
        {
            public readonly Dictionary<string, string> strings =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> longTextFiles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> longTextCache =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly Dictionary<string, LocaleCatalog> _catalogs =
            new Dictionary<string, LocaleCatalog>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _localeDirectories =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<LocalizationLocaleInfo> _locales = new List<LocalizationLocaleInfo>();

        public string GameRoot { get; }
        public string LocalizationRoot { get; }
        public IReadOnlyList<LocalizationLocaleInfo> Locales => _locales;

        public ExternalLocalizationStore(string gameRoot, string localizationRoot)
        {
            GameRoot = Path.GetFullPath(string.IsNullOrEmpty(gameRoot) ? "." : gameRoot);
            LocalizationRoot = Path.GetFullPath(string.IsNullOrEmpty(localizationRoot)
                ? Path.Combine(GameRoot, "Localization") : localizationRoot);
            DiscoverLocales();
        }

        public bool TryGetTextExact(string key, string locale, out string text)
        {
            text = null;
            if (string.IsNullOrWhiteSpace(key) || !IsSafeLocale(locale)) return false;
            LocaleCatalog catalog = GetOrLoadCatalog(locale);
            if (catalog == null) return false;
            if (catalog.strings.TryGetValue(key, out text)) return true;
            if (!catalog.longTextFiles.TryGetValue(key, out string file)) return false;
            if (catalog.longTextCache.TryGetValue(key, out text)) return true;
            try
            {
                text = ReadUtf8(file);
                catalog.longTextCache[key] = text;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshTextBaker] Failed to read localization text '{file}': {e.Message}");
                text = null;
                return false;
            }
        }

        public bool TryResolveFile(LocalizedContentKind kind, string reference, string locale,
            out string absolutePath)
        {
            absolutePath = null;
            if (string.IsNullOrWhiteSpace(reference)) return false;
            string value = reference.Trim().Trim('"').Replace('\\', '/');
            bool explicitFile = value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            if (explicitFile) value = value.Substring(5);
            else if (value.StartsWith("res:", StringComparison.OrdinalIgnoreCase)) return false;
            if (!IsSafeRelative(value)) return false;

            var relativeCandidates = new List<string>(6);
            string defaultFolder = kind == LocalizedContentKind.Image ? "images" : "fonts";
            string ext = Path.GetExtension(value);
            if (explicitFile || value.IndexOf('/') >= 0 || !string.IsNullOrEmpty(ext))
            {
                relativeCandidates.Add(value);
            }
            else
            {
                string[] extensions = kind == LocalizedContentKind.Image
                    ? new[] { ".png", ".jpg", ".jpeg" }
                    : new[] { ".ttf", ".otf" };
                for (int i = 0; i < extensions.Length; i++)
                    relativeCandidates.Add(defaultFolder + "/" + value + extensions[i]);
            }

            // Current locale, then its language parent (ru-RU → ru).
            foreach (string loc in EnumerateLocaleHierarchy(locale, includeEnglish: false))
            {
                string localeRoot = _localeDirectories.TryGetValue(loc, out string discovered)
                    ? discovered : Path.Combine(LocalizationRoot, loc);
                for (int i = 0; i < relativeCandidates.Count; i++)
                    if (TryExistingUnder(localeRoot, relativeCandidates[i], out absolutePath)) return true;
            }

            // Global mod beside the executable: Game/images/... or Game/fonts/....
            for (int i = 0; i < relativeCandidates.Count; i++)
                if (TryExistingUnder(GameRoot, relativeCandidates[i], out absolutePath)) return true;
            return false;
        }

        public static IEnumerable<string> EnumerateLocaleHierarchy(string locale, bool includeEnglish)
        {
            if (!string.IsNullOrWhiteSpace(locale))
            {
                string normalized = locale.Trim();
                yield return normalized;
                int dash = normalized.IndexOfAny(new[] { '-', '_' });
                if (dash > 0)
                {
                    string parent = normalized.Substring(0, dash);
                    if (!parent.Equals(normalized, StringComparison.OrdinalIgnoreCase)) yield return parent;
                }
            }
            if (includeEnglish && !string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase))
                yield return "en";
        }

        private void DiscoverLocales()
        {
            _locales.Clear();
            _localeDirectories.Clear();
            if (!Directory.Exists(LocalizationRoot)) return;
            foreach (string dir in Directory.EnumerateDirectories(LocalizationRoot))
            {
                string code = Path.GetFileName(dir);
                if (!IsSafeLocale(code)) continue;
                var info = new LocalizationLocaleInfo { code = code, displayName = code, fallback = "en" };
                string metadataPath = Path.Combine(dir, "locale.json");
                if (File.Exists(metadataPath))
                {
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (FlatJsonLocalizationParser.TryParse(ReadUtf8(metadataPath), values, out string error))
                    {
                        if (values.TryGetValue("code", out string v) && IsSafeLocale(v) &&
                            !string.Equals(v, code, StringComparison.OrdinalIgnoreCase))
                            Debug.LogWarning($"[MeshTextBaker] locale.json code '{v}' does not match folder '{code}'; folder wins.");
                        if (values.TryGetValue("displayName", out v)) info.displayName = v;
                        if (values.TryGetValue("fallback", out v)) info.fallback = v;
                        if (values.TryGetValue("author", out v)) info.author = v;
                        if (values.TryGetValue("version", out v)) info.version = v;
                    }
                    else Debug.LogWarning($"[MeshTextBaker] Invalid '{metadataPath}': {error}");
                }
                _locales.Add(info);
                _localeDirectories[code] = dir;
            }
            _locales.Sort((a, b) => string.Compare(a.code, b.code, StringComparison.OrdinalIgnoreCase));
        }

        private LocaleCatalog GetOrLoadCatalog(string locale)
        {
            if (_catalogs.TryGetValue(locale, out LocaleCatalog cached)) return cached;
            string root = _localeDirectories.TryGetValue(locale, out string discovered)
                ? discovered : Path.Combine(LocalizationRoot, locale);
            if (!Directory.Exists(root)) return null;
            var catalog = new LocaleCatalog();
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    string lower = relative.ToLowerInvariant();
                    if (lower == "locale.json" || lower == "readme_translators.txt" ||
                        lower.StartsWith("images/") || lower.StartsWith("fonts/"))
                        continue;
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension == ".json") LoadJsonFile(catalog, file, relative);
                    else if (extension == ".txt" || extension == ".md")
                    {
                        string key = PathToKey(relative);
                        AddLongFile(catalog, key, file);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshTextBaker] Failed to index locale '{locale}': {e.Message}");
            }
            _catalogs[locale] = catalog;
            return catalog;
        }

        private static void LoadJsonFile(LocaleCatalog catalog, string file, string relative)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!FlatJsonLocalizationParser.TryParse(ReadUtf8(file), entries, out string error))
            {
                Debug.LogWarning($"[MeshTextBaker] Invalid localization JSON '{file}': {error}");
                return;
            }
            string fileName = Path.GetFileNameWithoutExtension(relative);
            string prefix = PathToKey(relative);
            bool fullKeys = fileName.Equals("strings", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Equals("_all", StringComparison.OrdinalIgnoreCase);
            foreach (var pair in entries)
            {
                string key = fullKeys ? pair.Key : prefix + "." + pair.Key;
                if (catalog.strings.ContainsKey(key))
                    Debug.LogWarning($"[MeshTextBaker] Duplicate external localization key '{key}' in '{file}'.");
                catalog.strings[key] = pair.Value;
            }
        }

        private static void AddLongFile(LocaleCatalog catalog, string key, string file)
        {
            if (catalog.longTextFiles.ContainsKey(key) || catalog.strings.ContainsKey(key))
                Debug.LogWarning($"[MeshTextBaker] Duplicate external localization key '{key}' in '{file}'.");
            catalog.longTextFiles[key] = file;
        }

        private static string PathToKey(string relative)
        {
            string noExt = relative.Substring(0, relative.Length - Path.GetExtension(relative).Length);
            return noExt.Replace('\\', '/').Replace('/', '.');
        }

        private static bool TryExistingUnder(string root, string relative, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root) || !IsSafeRelative(relative)) return false;
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) return false;
            path = candidate;
            return true;
        }

        private static bool IsSafeRelative(string path)
            => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) &&
               path.IndexOf("..", StringComparison.Ordinal) < 0;

        private static bool IsSafeLocale(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return false;
            for (int i = 0; i < locale.Length; i++)
            {
                char c = locale[i];
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return false;
            }
            return true;
        }

        private static string ReadUtf8(string path)
        {
            // StreamReader detects UTF-8 BOM and otherwise uses UTF-8.
            using (var reader = new StreamReader(path, Encoding.UTF8, true)) return reader.ReadToEnd();
        }
    }
}
