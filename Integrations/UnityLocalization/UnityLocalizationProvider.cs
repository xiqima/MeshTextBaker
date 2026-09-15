// Optional integration. Compiled only when com.unity.localization is installed.
#if MTB_UNITY_LOCALIZATION
using System;
using System.Collections;
using System.Collections.Generic;
using MeshTextBaker;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

namespace MeshTextBaker.UnityLocalization
{
    [DefaultExecutionOrder(-11000)]
    [AddComponentMenu("Mesh Text Baker/Unity Localization Provider")]
    public class UnityLocalizationProvider : MonoBehaviour, ITextLocalizationProviderV2,
        ILocaleSelectionProvider
    {
        [Tooltip("String Table Collection used when a key has no 'Table::Entry' prefix.")]
        [SerializeField] private string defaultTable = "Game";
        [SerializeField] private string tableSeparator = "::";
        [SerializeField] private bool initializeOnAwake = true;
        [Tooltip("Added to this provider's built-in default priority (0). Higher wins when " +
                 "several providers register as scene default; the External Override decorator " +
                 "uses base 100, so it stays preferred over its base provider.")]
        [SerializeField] private int defaultPriorityOffset;

        /// <summary>Built-in scene-default priority for this provider.</summary>
        public const int BaseDefaultPriority = 0;

        private bool _ready;
        private readonly List<LocalizationLocaleInfo> _locales = new List<LocalizationLocaleInfo>();
        public bool IsReady => _ready;
        public string CurrentLocale => LocalizationSettings.SelectedLocale != null
            ? LocalizationSettings.SelectedLocale.Identifier.Code : "en";
        public event Action LocalizationChanged;

        private void Awake()
        {
            if (initializeOnAwake) Initialize();
            // Awake (not just OnEnable): readers in their own Awake (BookController, user
            // bootstrap) must already see the registered default. Registration is idempotent.
            MeshTextLocalizationRuntime.RegisterDefaultProvider(this,
                BaseDefaultPriority + defaultPriorityOffset);
        }

        private void OnEnable()
        {
            LocalizationSettings.SelectedLocaleChanged += OnSelectedLocaleChanged;
            MeshTextLocalizationRuntime.RegisterDefaultProvider(this,
                BaseDefaultPriority + defaultPriorityOffset);
        }

        private void OnDisable()
        {
            LocalizationSettings.SelectedLocaleChanged -= OnSelectedLocaleChanged;
            MeshTextLocalizationRuntime.UnregisterDefaultProvider(this);
        }

        private void OnDestroy()
        {
            MeshTextLocalizationRuntime.UnregisterDefaultProvider(this);
        }

        public void Initialize()
        {
            if (_ready) return;
            if (LocalizationSettings.InitializationOperation.IsDone)
            {
                CompleteInitialization();
                return;
            }
            if (isActiveAndEnabled) StartCoroutine(InitializeRoutine());
        }

        private IEnumerator InitializeRoutine()
        {
            yield return LocalizationSettings.InitializationOperation;
            CompleteInitialization();
        }

        private void CompleteInitialization()
        {
            _ready = true;
            RebuildLocales();
            LocalizationChanged?.Invoke();
        }

        public bool TryGetTextExact(string key, string localeCode, out LocalizedTextResult result)
        {
            result = LocalizedTextResult.Missing(localeCode);
            if (string.IsNullOrWhiteSpace(key)) return false;
            // Synchronous access is intentional: MeshTextBaker's GPU bake is synchronous too.
            // Initialization/table loading should happen before surfaces Start; on WebGL the
            // package cannot WaitForCompletion, so unresolved strings fall back to embedded text.
            if (!_ready && Application.platform == RuntimePlatform.WebGLPlayer) return false;

            var locale = LocalizationSettings.AvailableLocales.GetLocale(localeCode);
            if (locale == null) return false;
            SplitKey(key, out string table, out string entry);
            try
            {
                var tableEntry = LocalizationSettings.StringDatabase.GetTableEntry(
                    table, entry, locale, FallbackBehavior.DontUseFallback);
                if (tableEntry.Entry == null) return false;
                string text = tableEntry.Entry.GetLocalizedString();
                if (string.IsNullOrEmpty(text)) return false;
                result = new LocalizedTextResult
                {
                    found = true,
                    text = text,
                    requestedLocale = localeCode,
                    resolvedLocale = locale.Identifier.Code,
                    source = LocalizationTextSource.UnityLocalization
                };
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshTextBaker] Unity Localization lookup failed for '{key}' " +
                                 $"({localeCode}): {e.Message}", this);
                return false;
            }
        }

        public string GetText(string key, string locale)
            => TryGetTextExact(key, locale, out LocalizedTextResult result) ? result.text : "";

        public IReadOnlyList<LocalizationLocaleInfo> GetAvailableLocales()
        {
            if (_ready && _locales.Count == 0) RebuildLocales();
            return _locales;
        }

        public void SetLocale(string localeCode)
        {
            var locale = LocalizationSettings.AvailableLocales.GetLocale(localeCode);
            if (locale != null) LocalizationSettings.SelectedLocale = locale;
        }

        private void SplitKey(string key, out string table, out string entry)
        {
            int split = string.IsNullOrEmpty(tableSeparator) ? -1
                : key.IndexOf(tableSeparator, StringComparison.Ordinal);
            if (split > 0)
            {
                table = key.Substring(0, split);
                entry = key.Substring(split + tableSeparator.Length);
            }
            else
            {
                table = defaultTable;
                entry = key;
            }
        }

        private void RebuildLocales()
        {
            _locales.Clear();
            if (LocalizationSettings.AvailableLocales == null) return;
            foreach (var locale in LocalizationSettings.AvailableLocales.Locales)
            {
                if (locale == null) continue;
                _locales.Add(new LocalizationLocaleInfo
                {
                    code = locale.Identifier.Code,
                    displayName = locale.LocaleName,
                    fallback = "en"
                });
            }
        }

        private void OnSelectedLocaleChanged(UnityEngine.Localization.Locale locale)
        {
            LocalizationChanged?.Invoke();
        }
    }
}
#endif
