using System;
using System.IO;
using MeshTextBaker.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MeshTextBaker.Tests
{
    public class LocalizationExporterTests
    {
        private const string AssetFolder = "Assets/__MTBLocalizationExportTests";
        private string _output;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(AssetFolder)) AssetDatabase.CreateFolder("Assets", "__MTBLocalizationExportTests");
            CreateAsset("Short.asset", "ui.test.play", "Play");
            CreateAsset("Book.asset", "books.test_book", "# Test Book\n\nLong body");
            _output = Path.Combine(Path.GetTempPath(), "MTB_Export_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(AssetFolder);
            if (Directory.Exists(_output)) Directory.Delete(_output, true);
        }

        [Test]
        public void Export_WritesJsonTxtAndTranslatorReadme()
        {
            ExportReport report = MeshTextLocalizationExporter.Export(_output, "en", 512, true);
            Assert.GreaterOrEqual(report.stringsWritten, 1);
            Assert.GreaterOrEqual(report.longFilesWritten, 1);
            string root = Path.Combine(_output, "Localization", "en");
            Assert.IsTrue(File.Exists(Path.Combine(root, "ui", "test.json")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "books", "test_book.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "README_TRANSLATORS.txt")));
            StringAssert.Contains("\"play\": \"Play\"", File.ReadAllText(Path.Combine(root, "ui", "test.json")));
        }

        private static void CreateAsset(string file, string key, string text)
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            asset.key = key;
            asset.translations.Add(new LocalizedTextEntry { locale = "en", text = text });
            AssetDatabase.CreateAsset(asset, AssetFolder + "/" + file);
        }
    }
}
