using System;
using System.Collections.Generic;
using MeshTextBaker;
using NUnit.Framework;
using UnityEngine;

namespace MeshTextBaker.Tests
{
    public class LocalizationEventTests
    {
        [Test]
        public void DelegatedSynchronousLocaleChange_RaisesOneWrapperEvent()
        {
            var go = new GameObject("Localization event test");
            var wrapper = go.AddComponent<ExternalOverrideLocalizationProvider>();
            Assert.NotNull(wrapper,
                "AddComponent<ExternalOverrideLocalizationProvider> failed (returned null).");
            var delegated = new SynchronousLocaleProvider();
            try
            {
                // Runtime base-provider injection: the wrapper subscribes to the delegate with
                // its real handler, exactly like Initialize() does for serialized providers.
                wrapper.baseProvider = delegated;

                int eventCount = 0;
                wrapper.LocalizationChanged += () => eventCount++;

                wrapper.SetLocale("fr");

                Assert.AreEqual("fr", delegated.CurrentLocale);
                Assert.AreEqual("fr", wrapper.CurrentLocale);
                Assert.AreEqual(1, eventCount);
            }
            finally
            {
                // Never leak scene-default registration or bake context into other tests,
                // regardless of which (if any) edit-mode callbacks Unity invoked.
                MeshTextLocalizationRuntime.UnregisterDefaultProvider(wrapper);
                MeshTextLocalizationRuntime.ClearResolver(wrapper);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BaseProviderDirectChange_ForwardsCurrentLocaleAndEvent()
        {
            var go = new GameObject("Localization forward test");
            var wrapper = go.AddComponent<ExternalOverrideLocalizationProvider>();
            Assert.NotNull(wrapper,
                "AddComponent<ExternalOverrideLocalizationProvider> failed (returned null).");
            var delegated = new SynchronousLocaleProvider();
            try
            {
                wrapper.baseProvider = delegated;

                int eventCount = 0;
                wrapper.LocalizationChanged += () => eventCount++;

                delegated.SetLocale("de"); // the change originates at the base provider

                Assert.AreEqual("de", wrapper.CurrentLocale);
                Assert.AreEqual(1, eventCount);
            }
            finally
            {
                MeshTextLocalizationRuntime.UnregisterDefaultProvider(wrapper);
                MeshTextLocalizationRuntime.ClearResolver(wrapper);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // NOTE: this fake must stay a plain C# class. Test types live in the Editor-only test
        // assembly, and Unity refuses AddComponent for Editor-assembly types ("Can't add script
        // behaviour ... it is an editor script"). It is injected through the runtime
        // baseProvider property and is never attached to a GameObject.
        private sealed class SynchronousLocaleProvider :
            ITextLocalizationProviderV2, ILocaleSelectionProvider
        {
            public string CurrentLocale { get; private set; } = "en";
            public bool IsReady => true;
            public event Action LocalizationChanged;

            public void SetLocale(string locale)
            {
                CurrentLocale = locale;
                LocalizationChanged?.Invoke();
            }

            public string GetText(string key, string locale) => "";

            public bool TryGetTextExact(string key, string locale,
                out LocalizedTextResult result)
            {
                result = LocalizedTextResult.Missing(locale);
                return false;
            }

            public IReadOnlyList<LocalizationLocaleInfo> GetAvailableLocales()
                => new List<LocalizationLocaleInfo>();

            public void Initialize() { }
        }
    }
}
