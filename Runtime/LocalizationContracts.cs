// Mesh Text Baker — LocalizationContracts.cs
// Shared contracts for Unity Localization and loose-file override pipelines.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshTextBaker
{
    public enum LocalizationTextSource
    {
        None = 0,
        ExternalFile = 1,
        UnityLocalization = 2,
        CustomProvider = 3,
        MeshTextAsset = 4
    }

    public struct LocalizedTextResult
    {
        public bool found;
        public string text;
        public string requestedLocale;
        public string resolvedLocale;
        public LocalizationTextSource source;

        public static LocalizedTextResult Missing(string requested)
            => new LocalizedTextResult { requestedLocale = requested, resolvedLocale = requested };
    }

    [Serializable]
    public class LocalizationLocaleInfo
    {
        public string code;
        public string displayName;
        public string fallback;
        public string author;
        public string version;
    }

    /// <summary>
    /// Extended exact-locale API. Implementations MUST NOT silently fall back to another locale;
    /// MeshTextSurface owns the deterministic requested → parent → en → embedded fallback chain.
    /// </summary>
    public interface ITextLocalizationProviderV2 : ITextLocalizationProvider
    {
        bool TryGetTextExact(string key, string locale, out LocalizedTextResult result);
        IReadOnlyList<LocalizationLocaleInfo> GetAvailableLocales();
        bool IsReady { get; }
        event Action LocalizationChanged;
        void Initialize();
    }

    public interface ILocaleSelectionProvider
    {
        string CurrentLocale { get; }
        void SetLocale(string locale);
    }

    public enum LocalizedContentKind { Image, Font }

    /// <summary>Resolves external files relative to the active locale and executable root.</summary>
    public interface ILocalizedContentResolver
    {
        bool TryResolveFile(LocalizedContentKind kind, string reference, string locale,
            out string absolutePath);
    }

    /// <summary>
    /// Runtime context consumed by inline image/font parsers. One game-wide locale is expected;
    /// a surface refreshes the locale before measuring/baking.
    /// </summary>
    public static class MeshTextLocalizationRuntime
    {
        public static ILocalizedContentResolver ContentResolver { get; private set; }
        public static string CurrentLocale { get; private set; } = "en";

        public static void SetContext(ILocalizedContentResolver resolver, string locale)
        {
            ContentResolver = resolver;
            CurrentLocale = string.IsNullOrEmpty(locale) ? "en" : locale;
        }

        public static void ClearResolver(ILocalizedContentResolver resolver)
        {
            if (ReferenceEquals(ContentResolver, resolver)) ContentResolver = null;
        }

        // ── Scene default provider ─────────────────────────────────
        // Providers that want automatic discovery (the built-in ones do) register here in
        // Awake/OnEnable and unregister in OnDisable/OnDestroy. A MeshTextSurface with no
        // explicit provider uses DefaultProvider, so a single provider GameObject serves the
        // whole scene without per-surface wiring. Explicit per-surface assignment always wins.
        private sealed class DefaultEntry
        {
            public ITextLocalizationProvider provider;
            public int priority;
            public long order;
        }

        private static readonly List<DefaultEntry> s_defaultEntries = new List<DefaultEntry>(4);
        private static long s_defaultOrder;
        private static ITextLocalizationProvider s_defaultProvider;

        /// <summary>
        /// Highest-priority registered provider (ties go to the most recently registered),
        /// or null when nothing is registered. Dead Unity references are purged on access.
        /// </summary>
        public static ITextLocalizationProvider DefaultProvider
        {
            get
            {
                PurgeDeadDefaultEntries();
                // Silent recompute (no event): normal destroy paths notify through Unregister;
                // the purge only covers abnormal leftovers. A getter must never fire events —
                // subscribers rebake in response, which would reenter the getter mid-bake.
                s_defaultProvider = FindBestDefaultProvider();
                return s_defaultProvider;
            }
        }

        /// <summary>Fired whenever the effective <see cref="DefaultProvider"/> changes.</summary>
        public static event Action DefaultProviderChanged;

        /// <summary>
        /// Registers a scene-wide default provider. Higher <paramref name="priority"/> wins;
        /// ties go to the most recently registered entry. Re-registering the same instance
        /// updates its priority. Built-in providers self-register; custom providers opt in
        /// with a single call (in Awake and OnEnable, paired with Unregister in OnDisable —
        /// Awake registration matters for readers in their own Awake).
        /// </summary>
        public static void RegisterDefaultProvider(ITextLocalizationProvider provider, int priority = 0)
        {
            if (ReferenceEquals(provider, null) || IsDestroyedUnityObject(provider)) return;
            PurgeDeadDefaultEntries();
            for (int i = 0; i < s_defaultEntries.Count; i++)
            {
                if (!ReferenceEquals(s_defaultEntries[i].provider, provider)) continue;
                s_defaultEntries[i].priority = priority;
                s_defaultEntries[i].order = ++s_defaultOrder;
                UpdateDefaultProvider();
                return;
            }
            s_defaultEntries.Add(new DefaultEntry
            {
                provider = provider, priority = priority, order = ++s_defaultOrder
            });
            UpdateDefaultProvider();
        }

        /// <summary>Removes a provider previously added with <see cref="RegisterDefaultProvider"/>.</summary>
        public static void UnregisterDefaultProvider(ITextLocalizationProvider provider)
        {
            if (ReferenceEquals(provider, null)) return;
            bool removed = false;
            for (int i = s_defaultEntries.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(s_defaultEntries[i].provider, provider)) continue;
                s_defaultEntries.RemoveAt(i);
                removed = true;
            }
            if (removed || IsDestroyedUnityObject(s_defaultProvider))
            {
                PurgeDeadDefaultEntries();
                UpdateDefaultProvider();
            }
        }

        private static void PurgeDeadDefaultEntries()
        {
            for (int i = s_defaultEntries.Count - 1; i >= 0; i--)
            {
                ITextLocalizationProvider candidate = s_defaultEntries[i].provider;
                if (ReferenceEquals(candidate, null) || IsDestroyedUnityObject(candidate))
                    s_defaultEntries.RemoveAt(i);
            }
        }

        private static void UpdateDefaultProvider()
        {
            ITextLocalizationProvider best = FindBestDefaultProvider();
            if (ReferenceEquals(s_defaultProvider, best)) return;
            s_defaultProvider = best;
            DefaultProviderChanged?.Invoke();
        }

        private static ITextLocalizationProvider FindBestDefaultProvider()
        {
            ITextLocalizationProvider best = null;
            int bestPriority = int.MinValue;
            long bestOrder = long.MinValue;
            for (int i = 0; i < s_defaultEntries.Count; i++)
            {
                DefaultEntry entry = s_defaultEntries[i];
                if (entry.priority > bestPriority ||
                    (entry.priority == bestPriority && entry.order > bestOrder))
                {
                    best = entry.provider;
                    bestPriority = entry.priority;
                    bestOrder = entry.order;
                }
            }
            return best;
        }

        private static bool IsDestroyedUnityObject(ITextLocalizationProvider provider)
            => provider is UnityEngine.Object unityObject && unityObject == null;
    }
}
