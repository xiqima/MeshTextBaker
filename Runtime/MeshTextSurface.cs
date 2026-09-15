// Mesh Text Baker — MeshTextSurface.cs
// Main MonoBehaviour. Bakes localized text onto mesh albedo textures (zones in UV-space).
// Works with MeshRenderer or SkinnedMeshRenderer; zones may target child renderers.
// Book mode lays one text out as logical pages baked into PageSlot zones (see Book API).

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MeshTextBaker
{
    /// <summary>
    /// Core component for placing, localizing, and baking text onto mesh surfaces.
    /// Zones are defined in UV-space for camera-independent stability.
    /// </summary>
    [AddComponentMenu("Mesh Text Baker/Mesh Text Surface")]
    [ExecuteAlways]
    public class MeshTextSurface : MonoBehaviour
    {
        // ── References ──────────────────────────────────────────
        // Works with EITHER a MeshRenderer (+MeshFilter) or a SkinnedMeshRenderer on this
        // GameObject. Skinning/animation doesn't affect the bake: only UV0 and materials are
        // needed; the baked texture follows the mesh deformation (it's applied in UV space).
        [SerializeField, HideInInspector] private MeshRenderer _meshRenderer;
        [SerializeField, HideInInspector] private MeshFilter _meshFilter;
        [SerializeField, HideInInspector] private SkinnedMeshRenderer _skinnedRenderer;

        // ── Bake Settings ───────────────────────────────────────
        [SerializeField] private BakeSettings _bakeSettings = new BakeSettings();

        [Tooltip("Bake mode: Manual, EditorBake, or RuntimeBakeOnStart.")]
        [SerializeField] private BakeMode _bakeMode = BakeMode.RuntimeBakeOnStart;

        [Tooltip("Locale code to preview/bake in the editor.")]
        [SerializeField] private string _previewLocale = "en";

        [Tooltip("Optional explicit localization provider for THIS surface. Empty = automatic: " +
                 "the scene's default provider (e.g. one External Override for the whole scene), " +
                 "else embedded MeshTextAsset fallback.")]
        [SerializeField] private MonoBehaviour _localizationProviderBehaviour;

        [Tooltip("Ignore the scene's default provider on THIS surface: with no explicit provider " +
                 "assigned, always fall back to embedded MeshTextAsset text. Off (default) = follow " +
                 "the scene default automatically.")]
        [SerializeField] private bool _ignoreSceneDefaultProvider;

        
        [Tooltip("Single localized text streamed across PageSlot zones. Use <newpage> to force a page break.")]
        [SerializeField] private MeshTextAsset _bookText;
        [Tooltip("Optional global localization key for the book. Empty = use Book Text asset key.")]
        [SerializeField] private string _bookLocalizationKey = "";

        [Tooltip("Convert markdown in the book text to TMP rich text ONCE, before pagination. " +
                 "Keeps measuring and baking perfectly in sync.")]
        [SerializeField] private bool _bookMarkdownEnabled = true;

        [Tooltip("Enable PBR (<emission>/<surface> tags, per-zone emission & mask settings) " +
                 "for ALL PageSlot zones at once. Per-zone 'Use PBR' still governs " +
                 "non-book zones.")]
        [SerializeField] private bool _bookUsePbr = false;

        
        [Tooltip("Default font for PageNumber child zones (null = Bake Settings default font).")]
        [SerializeField] private TMPro.TMP_FontAsset _pageNumberFont;
        [Tooltip("Default font size mode for PageNumber zones.")]
        [SerializeField] private FontSizeMode _pageNumberFontSizeMode = FontSizeMode.PercentOfZoneHeight;
        [Tooltip("Default font size for PageNumber zones (PercentOfZoneHeight recommended).")]
        [SerializeField] private float _pageNumberFontSize = 70f;
        [Tooltip("Default alignment for PageNumber zones.")]
        [SerializeField] private TMPro.TextAlignmentOptions _pageNumberAlignment = TMPro.TextAlignmentOptions.Center;
        [Tooltip("When true, PageNumber zones use Bake Settings default text color.")]
        [SerializeField] private bool _pageNumberUseDefaultTextColor = true;
        [Tooltip("Default text color for PageNumber zones (if not using default).")]
        [SerializeField] private Color _pageNumberTextColor = Color.black;
        [Tooltip("Default padding for PageNumber zones.")]
        [SerializeField] private float _pageNumberPadding = 0.005f;

        // ── Zones ───────────────────────────────────────────────
        [SerializeField] private List<TextZone> _zones = new List<TextZone>();

        // ── Bake restore data (SERIALIZED so ClearBake survives domain reloads) ──
        [Serializable]
        private struct TouchedSlot
        {
            public Renderer renderer;
            public int materialIndex;
            public Material originalMaterial;
        }
        [SerializeField, HideInInspector] private List<TouchedSlot> _touchedSlots = new List<TouchedSlot>();

        // A persistent "Bake to Asset" material must not become the background of the next
        // export. Keep the author-assigned source material alongside the exported material so
        // this relationship survives domain reloads and scene saves. The exported reference is
        // part of the guard: if a user assigns another material manually, the stale baseline is
        // ignored rather than overriding their choice.
        [Serializable]
        private struct PersistentBakeSourceSlot
        {
            public Renderer renderer;
            public int materialIndex;
            public Material sourceMaterial;
            public Material exportedMaterial;
        }
        [SerializeField, HideInInspector]
        private List<PersistentBakeSourceSlot> _persistentBakeSources =
            new List<PersistentBakeSourceSlot>();

        // ── Runtime State ───────────────────────────────────────
        // A bake target is one (renderer, materialIndex) pair. We keep the pooled RenderTexture,
        // the created material instance, and the original shared material to restore on clear.
        private struct MatTarget : IEquatable<MatTarget>
        {
            public Renderer renderer;
            public int materialIndex;
            public MatTarget(Renderer r, int i) { renderer = r; materialIndex = i; }
            public bool Equals(MatTarget o) => renderer == o.renderer && materialIndex == o.materialIndex;
            public override bool Equals(object o) => o is MatTarget m && Equals(m);
            // Object.GetHashCode() instead of GetInstanceID()/GetEntityId(): stable for the
            // object's lifetime and compiles on every Unity version (2022.3 LTS → Unity 6.x,
            // where the instance-id APIs were replaced by entity ids).
            public override int GetHashCode()
                => ((renderer != null ? renderer.GetHashCode() : 0) * 397) ^ materialIndex;
        }

        [NonSerialized] private readonly Dictionary<MatTarget, RenderTexture> _rtPool = new Dictionary<MatTarget, RenderTexture>();
        [NonSerialized] private readonly Dictionary<MatTarget, RenderTexture> _emissionRtPool = new Dictionary<MatTarget, RenderTexture>();
        [NonSerialized] private readonly Dictionary<MatTarget, RenderTexture> _maskRtPool = new Dictionary<MatTarget, RenderTexture>();
        [NonSerialized] private readonly Dictionary<MatTarget, RenderTexture> _heightRtPool = new Dictionary<MatTarget, RenderTexture>();
        [NonSerialized] private readonly Dictionary<MatTarget, Material> _materialInstances = new Dictionary<MatTarget, Material>();
        // Material to restore when a live bake is cleared (may itself be a persistent export).
        [NonSerialized] private readonly Dictionary<MatTarget, Material> _originalSharedMaterials = new Dictionary<MatTarget, Material>();
        // Immutable author baseline used as the background while compositing.
        [NonSerialized] private readonly Dictionary<MatTarget, Material> _bakeSourceMaterials = new Dictionary<MatTarget, Material>();
        [NonSerialized] private string _currentBakedLocale;
        [NonSerialized] private ITextLocalizationProvider _localizationProvider;
        [NonSerialized] private bool _localizationProviderExplicit; // assigned via the property setter
        [NonSerialized] private ITextLocalizationProviderV2 _localizationEvents;
        [NonSerialized] private bool _refreshingDefaultProvider; // reentrancy guard
        private static readonly DefaultTextLocalizationProvider s_defaultLocalizationProvider =
            new DefaultTextLocalizationProvider();

        // Book caches (invalidated on locale change / text change).
        [NonSerialized] private string _bookTextCacheLocale;
        [NonSerialized] private string _bookTextCache;
        // id → char index in the (converted, bookmark-stripped) book text.
        [NonSerialized] private Dictionary<string, int> _bookBookmarkMap;
        // Last text baked into each PageSlot zone (id → text). Lets a partial re-bake keep the
        // content of sibling page slots that share the same texture.
        [NonSerialized] private readonly Dictionary<string, string> _lastBakedPageTexts = new Dictionary<string, string>();
        [NonSerialized] private List<TextZone> _pageSlotCache;
        [NonSerialized] private int _pageSlotCacheVersion = -1;
        [NonSerialized] private int _zonesVersion;

        /// <summary>
        /// Returns a persistent RenderTexture for (renderer, materialIndex), reused across bakes.
        /// Created on first use; recreated if the resolution changed. Lets the baker avoid
        /// ReadPixels — the material samples the RT directly.
        /// </summary>
        internal RenderTexture GetOrCreatePooledRT(Renderer renderer, int materialIndex, int resolution)
            => GetOrCreatePooledRT(renderer, materialIndex, resolution, resolution);

        /// <summary>
        /// Non-square variant: the pooled RT matches a WxH bake target (e.g. a custom NPOT
        /// background texture). Recreated when either side changes.
        /// </summary>
        internal RenderTexture GetOrCreatePooledRT(Renderer renderer, int materialIndex, int bakeW, int bakeH)
            => GetOrCreatePooledRTInternal(_rtPool, renderer, materialIndex, bakeW, bakeH,
                RenderTextureReadWrite.Default);

        /// <summary>Pooled RT for the EMISSION map of (renderer, slot) — separate from albedo.</summary>
        internal RenderTexture GetOrCreatePooledEmissionRT(Renderer renderer, int materialIndex, int resolution)
            => GetOrCreatePooledEmissionRT(renderer, materialIndex, resolution, resolution);

        internal RenderTexture GetOrCreatePooledEmissionRT(Renderer renderer, int materialIndex, int bakeW, int bakeH)
            => GetOrCreatePooledRTInternal(_emissionRtPool, renderer, materialIndex, bakeW, bakeH,
                RenderTextureReadWrite.Default);

        /// <summary>Pooled RT for the PBR mask map (metallic/smoothness) — Linear data, not sRGB.</summary>
        internal RenderTexture GetOrCreatePooledMaskRT(Renderer renderer, int materialIndex, int resolution)
            => GetOrCreatePooledMaskRT(renderer, materialIndex, resolution, resolution);

        internal RenderTexture GetOrCreatePooledMaskRT(Renderer renderer, int materialIndex, int bakeW, int bakeH)
            => GetOrCreatePooledRTInternal(_maskRtPool, renderer, materialIndex, bakeW, bakeH,
                RenderTextureReadWrite.Linear);

        /// <summary>Pooled linear RT for signed height/parallax data.</summary>
        internal RenderTexture GetOrCreatePooledHeightRT(Renderer renderer, int materialIndex, int resolution)
            => GetOrCreatePooledHeightRT(renderer, materialIndex, resolution, resolution);

        internal RenderTexture GetOrCreatePooledHeightRT(Renderer renderer, int materialIndex, int bakeW, int bakeH)
            => GetOrCreatePooledRTInternal(_heightRtPool, renderer, materialIndex, bakeW, bakeH,
                RenderTextureReadWrite.Linear);

        private RenderTexture GetOrCreatePooledRTInternal(Dictionary<MatTarget, RenderTexture> pool,
            Renderer renderer, int materialIndex, int bakeW, int bakeH, RenderTextureReadWrite readWrite)
        {
            bakeW = Mathf.Max(8, bakeW);
            bakeH = Mathf.Max(8, bakeH);
            var key = new MatTarget(renderer, materialIndex);
            if (pool.TryGetValue(key, out var rt) && rt != null)
            {
                if (rt.width == bakeW && rt.height == bakeH) return rt;
                rt.Release();
                if (Application.isPlaying) Destroy(rt); else DestroyImmediate(rt);
            }

            // Albedo/emission use Default (project color space). Mask maps are DATA textures
            // and must be Linear — sRGB would warp metallic/smoothness values.
            // Mipmaps keep baked text readable at distance when enabled in settings.
            var newRT = new RenderTexture(bakeW, bakeH, 0,
                RenderTextureFormat.ARGB32, readWrite)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = _bakeSettings.generateMipMaps,
                autoGenerateMips = _bakeSettings.generateMipMaps,
                hideFlags = HideFlags.DontSave
            };
            newRT.Create();
            pool[key] = newRT;
            return newRT;
        }

        // ══════════════════════════════════════════════════════════
        // Public Properties
        // ══════════════════════════════════════════════════════════
        public MeshRenderer meshRenderer
        {
            get
            {
                if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
                return _meshRenderer;
            }
        }

        public MeshFilter meshFilter
        {
            get
            {
                if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
                return _meshFilter;
            }
        }

        public SkinnedMeshRenderer skinnedRenderer
        {
            get
            {
                if (_skinnedRenderer == null) _skinnedRenderer = GetComponent<SkinnedMeshRenderer>();
                return _skinnedRenderer;
            }
        }

        /// <summary>The renderer used for baking — MeshRenderer or SkinnedMeshRenderer.</summary>
        public Renderer targetRenderer
        {
            get
            {
                if (skinnedRenderer != null) return skinnedRenderer;
                return meshRenderer;
            }
        }

        /// <summary>The mesh whose UV0 defines zone placement (from MeshFilter or SkinnedMeshRenderer).</summary>
        public Mesh sharedMesh
        {
            get
            {
                if (skinnedRenderer != null && skinnedRenderer.sharedMesh != null)
                    return skinnedRenderer.sharedMesh;
                if (meshFilter != null) return meshFilter.sharedMesh;
                return null;
            }
        }

        /// <summary>The renderer a given zone bakes onto (its targetRenderer, or this surface's).</summary>
        public Renderer GetZoneRenderer(TextZone zone)
        {
            if (zone != null && zone.targetRenderer != null) return zone.targetRenderer;
            return targetRenderer;
        }

        /// <summary>The mesh whose UV0 a given zone uses (from its target renderer).</summary>
        public Mesh GetZoneMesh(TextZone zone)
        {
            var r = GetZoneRenderer(zone);
            return GetMeshOfRenderer(r);
        }

        /// <summary>Returns the shared mesh of any supported renderer (MeshRenderer or Skinned).</summary>
        public static Mesh GetMeshOfRenderer(Renderer r)
        {
            if (r == null) return null;
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        public List<TextZone> zones => _zones;
        public BakeSettings bakeSettings => _bakeSettings;
        public BakeMode bakeMode => _bakeMode;

        public string previewLocale
        {
            get => _previewLocale;
            set => _previewLocale = value;
        }

        public string currentBakedLocale => _currentBakedLocale;

        public Material GetMaterialInstance(Renderer renderer, int materialIndex)
            => _materialInstances.TryGetValue(new MatTarget(renderer, materialIndex), out var m) ? m : null;

        /// <summary>The original shared material recorded for the current live bake, or null.</summary>
        public Material GetOriginalMaterial(Renderer renderer, int materialIndex)
            => _originalSharedMaterials.TryGetValue(new MatTarget(renderer, materialIndex), out var m) ? m : null;

        /// <summary>
        /// Returns the immutable source material for a bake target. This includes the source of
        /// a persistent editor export when that exported material is still assigned to the slot.
        /// </summary>
        public Material GetBakeSourceMaterial(Renderer renderer, int materialIndex)
        {
            var target = new MatTarget(renderer, materialIndex);
            if (_bakeSourceMaterials.TryGetValue(target, out Material activeSource) &&
                activeSource != null)
                return activeSource;
            if (renderer == null) return null;

            Material[] materials = renderer.sharedMaterials;
            if (materialIndex < 0 || materialIndex >= materials.Length) return null;
            Material current = materials[materialIndex];
            for (int i = 0; i < _persistentBakeSources.Count; i++)
            {
                PersistentBakeSourceSlot slot = _persistentBakeSources[i];
                if (slot.renderer == renderer && slot.materialIndex == materialIndex &&
                    slot.sourceMaterial != null && slot.exportedMaterial != null &&
                    current == slot.exportedMaterial)
                    return slot.sourceMaterial;
            }

            // Ordinary live bakes use the material that will be restored on ClearBake as both
            // restore target and compositing source.
            return GetOriginalMaterial(renderer, materialIndex);
        }

        /// <summary>
        /// Records the source/export pair used by editor asset export. The exported-material
        /// guard prevents an old record from affecting a slot that the user later changed.
        /// </summary>
        public void RecordPersistentBakeSource(Renderer renderer, int materialIndex,
            Material sourceMaterial, Material exportedMaterial)
        {
            if (renderer == null || sourceMaterial == null || exportedMaterial == null) return;
            for (int i = 0; i < _persistentBakeSources.Count; i++)
            {
                PersistentBakeSourceSlot slot = _persistentBakeSources[i];
                if (slot.renderer != renderer || slot.materialIndex != materialIndex) continue;
                // Keep only the currently assigned export for this slot. This is enough for
                // sequential locale exports and avoids serializing references to every locale's
                // material into the scene (which would force all of them into a player build).
                slot.sourceMaterial = sourceMaterial;
                slot.exportedMaterial = exportedMaterial;
                _persistentBakeSources[i] = slot;
                MarkSurfaceDirtyInEditor();
                return;
            }
            _persistentBakeSources.Add(new PersistentBakeSourceSlot
            {
                renderer = renderer,
                materialIndex = materialIndex,
                sourceMaterial = sourceMaterial,
                exportedMaterial = exportedMaterial
            });
            MarkSurfaceDirtyInEditor();
        }

        private void MarkSurfaceDirtyInEditor()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                try
                {
                    UnityEditor.EditorUtility.SetDirty(this);
                    if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this))
                        UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[MeshTextBaker] Could not mark the persistent bake-source " +
                        "record dirty: " + e.Message, this);
                }
            }
#endif
        }

        /// <summary>
        /// Releases the live bake state (RT pool, material instances, restore records) WITHOUT
        /// touching the renderers' current materials. Used by "Bake to Asset": the renderers
        /// have just been switched to persistent materials, so restoring originals (what
        /// ClearBake does) would undo that assignment.
        /// </summary>
        public void ReleaseLiveBakeKeepingMaterials()
        {
            // This specialized path is only valid after every generated instance has been
            // replaced. If called directly while one is still assigned, fall back to the safe
            // public cleanup behavior instead of leaving a renderer with a destroyed material.
            bool generatedMaterialStillAssigned = false;
            foreach (var kv in _materialInstances)
            {
                Renderer renderer = kv.Key.renderer;
                Material instance = kv.Value;
                if (renderer == null || instance == null) continue;
                Material[] assigned = renderer.sharedMaterials;
                int slot = kv.Key.materialIndex;
                if (slot < 0 || slot >= assigned.Length || assigned[slot] != instance) continue;
                generatedMaterialStillAssigned = true;
                break;
            }
            if (generatedMaterialStillAssigned)
            {
                ClearBake();
                return;
            }

            foreach (var kv in _materialInstances)
            {
                var inst = kv.Value;
                if (inst == null) continue;
                if (Application.isPlaying) Destroy(inst);
                else DestroyImmediate(inst);
            }
            _materialInstances.Clear();
            _originalSharedMaterials.Clear();
            _bakeSourceMaterials.Clear();
            _touchedSlots.Clear();
            _lastBakedPageTexts.Clear();
            _currentBakedLocale = null;
            ReleasePool();
        }

        /// <summary>
        /// Effective localization provider. Resolution order: code-assigned (setter) →
        /// inspector assignment → scene default provider (any registered
        /// <see cref="ITextLocalizationProvider"/>, e.g. one External Override for the whole
        /// scene) → edit-mode scene search → embedded MeshTextAsset fallback
        /// (<see cref="DefaultTextLocalizationProvider"/>).
        /// Assigning null via the setter clears the code override and returns to automatic mode.
        /// </summary>
        public ITextLocalizationProvider localizationProvider
        {
            get
            {
                if (_localizationProviderExplicit)
                {
                    if (IsProviderAlive(_localizationProvider)) return _localizationProvider;
                    _localizationProviderExplicit = false;
                    _localizationProvider = null;
                }
                if (_localizationProviderBehaviour != null &&
                    _localizationProviderBehaviour is ITextLocalizationProvider behaviourProvider)
                {
                    _localizationProvider = behaviourProvider;
                    return behaviourProvider;
                }
                if (!_ignoreSceneDefaultProvider)
                {
                    ITextLocalizationProvider registered =
                        MeshTextLocalizationRuntime.DefaultProvider;
                    if (registered != null)
                    {
                        _localizationProvider = registered;
                        return registered;
                    }
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                    {
                        // Providers don't run Awake/OnEnable registration outside play mode, so
                        // search the scene directly. A live find is cached (dropped on refresh);
                        // "nothing found" retries on every access so a newly added provider is
                        // picked up by the next bake or inspector preview.
                        if (IsProviderAlive(_localizationProvider) &&
                            !(_localizationProvider is DefaultTextLocalizationProvider))
                            return _localizationProvider;
                        if (FindSceneLocalizationProvider() is ITextLocalizationProvider found)
                        {
                            _localizationProvider = found;
                            return found;
                        }
                    }
#endif
                }
                _localizationProvider = s_defaultLocalizationProvider;
                return s_defaultLocalizationProvider;
            }
            set
            {
                _localizationProvider = value;
                _localizationProviderExplicit = value != null;
                if (value == null) _localizationProvider = null; // re-resolve lazily (auto)
                SubscribeLocalizationEvents();
            }
        }

        /// <summary>
        /// Drops the automatic provider cache and resubscribes to the currently effective
        /// provider. Code-assigned (setter) and inspector-assigned providers are unaffected.
        /// Called automatically when the scene default changes; also available for editor tooling.
        /// </summary>
        public void RefreshLocalizationProvider()
        {
            if (_localizationProviderExplicit) return;
            if (_localizationProviderBehaviour != null &&
                _localizationProviderBehaviour is ITextLocalizationProvider) return;
            _localizationProvider = null;
            SubscribeLocalizationEvents();
            InvalidateBookCache();
        }

        /// <summary>True when this surface follows the scene default provider (automatic mode).</summary>
        public bool UsesAutomaticLocalizationProvider()
            => !_ignoreSceneDefaultProvider && !_localizationProviderExplicit &&
               !(_localizationProviderBehaviour != null &&
                 _localizationProviderBehaviour is ITextLocalizationProvider);

        private static bool IsProviderAlive(ITextLocalizationProvider provider)
        {
            if (ReferenceEquals(provider, null)) return false;
            // NOTE: an interface reference never uses Unity's == overload, so a destroyed
            // component must be detected through an explicit UnityEngine.Object cast.
            if (provider is UnityEngine.Object unityObject) return unityObject != null;
            return true; // plain C# providers have no native lifetime
        }

#if UNITY_EDITOR
        /// <summary>Edit-mode scene search for any provider (External Override preferred).</summary>
        private MonoBehaviour FindSceneLocalizationProvider()
        {
            MonoBehaviour best = null;
            int bestScore = int.MinValue;
#if UNITY_2023_1_OR_NEWER
#pragma warning disable 618
            MonoBehaviour[] all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore 618
#else
            MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
#endif
            for (int i = 0; i < all.Length; i++)
            {
                MonoBehaviour candidate = all[i];
                if (candidate == null || !(candidate is ITextLocalizationProvider)) continue;
                int score = 0;
                if (candidate is ExternalOverrideLocalizationProvider) score += 100;
                if (candidate.gameObject.activeInHierarchy) score += 10;
                if (candidate.enabled) score += 1;
                if (score > bestScore) { bestScore = score; best = candidate; }
            }
            return best;
        }
#endif

        // ══════════════════════════════════════════════════════════
        // Unity Lifecycle
        // ══════════════════════════════════════════════════════════
        private void Awake() => CacheComponents();

        private void Start()
        {
            if (_bakeMode == BakeMode.RuntimeBakeOnStart && Application.isPlaying)
            {
                string locale = localizationProvider is ILocaleSelectionProvider selector
                    ? selector.CurrentLocale : _previewLocale;
                BakeForLocale(locale);
            }
        }

        private void OnEnable()
        {
            CacheComponents();
            // After a domain reload the runtime dictionaries are empty but the serialized
            // _touchedSlots list still knows which material slots we replaced. The RTs and
            // material instances are gone (DontSave), so restore the originals to avoid
            // pink/black textures.
            RestoreTouchedSlotsIfOrphaned();
            MeshTextLocalizationRuntime.DefaultProviderChanged -= OnDefaultProviderChanged;
            MeshTextLocalizationRuntime.DefaultProviderChanged += OnDefaultProviderChanged;
            SubscribeLocalizationEvents();
        }

        private void OnDisable()
        {
            MeshTextLocalizationRuntime.DefaultProviderChanged -= OnDefaultProviderChanged;
            if (_localizationEvents != null)
                _localizationEvents.LocalizationChanged -= OnLocalizationChanged;
            _localizationEvents = null;
        }

        private void OnDestroy() { ClearBake(); }  // ClearBake also ReleasePool()

        private void SubscribeLocalizationEvents()
        {
            if (_localizationEvents != null)
                _localizationEvents.LocalizationChanged -= OnLocalizationChanged;
            _localizationEvents = localizationProvider as ITextLocalizationProviderV2;
            if (_localizationEvents != null)
                _localizationEvents.LocalizationChanged += OnLocalizationChanged;
        }

        private void OnLocalizationChanged()
        {
            InvalidateBookCache();
            if (!Application.isPlaying || _bakeMode != BakeMode.RuntimeBakeOnStart) return;
            // BookController owns spread-aware rebaking and subscribes to the same provider.
            if (GetComponentInParent<BookController>() != null) return;
            string locale = _localizationEvents is ILocaleSelectionProvider selector
                ? selector.CurrentLocale : _previewLocale;
            BakeForLocale(locale);
        }

        /// <summary>Scene default appeared/changed/disappeared — re-resolve in automatic mode.</summary>
        private void OnDefaultProviderChanged()
        {
            if (_refreshingDefaultProvider) return;
            if (!UsesAutomaticLocalizationProvider()) return;
            _refreshingDefaultProvider = true;
            try
            {
                _localizationProvider = null;
                SubscribeLocalizationEvents();
                InvalidateBookCache();
                if (!Application.isPlaying || _bakeMode != BakeMode.RuntimeBakeOnStart) return;
                // Not baked yet → Start (or the owning book) bakes with the new provider.
                if (_currentBakedLocale == null) return;
                // BookController owns spread-aware rebaking and refreshes itself too.
                if (GetComponentInParent<BookController>() != null) return;
                string locale = _localizationEvents is ILocaleSelectionProvider selector
                    ? selector.CurrentLocale : _previewLocale;
                BakeForLocale(locale);
            }
            finally
            {
                _refreshingDefaultProvider = false;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Inspector edits bypass property setters: drop caches that depend on serialized
            // state (book text asset, zone roles/order for the page-slot cache, provider ref).
            _localizationProvider = null;
            _localizationProviderExplicit = false;
            InvalidateBookCache();
            NormalizeZoneOrder();
            MarkZonesChanged();
        }
#endif

        // ══════════════════════════════════════════════════════════
        // Public API
        // ══════════════════════════════════════════════════════════

        internal void PrepareLocalizationContext(string locale)
        {
            ILocalizedContentResolver resolver = localizationProvider as ILocalizedContentResolver;
            MeshTextLocalizationRuntime.SetContext(resolver, locale);
        }

        /// <summary>
        /// Deterministic provider/embedded fallback: requested locale, language parent, then en.
        /// V2 providers are exact-locale, which lets External Override replace one key without
        /// swallowing a valid embedded translation for the same language.
        /// </summary>
        public LocalizedTextResult ResolveLocalizedText(string key, MeshTextAsset embedded, string locale)
        {
            string requested = string.IsNullOrWhiteSpace(locale) ? "en" : locale;
            ITextLocalizationProvider provider = localizationProvider;

            foreach (string candidate in ExternalLocalizationStore.EnumerateLocaleHierarchy(requested, false))
            {
                if (TryProviderExact(provider, key, candidate, requested, out LocalizedTextResult result))
                    return result;
                if (embedded != null && embedded.TryGetTextExact(candidate, out string assetText))
                    return AssetResult(assetText, requested, candidate);
            }

            // English is the canonical final language fallback requested by the external pipeline.
            if (!string.Equals(requested, "en", StringComparison.OrdinalIgnoreCase))
            {
                if (TryProviderExact(provider, key, "en", requested, out LocalizedTextResult english))
                    return english;
                if (embedded != null && embedded.TryGetTextExact("en", out string englishAsset))
                    return AssetResult(englishAsset, requested, "en");
            }

            // Preserve legacy MeshTextAsset behavior as the final non-empty source.
            string last = embedded != null ? embedded.GetText(requested) : "";
            return string.IsNullOrEmpty(last) ? LocalizedTextResult.Missing(requested)
                : AssetResult(last, requested, embedded.fallbackLocale);
        }

        private static bool TryProviderExact(ITextLocalizationProvider provider, string key,
            string locale, string requested, out LocalizedTextResult result)
        {
            result = LocalizedTextResult.Missing(requested);
            if (provider == null || string.IsNullOrWhiteSpace(key)) return false;
            if (provider is ITextLocalizationProviderV2 exact)
            {
                if (!exact.TryGetTextExact(key, locale, out result) || !result.found ||
                    string.IsNullOrEmpty(result.text)) return false;
                result.requestedLocale = requested;
                if (string.IsNullOrEmpty(result.resolvedLocale)) result.resolvedLocale = locale;
                return true;
            }
            string text = provider.GetText(key, locale);
            if (string.IsNullOrEmpty(text)) return false;
            result = new LocalizedTextResult
            {
                found = true, text = text, requestedLocale = requested, resolvedLocale = locale,
                source = LocalizationTextSource.CustomProvider
            };
            return true;
        }

        private static LocalizedTextResult AssetResult(string text, string requested, string resolved)
            => new LocalizedTextResult
            {
                found = true, text = text, requestedLocale = requested, resolvedLocale = resolved,
                source = LocalizationTextSource.MeshTextAsset
            };

        /// <summary>
        /// Bakes all zones for the given locale. Kept void for UnityEvent and source
        /// compatibility; callers that need success/failure use <see cref="BakeForLocaleWithResult"/>.
        /// </summary>
        public void BakeForLocale(string locale) => BakeForLocaleWithResult(locale);

        /// <summary>
        /// Bakes all zones for the given locale and returns the exact fresh-bake result.
        /// </summary>
        public MeshTextBakerCore.BakeResult BakeForLocaleWithResult(string locale)
        {
            CacheComponents();
            PrepareLocalizationContext(locale);

            // A failed fresh bake must not leave materials from an older locale assigned. The
            // pooled RTs remain reusable, but generated materials and locale state are cleared.
            ClearBakeInternal();
            _currentBakedLocale = null;

            // A renderer on THIS object is only required for zones that don't specify their
            // own targetRenderer. A book rig (empty root + child page renderers, every zone
            // targeted explicitly) is perfectly valid without one.
            if (targetRenderer == null && AnyZoneNeedsOwnRenderer())
            {
                const string message = "No MeshRenderer or SkinnedMeshRenderer found on this " +
                    "object, and at least one zone has no Target Renderer set. Either add a " +
                    "renderer here or assign Target Renderer on every zone.";
                Debug.LogError("[MeshTextBaker] " + message, this);
                var failed = new MeshTextBakerCore.BakeResult { success = false };
                failed.warnings.Add(message);
                return failed;
            }

            // Explicit user action → never serve stale book text. The cache is otherwise
            // only invalidated by OnValidate (inspector pokes), which was exactly the
            // "page slots update only after touching a parameter" bug.
            InvalidateBookCache();

            // Book mode: "Bake Now" also fills PageSlot zones — the first logical pages are
            // laid out into the slots in reading order (slot 0 = page 0, slot 1 = page 1, …).
            // Without this the full bake would wipe page slots to empty.
            Dictionary<string, string> pageSlotTexts = BuildInitialPageSlotTexts(locale);

            var result = MeshTextBakerCore.Bake(this, locale, pageSlotTexts, null);
            if (!result.success)
            {
                Debug.LogError("[MeshTextBaker] Bake failed.", this);
                return result;
            }
            foreach (var warning in result.warnings)
                Debug.LogWarning($"[MeshTextBaker] {warning}", this);

            ApplyBakeResult(result);
            _currentBakedLocale = locale;
            return result;
        }

        /// <summary>True if any zone falls back to this object's own renderer.</summary>
        private bool AnyZoneNeedsOwnRenderer()
        {
            foreach (var z in _zones)
                if (z != null && z.targetRenderer == null) return true;
            return _zones.Count == 0; // no zones at all → the old warning is still helpful
        }

        /// <summary>
        /// Lays out the first logical pages of the book text into the PageSlot zones (reading
        /// order). Returns null when there is no book text or no page slots — the baker then
        /// treats page slots as empty, same as before.
        /// </summary>
        private Dictionary<string, string> BuildInitialPageSlotTexts(string locale)
        {
            var slots = GetPageSlots();
            if (slots == null || slots.Count == 0) return null;

            string full = GetBookText(locale);
            if (string.IsNullOrEmpty(full)) return null;

            var texts = new Dictionary<string, string>(slots.Count);
            int cursor = 0;
            foreach (var slot in slots)
            {
                if (cursor >= full.Length) { texts[slot.id] = ""; continue; }
                texts[slot.id] = GetPageText(full, cursor, slot, out cursor);
            }
            return texts;
        }

        /// <summary>Applies all per-material baked textures to material instances on the renderers.</summary>
        private void ApplyBakeResult(MeshTextBakerCore.BakeResult result)
        {
            string texProp = string.IsNullOrEmpty(_bakeSettings.texturePropertyName)
                ? MeshTextBakerCore.DetectTexturePropertyName()
                : _bakeSettings.texturePropertyName;
            string emitProp = string.IsNullOrEmpty(_bakeSettings.emissionPropertyName)
                ? MeshTextBakerCore.DetectEmissionPropertyName()
                : _bakeSettings.emissionPropertyName;

            // Group bakes by renderer so we touch each renderer's sharedMaterials array once.
            var byRenderer = new Dictionary<Renderer, List<MeshTextBakerCore.MaterialBake>>();
            foreach (var bake in result.bakes)
            {
                Renderer r = bake.renderer != null ? bake.renderer : targetRenderer;
                if (r == null) continue;
                if (!byRenderer.TryGetValue(r, out var list))
                {
                    list = new List<MeshTextBakerCore.MaterialBake>();
                    byRenderer[r] = list;
                }
                list.Add(bake);
            }

            foreach (var kv in byRenderer)
            {
                Renderer r = kv.Key;
                var materials = r.sharedMaterials;

                foreach (var bake in kv.Value)
                {
                    int idx = bake.materialIndex;
                    if (idx < 0 || idx >= materials.Length || materials[idx] == null)
                    {
                        Debug.LogWarning($"[MeshTextBaker] Material slot {idx} invalid on '{r.name}'; skipping.", this);
                        continue;
                    }

                    var target = new MatTarget(r, idx);
                    if (!_originalSharedMaterials.ContainsKey(target))
                    {
                        Material assignedMaterial = materials[idx];
                        Material sourceMaterial = GetBakeSourceMaterial(r, idx) ?? assignedMaterial;

                        // Stale live instances are never valid restore/source materials.
                        if (IsTextBakedMaterial(assignedMaterial))
                        {
                            Material recovered = FindTouchedOriginal(r, idx);
                            if (recovered != null)
                            {
                                assignedMaterial = recovered;
                                sourceMaterial = recovered;
                            }
                            else
                            {
                                Debug.LogWarning($"[MeshTextBaker] Slot {idx} on '{r.name}' holds a stale '(TextBaked)' material and no original is recorded.", this);
                            }
                        }

                        // Restore what was actually assigned (including a persistent export),
                        // but composite from the immutable author baseline.
                        _originalSharedMaterials[target] = assignedMaterial;
                        _bakeSourceMaterials[target] = sourceMaterial;
                        RecordTouchedSlot(r, idx, assignedMaterial);
                    }

                    // A book reuses the same material instance for many logical pages. Always
                    // reset it to the SOURCE material before applying this page's maps. PBR
                    // setup deliberately opens metallic/smoothness multipliers to 1; without
                    // this reset, removing the generated mask on a later non-PBR page leaves
                    // _Metallic = 1 and turns the entire stack/flipper into metal.
                    Material original = _bakeSourceMaterials[target];
                    if (_materialInstances.TryGetValue(target, out var inst) && inst != null)
                    {
                        // Copies properties, keywords, render queue and GI flags, but keeps the
                        // generated instance's shader/name/hideFlags. The baked maps below are
                        // then applied from a clean, author-defined baseline.
                        inst.CopyPropertiesFromMaterial(original);
                    }
                    else
                    {
                        inst = MeshTextBakerCore.CreateMaterialInstance(
                            original, bake.texture, texProp);
                        _materialInstances[target] = inst;
                    }

                    // CopyPropertiesFromMaterial restored the original albedo, so point it back
                    // to the freshly baked page (also harmless for a newly created instance).
                    if (inst != null && inst.HasProperty(texProp))
                        inst.SetTexture(texProp, bake.texture);
                    // The original inspector tint was folded into the baked albedo background.
                    // Keep the live material white so it does not tint text a second time.
                    MeshTextBakerCore.SetMaterialAlbedoColorWhite(inst);

                    // Emission map (glowing zones). Enabling the keyword is required for the
                    // shader to actually sample the map (Standard/URP: _EMISSION; HDRP uses
                    // its own toggle but tolerates the keyword).
                    ApplyEmission(inst, bake.emission, emitProp, bake.emissionIntensity);

                    // PBR mask map (metallic/smoothness) and signed height/parallax map.
                    ApplyPbrMask(inst, bake.pbrMask);
                    ApplyHeight(inst, bake.height, bake.heightAmplitude);

                    materials[idx] = inst;
                }

                r.sharedMaterials = materials;
            }
        }

        private static void ApplyEmission(Material inst, RenderTexture emissionRT, string emitProp,
            float intensity)
        {
            if (inst == null) return;

            if (emissionRT == null)
            {
                // ApplyBakeResult reset the instance from the original material first. Leave
                // that baseline untouched: this both removes a previous generated map and
                // preserves emission that the author had configured on the source material.
                return;
            }

            if (!inst.HasProperty(emitProp))
            {
                Debug.LogWarning($"[MeshTextBaker] Material '{inst.name}' has no emission property " +
                                 $"'{emitProp}' — glowing zones will not be visible. " +
                                 "Use a Lit/Standard shader or set Emission Property Name in Bake Settings.");
                return;
            }

            inst.SetTexture(emitProp, emissionRT);
            inst.EnableKeyword("_EMISSION");
            inst.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            if (intensity < 1f) intensity = 1f;

            bool isHdrp = inst.HasProperty("_EmissiveColorMap") || inst.HasProperty("_EmissiveColor");
            if (isHdrp)
            {
                // Full HDRP Lit setup — everything that otherwise must be clicked by hand:
                //   Use Emission Intensity ON, white LDR emissive color (tint lives in the map),
                //   Exposure Weight 0 (glow independent of scene exposure), intensity in nits
                //   derived from the zones' HDR colors.
                //
                // CRITICAL: without the _EMISSIVE_COLOR_MAP keyword HDRP Lit IGNORES the map
                // and multiplies the whole surface by the flat _EmissiveColor — that was the
                // "solid bright white until any inspector tweak revalidates the material" bug.
                inst.EnableKeyword("_EMISSIVE_COLOR_MAP");
                if (inst.HasProperty("_UseEmissiveIntensity")) inst.SetFloat("_UseEmissiveIntensity", 1f);
                if (inst.HasProperty("_EmissiveIntensityUnit")) inst.SetFloat("_EmissiveIntensityUnit", 0f); // 0 = Nits
                if (inst.HasProperty("_EmissiveExposureWeight")) inst.SetFloat("_EmissiveExposureWeight", 0f);

                // With Use Emissive Intensity on, HDRP computes _EmissiveColor = LDR color *
                // intensity. Replicate that here so the shader sees the right final value.
                float nits = EmissionIntensityToNits(intensity);
                if (inst.HasProperty("_EmissiveIntensity")) inst.SetFloat("_EmissiveIntensity", nits);
                if (inst.HasProperty("_EmissiveColorLDR")) inst.SetColor("_EmissiveColorLDR", Color.white);
                if (inst.HasProperty("_EmissiveColor")) inst.SetColor("_EmissiveColor", Color.white * nits);
            }
            else
            {
                // Built-in Standard / URP Lit: the emission COLOR carries the HDR intensity.
                if (inst.HasProperty("_EmissionColor")) inst.SetColor("_EmissionColor", Color.white * intensity);
            }

            // Run the pipeline's own material validation (HDMaterial.ValidateMaterial /
            // URP Setup) so ALL dependent keywords/passes are synced exactly the way the
            // inspector does it on any tweak — no manual "wiggle a slider to fix it".
            MaterialPipelineUtility.ValidateMaterial(inst);
        }

        /// <summary>
        /// Maps the zones' HDR color intensity (a bloom-style multiplier, ~1..16) to HDRP nits.
        /// ~100 nits reads as "clearly glowing" under default HDRP exposure; the multiplier
        /// scales from there. Tune per zone via the HDR intensity of Emission Color.
        /// </summary>
        private static float EmissionIntensityToNits(float intensity) => 100f * intensity;

        private void ApplyPbrMask(Material inst, RenderTexture maskRT)
        {
            if (inst == null) return;
            string maskProp = MeshTextBakerCore.DetectMaskPropertyName(inst);

            if (maskRT == null)
            {
                // ApplyBakeResult has already restored the complete original material state.
                // Do not clear the slot here: the source material may have its own mask map.
                // The reset also removes generated-map keywords and, critically, restores the
                // scalar _Metallic/_Smoothness values raised to 1 for the previous PBR page.
                return;
            }

            if (!inst.HasProperty(maskProp))
            {
                Debug.LogWarning($"[MeshTextBaker] Material '{inst.name}' has no mask property " +
                                 $"'{maskProp}' — PBR mask zones will not be visible. " +
                                 "Use HDRP Lit (Mask Map) or URP Lit/Standard (Metallic Map).");
                return;
            }

            inst.SetTexture(maskProp, maskRT);

            if (maskProp == "_MaskMap")
            {
                // HDRP Lit: enable mask map sampling and open the smoothness remap range so
                // the map's alpha maps 1:1 to smoothness.
                inst.EnableKeyword("_MASKMAP");
                if (inst.HasProperty("_SmoothnessRemapMin")) inst.SetFloat("_SmoothnessRemapMin", 0f);
                if (inst.HasProperty("_SmoothnessRemapMax")) inst.SetFloat("_SmoothnessRemapMax", 1f);
                if (inst.HasProperty("_MetallicRemapMin")) inst.SetFloat("_MetallicRemapMin", 0f);
                if (inst.HasProperty("_MetallicRemapMax")) inst.SetFloat("_MetallicRemapMax", 1f);
                if (inst.HasProperty("_AORemapMin")) inst.SetFloat("_AORemapMin", 0f);
                if (inst.HasProperty("_AORemapMax")) inst.SetFloat("_AORemapMax", 1f);
            }
            else
            {
                // URP Lit / Built-in Standard: metallic map keyword + full-range values so
                // the map alone drives the result.
                inst.EnableKeyword("_METALLICGLOSSMAP");
                inst.EnableKeyword("_METALLICSPECGLOSSMAP"); // URP variant name
                if (inst.HasProperty("_Metallic")) inst.SetFloat("_Metallic", 1f);
                if (inst.HasProperty("_GlossMapScale")) inst.SetFloat("_GlossMapScale", 1f);
                if (inst.HasProperty("_Smoothness")) inst.SetFloat("_Smoothness", 1f);
                if (inst.HasProperty("_SmoothnessTextureChannel")) inst.SetFloat("_SmoothnessTextureChannel", 0f);
            }

            MaterialPipelineUtility.ValidateMaterial(inst);
        }

        private void ApplyHeight(Material inst, RenderTexture heightRT, float amplitude)
        {
            if (inst == null || heightRT == null) return; // original baseline was already restored
            string heightProp = string.IsNullOrEmpty(_bakeSettings.heightPropertyName)
                ? MeshTextBakerCore.DetectHeightPropertyName(inst)
                : _bakeSettings.heightPropertyName;
            if (string.IsNullOrEmpty(heightProp) || !inst.HasProperty(heightProp))
            {
                Debug.LogWarning($"[MeshTextBaker] Material '{inst.name}' has no height property " +
                    $"'{heightProp}'. Use a Lit/Standard or custom shader exposing _HeightMap or " +
                    "_ParallaxMap (URP support depends on its shader/version).", this);
                return;
            }

            inst.SetTexture(heightProp, heightRT);
            amplitude = Mathf.Max(0.000001f, amplitude);
            if (heightProp == "_ParallaxMap")
            {
                inst.EnableKeyword("_PARALLAXMAP");
                if (inst.HasProperty("_Parallax")) inst.SetFloat("_Parallax", amplitude);
                if (inst.HasProperty("_HeightScale")) inst.SetFloat("_HeightScale", amplitude);
            }
            else
            {
                inst.EnableKeyword("_HEIGHTMAP");
                // HDRP amplitude parametrization with a centered signed data map.
                if (inst.HasProperty("_HeightMapParametrization")) inst.SetFloat("_HeightMapParametrization", 1f);
                if (inst.HasProperty("_HeightCenter")) inst.SetFloat("_HeightCenter", 0.5f);
                if (inst.HasProperty("_HeightAmplitude")) inst.SetFloat("_HeightAmplitude", amplitude);
                if (inst.HasProperty("_HeightTessCenter")) inst.SetFloat("_HeightTessCenter", 0.5f);
                if (inst.HasProperty("_HeightTessAmplitude")) inst.SetFloat("_HeightTessAmplitude", amplitude * 100f);
                if (inst.HasProperty("_HeightPoMAmplitude")) inst.SetFloat("_HeightPoMAmplitude", amplitude * 100f);

                // Prefer pixel displacement when HDRP exposes its mode. Custom shaders that only
                // consume _HeightMap simply ignore these optional properties/keywords.
                if (inst.HasProperty("_DisplacementMode")) inst.SetFloat("_DisplacementMode", 2f);
                inst.EnableKeyword("_PIXEL_DISPLACEMENT");
            }
            MaterialPipelineUtility.ValidateMaterial(inst);
        }

        /// <summary>
        /// Safely frees all generated materials and pooled RenderTextures. Materials are
        /// restored before their textures are destroyed, so no renderer keeps a dead RT.
        /// Textures and material instances are recreated automatically on the next bake.
        /// </summary>
        public void ReleaseRenderTextures() => ClearBake();

        /// <summary>
        /// Clears the current bake: restores original materials, destroys generated material
        /// instances AND releases pooled RenderTextures (VRAM). Use this for sleep/close and
        /// editor lifecycle. Re-bakes recreate the pool automatically.
        /// </summary>
        public void ClearBake()
        {
            ClearBakeInternal();
            ReleasePool();
            _currentBakedLocale = null;
        }

        /// <summary>
        /// Creates an overflow sub-zone linked to the given parent (copies style, sets role,
        /// links parent → child, switches parent to ContinueToSubZones).
        /// </summary>
        public TextZone CreateSubZone(TextZone parent)
        {
            if (parent == null) return null;

            var sub = new TextZone();
            sub.displayName = parent.displayName + " \u25b8 Overflow";
            sub.orderIndex = _zones.Count;
            sub.role = TextZoneRole.OverflowOnly;
            sub.CopyStyleFrom(parent);

            var r = parent.uvRect;
            float newY = Mathf.Clamp01(r.y - r.height - 0.02f);
            sub.uvRect = new Rect(r.x, newY, r.width, r.height);
            sub.rotation = parent.rotation;

            _zones.Add(sub);
            MarkZonesChanged();

            parent.overflowBehavior = TextZoneOverflow.ContinueToSubZones;
            parent.continueToZoneId = sub.id;

            return sub;
        }

        private void ApplyNewZoneDefaults(TextZone zone)
        {
            if (zone == null || _bakeSettings == null) return;
            zone.alignment = _bakeSettings.defaultAlignment;
            zone.richTextEnabled = _bakeSettings.defaultRichTextEnabled;
            zone.markdownEnabled = _bakeSettings.defaultMarkdownEnabled;
            zone.usePbr = _bakeSettings.defaultUsePbr;
            zone.blendMode = _bakeSettings.defaultBlendMode;
            zone.opacity = _bakeSettings.defaultOpacity;
        }

        /// <summary>Adds a new zone with surface default settings.</summary>
        public TextZone AddZone()
        {
            var zone = new TextZone();
            ApplyNewZoneDefaults(zone);
            zone.displayName = $"Zone {_zones.Count + 1}";
            zone.orderIndex = _zones.Count;
            zone.uvRect = new Rect(0.1f, 0.1f, 0.8f, 0.35f);
            _zones.Add(zone);
            MarkZonesChanged();
            return zone;
        }

        /// <summary>
        /// Creates a small PageNumber zone linked to a PageSlot (same renderer + material).
        /// User can freely move/resize it in the UV editor. BookController fills the number at bake.
        /// </summary>
        public TextZone CreatePageNumberZone(TextZone pageSlot)
        {
            if (pageSlot == null) return null;

            var zone = new TextZone();
            zone.displayName = (pageSlot.displayName ?? "Page") + " #";
            zone.orderIndex = _zones.Count;
            zone.role = TextZoneRole.PageNumber;
            zone.parentZoneId = pageSlot.id;
            zone.previewColor = TextZone.DefaultPreviewColor(TextZoneRole.PageNumber);
            zone.targetRenderer = pageSlot.targetRenderer;
            zone.materialIndex = pageSlot.materialIndex;
            zone.rotation = pageSlot.rotation;
            zone.overflowBehavior = TextZoneOverflow.Clip;
            zone.overridePageNumberStyle = false;
            ApplyPageNumberDefaults(zone);

            // Small square near bottom-center of the page slot (user repositions freely).
            Rect p = pageSlot.uvRect;
            float s = Mathf.Min(p.width, p.height) * 0.12f;
            s = Mathf.Clamp(s, 0.04f, 0.15f);
            zone.uvRect = new Rect(
                p.x + (p.width - s) * 0.5f,
                p.y + p.height * 0.02f,
                s, s);

            _zones.Add(zone);
            MarkZonesChanged();
            return zone;
        }

        /// <summary>All PageNumber zones that belong to the given PageSlot (by parentZoneId).</summary>
        public List<TextZone> GetPageNumberChildren(TextZone pageSlot)
        {
            var list = new List<TextZone>();
            if (pageSlot == null) return list;
            foreach (var z in _zones)
            {
                if (z == null || z.role != TextZoneRole.PageNumber) continue;
                if (z.parentZoneId == pageSlot.id) list.Add(z);
            }
            return list;
        }

        public void RemoveZone(TextZone zone)
        {
            _zones.Remove(zone);
            NormalizeZoneOrder();
            MarkZonesChanged();
        }

        /// <summary>List position is the single source of truth for bake/reading order.</summary>
        public void NormalizeZoneOrder()
        {
            for (int i = 0; i < _zones.Count; i++)
                if (_zones[i] != null) _zones[i].orderIndex = i;
        }

        /// <summary>Call after structurally modifying the zones list from external code.</summary>
        public void MarkZonesChanged() => _zonesVersion++;

        public TextZone FindZoneById(string id)
        {
            foreach (var zone in _zones)
                if (zone != null && zone.id == id) return zone;
            return null;
        }

        public BakeSettings GetBakeSettings() => _bakeSettings;

        // ══════════════════════════════════════════════════════════
        // Book API (page-based)
        // ══════════════════════════════════════════════════════════
        // Model: the book text is split into a sequence of LOGICAL PAGES. Each page holds as much
        // text as fits (by height) in one PageSlot zone. BookController/BookPaginator map pages
        // onto physical page surfaces. This component is page-oriented: it can measure where a
        // page ends and bake given text into given zones.
        //
        // IMPORTANT: markdown is converted to rich text ONCE, before pagination (when Book
        // Markdown is enabled). Measure and bake therefore always see the same string; the page
        // break token is searched in the converted text.

        /// <summary>Token in book text that forces the rest onto the next page.</summary>
        public const string PageBreakToken = "<newpage>";

        /// <summary>Named jump target in book text: &lt;bookmark=id&gt; (stripped at bake time).</summary>
        public const string BookmarkTag = "bookmark";

        public MeshTextAsset bookText
        {
            get => _bookText;
            set { _bookText = value; InvalidateBookCache(); }
        }
        public string bookLocalizationKey
        {
            get => _bookLocalizationKey;
            set { _bookLocalizationKey = value ?? ""; InvalidateBookCache(); }
        }

        /// <summary>Book-wide PBR switch: applies to every PageSlot zone.</summary>
        public bool bookUsePbr { get => _bookUsePbr; set => _bookUsePbr = value; }

        public TMPro.TMP_FontAsset pageNumberFont { get => _pageNumberFont; set => _pageNumberFont = value; }
        public FontSizeMode pageNumberFontSizeMode { get => _pageNumberFontSizeMode; set => _pageNumberFontSizeMode = value; }
        public float pageNumberFontSize { get => _pageNumberFontSize; set => _pageNumberFontSize = value; }
        public TMPro.TextAlignmentOptions pageNumberAlignment { get => _pageNumberAlignment; set => _pageNumberAlignment = value; }
        public bool pageNumberUseDefaultTextColor { get => _pageNumberUseDefaultTextColor; set => _pageNumberUseDefaultTextColor = value; }
        public Color pageNumberTextColor { get => _pageNumberTextColor; set => _pageNumberTextColor = value; }
        public float pageNumberPadding { get => _pageNumberPadding; set => _pageNumberPadding = value; }

        /// <summary>Apply surface Page Number Defaults onto a PageNumber zone (unless it overrides).</summary>
        public void ApplyPageNumberDefaults(TextZone zone)
        {
            if (zone == null || zone.role != TextZoneRole.PageNumber) return;
            if (zone.overridePageNumberStyle) return;
            zone.fontOverride = _pageNumberFont;
            zone.fontSizeMode = _pageNumberFontSizeMode;
            zone.fontSize = _pageNumberFontSize;
            zone.alignment = _pageNumberAlignment;
            zone.useDefaultTextColor = _pageNumberUseDefaultTextColor;
            zone.textColor = _pageNumberTextColor;
            zone.padding = _pageNumberPadding;
            zone.richTextEnabled = true;
            zone.markdownEnabled = false;
            zone.blendMode = MeshTextBlendMode.Normal;
            zone.opacity = 1f;
            zone.usePbr = false;
        }

        /// <summary>Drops the cached (markdown-converted) book text. Call on locale/text change.</summary>
        public void InvalidateBookCache()
        {
            _bookTextCache = null;
            _bookTextCacheLocale = null;
            _bookBookmarkMap = null;
        }

        /// <summary>
        /// Named bookmarks extracted from the current locale's book text
        /// (id → character index in <see cref="GetBookText"/>). Empty if none.
        /// </summary>
        public IReadOnlyDictionary<string, int> GetBookBookmarks(string locale)
        {
            GetBookText(locale); // ensure cache + map
            return _bookBookmarkMap ?? (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();
        }

        /// <summary>All zones with role == PageSlot, ordered by orderIndex (reading order). Cached.</summary>
        public List<TextZone> GetPageSlots()
        {
            if (_pageSlotCache != null && _pageSlotCacheVersion == _zonesVersion)
                return _pageSlotCache;

            _pageSlotCache = new List<TextZone>();
            foreach (var z in _zones)
                if (z.role == TextZoneRole.PageSlot) _pageSlotCache.Add(z);
            _pageSlotCache.Sort((a, b) => a.orderIndex.CompareTo(b.orderIndex));
            _pageSlotCacheVersion = _zonesVersion;
            return _pageSlotCache;
        }

        /// <summary>
        /// Returns the localized book text, markdown-converted ONCE if Book Markdown is enabled
        /// (cached per locale). All pagination cursors index into THIS string.
        /// </summary>
        public string GetBookText(string locale)
        {
            string key = !string.IsNullOrWhiteSpace(_bookLocalizationKey) ? _bookLocalizationKey
                : (_bookText != null ? _bookText.key : "");
            if (_bookText == null && string.IsNullOrWhiteSpace(key)) return "";
            if (_bookTextCache != null && _bookTextCacheLocale == locale) return _bookTextCache;

            PrepareLocalizationContext(locale);
            LocalizedTextResult resolved = ResolveLocalizedText(key, _bookText, locale);
            string raw = resolved.found ? (resolved.text ?? "") : "";
            string converted = _bookMarkdownEnabled ? MarkdownParser.ConvertToRichText(raw) : raw;
            // <image=...> tags stay in book text: MeasureFittingChars measures the processed
            // form but maps fit indices back to the source, so page slicing keeps tags whole
            // (an image never splits across a page — it moves to the next one).
            // Custom scopes are converted ONCE before pagination so measuring and slicing use
            // the exact same zero-width markup as the draw pass.
            converted = RichTextPbrTags.Preprocess(converted);
            converted = RichTextBlendTags.Preprocess(converted);
            converted = RichTextOpacityTags.Preprocess(converted);
            converted = InlineFontResolver.Process(converted, _bakeSettings.fontLibrary);
            // <bookmark=id> → stripped (zero layout cost); map id → char index kept for jumps.
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _bookTextCache = BookmarkUtility.Process(converted, map);
            _bookBookmarkMap = map;
            _bookTextCacheLocale = locale;
            return _bookTextCache;
        }

        /// <summary>Total character length of the localized (converted) book text.</summary>
        public int GetBookTextLength(string locale) => GetBookText(locale).Length;

        /// <summary>
        /// Given the start char of a logical page and the zone it will be laid out in, returns
        /// the text for that page AND (via out) the start char of the NEXT page. Measure + slice
        /// share the exact same logic so cursors can never drift.
        /// </summary>
        public string GetPageText(string full, int startChar, TextZone zone, out int nextChar)
        {
            ResolveSlotTargetSize(zone, out int bakeW, out int bakeH);
            var oneText = new Dictionary<string, string>(1);
            nextChar = LayoutOneSlot(full, Mathf.Clamp(startChar, 0, full.Length), zone, bakeW, bakeH, oneText);
            return oneText.TryGetValue(zone.id, out var t) ? t : "";
        }

        /// <summary>
        /// Resolves the WxH bake target a zone will be drawn into: the long side comes from
        /// Bake Settings Resolution, the aspect from the zone target's background texture
        /// (Base Albedo Texture override, else the material's texture). Square when there is
        /// no background texture. Pagination measures with the same size the baker draws with.
        /// </summary>
        private void ResolveSlotTargetSize(TextZone zone, out int bakeW, out int bakeH)
        {
            string texProp = string.IsNullOrEmpty(_bakeSettings.texturePropertyName)
                ? MeshTextBakerCore.DetectTexturePropertyName()
                : _bakeSettings.texturePropertyName;
            MeshTextBakerCore.ResolveBakeTargetSize(this, GetZoneRenderer(zone),
                zone != null ? zone.materialIndex : 0, _bakeSettings, texProp, out bakeW, out bakeH);
        }

        /// <summary>
        /// Measures where a logical page ENDS without baking. Returns the start char of the next
        /// page (== text length at the book's end).
        /// </summary>
        public int MeasurePageEnd(string full, int startChar, TextZone zone)
        {
            if (string.IsNullOrEmpty(full) || zone == null) return startChar;
            ResolveSlotTargetSize(zone, out int bakeW, out int bakeH);
            return LayoutOneSlot(full, Mathf.Clamp(startChar, 0, full.Length), zone, bakeW, bakeH, null);
        }

        /// <summary>
        /// Bakes specific text into specific zones (id → text). ONLY the given zones are touched;
        /// all other pages keep their existing baked textures (no flicker). Synchronous.
        /// </summary>
        public void BakeZones(Dictionary<string, string> zoneTexts, List<TextZone> zones, string locale)
        {
            CacheComponents();
            BakeBook(zoneTexts, zones, locale);
        }

        /// <summary>
        /// Coroutine variant of <see cref="BakeZones"/>: yields a frame between zone measurements
        /// to spread the layout cost across frames (less visible hitch during a page turn).
        /// <paramref name="pageStarts"/> maps zone.id → the page's start char in the book text.
        /// </summary>
        public IEnumerator BakeZonesAsync(List<TextZone> zones, Dictionary<string, int> pageStarts,
            string locale, Action onComplete)
        {
            CacheComponents();

            if (zones == null || zones.Count == 0)
            {
                onComplete?.Invoke();
                yield break;
            }

            string full = GetBookText(locale);
            var zoneTexts = new Dictionary<string, string>(zones.Count);

            foreach (var zone in zones)
            {
                if (zone == null) continue;
                int start = (pageStarts != null && pageStarts.TryGetValue(zone.id, out var s)) ? s : 0;
                string pageText = string.IsNullOrEmpty(full)
                    ? ""
                    : GetPageText(full, start, zone, out _);
                zoneTexts[zone.id] = pageText;
                yield return null; // spread the layout cost across frames
            }

            BakeBook(zoneTexts, zones, locale);
            onComplete?.Invoke();
        }

        /// <summary>
        /// Fits text from <paramref name="cursor"/> into one slot and returns the advanced cursor.
        /// When <paramref name="slotTexts"/> is non-null, stores the slot's text under slot.id.
        /// This is the single source of truth for page layout — measure and bake share it.
        /// Rich-text scopes crossing the page boundary are repaired: the page text is prefixed
        /// with reopening tags and suffixed with closing tags.
        /// </summary>
        private int LayoutOneSlot(string full, int cursor, TextZone slot, int bakeW, int bakeH,
            Dictionary<string, string> slotTexts)
        {
            if (cursor >= full.Length)
            {
                if (slotTexts != null) slotTexts[slot.id] = "";
                return cursor;
            }

            // Never allocate/measure the whole remaining book — a page holds at most a few
            // hundred chars, so work within a capped window from the cursor.
            // The window is extended by (token length - 1) chars for the SEARCH only, so a
            // <newpage> token straddling the window boundary is still found (and not half-baked
            // into the page as literal text).
            int measureWindow = Mathf.Max(256, _bakeSettings.measureWindow);
            int windowLen = Mathf.Min(measureWindow, full.Length - cursor);
            int searchLen = Mathf.Min(windowLen + PageBreakToken.Length - 1, full.Length - cursor);
            string window = full.Substring(cursor, searchLen);

            int brk = window.IndexOf(PageBreakToken, StringComparison.Ordinal);
            if (brk >= windowLen) brk = -1; // token starts beyond the measure window — next page's business
            string slotInput = (brk >= 0) ? window.Substring(0, brk) : window.Substring(0, windowLen);

            // Tags opened before this page must be re-opened here — and they also change layout
            // (<size>, <font> affect line heights), so they are part of the MEASURED text too.
            bool repairTags = slot.richTextEnabled;
            string reopenPrefix = repairTags ? RichTextTagRepair.GetReopenPrefix(full, cursor) : "";

            string measured = reopenPrefix.Length > 0 ? reopenPrefix + slotInput : slotInput;
            int fit = TextLayoutEngine.MeasureFittingChars(measured, slot, _bakeSettings, bakeW, bakeH);
            fit = Mathf.Clamp(fit - reopenPrefix.Length, 0, slotInput.Length);

            if (slotTexts != null)
            {
                string head = slotInput.Substring(0, fit);
                if (repairTags)
                {
                    string page = reopenPrefix + head;
                    slotTexts[slot.id] = page + RichTextTagRepair.GetClosingSuffix(page);
                }
                else
                {
                    slotTexts[slot.id] = head;
                }
            }

            if (fit >= slotInput.Length && brk >= 0)
            {
                cursor += brk + PageBreakToken.Length;
                while (cursor < full.Length && full[cursor] == '\n') cursor++;
            }
            else
            {
                cursor += fit;
                while (cursor < full.Length && (full[cursor] == '\n' || full[cursor] == ' ')) cursor++;
            }
            return cursor;
        }

        private void BakeBook(Dictionary<string, string> zoneTexts, List<TextZone> zones, string locale)
        {
            PrepareLocalizationContext(locale);
            // Before partial book bakes, remove live/editor baked slots that no longer correspond
            // to any current zone target. This is critical after changing material slot layout
            // from e.g. stack_L slot 1 back to slot 0.
            RestoreTouchedSlotsIfOrphaned();
            RestoreOrphanedTextBakedSlotsFor(zones);

            // We do NOT call ClearBakeInternal(): the book re-bakes only the affected zones;
            // clearing everything would wipe other pages' textures and cause flicker. Pooled
            // RenderTextures and material instances are reused per (renderer, materialIndex), so
            // re-baking just these zones repaints only their textures.
            //
            // The core expands the filter with sibling zones sharing the affected textures.
            // For those siblings we replay their LAST baked text so their content survives the
            // background re-blit (otherwise they'd be wiped blank).
            if (_currentBakedLocale == locale)
            {
                foreach (var kv in _lastBakedPageTexts)
                    if (!zoneTexts.ContainsKey(kv.Key)) zoneTexts[kv.Key] = kv.Value;
            }
            else
            {
                _lastBakedPageTexts.Clear(); // stale locale — do not replay old-locale text
            }

            var result = MeshTextBakerCore.Bake(this, locale, zoneTexts, zones);
            if (!result.success)
            {
                Debug.LogError("[MeshTextBaker] Book bake failed.", this);
                return;
            }
            foreach (var w in result.warnings) Debug.LogWarning($"[MeshTextBaker] {w}", this);

            ApplyBakeResult(result);
            _currentBakedLocale = locale;

            // Remember what each page slot now shows (for sibling replay on the next partial bake).
            foreach (var kv in zoneTexts)
                _lastBakedPageTexts[kv.Key] = kv.Value;
        }

        // ══════════════════════════════════════════════════════════
        // Internal
        // ══════════════════════════════════════════════════════════
        private void CacheComponents()
        {
            if (_meshRenderer == null) _meshRenderer = GetComponent<MeshRenderer>();
            if (_meshFilter == null) _meshFilter = GetComponent<MeshFilter>();
            if (_skinnedRenderer == null) _skinnedRenderer = GetComponent<SkinnedMeshRenderer>();
            NormalizeZoneOrder();

            // Auto-heal empty/duplicated zone ids (happens when zones are copy-pasted in the
            // inspector). Duplicates silently break overflow links and book baking, so fix
            // them proactively — the inspector additionally warns about links that pointed
            // at a healed id.
            if (_zones != null && _zones.Count > 1)
            {
                HashSet<string> seen = null; // allocate only when actually needed
                bool changed = false;
                for (int i = 0; i < _zones.Count; i++)
                {
                    var z = _zones[i];
                    if (z == null) continue;
                    if (seen == null) seen = new HashSet<string>();
                    if (string.IsNullOrEmpty(z.id) || !seen.Add(z.id))
                    {
                        z.RegenerateId();
                        seen.Add(z.id);
                        changed = true;
                    }
                }
#if UNITY_EDITOR
                if (changed && !Application.isPlaying && !UnityEditor.EditorUtility.IsPersistent(this))
                    UnityEditor.EditorUtility.SetDirty(this);
#endif
            }
        }

        private const string TextBakedSuffix = " (TextBaked)";

        private static bool IsTextBakedMaterial(Material mat)
        {
            // Handles "Page (TextBaked)" and Unity's "Page (TextBaked) (Instance)".
            return GetTextBakedBaseName(mat) != null;
        }

        private static string StripUnityCloneSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string instanceSuffix = " (Instance)";
            if (name.EndsWith(instanceSuffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - instanceSuffix.Length);
            return name;
        }

        private static string GetTextBakedBaseName(Material mat)
        {
            if (mat == null || string.IsNullOrEmpty(mat.name)) return null;
            string name = StripUnityCloneSuffix(mat.name);
            if (name.EndsWith(TextBakedSuffix, StringComparison.Ordinal))
                return name.Substring(0, name.Length - TextBakedSuffix.Length);
            return null;
        }

        private Material FindTouchedOriginal(Renderer r, int idx)
        {
            for (int i = 0; i < _touchedSlots.Count; i++)
            {
                if (_touchedSlots[i].renderer == r &&
                    _touchedSlots[i].materialIndex == idx &&
                    _touchedSlots[i].originalMaterial != null &&
                    !_touchedSlots[i].originalMaterial.name.EndsWith(TextBakedSuffix, StringComparison.Ordinal))
                {
                    return _touchedSlots[i].originalMaterial;
                }
            }
            return null;
        }

        private HashSet<MatTarget> BuildConfiguredBakeTargets()
        {
            var targets = new HashSet<MatTarget>();
            if (_zones == null) return targets;
            foreach (var zone in _zones)
            {
                if (zone == null) continue;
                Renderer r = GetZoneRenderer(zone);
                if (r == null) continue;
                targets.Add(new MatTarget(r, zone.materialIndex));
            }
            return targets;
        }

        private void RestoreOrphanedTextBakedSlotsFor(List<TextZone> zonesToBake)
        {
            if (zonesToBake == null || _touchedSlots.Count == 0) return;

            var seenRenderers = new HashSet<Renderer>();
            foreach (var z in zonesToBake)
            {
                if (z == null) continue;
                var r = GetZoneRenderer(z);
                if (r == null || !seenRenderers.Add(r)) continue;

                var materials = r.sharedMaterials;
                bool changed = false;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    var cur = materials[slot];
                    if (cur == null) continue;
                    if (!IsTextBakedMaterial(cur)) continue;

                    // If this is a configured active bake target for some zone in this bake, leave it.
                    bool isActiveTarget = false;
                    foreach (var activeZone in zonesToBake)
                    {
                        if (activeZone == null) continue;
                        if (GetZoneRenderer(activeZone) == r && activeZone.materialIndex == slot)
                        {
                            isActiveTarget = true;
                            break;
                        }
                    }
                    if (isActiveTarget) continue;

                    // It is a stale/orphaned bake slot. Recover the true original material.
                    Material orig = FindTouchedOriginal(r, slot);
                    if (orig != null)
                    {
                        materials[slot] = orig;
                        changed = true;
                    }
                }
                if (changed) r.sharedMaterials = materials;
            }
        }

        private void RecordTouchedSlot(Renderer r, int idx, Material original)
        {
            for (int i = 0; i < _touchedSlots.Count; i++)
                if (_touchedSlots[i].renderer == r && _touchedSlots[i].materialIndex == idx)
                    return;

            _touchedSlots.Add(new TouchedSlot { renderer = r, materialIndex = idx, originalMaterial = original });
#if UNITY_EDITOR
            if (!Application.isPlaying && !UnityEditor.EditorUtility.IsPersistent(this))
                UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        /// <summary>
        /// After a domain reload, generated instances/RTs are destroyed (DontSave) but the slots
        /// may still hold dead references. Restore the serialized originals if the runtime
        /// dictionaries are empty (i.e. this instance has no live bake).
        /// </summary>
        private void RestoreTouchedSlotsIfOrphaned()
        {
            if (_touchedSlots.Count == 0) return;
            if (_materialInstances.Count > 0) return; // live bake in this domain — nothing orphaned

            bool restoredAny = false;
            foreach (var slot in _touchedSlots)
            {
                var r = slot.renderer;
                if (r == null || slot.originalMaterial == null) continue;
                var materials = r.sharedMaterials;
                if (slot.materialIndex < 0 || slot.materialIndex >= materials.Length) continue;

                var current = materials[slot.materialIndex];

                // ONLY restore if the slot currently holds a dead/generated material.
                if (current != null && !IsTextBakedMaterial(current))
                {
                    // Stale record! Discard and do not touch the slot.
                    continue;
                }

                // Name sanity check: only restore if the original material name matches the base name
                if (current != null)
                {
                    string currentBakedBase = GetTextBakedBaseName(current);
                    if (currentBakedBase != null)
                    {
                        string originalBase = StripUnityCloneSuffix(slot.originalMaterial.name);
                        if (currentBakedBase != originalBase)
                        {
                            // Material name mismatch (stale record!). Do not restore.
                            continue;
                        }
                    }
                }

                if (current == null || IsTextBakedMaterial(current))
                {
                    materials[slot.materialIndex] = slot.originalMaterial;
                    r.sharedMaterials = materials;
                    restoredAny = true;
                }
            }
            bool hadRecords = true; // we entered with Count > 0
            _touchedSlots.Clear();
#if UNITY_EDITOR
            // Mark dirty whenever records were cleared (even if no slot was restored) so
            // stale lists do not reappear after a domain reload / scene save.
            if ((restoredAny || hadRecords) && !Application.isPlaying && !UnityEditor.EditorUtility.IsPersistent(this))
                UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        private void ClearBakeInternal()
        {
            // Restore original materials for every (renderer, slot) we touched.
            var restoreByRenderer = new Dictionary<Renderer, List<KeyValuePair<int, Material>>>();
            foreach (var kv in _originalSharedMaterials)
            {
                var r = kv.Key.renderer;
                if (r == null) continue;
                if (!restoreByRenderer.TryGetValue(r, out var list))
                {
                    list = new List<KeyValuePair<int, Material>>();
                    restoreByRenderer[r] = list;
                }
                list.Add(new KeyValuePair<int, Material>(kv.Key.materialIndex, kv.Value));
            }
            foreach (var kv in restoreByRenderer)
            {
                var r = kv.Key;
                var materials = r.sharedMaterials;
                foreach (var pair in kv.Value)
                    if (pair.Key >= 0 && pair.Key < materials.Length)
                        materials[pair.Key] = pair.Value;
                r.sharedMaterials = materials;
            }

            // Fallback restore path for slots touched before a domain reload.
            RestoreTouchedSlotsIfOrphanedAfterExplicitClear();

            foreach (var kv in _materialInstances)
            {
                var inst = kv.Value;
                if (inst == null) continue;
                if (Application.isPlaying) Destroy(inst);
                else DestroyImmediate(inst);
            }
            _materialInstances.Clear();

            // Pooled RenderTextures in _rtPool are reused across bakes; released in ReleasePool().
            _originalSharedMaterials.Clear();
            _bakeSourceMaterials.Clear();
            _touchedSlots.Clear();
            _lastBakedPageTexts.Clear();
        }

        private void RestoreTouchedSlotsIfOrphanedAfterExplicitClear()
        {
            // Serialized slots not present in the runtime dictionary (e.g. recorded before a
            // domain reload) still need their originals back.
            foreach (var slot in _touchedSlots)
            {
                var key = new MatTarget(slot.renderer, slot.materialIndex);
                if (_originalSharedMaterials.ContainsKey(key)) continue; // handled above
                var r = slot.renderer;
                if (r == null || slot.originalMaterial == null) continue;
                var materials = r.sharedMaterials;
                if (slot.materialIndex < 0 || slot.materialIndex >= materials.Length) continue;
                var current = materials[slot.materialIndex];

                // ONLY restore if the slot currently holds a dead/generated material.
                if (current != null && !IsTextBakedMaterial(current))
                {
                    continue;
                }

                // Name sanity check
                if (current != null)
                {
                    string currentBakedBase = GetTextBakedBaseName(current);
                    if (currentBakedBase != null)
                    {
                        string originalBase = StripUnityCloneSuffix(slot.originalMaterial.name);
                        if (currentBakedBase != originalBase)
                        {
                            continue;
                        }
                    }
                }

                if (current == null || IsTextBakedMaterial(current))
                {
                    materials[slot.materialIndex] = slot.originalMaterial;
                    r.sharedMaterials = materials;
                }
            }
        }

        private void ReleasePool()
        {
            ReleasePoolDict(_rtPool);
            ReleasePoolDict(_emissionRtPool);
            ReleasePoolDict(_maskRtPool);
            ReleasePoolDict(_heightRtPool);
        }

        private static void ReleasePoolDict(Dictionary<MatTarget, RenderTexture> pool)
        {
            foreach (var kv in pool)
            {
                var rt = kv.Value;
                if (rt == null) continue;
                rt.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(rt);
                else UnityEngine.Object.DestroyImmediate(rt);
            }
            pool.Clear();
        }
    }
}
