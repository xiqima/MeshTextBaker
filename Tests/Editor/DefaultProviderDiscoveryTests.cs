using System;
using System.Collections.Generic;
using MeshTextBaker;
using NUnit.Framework;
using UnityEngine;

namespace MeshTextBaker.Tests
{
    /// <summary>
    /// Automatic scene-default provider discovery: one registered provider serves every
    /// MeshTextSurface with no explicit assignment. Explicit (inspector/setter) providers win.
    /// </summary>
    public class DefaultProviderDiscoveryTests
    {
        private sealed class FakeProvider : ITextLocalizationProvider
        {
            public string GetText(string key, string locale) => "";
        }

        private readonly List<ITextLocalizationProvider> _registered =
            new List<ITextLocalizationProvider>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _registered.Count; i++)
                MeshTextLocalizationRuntime.UnregisterDefaultProvider(_registered[i]);
            _registered.Clear();
        }

        private void Register(ITextLocalizationProvider provider, int priority = 0)
        {
            _registered.Add(provider);
            MeshTextLocalizationRuntime.RegisterDefaultProvider(provider, priority);
        }

        [Test]
        public void NoRegistrationNoSceneProvider_FallsBackToDefault()
        {
            // EditMode tests run in the currently open scene: ignore (don't fail) when the
            // developer's scene legitimately contains a provider.
            if (MeshTextLocalizationRuntime.DefaultProvider != null)
                Assert.Ignore("A default provider is already registered.");
            if (SceneHasAnyProvider())
                Assert.Ignore("The open scene already contains a localization provider.");

            var go = new GameObject("Discovery test surface");
            try
            {
                var surface = go.AddComponent<MeshTextSurface>();
                Assert.NotNull(surface);
                Assert.IsInstanceOf<DefaultTextLocalizationProvider>(surface.localizationProvider);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RegisteredDefault_IsUsedAutomatically()
        {
            var fake = new FakeProvider();
            Register(fake);

            var go = new GameObject("Discovery test surface");
            try
            {
                var surface = go.AddComponent<MeshTextSurface>();
                Assert.AreSame(fake, surface.localizationProvider);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HigherPriority_WinsOverRegistrationOrder()
        {
            var low = new FakeProvider();
            var high = new FakeProvider();
            Register(low, priority: 0);
            Register(high, priority: 100);

            Assert.AreSame(high, MeshTextLocalizationRuntime.DefaultProvider);

            var go = new GameObject("Discovery test surface");
            try
            {
                var surface = go.AddComponent<MeshTextSurface>();
                Assert.AreSame(high, surface.localizationProvider);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Unregister_FallsBackToNextProvider()
        {
            var low = new FakeProvider();
            var high = new FakeProvider();
            Register(low, priority: 0);
            Register(high, priority: 100);
            Assert.AreSame(high, MeshTextLocalizationRuntime.DefaultProvider);

            MeshTextLocalizationRuntime.UnregisterDefaultProvider(high);

            Assert.AreSame(low, MeshTextLocalizationRuntime.DefaultProvider);
        }

        [Test]
        public void ExplicitSetter_WinsOverRegisteredDefault_AndNullReturnsToAuto()
        {
            var registered = new FakeProvider();
            var explicitProvider = new FakeProvider();
            Register(registered);

            var go = new GameObject("Discovery test surface");
            try
            {
                var surface = go.AddComponent<MeshTextSurface>();
                Assert.AreSame(registered, surface.localizationProvider);

                surface.localizationProvider = explicitProvider;
                Assert.AreSame(explicitProvider, surface.localizationProvider);

                surface.localizationProvider = null; // back to automatic
                Assert.AreSame(registered, surface.localizationProvider);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DefaultProviderChanged_FiresOnlyOnEffectiveChange()
        {
            int eventCount = 0;
            Action handler = () => eventCount++;
            MeshTextLocalizationRuntime.DefaultProviderChanged += handler;
            try
            {
                var first = new FakeProvider();
                Register(first); // null -> first : fires
                Assert.AreEqual(1, eventCount);

                var second = new FakeProvider();
                Register(second); // priority tie: most recent wins -> second : fires
                Assert.AreEqual(2, eventCount);

                MeshTextLocalizationRuntime.UnregisterDefaultProvider(first); // best still second
                Assert.AreEqual(2, eventCount);

                MeshTextLocalizationRuntime.UnregisterDefaultProvider(second); // second -> null
                Assert.AreEqual(3, eventCount);
            }
            finally
            {
                MeshTextLocalizationRuntime.DefaultProviderChanged -= handler;
            }
        }

        private static bool SceneHasAnyProvider()
        {
#if UNITY_2023_1_OR_NEWER
#pragma warning disable 618
            MonoBehaviour[] all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore 618
#else
            MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
#endif
            for (int i = 0; i < all.Length; i++)
                if (all[i] is ITextLocalizationProvider) return true;
            return false;
        }
    }
}
