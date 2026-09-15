using System;
using System.IO;
using System.Linq;
using MeshTextBaker;
using NUnit.Framework;

namespace MeshTextBaker.Tests
{
    public class ExternalLocalizationTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "MTB_Loc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Localization", "ru", "ui"));
            Directory.CreateDirectory(Path.Combine(_root, "Localization", "ru", "books"));
            Directory.CreateDirectory(Path.Combine(_root, "Localization", "ru", "images"));
            File.WriteAllText(Path.Combine(_root, "Localization", "ru", "locale.json"),
                "{\"displayName\":\"Русский\",\"fallback\":\"en\"}");
            File.WriteAllText(Path.Combine(_root, "Localization", "ru", "ui", "main_menu.json"),
                "{\"play\":\"Играть\",\"line\":\"one\\ntwo\"}");
            File.WriteAllText(Path.Combine(_root, "Localization", "ru", "books", "grimoire.txt"),
                "# Гримуар\nТекст книги");
            File.WriteAllBytes(Path.Combine(_root, "Localization", "ru", "images", "seal.png"),
                new byte[] { 1, 2, 3 });
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void JsonAndLongText_AreIndexedByRelativePath()
        {
            var store = new ExternalLocalizationStore(_root, Path.Combine(_root, "Localization"));
            Assert.IsTrue(store.TryGetTextExact("ui.main_menu.play", "ru", out string play));
            Assert.AreEqual("Играть", play);
            Assert.IsTrue(store.TryGetTextExact("ui.main_menu.line", "ru", out string line));
            Assert.AreEqual("one\ntwo", line);
            Assert.IsTrue(store.TryGetTextExact("books.grimoire", "ru", out string book));
            StringAssert.Contains("Гримуар", book);
        }

        [Test]
        public void LocalesAndLocalizedAliasResource_AreDiscovered()
        {
            var store = new ExternalLocalizationStore(_root, Path.Combine(_root, "Localization"));
            Assert.IsTrue(store.Locales.Any(l => l.code == "ru" && l.displayName == "Русский"));
            Assert.IsTrue(store.TryResolveFile(LocalizedContentKind.Image, "seal", "ru", out string path));
            StringAssert.EndsWith("seal.png", path.Replace('\\', '/'));
        }

        [Test]
        public void MissingOverride_ReturnsFalseInsteadOfEmptyText()
        {
            var store = new ExternalLocalizationStore(_root, Path.Combine(_root, "Localization"));
            Assert.IsFalse(store.TryGetTextExact("ui.missing", "ru", out string text));
            Assert.IsNull(text);
        }

        [Test]
        public void JsonWithTrailingGarbage_IsRejectedAsAWhole()
        {
            File.WriteAllText(Path.Combine(_root, "Localization", "ru", "ui", "invalid.json"),
                "{\"first\":\"would be partial\"} trailing");
            var store = new ExternalLocalizationStore(_root, Path.Combine(_root, "Localization"));

            Assert.IsFalse(store.TryGetTextExact("ui.invalid.first", "ru", out string text));
            Assert.IsNull(text);
        }
    }
}
