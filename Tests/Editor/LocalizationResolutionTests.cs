using System;
using System.Collections.Generic;
using MeshTextBaker;
using NUnit.Framework;
using UnityEngine;

namespace MeshTextBaker.Tests
{
    public class LocalizationResolutionTests
    {
        private sealed class FakeExactProvider : ITextLocalizationProviderV2
        {
            public readonly Dictionary<string, string> values = new Dictionary<string, string>();
            public bool IsReady => true;
            public event Action LocalizationChanged { add { } remove { } }
            public void Initialize() { }
            public IReadOnlyList<LocalizationLocaleInfo> GetAvailableLocales() => Array.Empty<LocalizationLocaleInfo>();
            public string GetText(string key, string locale)
                => TryGetTextExact(key, locale, out var r) ? r.text : "";
            public bool TryGetTextExact(string key, string locale, out LocalizedTextResult result)
            {
                if (values.TryGetValue(locale + ":" + key, out string text))
                {
                    result = new LocalizedTextResult
                    {
                        found = true, text = text, requestedLocale = locale,
                        resolvedLocale = locale, source = LocalizationTextSource.CustomProvider
                    };
                    return true;
                }
                result = LocalizedTextResult.Missing(locale);
                return false;
            }
        }

        [Test]
        public void EmbeddedRequestedLocale_BeatsProviderEnglishFallback()
        {
            var go = new GameObject("localization test");
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            try
            {
                asset.key = "notes.test";
                asset.translations.Add(new LocalizedTextEntry { locale = "ru", text = "Встроенный русский" });
                asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "Embedded English" });
                var provider = new FakeExactProvider();
                provider.values["en:notes.test"] = "Provider English";
                var surface = go.AddComponent<MeshTextSurface>();
                surface.localizationProvider = provider;
                LocalizedTextResult result = surface.ResolveLocalizedText(asset.key, asset, "ru");
                Assert.AreEqual("Встроенный русский", result.text);
                Assert.AreEqual(LocalizationTextSource.MeshTextAsset, result.source);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ExactProviderOverride_BeatsEmbeddedText()
        {
            var go = new GameObject("localization test");
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            try
            {
                asset.key = "notes.test";
                asset.translations.Add(new LocalizedTextEntry { locale = "ru", text = "Embedded" });
                var provider = new FakeExactProvider();
                provider.values["ru:notes.test"] = "External override";
                var surface = go.AddComponent<MeshTextSurface>();
                surface.localizationProvider = provider;
                LocalizedTextResult result = surface.ResolveLocalizedText(asset.key, asset, "ru");
                Assert.AreEqual("External override", result.text);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
