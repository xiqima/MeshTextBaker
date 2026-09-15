// Mesh Text Baker — MeshTextLocalizationExportWindow.cs
// Exports every keyed MeshTextAsset into the same loose-file layout read by
// ExternalOverrideLocalizationProvider. This is deliberately MTB-scoped, not a UI/dialogue scanner.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MeshTextBaker.Editor
{
    public class MeshTextLocalizationExportWindow : EditorWindow
    {
        private string _locale = "en";
        private string _outputRoot = "";
        private int _longTextThreshold = 512;
        private bool _overwrite = true;

        [MenuItem("Tools/Mesh Text Baker/Localization Export")]
        public static void Open() => GetWindow<MeshTextLocalizationExportWindow>("MTB Localization Export");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Export MeshTextAsset Baseline", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Exports keyed MeshTextAsset content only. Unity UI/dialogue localization remains " +
                "owned by Unity Localization, I2, or your existing system.", MessageType.Info);
            _locale = EditorGUILayout.TextField("Source Locale", _locale);
            _longTextThreshold = EditorGUILayout.IntSlider("Long Text Threshold", _longTextThreshold, 128, 4096);
            _overwrite = EditorGUILayout.Toggle("Overwrite Existing", _overwrite);

            EditorGUILayout.BeginHorizontal();
            _outputRoot = EditorGUILayout.TextField("Output Root", _outputRoot);
            if (GUILayout.Button("Browse", GUILayout.Width(70)))
            {
                string start = string.IsNullOrEmpty(_outputRoot)
                    ? Directory.GetParent(Application.dataPath)?.FullName : _outputRoot;
                string picked = EditorUtility.OpenFolderPanel("Localization output root", start, "Localization");
                if (!string.IsNullOrEmpty(picked)) _outputRoot = picked;
            }
            EditorGUILayout.EndHorizontal();

            string target = string.IsNullOrEmpty(_outputRoot) ? "(choose a folder)"
                : Path.Combine(_outputRoot, "Localization", _locale);
            EditorGUILayout.LabelField("Will export to", target, EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_locale) || string.IsNullOrWhiteSpace(_outputRoot)))
            {
                if (GUILayout.Button("Export Baseline", GUILayout.Height(30)))
                {
                    try
                    {
                        ExportReport report = MeshTextLocalizationExporter.Export(
                            _outputRoot, _locale, _longTextThreshold, _overwrite);
                        EditorUtility.DisplayDialog("MeshTextBaker Localization Export", report.ToString(), "OK");
                        if (!string.IsNullOrEmpty(report.outputFolder)) EditorUtility.RevealInFinder(report.outputFolder);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                        EditorUtility.DisplayDialog("MeshTextBaker Localization Export", e.Message, "OK");
                    }
                }
            }
        }
    }

    public struct ExportReport
    {
        public string outputFolder;
        public int assetsScanned;
        public int stringsWritten;
        public int longFilesWritten;
        public int skippedMissingKey;
        public int skippedMissingLocale;
        public int duplicates;
        public override string ToString()
            => $"Output: {outputFolder}\nAssets: {assetsScanned}\nShort strings: {stringsWritten}" +
               $"\nLong files: {longFilesWritten}\nMissing key: {skippedMissingKey}" +
               $"\nMissing locale: {skippedMissingLocale}\nDuplicate keys: {duplicates}";
    }

    public static class MeshTextLocalizationExporter
    {
        public static ExportReport Export(string root, string locale, int longThreshold, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(locale) || locale.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                locale.Contains("/") || locale.Contains("\\") || locale.Contains(".."))
                throw new ArgumentException("Invalid locale folder name.", nameof(locale));
            string output = Path.Combine(Path.GetFullPath(root), "Localization", locale);
            Directory.CreateDirectory(output);
            var report = new ExportReport { outputFolder = output };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jsonFiles = new Dictionary<string, SortedDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            string[] guids = AssetDatabase.FindAssets("t:MeshTextAsset");
            report.assetsScanned = guids.Length;
            Array.Sort(guids, StringComparer.Ordinal);
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                MeshTextAsset asset = AssetDatabase.LoadAssetAtPath<MeshTextAsset>(assetPath);
                if (asset == null) continue;
                string key = NormalizeKey(asset.key);
                if (string.IsNullOrEmpty(key)) { report.skippedMissingKey++; continue; }
                if (!seen.Add(key)) { report.duplicates++; Debug.LogError($"Duplicate MeshTextAsset key '{key}' ({assetPath})."); continue; }
                if (!asset.TryGetTextExact(locale, out string text)) { report.skippedMissingLocale++; continue; }

                bool isLong = text.Length >= longThreshold || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
                bool explicitTableKey = key.Contains("::");
                string[] parts = key.Split('.');
                if (isLong && !explicitTableKey)
                {
                    string relative = string.Join("/", parts) + ".txt";
                    string path = SafeOutputPath(output, relative);
                    if (WriteText(path, text, overwrite)) report.longFilesWritten++;
                    continue;
                }

                string entry;
                string relativeJson;
                if (parts.Length == 1 || explicitTableKey)
                {
                    relativeJson = "strings.json";
                    entry = key;
                }
                else
                {
                    entry = parts[parts.Length - 1];
                    relativeJson = string.Join("/", parts, 0, parts.Length - 1) + ".json";
                }
                if (!jsonFiles.TryGetValue(relativeJson, out var entries))
                {
                    entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    jsonFiles[relativeJson] = entries;
                }
                entries[entry] = text;
            }

            foreach (var pair in jsonFiles)
            {
                string path = SafeOutputPath(output, pair.Key);
                if (File.Exists(path) && !overwrite) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, ToJson(pair.Value), new UTF8Encoding(false));
                report.stringsWritten += pair.Value.Count;
            }

            WriteTranslatorReadme(output, overwrite);
            AssetDatabase.Refresh();
            Debug.Log("[MeshTextBaker] " + report);
            return report;
        }

        private static bool WriteText(string path, string text, bool overwrite)
        {
            if (File.Exists(path) && !overwrite) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text ?? "", new UTF8Encoding(false));
            return true;
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = key.Trim().Replace('/', '.').Replace('\\', '.');
            if (key.Contains("::"))
            {
                // Explicit Unity table keys stay verbatim inside root strings.json; they are not
                // used as file paths, so ':' is safe here.
                return key.Contains("..") ? null : key;
            }
            string[] parts = key.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(parts[i]) || parts[i] == "..") return null;
                foreach (char c in Path.GetInvalidFileNameChars())
                    if (parts[i].IndexOf(c) >= 0) return null;
            }
            return string.Join(".", parts);
        }

        private static string SafeOutputPath(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Localization key escapes output root: " + relative);
            return path;
        }

        private static string ToJson(SortedDictionary<string, string> entries)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            int i = 0;
            foreach (var pair in entries)
            {
                sb.Append("  \"").Append(Escape(pair.Key)).Append("\": \"")
                  .Append(Escape(pair.Value)).Append('"');
                if (++i < entries.Count) sb.Append(',');
                sb.Append('\n');
            }
            return sb.Append("}\n").ToString();
        }

        private static string Escape(string value)
        {
            if (value == null) return "";
            var sb = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void WriteTranslatorReadme(string output, bool overwrite)
        {
            string path = Path.Combine(output, "README_TRANSLATORS.txt");
            if (File.Exists(path) && !overwrite) return;
            const string text =
                "MeshTextBaker translation baseline\n\n" +
                "Encoding: UTF-8. Copy this locale folder and rename it to your locale code.\n" +
                "Do not rename JSON keys or TXT paths. Translate values/text only.\n" +
                "Keep markup such as <newpage>, <image>, <font>, <surface>, <emission>, <blend>, <opacity>.\n" +
                "Locale images go in images/, fonts in fonts/. Missing entries fall back to the base language.\n";
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
