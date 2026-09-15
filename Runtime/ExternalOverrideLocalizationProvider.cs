// Mesh Text Baker — ExternalOverrideLocalizationProvider.cs
// Optional, MeshTextBaker-scoped loose-file overlay. It decorates Unity Localization,
// I2, or any custom ITextLocalizationProvider instead of replacing the game's system.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MeshTextBaker
{
    [DefaultExecutionOrder(-10000)]
    [AddComponentMenu("Mesh Text Baker/External Override Localization Provider")]
    public class ExternalOverrideLocalizationProvider : MonoBehaviour, ITextLocalizationProviderV2,
        ILocalizedContentResolver, ILocaleSelectionProvider
    {
        [Tooltip("Unity Localization adapter, I2 bridge, or another provider. Loose files are " +
                 "checked first; missing overrides fall through to this provider.")]
        [SerializeField] private MonoBehaviour baseProviderBehaviour;
        [SerializeField] private string currentLocale = "en";
        [SerializeField] private string fallbackLocale = "en";
        [Tooltip("Optional. Absolute path, or path relative to the executable directory. " +
                 "Empty = <GameRoot>/Localization (Editor: <ProjectRoot>/Localization).")]
        [SerializeField] private string localizationRootOverride = "";
        [SerializeField] private bool initializeOnAwake = true;
        [Tooltip("Added to this decorator's built-in default priority (100). Higher wins when " +
                 "several providers register as scene default; 0 keeps the built-in order " +
                 "(decorator above its base provider).")]
        [SerializeField] private int defaultPriorityOffset;

        /// <summary>Built-in scene-default priority: the decorator outranks plain providers.</summary>
        public const int BaseDefaultPriority = 100;

        private ExternalLocalizationStore _external;
        private ITextLocalizationProvider _baseProvider;
        private ITextLocalizationProvider _baseProviderRuntimeOverride;
        private readonly List<LocalizationLocaleInfo> _available = new List<LocalizationLocaleInfo>();
        private bool _initialized;
        private int _baseLocalizationChangeVersion;

        public string CurrentLocale
        {
            get
            {
                if (_baseProvider is ILocaleSelectionProvider selector &&
                    !string.IsNullOrWhiteSpace(selector.CurrentLocale)) return selector.CurrentLocale;
                return currentLocale;
            }
        }
        public string FallbackLocale => fallbackLocale;
        public bool IsReady
        {
            get
            {
                if (!_initialized) return false;
                return !(_baseProvider is ITextLocalizationProviderV2 v2) || v2.IsReady;
            }
        }
        public string GameRoot { get; private set; }
        public string LocalizationRoot { get; private set; }
        public event Action LocalizationChanged;

        /// <summary>
        /// Base provider used at runtime. Assigning here (bootstrap code, tests) overrides the
        /// serialized Base Provider and rewires change events immediately. Assigning null reverts
        /// to the serialized configuration right away.
        /// </summary>
        public ITextLocalizationProvider baseProvider
        {
            get => _baseProvider;
            set
            {
                if (value != null && ReferenceEquals(_baseProvider, value) &&
                    ReferenceEquals(_baseProviderRuntimeOverride, value)) return;
                if (_baseProvider is ITextLocalizationProviderV2 oldProvider)
                    oldProvider.LocalizationChanged -= OnBaseLocalizationChanged;
                _baseProviderRuntimeOverride = value;
                if (value != null) _baseProvider = value;
                else ResolveBaseProvider(); // revert to the serialized configuration
                if (_baseProvider is ITextLocalizationProviderV2 v2)
                {
                    v2.LocalizationChanged -= OnBaseLocalizationChanged;
                    v2.LocalizationChanged += OnBaseLocalizationChanged;
                    if (_initialized) v2.Initialize();
                }
                if (_initialized)
                {
                    RebuildAvailableLocales();
                    MeshTextLocalizationRuntime.SetContext(this, CurrentLocale);
                }
            }
        }

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
            MeshTextLocalizationRuntime.RegisterDefaultProvider(this,
                BaseDefaultPriority + defaultPriorityOffset);
        }

        private void OnDisable()
        {
            MeshTextLocalizationRuntime.UnregisterDefaultProvider(this);
        }

        private void OnDestroy()
        {
            MeshTextLocalizationRuntime.ClearResolver(this);
            MeshTextLocalizationRuntime.UnregisterDefaultProvider(this);
            if (_baseProvider is ITextLocalizationProviderV2 v2)
                v2.LocalizationChanged -= OnBaseLocalizationChanged;
        }

        public void Initialize()
        {
            if (string.IsNullOrWhiteSpace(currentLocale)) currentLocale = "en";
            if (string.IsNullOrWhiteSpace(fallbackLocale)) fallbackLocale = "en";
            if (_baseProvider is ITextLocalizationProviderV2 oldProvider)
                oldProvider.LocalizationChanged -= OnBaseLocalizationChanged;
            ResolveBaseProvider();
            if (_baseProvider is ITextLocalizationProviderV2 v2)
            {
                v2.LocalizationChanged -= OnBaseLocalizationChanged;
                v2.LocalizationChanged += OnBaseLocalizationChanged;
                v2.Initialize();
            }
            GameRoot = ResolveGameRoot();
            LocalizationRoot = ResolveLocalizationRoot(GameRoot);
            _external = new ExternalLocalizationStore(GameRoot, LocalizationRoot);
            _initialized = true;
            RebuildAvailableLocales();
            MeshTextLocalizationRuntime.SetContext(this, CurrentLocale);
        }

        public void ReloadLocalization()
        {
            _initialized = false;
            Initialize();
            LocalizationChanged?.Invoke();
        }

        public void SetLocale(string locale)
        {
            if (string.IsNullOrWhiteSpace(locale)) return;
            currentLocale = locale.Trim();
            int changeVersionBeforeDelegate = _baseLocalizationChangeVersion;
            if (_baseProvider is ILocaleSelectionProvider selector &&
                !string.Equals(selector.CurrentLocale, currentLocale, StringComparison.OrdinalIgnoreCase))
                selector.SetLocale(currentLocale);

            // A delegated provider may raise its event synchronously from SetLocale. In that
            // case OnBaseLocalizationChanged already updated the context and notified listeners;
            // do not emit the same logical change a second time.
            if (_baseLocalizationChangeVersion == changeVersionBeforeDelegate)
            {
                MeshTextLocalizationRuntime.SetContext(this, CurrentLocale);
                LocalizationChanged?.Invoke();
            }
        }

        public string GetText(string key, string locale)
        {
            string requested = string.IsNullOrWhiteSpace(locale) ? CurrentLocale : locale;
            foreach (string candidate in ExternalLocalizationStore.EnumerateLocaleHierarchy(requested, false))
                if (TryGetTextExact(key, candidate, out LocalizedTextResult result)) return result.text;
            if (!string.Equals(requested, fallbackLocale, StringComparison.OrdinalIgnoreCase) &&
                TryGetTextExact(key, fallbackLocale, out LocalizedTextResult fallback)) return fallback.text;
            if (!string.Equals(fallbackLocale, "en", StringComparison.OrdinalIgnoreCase) &&
                TryGetTextExact(key, "en", out fallback)) return fallback.text;
            return "";
        }

        public bool TryGetTextExact(string key, string locale, out LocalizedTextResult result)
        {
            if (!_initialized) Initialize();
            result = LocalizedTextResult.Missing(locale);
            if (string.IsNullOrWhiteSpace(key)) return false;

            if (_external != null && _external.TryGetTextExact(key, locale, out string externalText))
            {
                result = new LocalizedTextResult
                {
                    found = true, text = externalText, requestedLocale = locale,
                    resolvedLocale = locale, source = LocalizationTextSource.ExternalFile
                };
                return true;
            }
            if (_baseProvider is ITextLocalizationProviderV2 exact &&
                exact.TryGetTextExact(key, locale, out result) && result.found &&
                !string.IsNullOrEmpty(result.text))
            {
                if (result.source == LocalizationTextSource.None)
                    result.source = LocalizationTextSource.CustomProvider;
                return true;
            }
            if (_baseProvider != null)
            {
                string text = _baseProvider.GetText(key, locale);
                if (!string.IsNullOrEmpty(text))
                {
                    result = new LocalizedTextResult
                    {
                        found = true, text = text, requestedLocale = locale,
                        resolvedLocale = locale, source = LocalizationTextSource.CustomProvider
                    };
                    return true;
                }
            }
            return false;
        }

        public IReadOnlyList<LocalizationLocaleInfo> GetAvailableLocales()
        {
            if (!_initialized) Initialize();
            return _available;
        }

        public bool TryResolveFile(LocalizedContentKind kind, string reference, string locale,
            out string absolutePath)
        {
            absolutePath = null;
            if (!_initialized) Initialize();
            return _external != null && _external.TryResolveFile(kind, reference,
                string.IsNullOrWhiteSpace(locale) ? CurrentLocale : locale, out absolutePath);
        }

        private void ResolveBaseProvider()
        {
            if (_baseProviderRuntimeOverride != null)
            {
                if (_baseProviderRuntimeOverride is UnityEngine.Object unityObject &&
                    unityObject == null)
                {
                    _baseProviderRuntimeOverride = null; // override target was destroyed
                }
                else
                {
                    _baseProvider = _baseProviderRuntimeOverride;
                    return;
                }
            }
            _baseProvider = baseProviderBehaviour as ITextLocalizationProvider;
            if (_baseProvider == null)
            {
                MonoBehaviour[] components = GetComponents<MonoBehaviour>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == this || !(components[i] is ITextLocalizationProvider candidate)) continue;
                    _baseProvider = candidate;
                    break;
                }
            }
            if (ReferenceEquals(_baseProvider, this)) _baseProvider = null;
        }

        private void RebuildAvailableLocales()
        {
            _available.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_external != null)
                foreach (LocalizationLocaleInfo info in _external.Locales)
                    if (info != null && seen.Add(info.code)) _available.Add(info);
            if (_baseProvider is ITextLocalizationProviderV2 v2)
            {
                IReadOnlyList<LocalizationLocaleInfo> baseLocales = v2.GetAvailableLocales();
                if (baseLocales != null)
                    for (int i = 0; i < baseLocales.Count; i++)
                    {
                        LocalizationLocaleInfo info = baseLocales[i];
                        if (info != null && seen.Add(info.code)) _available.Add(info);
                    }
            }
            if (seen.Add(fallbackLocale))
                _available.Add(new LocalizationLocaleInfo { code = fallbackLocale, displayName = fallbackLocale });
            _available.Sort((a, b) => string.Compare(a.code, b.code, StringComparison.OrdinalIgnoreCase));
        }

        private void OnBaseLocalizationChanged()
        {
            _baseLocalizationChangeVersion++;
            if (_baseProvider is ILocaleSelectionProvider selector &&
                !string.IsNullOrWhiteSpace(selector.CurrentLocale)) currentLocale = selector.CurrentLocale;
            MeshTextLocalizationRuntime.SetContext(this, CurrentLocale);
            RebuildAvailableLocales();
            LocalizationChanged?.Invoke();
        }

        private static string ResolveGameRoot()
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                return parent != null ? parent.FullName : Directory.GetCurrentDirectory();
            }
            catch { return Directory.GetCurrentDirectory(); }
        }

        private string ResolveLocalizationRoot(string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(localizationRootOverride))
                return Path.Combine(gameRoot, "Localization");
            return Path.IsPathRooted(localizationRootOverride)
                ? Path.GetFullPath(localizationRootOverride)
                : Path.GetFullPath(Path.Combine(gameRoot, localizationRootOverride));
        }
    }
}
