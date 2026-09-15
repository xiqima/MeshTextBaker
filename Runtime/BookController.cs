// Mesh Text Baker — BookController.cs
// "Two stacks + flipper" book model (the only book driver; old carousel controller removed).
//
// Rig contract (see the art brief in the docs):
//   Book_Root
//   ├── Cover_L, Cover_R, Spine       (covers; animated by Book_Open/Book_Close only)
//   ├── PageStack_L                   (static mesh: the visible LEFT page, 1 material slot)
//   ├── PageStack_R                   (static mesh: the visible RIGHT page, 1 material slot)
//   └── FlipPivot                     (empty node EXACTLY on the spine axis)
//       └── FlipSheet                 (1 sheet, 2 material slots: 0 = Front, 1 = Back)
//
// Invariant: outside of a turn only PageStack_L (page 2n) and PageStack_R (page 2n+1) are
// visible; FlipSheet is disabled. During a forward turn the flipper carries the current right
// page on its front and the next left page on its back, while PageStack_R already shows the
// next right page underneath. The flip itself is a code-driven rotation of FlipPivot
// (0° → 180° around the spine axis) — no animation reverse-engineering, no carousel tables.
//
// Zones: 4 PageSlot zones on the MeshTextSurface, one per surface, bound via targetRenderer:
//   stackL  → PageStack_L renderer,  materialIndex 0
//   stackR  → PageStack_R renderer,  materialIndex 0
//   flipF   → FlipSheet renderer,    materialIndex 0 (front)
//   flipB   → FlipSheet renderer,    materialIndex 1 (back)

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MeshTextBaker
{
    public enum BookState
    {
        Closed,
        Opening,
        Open,
        Turning,
        Closing
    }

    /// <summary>Which spread Open() jumps to.</summary>
    public enum BookOpenAt
    {
        /// <summary>Always open on the first spread (spread 0).</summary>
        Start,
        /// <summary>Re-open on the spread that was current when the book was last closed.</summary>
        ResumeLast
    }

    /// <summary>How GoToBookmark / JumpToSpread moves between spreads.</summary>
    public enum BookmarkJumpMode
    {
        /// <summary>Instant teleport (no flip).</summary>
        Instant,
        /// <summary>One flip animation, then land on the target spread.</summary>
        SingleFlip,
        /// <summary>Flip through every intermediate page.</summary>
        MultiFlip
    }

    /// <summary>How the page-flip motion is produced.</summary>
    public enum FlipMode
    {
        /// <summary>The controller rotates FlipPivot in code (no animation asset needed).</summary>
        Code,
        /// <summary>An Animator state ("FlipPage") is scrubbed via normalized time —
        /// artist-authored bend/secondary motion; the clip animates ONLY the flip rig.</summary>
        Animator
    }

    /// <summary>Which local axis of a stack mesh represents its thickness.</summary>
    public enum ThicknessAxis { X, Y, Z }

    /// <summary>How dynamic stack thickness is driven.</summary>
    public enum StackThicknessMode
    {
        /// <summary>Controller scales the stack transforms (needs bottom pivots and a
        /// scale axis the Animator does not touch).</summary>
        TransformScale,
        /// <summary>Controller drives a blend shape on each stack's SkinnedMeshRenderer.
        /// Ideal for boned/skinned rigs: the Animator keeps full ownership of transforms,
        /// and no pivot requirements apply (bottom verts simply have zero delta).</summary>
        BlendShape
    }

    /// <summary>Which UV axis of the edge texture runs across the page stripes.</summary>
    public enum EdgeUvAxis { U, V }

    [AddComponentMenu("Mesh Text Baker/Book Controller")]
    public class BookController : MonoBehaviour
    {
        [Header("Rig")]
        [SerializeField] private MeshTextSurface surface;
        [Tooltip("Optional. Animator with Closed/Open states for the covers (Book_Open / Book_Close). " +
                 "Open/Close clips MAY animate stacks/covers. FlipPage (if used) must only animate the flip rig.")]
        [SerializeField] private Animator animator;
        [SerializeField] private Renderer stackL;
        [SerializeField] private Renderer stackR;
        [Tooltip("Flip sheet renderer with 2 material slots: 0 = Front, 1 = Back.")]
        [SerializeField] private Renderer flipSheet;
        [Tooltip("Pivot on the spine axis. Local Z rotation: 0° = sheet lies right, 180° = left.")]
        [SerializeField] private Transform flipPivot;

        [Header("Animator parameters")]
        [Tooltip("Trigger parameter OR state name. If the Animator has a Trigger with this " +
                 "name, it is set (your transitions run). Otherwise the string is treated as " +
                 "a STATE name and played directly — no transitions needed. A state with " +
                 "negative speed (e.g. Close = the Open clip at speed -1) starts from its end.")]
        [SerializeField] private string openTrigger = "Open";
        [Tooltip("Same rules as Open Trigger.")]
        [SerializeField] private string closeTrigger = "Close";
        [Tooltip("Animator layer index used for Open and Close states (typically 0 for Base Layer).")]
        [SerializeField] private int openCloseLayer = 0;

        [Header("Page flip")]
        [Tooltip("Code — the controller rotates FlipPivot itself (default, zero animation " +
                 "work). Animator — plays the 'FlipPage' state and scrubs it via normalized " +
                 "time, letting an artist-authored clip (bend, sound cues) drive the look. " +
                 "The clip must ONLY animate FlipPivot rotation / bend bones / blendshapes.")]
        [SerializeField] private FlipMode flipMode = FlipMode.Animator;
        [Tooltip("Animator state name played/scrubbed when Flip Mode = Animator.")]
        [SerializeField] private string flipStateName = "FlipPage";
        [Tooltip("Animator layer index used for the Page Flip state (typically 1 when using a mask).")]
        [SerializeField] private int flipLayer = 1;

        [Header("Dynamic stack thickness (optional)")]
        [Tooltip("When assigned, the stacks visually grow/shrink as pages are turned: " +
                 "the LEFT stack thickens toward the end of the book, the RIGHT thins. " +
                 "Implemented by scaling the stack meshes on the local thickness axis.")]
        [SerializeField] private bool dynamicStackThickness = false;
        [Tooltip("TransformScale — controller scales the stack transforms. BlendShape — " +
                 "controller drives a blend shape on each stack's SkinnedMeshRenderer " +
                 "(recommended for boned rigs; the shape must NOT be keyed in any clip).")]
        [SerializeField] private StackThicknessMode thicknessMode = StackThicknessMode.BlendShape;
        [Tooltip("TransformScale mode: local axis of the stack meshes that represents thickness.")]
        [SerializeField] private ThicknessAxis thicknessAxis = ThicknessAxis.Y;
        [Tooltip("BlendShape mode: shape name present on BOTH stacks. " +
                 "Weight 100 = full book thickness, 0 = no pages.")]
        [SerializeField] private string thicknessBlendShape = "thickness";
        [Tooltip("Tick if your shape is authored the other way round (weight 100 = empty stack).")]
        [SerializeField] private bool invertThicknessShape = false;
        [Tooltip("Drive 'lift' blend shapes on the flip sheet so it takes off from and " +
                 "lands onto stacks of different heights flush. The sheet mesh needs two " +
                 "shapes authored against a zero-thickness bind pose: lift toward the " +
                 "right stack and (mirrored through the sheet) toward the left. " +
                 "Weight 100 = full book thickness — same scale as the stack shape. " +
                 "Do NOT key these shapes in the FlipPage clip.")]
        [SerializeField] private bool useFlipLiftShapes = true;
        [Tooltip("Blend shape on the flip sheet: lift while lying on the RIGHT stack.")]
        [SerializeField] private string flipLiftShapeR = "lift_R";
        [Tooltip("Blend shape on the flip sheet: lift while lying on the LEFT stack " +
                 "(authored as a mirrored bend — it points 'down' in bind pose and turns " +
                 "into an upward lift after the 180° flip).")]
        [SerializeField] private string flipLiftShapeL = "lift_L";
        [Tooltip("Scales the computed lift weight for the RIGHT shape (1 = as authored in Blender). " +
                 "Tweak here instead of re-exporting the mesh.")]
        [Range(0f, 2f)]
        [SerializeField] private float flipLiftScaleR = 1f;
        [Tooltip("Scales the computed lift weight for the LEFT shape.")]
        [Range(0f, 2f)]
        [SerializeField] private float flipLiftScaleL = 1f;
        [Tooltip("Added to the take-off lift fraction (before scale). Use small values like ±0.05–0.15.")]
        [Range(-0.5f, 0.5f)]
        [SerializeField] private float flipLiftTakeoffBias = 0f;
        [Tooltip("Added to the landing lift fraction (before scale).")]
        [Range(-0.5f, 0.5f)]
        [SerializeField] private float flipLiftLandingBias = 0f;
        [Tooltip("Hard cap for lift blend-shape weights (Unity 0–100).")]
        [Range(0f, 100f)]
        [SerializeField] private float flipLiftMaxWeight = 100f;
        [Range(0.05f, 1f)]
        [Tooltip("Minimum scale a stack shrinks to when it holds no pages.")]
        [SerializeField] private float minStackScale = 0.15f;
        [Tooltip("Scaling a mesh squeezes its UVs, so the page-edge stripes would get " +
                 "thinner instead of fewer. When enabled, the edge material's tiling is " +
                 "compensated via MaterialPropertyBlock: at scale k only a k-fraction of " +
                 "the texture is shown, keeping stripe size constant in world space " +
                 "(a thin stack honestly shows fewer pages).")]
        [SerializeField] private bool compensateEdgeTiling = true;
        [Tooltip("Material slot of the LEFT stack mesh that holds the page-edge texture.")]
        [SerializeField] private int edgeMaterialIndexL = 1;
        [Tooltip("Material slot of the RIGHT stack mesh that holds the page-edge texture.")]
        [SerializeField] private int edgeMaterialIndexR = 1;
        [Tooltip("UV axis of the edge texture that runs along the stack thickness " +
                 "(across the page stripes). Usually V.")]
        [SerializeField] private EdgeUvAxis edgeUvAxis = EdgeUvAxis.V;

        [Header("Performance")]
        [Tooltip("While the book is Closed, its pooled RenderTextures are released and " +
                 "nothing is baked — an idle book costs no VRAM and no CPU. Recommended " +
                 "for scenes with many books; textures come back on Open().")]
        [SerializeField] private bool sleepWhileClosed = true;

        [Header("Flow")]
        [Tooltip("Real books start with page 1 on the RIGHT: at the first spread the left " +
                 "stack is empty (and hidden if Auto Hide Empty Stacks is on). " +
                 "Off = legacy behavior, page 0 opens on the left.")]
        [SerializeField] private bool firstPageOnRight = true;
        [Tooltip("While fully Open, hides a stack that currently holds no pages. " +
                 "Never forces renderers ON and never runs during Open/Close/Closed — " +
                 "author initial visibility yourself (or via animation clips).")]
        [SerializeField] private bool autoHideEmptyStacks = true;
        [Tooltip("Where Open() starts reading. Start = spread 0. ResumeLast = the spread the " +
                 "player was on when the book was last closed (or Start if never opened).")]
        [SerializeField] private BookOpenAt openAt = BookOpenAt.Start;

        
        [Tooltip("How GoToBookmark / GoToSpreadAnimated moves to a far spread.")]
        [SerializeField] private BookmarkJumpMode bookmarkJumpMode = BookmarkJumpMode.MultiFlip;
        [Tooltip("Turn duration used only for MultiFlip bookmark jumps (seconds per page). " +
                 "Normal Next/Previous still use Turn Duration below.")]
        [SerializeField] private float bookmarkMultiFlipDuration = 0.25f;


        [SerializeField] private string locale = "en";
        [SerializeField] private float turnDuration = 0.7f;
        [SerializeField] private AnimationCurve turnCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [SerializeField] private float openDuration = 0.8f;

        // ── Events ─────────────────────────────────────────────
        /// <summary>(leftPage, rightPage) after a spread becomes current.</summary>
        public event Action<int, int> OnSpreadChanged;
        /// <summary>True while opening/closing/turning.</summary>
        public event Action<bool> OnBusyChanged;

        // ── State ──────────────────────────────────────────────
        private BookState _state = BookState.Closed;
        private int _spread;
        private int _lastClosedSpread; // remembered for BookOpenAt.ResumeLast
        private int? _queuedDir; // input queue of depth 1
        private bool _localeDirty; // locale changed mid-turn → rebake after the turn
        private ITextLocalizationProviderV2 _localizationEvents;
        private BookPaginator _pages;
        private bool _bookmarksDirty = true;
        private readonly Dictionary<string, int> _bookmarks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private float _thickFromL, _thickFromR, _thickToL, _thickToR;
        private bool _thicknessLerping;
        private float _turnDurationOverride = -1f; // >=0 replaces turnDuration for one Turn

        private TextZone _zStackL, _zStackR, _zFlipFront, _zFlipBack;
        private bool _zonesResolved;
        private Vector3 _stackLBaseScale = Vector3.one, _stackRBaseScale = Vector3.one;
        private bool _stackScalesCached;
        private int _totalPagesCache = -1;
        private int _shapeIdxL = -2, _shapeIdxR = -2; // -2 = unresolved, -1 = missing
        private int _liftIdxR = -2, _liftIdxL = -2;   // flip sheet lift shapes
        private MaterialPropertyBlock _edgeMpb;
        private Vector2 _edgeBaseTilingL = Vector2.one;
        private Vector2 _edgeBaseTilingR = Vector2.one;
        private bool _edgeBaseTilingCached;
        private bool _isTurning;
        private float _turnPhase;
        private int _turnDir;
        private float _takeoffLift;
        private float _landingLift;
        private static readonly int BaseColorMapStId = Shader.PropertyToID("_BaseColorMap_ST"); // HDRP Lit
        private static readonly int BaseMapStId = Shader.PropertyToID("_BaseMap_ST");           // URP Lit
        private static readonly int MainTexStId = Shader.PropertyToID("_MainTex_ST");           // Built-in / custom

        public BookState state => _state;
        public int spread => _spread;
        public int leftPage => LeftPage(_spread);
        public int rightPage => RightPage(_spread);
        public bool isBusy => _state == BookState.Turning || _state == BookState.Opening || _state == BookState.Closing;

        public int OpenCloseLayer { get => openCloseLayer; set => openCloseLayer = value; }
        public int FlipLayer { get => flipLayer; set => flipLayer = value; }

        public int EdgeMaterialIndexL { get => edgeMaterialIndexL; set => edgeMaterialIndexL = value; }
        public int EdgeMaterialIndexR { get => edgeMaterialIndexR; set => edgeMaterialIndexR = value; }

        public BookOpenAt OpenAt { get => openAt; set => openAt = value; }
        public BookmarkJumpMode BookmarkJumpMode { get => bookmarkJumpMode; set => bookmarkJumpMode = value; }
        public float BookmarkMultiFlipDuration { get => bookmarkMultiFlipDuration; set => bookmarkMultiFlipDuration = Mathf.Max(0.05f, value); }
        public int LastClosedSpread => _lastClosedSpread;
        /// <summary>Forces the resume bookmark (used by save games).</summary>
        public void SetResumeSpread(int spreadIndex) => _lastClosedSpread = Mathf.Max(0, spreadIndex);

        // ── Named bookmarks (<bookmark=id> in book text) ─────────
        /// <summary>True if a &lt;bookmark=id&gt; with this id exists in the current locale's text.</summary>
        public bool HasBookmark(string id)
        {
            EnsureBookmarks();
            return !string.IsNullOrEmpty(id) && _bookmarks.ContainsKey(id);
        }

        /// <summary>Logical page index of the bookmark, or -1 if missing.</summary>
        public int GetBookmarkPage(string id)
        {
            EnsureBookmarks();
            if (string.IsNullOrEmpty(id)) return -1;
            return _bookmarks.TryGetValue(id, out int page) ? page : -1;
        }

        /// <summary>Spread that contains the bookmark page, or -1 if missing.</summary>
        public int GetBookmarkSpread(string id)
        {
            int page = GetBookmarkPage(id);
            if (page < 0) return -1;
            return PageToSpread(page);
        }

        /// <summary>
        /// Jumps to the spread that contains &lt;bookmark=id&gt;. Works while Open
        /// (instant). Returns false if the bookmark is unknown.
        /// </summary>
        /// <summary>
        /// Jump to the bookmark. When the book is Open, pages flip one-by-one with the normal
        /// turn animation (not an instant teleport). While Closed, stores the target for ResumeLast.
        /// </summary>
        public bool GoToBookmark(string id)
        {
            int spread = GetBookmarkSpread(id);
            if (spread < 0) return false;
            return JumpToSpread(spread);
        }

        /// <summary>Jump using <see cref="bookmarkJumpMode"/> (Instant / SingleFlip / MultiFlip).</summary>
        public bool JumpToSpread(int targetSpread)
        {
            targetSpread = Mathf.Max(0, targetSpread);
            if (_state == BookState.Open)
            {
                if (targetSpread == _spread) return true;
                switch (bookmarkJumpMode)
                {
                    case BookmarkJumpMode.Instant:
                        GoToSpread(targetSpread);
                        break;
                    case BookmarkJumpMode.SingleFlip:
                        StartCoroutine(SingleFlipTo(targetSpread));
                        break;
                    default:
                        StartCoroutine(TurnToward(targetSpread));
                        break;
                }
                return true;
            }
            // Closed / busy: remember as resume target.
            _lastClosedSpread = targetSpread;
            openAt = BookOpenAt.ResumeLast;
            return true;
        }

        /// <summary>Flip page-by-page toward target (uses bookmarkMultiFlipDuration per page).</summary>
        public void GoToSpreadAnimated(int targetSpread)
        {
            if (_state != BookState.Open) return;
            if (targetSpread == _spread) return;
            StartCoroutine(TurnToward(targetSpread));
        }

        private IEnumerator TurnToward(int targetSpread)
        {
            targetSpread = Mathf.Max(0, targetSpread);
            while (_state == BookState.Turning)
                yield return null;

            float prev = _turnDurationOverride;
            _turnDurationOverride = Mathf.Max(0.05f, bookmarkMultiFlipDuration);
            try
            {
                while (_spread != targetSpread)
                {
                    if (_state != BookState.Open) yield break;
                    int dir = targetSpread > _spread ? 1 : -1;
                    if (!CanTurn(dir)) yield break;
                    yield return Turn(dir);
                }
            }
            finally
            {
                _turnDurationOverride = prev;
            }
        }

        /// <summary>
        /// One flip animation: pre-bake the landing spread onto the flipper, play a single turn,
        /// then set _spread to the target (skips intermediate page content).
        /// </summary>
        private IEnumerator SingleFlipTo(int targetSpread)
        {
            targetSpread = Mathf.Max(0, targetSpread);
            while (_state == BookState.Turning)
                yield return null;
            if (_state != BookState.Open || targetSpread == _spread) yield break;

            int dir = targetSpread > _spread ? 1 : -1;
            // Temporarily pretend the next spread is the final target for pre-bake, then jump.
            // Reuse Turn() only if adjacent; otherwise custom single flip.
            if (targetSpread == _spread + dir)
            {
                yield return Turn(dir);
                yield break;
            }

            SetState(BookState.Turning);
            ResolveZones();

            // Pre-bake flipper as if going from current to target in one motion.
            if (dir > 0)
            {
                BakeZonePage(_zFlipFront, RightPage(_spread));
                BakeZonePage(_zFlipBack, LeftPage(targetSpread));
                BakeZonePage(_zStackR, RightPage(targetSpread));
                if (autoHideEmptyStacks && stackR != null)
                    stackR.enabled = _pages.PageHasText(Mathf.Max(0, RightPage(targetSpread)));
            }
            else
            {
                BakeZonePage(_zFlipBack, LeftPage(_spread));
                BakeZonePage(_zFlipFront, RightPage(targetSpread));
                BakeZonePage(_zStackL, LeftPage(targetSpread));
                if (autoHideEmptyStacks && stackL != null)
                    stackL.enabled = LeftPage(targetSpread) >= 0;
            }

            float takeoffLift = 0f, landingLift = 0f;
            if (useFlipLiftShapes && dynamicStackThickness)
            {
                float curL, curR, nextL, nextR;
                StackWeightsAt(_spread, out curL, out curR);
                StackWeightsAt(targetSpread, out nextL, out nextR);
                takeoffLift = dir > 0 ? curR : curL;
                landingLift = dir > 0 ? nextL : nextR;
                takeoffLift = Mathf.Clamp01(takeoffLift + flipLiftTakeoffBias);
                landingLift = Mathf.Clamp01(landingLift + flipLiftLandingBias);
            }

            StackWeightsAt(_spread, out _thickFromL, out _thickFromR);
            StackWeightsAt(targetSpread, out _thickToL, out _thickToR);
            _thicknessLerping = dynamicStackThickness;

            _isTurning = true;
            _turnPhase = 0f;
            _turnDir = dir;
            _takeoffLift = takeoffLift;
            _landingLift = landingLift;
            SetFlipVisible(true);

            float from = dir > 0 ? 0f : 180f;
            float to = 180f - from;
            float dur = Mathf.Max(0.01f, turnDuration);

            if (flipMode == FlipMode.Animator && animator != null)
            {
                int flipHash = Animator.StringToHash(flipStateName);
                if (animator.HasState(flipLayer, flipHash))
                {
                    for (float t = 0f; t < 1f; t += Time.deltaTime / dur)
                    {
                        float phase = turnCurve.Evaluate(Mathf.Clamp01(t));
                        _turnPhase = phase;
                        float scrub = dir < 0 ? 1f - phase : phase;
                        animator.Play(flipHash, flipLayer, Mathf.Clamp01(scrub));
                        yield return null;
                    }
                    _turnPhase = 1f;
                    animator.Play(flipHash, flipLayer, dir > 0 ? 1f : 0f);
                    yield return null;
                }
                else
                {
                    SetFlipAngle(from);
                    for (float t = 0f; t < 1f; t += Time.deltaTime / dur)
                    {
                        float phase = turnCurve.Evaluate(Mathf.Clamp01(t));
                        _turnPhase = phase;
                        SetFlipAngle(Mathf.Lerp(from, to, phase));
                        yield return null;
                    }
                    _turnPhase = 1f;
                    SetFlipAngle(to);
                }
            }
            else
            {
                SetFlipAngle(from);
                for (float t = 0f; t < 1f; t += Time.deltaTime / dur)
                {
                    float phase = turnCurve.Evaluate(Mathf.Clamp01(t));
                    _turnPhase = phase;
                    SetFlipAngle(Mathf.Lerp(from, to, phase));
                    yield return null;
                }
                _turnPhase = 1f;
                SetFlipAngle(to);
            }

            _isTurning = false;
            _thicknessLerping = false;

            if (dir > 0) BakeZonePage(_zStackL, LeftPage(targetSpread));
            else BakeZonePage(_zStackR, RightPage(targetSpread));
            SetFlipVisible(false);
            SetFlipAngle(0f);
            SetFlipLift(0f, 0f);

            _spread = targetSpread;
            SetState(BookState.Open);
            UpdateStackThickness();
            UpdateStackVisibility(_spread);
            OnSpreadChanged?.Invoke(leftPage, rightPage);
            TryDequeue();
        }

        /// <summary>All bookmark ids defined in the current locale's book text.</summary>
        public IReadOnlyCollection<string> GetBookmarkIds()
        {
            EnsureBookmarks();
            return _bookmarks.Keys;
        }

        private int PageToSpread(int pageIndex)
        {
            if (firstPageOnRight)
                return Mathf.Max(0, (pageIndex + 1) / 2);
            return Mathf.Max(0, pageIndex / 2);
        }

        private void EnsureBookmarks()
        {
            if (!_bookmarksDirty) return;
            _bookmarks.Clear();
            _bookmarksDirty = false;
            if (surface == null || _pages == null) return;

            // Ensure book text (and bookmark map) is loaded for the current locale.
            if (_pages != null && string.IsNullOrEmpty(_pages.fullText))
                _pages.InvalidateCache();
            var map = surface.GetBookBookmarks(locale);
            if (map == null || map.Count == 0) return;

            foreach (var kv in map)
            {
                int page = CharIndexToPage(kv.Value);
                _bookmarks[kv.Key] = page;
            }
        }

        private int CharIndexToPage(int charIndex)
        {
            if (_pages == null) return 0;
            // Find largest page whose start <= charIndex.
            int lo = 0, hi = Mathf.Max(0, _pages.CountPages());
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_pages.GetPageStart(mid) <= charIndex) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>
        /// Forces zone rebinding on the next bake/turn. Call after reassigning stack/flip
        /// renderers or PageSlot targetRenderer/materialIndex at runtime.
        /// </summary>
        public void InvalidateZoneBindings()
        {
            _zonesResolved = false;
            _zStackL = _zStackR = _zFlipFront = _zFlipBack = null;
            _shapeIdxL = _shapeIdxR = -2;
            _liftIdxR = _liftIdxL = -2;
            _edgeBaseTilingCached = false;
            _totalPagesCache = -1;
        }

        // firstPageOnRight shifts the whole book by one page: spread 0 = (none, page 0).
        // Page index -1 = "no page" (BakeZonePage bakes an empty page for it).
        private int LeftPage(int s) => firstPageOnRight ? 2 * s - 1 : 2 * s;
        private int RightPage(int s) => firstPageOnRight ? 2 * s : 2 * s + 1;

        private void Reset()
        {
            surface = GetComponentInChildren<MeshTextSurface>();
            animator = GetComponentInChildren<Animator>();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Page-number reserve changes pagination — drop caches so the next Open remeasures.
            _totalPagesCache = -1;
            _bookmarksDirty = true;
            if (_pages != null) _pages.InvalidateCache();
        }
#endif

        private void Awake()
        {
            if (surface != null)
            {
                // Providers register their scene default in their own Awake (ordered first),
                // so the surface already resolves the automatic provider here.
                ITextLocalizationProvider provider = surface.localizationProvider;
                if (provider is ILocaleSelectionProvider selector &&
                    !string.IsNullOrWhiteSpace(selector.CurrentLocale))
                    locale = selector.CurrentLocale;
            }
            RefreshLocalizationSubscription();
            _pages = new BookPaginator(surface);
            _pages.measureZoneSelector = PageZoneForMeasure;
            _pages.SetLocale(locale);
            ResolveZones();
            SetFlipVisible(false);
            SetFlipAngle(0f);
            // Stack renderer enabled-state is authored by the user / Open-Close clips.
            // We never force stacks on. Auto-hide (if enabled) only runs while fully Open.
            _bookmarksDirty = true;
        }

        private void OnEnable()
        {
            MeshTextLocalizationRuntime.DefaultProviderChanged -= OnDefaultProviderChanged;
            MeshTextLocalizationRuntime.DefaultProviderChanged += OnDefaultProviderChanged;
            RefreshLocalizationSubscription();
        }

        private void OnDisable()
        {
            MeshTextLocalizationRuntime.DefaultProviderChanged -= OnDefaultProviderChanged;
        }

        private void OnDestroy()
        {
            MeshTextLocalizationRuntime.DefaultProviderChanged -= OnDefaultProviderChanged;
            if (_localizationEvents != null)
                _localizationEvents.LocalizationChanged -= OnLocalizationChanged;
            _localizationEvents = null;
        }

        private void OnLocalizationChanged()
        {
            string next = _localizationEvents is ILocaleSelectionProvider selector
                ? selector.CurrentLocale : locale;
            SetLocale(next);
        }

        /// <summary>
        /// Follows the surface's effective provider when the scene default changes (automatic
        /// mode). Explicitly wired surfaces are unaffected: the refresh below no-ops and the
        /// locale already matches.
        /// </summary>
        private void OnDefaultProviderChanged()
        {
            if (surface == null) return;
            ITextLocalizationProvider provider = surface.localizationProvider;
            RefreshLocalizationSubscription();
            if (provider is ILocaleSelectionProvider selector &&
                !string.IsNullOrWhiteSpace(selector.CurrentLocale) &&
                !string.Equals(locale, selector.CurrentLocale, StringComparison.Ordinal))
                SetLocale(selector.CurrentLocale);
        }

        /// <summary>Idempotent (re)subscription to the surface's current event source.</summary>
        private void RefreshLocalizationSubscription()
        {
            ITextLocalizationProviderV2 next = null;
            if (surface != null)
            {
                next = surface.localizationProvider as ITextLocalizationProviderV2;
                if (next is UnityEngine.Object unityObject && unityObject == null) next = null;
            }
            if (ReferenceEquals(_localizationEvents, next)) return;
            if (_localizationEvents != null)
                _localizationEvents.LocalizationChanged -= OnLocalizationChanged;
            _localizationEvents = next;
            if (_localizationEvents != null)
                _localizationEvents.LocalizationChanged += OnLocalizationChanged;
        }

        private void LateUpdate()
        {
            if (_state != BookState.Closed)
            {
                if (_isTurning && _thicknessLerping)
                    UpdateStackThicknessLerped(_turnPhase);
                else
                    UpdateStackThickness();

                if (_isTurning)
                    ApplyLiftAtPhase(_turnPhase, _turnDir, _takeoffLift, _landingLift);
            }
        }

        // ══════════════════════════════════════════════════════════
        // Public API
        // ══════════════════════════════════════════════════════════
        public void Open()
        {
            if (_state != BookState.Closed) return;
            StartCoroutine(OpenRoutine());
        }

        public void Close()
        {
            if (_state != BookState.Open) return;
            StartCoroutine(CloseRoutine());
        }

        public void NextPage() => EnqueueOrStart(+1);

        public void PreviousPage()
        {
            if (_spread <= 0 && _state == BookState.Open) return;
            EnqueueOrStart(-1);
        }

        /// <summary>Jumps straight to the given spread (no page-by-page animation).</summary>
        public void GoToSpread(int targetSpread)
        {
            if (_state != BookState.Open) return;
            targetSpread = Mathf.Max(0, targetSpread);
            // Clamp to the last spread that still has at least one page with text.
            if (_pages != null)
            {
                int total = Mathf.Max(1, _pages.CountPages());
                // With firstPageOnRight, spreads map to pages roughly 0..ceil(total/2).
                int maxSpread = firstPageOnRight
                    ? Mathf.Max(0, (total + 1) / 2)
                    : Mathf.Max(0, (total - 1) / 2);
                // Walk back if the candidate has no content (handles odd counts / empty tail).
                while (maxSpread > 0 &&
                       !_pages.PageHasText(Mathf.Max(0, LeftPage(maxSpread))) &&
                       !_pages.PageHasText(Mathf.Max(0, RightPage(maxSpread))))
                    maxSpread--;
                targetSpread = Mathf.Min(targetSpread, maxSpread);
            }
            if (targetSpread == _spread) return;
            _spread = targetSpread;
            RebakeVisible();
            UpdateStackThickness();
            OnSpreadChanged?.Invoke(leftPage, rightPage);
        }

        public void SetLocale(string newLocale)
        {
            locale = newLocale;
            if (_pages == null)
            {
                // Awake has not run yet (e.g. Example switcher calls SetLocale from its own
                // Awake). Remember the locale and bookmark state; the paginator will be
                // initialized with this locale in Awake().
                _totalPagesCache = -1;
                _bookmarksDirty = true;
                return;
            }
            _pages.SetLocale(newLocale);
            _totalPagesCache = -1; // page count differs per locale
            _bookmarksDirty = true;
            if (_state == BookState.Open)
            {
                RebakeVisible();
            }
            else if (_state == BookState.Turning || _state == BookState.Opening)
            {
                // Mid-turn / mid-open: re-baking now would mix locales or fight the opening
                // pre-bake. Defer the full rebake until the routine completes.
                _localeDirty = true;
            }
        }

        // ══════════════════════════════════════════════════════════
        // Open / Close
        // ══════════════════════════════════════════════════════════
        private IEnumerator OpenRoutine()
        {
            SetState(BookState.Opening);

            _totalPagesCache = -1;
            if (_pages != null) _pages.InvalidateCache();

            // Choose starting spread before the first bake.
            if (openAt == BookOpenAt.ResumeLast)
                _spread = Mathf.Max(0, _lastClosedSpread);
            else
                _spread = 0;

            _bookmarksDirty = true;
            RebakeVisible();
            PlayTriggerOrState(openTrigger);
            if (openDuration > 0f) yield return new WaitForSeconds(openDuration);

            SetState(BookState.Open);
            if (_localeDirty)
            {
                _localeDirty = false;
                RebakeVisible();
            }
            UpdateStackThickness();
            UpdateStackVisibility(_spread);
            OnSpreadChanged?.Invoke(leftPage, rightPage);
            TryDequeue();
        }

        private IEnumerator CloseRoutine()
        {
            SetState(BookState.Closing);
            _queuedDir = null;
            _lastClosedSpread = _spread;

            // Do not touch stack renderer.enabled — Open/Close clips and the user own it.
            PlayTriggerOrState(closeTrigger);
            if (openDuration > 0f) yield return new WaitForSeconds(openDuration);

            // Ensure the Animator close clip actually finished before releasing bake data.
            // Without this, ClearBake() could run while the close animation still shows
            // page textures and they would go blank mid-close.
            if (animator != null)
            {
                yield return null;
                float timeout = Mathf.Max(1f, openDuration * 2f + 1f);
                float elapsed = 0f;
                while (elapsed < timeout)
                {
                    if (!animator.IsInTransition(openCloseLayer))
                    {
                        var st = animator.GetCurrentAnimatorStateInfo(openCloseLayer);
                        // When close was played as a state, wait until it completes.
                        // When driven by a trigger, any transition finishing means close reached its destination.
                        if (st.normalizedTime >= 0.99f || st.length <= 0f)
                            break;
                        int closeHash = Animator.StringToHash(closeTrigger);
                        if (st.shortNameHash != closeHash && st.fullPathHash != closeHash)
                        {
                            // No longer in the close state, assume close clip finished.
                            break;
                        }
                    }
                    elapsed += Time.deltaTime;
                    yield return null;
                }
            }

            SetState(BookState.Closed);
            ApplyClosedStackPresentation();

            // Bake data is released only AFTER the close animation is done.
            if (sleepWhileClosed && surface != null)
                surface.ReleaseRenderTextures();
        }

        /// <summary>
        /// Scales the two stack meshes along the thickness axis according to reading
        /// progress: left grows, right shrinks. Purely visual; zones/UVs are unaffected
        /// because the top page keeps its full UV layout.
        /// </summary>
        /// <summary>Thickness fractions of the two stacks at a given spread (with the minStackScale floor).</summary>
        private void StackWeightsAt(int spreadIndex, out float leftK, out float rightK)
        {
            if (_totalPagesCache < 0 && _pages != null)
                _totalPagesCache = Mathf.Max(2, _pages.CountPages());

            float progress = _totalPagesCache > 1
                ? Mathf.Clamp01((float)Mathf.Max(0, LeftPage(spreadIndex)) / (_totalPagesCache - 1))
                : 0f;

            leftK = Mathf.Lerp(minStackScale, 1f, progress);
            rightK = Mathf.Lerp(1f, minStackScale, progress);
        }

        private void UpdateStackThickness()
        {
            if (!dynamicStackThickness || stackL == null || stackR == null) return;
            float leftK, rightK;
            StackWeightsAt(_spread, out leftK, out rightK);
            ApplyStackThicknessWeights(leftK, rightK);
        }

        /// <summary>Interpolate stack thickness across a page turn (phase 0 = start, 1 = end).</summary>
        private void UpdateStackThicknessLerped(float phase)
        {
            if (!dynamicStackThickness || stackL == null || stackR == null) return;
            phase = Mathf.Clamp01(phase);
            float leftK = Mathf.Lerp(_thickFromL, _thickToL, phase);
            float rightK = Mathf.Lerp(_thickFromR, _thickToR, phase);
            ApplyStackThicknessWeights(leftK, rightK);
        }

        private void ApplyStackThicknessWeights(float leftK, float rightK)
        {
            if (thicknessMode == StackThicknessMode.BlendShape)
            {
                ApplyThicknessShape(stackL, ref _shapeIdxL, leftK);
                ApplyThicknessShape(stackR, ref _shapeIdxR, rightK);
            }
            else
            {
                if (!_stackScalesCached)
                {
                    _stackLBaseScale = stackL.transform.localScale;
                    _stackRBaseScale = stackR.transform.localScale;
                    _stackScalesCached = true;
                }
                stackL.transform.localScale = ScaleAlongAxis(_stackLBaseScale, leftK);
                stackR.transform.localScale = ScaleAlongAxis(_stackRBaseScale, rightK);
            }

            ApplyEdgeTiling(stackL, leftK, edgeMaterialIndexL);
            ApplyEdgeTiling(stackR, rightK, edgeMaterialIndexR);
        }

        /// <summary>
        /// BlendShape thickness mode: weight fraction == thickness fraction
        /// (weight 100 = full stack). The shape must not be keyed in any Animator clip,
        /// or the Animator will stomp the controller's value every frame.
        /// </summary>
        private void ApplyThicknessShape(Renderer r, ref int shapeIdx, float k)
        {
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr == null || smr.sharedMesh == null)
            {
                if (shapeIdx == -2)
                    Debug.LogWarning("[MeshTextBaker] Thickness Mode = BlendShape requires the stacks " +
                                     "to be SkinnedMeshRenderers with a '" + thicknessBlendShape + "' shape.", r);
                shapeIdx = -1;
                return;
            }
            if (shapeIdx == -2)
            {
                shapeIdx = smr.sharedMesh.GetBlendShapeIndex(thicknessBlendShape);
                if (shapeIdx < 0)
                    Debug.LogWarning("[MeshTextBaker] Blend shape '" + thicknessBlendShape +
                                     "' not found on '" + r.name + "'.", r);
            }
            if (shapeIdx < 0) return;

            float w = invertThicknessShape ? 1f - k : k;
            smr.SetBlendShapeWeight(shapeIdx, Mathf.Clamp01(w) * 100f);
        }

        /// <summary>
        /// Counteracts UV squeeze on the page-edge material: at scale k, show only a
        /// k-fraction of the texture so stripes keep their world-space size. Uses a
        /// MaterialPropertyBlock on the edge slot only — no material instances, and it
        /// survives the baker swapping the page material in slot 0.
        /// The window is anchored at V=0 (texture bottom); with a uniform stripe
        /// pattern the anchor is invisible anyway.
        /// </summary>
        private void CacheEdgeBaseTilingIfNeeded()
        {
            if (_edgeBaseTilingCached) return;
            _edgeBaseTilingL = ReadBaseTiling(stackL, edgeMaterialIndexL);
            _edgeBaseTilingR = ReadBaseTiling(stackR, edgeMaterialIndexR);
            _edgeBaseTilingCached = true;
        }

        private static Vector2 ReadBaseTiling(Renderer r, int slotIndex)
        {
            if (r == null) return Vector2.one;
            var mats = r.sharedMaterials; // one alloc at cache time, not every LateUpdate
            int slot = Mathf.Max(0, slotIndex);
            if (slot >= mats.Length || mats[slot] == null) return Vector2.one;
            Material mat = mats[slot];
            string[] texProperties = { "_BaseColorMap", "_BaseMap", "_MainTex" };
            foreach (var prop in texProperties)
            {
                if (mat.HasProperty(prop))
                    return mat.GetTextureScale(prop);
            }
            return Vector2.one;
        }

        private void ApplyEdgeTiling(Renderer r, float k, int slotIndex)
        {
            if (!compensateEdgeTiling || r == null) return;
            int slot = Mathf.Max(0, slotIndex);
            // Avoid sharedMaterials alloc every frame — only bounds-check via property block path.
            CacheEdgeBaseTilingIfNeeded();
            Vector2 baseTiling = (r == stackL) ? _edgeBaseTilingL : _edgeBaseTilingR;

            if (_edgeMpb == null) _edgeMpb = new MaterialPropertyBlock();
            r.GetPropertyBlock(_edgeMpb, slot);

            // ST vector layout: (tiling.x, tiling.y, offset.x, offset.y).
            Vector4 st = edgeUvAxis == EdgeUvAxis.U
                ? new Vector4(baseTiling.x * k, baseTiling.y, 0f, 0f)
                : new Vector4(baseTiling.x, baseTiling.y * k, 0f, 0f);
            _edgeMpb.SetVector(BaseColorMapStId, st);
            _edgeMpb.SetVector(BaseMapStId, st);
            _edgeMpb.SetVector(MainTexStId, st);

            r.SetPropertyBlock(_edgeMpb, slot);
        }

        private Vector3 ScaleAlongAxis(Vector3 baseScale, float k)
        {
            switch (thicknessAxis)
            {
                case ThicknessAxis.X: return new Vector3(baseScale.x * k, baseScale.y, baseScale.z);
                case ThicknessAxis.Z: return new Vector3(baseScale.x, baseScale.y, baseScale.z * k);
                default:              return new Vector3(baseScale.x, baseScale.y * k, baseScale.z);
            }
        }

        /// <summary>
        /// Phase 0..1 of the flip → lift weights: the take-off lift fades out over the
        /// first half, the landing lift grows over the second half. Which physical shape
        /// (R or L) plays which role depends on the turn direction.
        /// </summary>
        private void ApplyLiftAtPhase(float phase, int dir, float takeoffLift, float landingLift)
        {
            if (!useFlipLiftShapes || !dynamicStackThickness) return;
            float takeoff = takeoffLift * Mathf.Clamp01(1f - phase * 2f);        // 1 → 0 over first half
            float landing = landingLift * Mathf.Clamp01(phase * 2f - 1f);        // 0 → 1 over second half
            if (dir > 0) SetFlipLift(takeoff, landing);
            else SetFlipLift(landing, takeoff);
        }

        /// <summary>
        /// Sets the flip sheet lift shapes (fractions 0..1 of full book thickness).
        /// The FlipPage clip is authored against zero stack thickness; these shapes are
        /// the runtime height correction, so the sheet takes off and lands flush no
        /// matter how the two stacks are currently weighted (e.g. 0.75 / 0.25).
        /// </summary>
        private void SetFlipLift(float liftR, float liftL)
        {
            if (!useFlipLiftShapes || !dynamicStackThickness) return;
            SkinnedMeshRenderer smr = flipSheet as SkinnedMeshRenderer;
            if (smr == null || smr.sharedMesh == null)
            {
                if (_liftIdxR == -2)
                    Debug.LogWarning("[MeshTextBaker] Use Flip Lift Shapes requires the flip sheet " +
                                     "to be a SkinnedMeshRenderer with lift blend shapes.", flipSheet);
                _liftIdxR = _liftIdxL = -1;
                return;
            }
            if (_liftIdxR == -2)
            {
                _liftIdxR = smr.sharedMesh.GetBlendShapeIndex(flipLiftShapeR);
                _liftIdxL = smr.sharedMesh.GetBlendShapeIndex(flipLiftShapeL);
                if (_liftIdxR < 0 || _liftIdxL < 0)
                    Debug.LogWarning("[MeshTextBaker] Lift shapes '" + flipLiftShapeR + "'/'" +
                                     flipLiftShapeL + "' not found on '" + smr.name + "'.", smr);
            }
            float maxW = Mathf.Clamp(flipLiftMaxWeight, 0f, 100f);
            if (_liftIdxR >= 0)
                smr.SetBlendShapeWeight(_liftIdxR, Mathf.Clamp01(liftR * flipLiftScaleR) * maxW);
            if (_liftIdxL >= 0)
                smr.SetBlendShapeWeight(_liftIdxL, Mathf.Clamp01(liftL * flipLiftScaleL) * maxW);
        }

        // ══════════════════════════════════════════════════════════
        // Page turning
        // ══════════════════════════════════════════════════════════
        private void EnqueueOrStart(int dir)
        {
            if (_state == BookState.Turning)
            {
                // Queue of depth 1: remember only the latest request.
                _queuedDir = dir;
                return;
            }
            if (_state != BookState.Open) return;
            if (!CanTurn(dir)) return;
            StartCoroutine(Turn(dir));
        }

        private bool CanTurn(int dir)
        {
            if (dir > 0) return _pages.PageHasText(Mathf.Max(0, LeftPage(_spread + 1)));
            return _spread > 0;
        }

        private float EffectiveTurnDuration()
        {
            return _turnDurationOverride > 0f ? _turnDurationOverride : turnDuration;
        }

        private IEnumerator Turn(int dir)
        {
            SetState(BookState.Turning);
            ResolveZones();

            int target = _spread + dir;

            // 1. Pre-bake the sides that must be correct BEFORE the flip starts.
            if (dir > 0)
            {
                BakeZonePage(_zFlipFront, RightPage(_spread)); // copy of the current right page
                BakeZonePage(_zFlipBack, LeftPage(target));    // the upcoming left page
                BakeZonePage(_zStackR, RightPage(target));     // already underneath the flipper
                if (autoHideEmptyStacks && stackR != null)
                    stackR.enabled = _pages.PageHasText(Mathf.Max(0, RightPage(target)));
            }
            else
            {
                BakeZonePage(_zFlipBack, LeftPage(_spread));   // copy of the current left page
                BakeZonePage(_zFlipFront, RightPage(target));  // the upcoming right page
                BakeZonePage(_zStackL, LeftPage(target));      // already underneath the flipper
                if (autoHideEmptyStacks && stackL != null)
                    stackL.enabled = LeftPage(target) >= 0;
            }
            // Lift correction: the FlipPage motion is authored against zero-thickness
            // stacks, so the sheet must start raised to the take-off stack's current
            // height and land raised to the landing stack's height AFTER this turn.
            // First half of the flip: take-off lift fades to 0 (sheet straightens),
            // second half: landing lift grows in — flush at both ends, e.g. 0.75/0.25.
            float takeoffLift = 0f, landingLift = 0f;
            if (useFlipLiftShapes && dynamicStackThickness)
            {
                float curL, curR, nextL, nextR;
                StackWeightsAt(_spread, out curL, out curR);
                StackWeightsAt(target, out nextL, out nextR);
                takeoffLift = dir > 0 ? curR : curL;   // where the sheet starts
                landingLift = dir > 0 ? nextL : nextR; // where it lands
                takeoffLift = Mathf.Clamp01(takeoffLift + flipLiftTakeoffBias);
                landingLift = Mathf.Clamp01(landingLift + flipLiftLandingBias);
            }

            // Smooth stack thickness from current spread → target across the whole flip.
            StackWeightsAt(_spread, out _thickFromL, out _thickFromR);
            StackWeightsAt(target, out _thickToL, out _thickToR);
            _thicknessLerping = dynamicStackThickness;

            _isTurning = true;
            _turnPhase = 0f;
            _turnDir = dir;
            _takeoffLift = takeoffLift;
            _landingLift = landingLift;

            SetFlipVisible(true);

            // 2. One sheet turns over: either code-driven rotation (default) or an
            // artist clip scrubbed by normalized time (FlipMode.Animator). Backward turns
            // scrub the same clip in reverse — no second clip needed.
            float from = dir > 0 ? 0f : 180f;
            float to = 180f - from;
            if (flipMode == FlipMode.Animator && animator != null)
            {
                int flipHash = Animator.StringToHash(flipStateName);
                if (!animator.HasState(flipLayer, flipHash))
                {
                    Debug.LogWarning("[MeshTextBaker] Flip Mode = Animator but state '" +
                                     flipStateName + "' was not found on layer " + flipLayer +
                                     ". Falling back to Code rotation for this turn.", animator);
                    // fall through to code path below via nulling the branch
                    flipHash = 0;
                }
                if (flipHash != 0)
                {
                    for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(0.01f, EffectiveTurnDuration()))
                    {
                        float phase = turnCurve.Evaluate(Mathf.Clamp01(t));
                        _turnPhase = phase;
                        if (dir < 0) phase = 1f - phase;
                        animator.Play(flipHash, flipLayer, Mathf.Clamp01(phase));
                        yield return null;
                    }
                    _turnPhase = 1f;
                    animator.Play(flipHash, flipLayer, dir > 0 ? 1f : 0f);
                    yield return null; // let the final pose apply before hiding the flipper
                }
                else
                {
                    // Code fallback when the Animator state is missing.
                    float fromA = dir > 0 ? 0f : 180f;
                    float toA = 180f - fromA;
                    SetFlipAngle(fromA);
                    for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(0.01f, EffectiveTurnDuration()))
                    {
                        float phase = turnCurve.Evaluate(Mathf.Clamp01(t));
                        _turnPhase = phase;
                        SetFlipAngle(Mathf.Lerp(fromA, toA, phase));
                        yield return null;
                    }
                    _turnPhase = 1f;
                    SetFlipAngle(toA);
                }
            }
            else
            {
                SetFlipAngle(from);
                for (float t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(0.01f, EffectiveTurnDuration()))
                {
                    float phase = turnCurve.Evaluate(Mathf.Clamp01(t));
                    _turnPhase = phase;
                    SetFlipAngle(Mathf.Lerp(from, to, phase));
                    yield return null;
                }
                _turnPhase = 1f;
                SetFlipAngle(to);
            }

            _isTurning = false;
            _thicknessLerping = false;

            // 3. Finalize: the stack the flipper landed on takes over, flipper hides.
            if (dir > 0) BakeZonePage(_zStackL, LeftPage(target));
            else BakeZonePage(_zStackR, RightPage(target));
            SetFlipVisible(false);
            SetFlipAngle(0f);
            SetFlipLift(0f, 0f);

            _spread = target;
            SetState(BookState.Open);
            UpdateStackThickness();
            UpdateStackVisibility(_spread);

            // Locale changed mid-turn: pagination has been rebuilt, rebake the whole spread.
            if (_localeDirty)
            {
                _localeDirty = false;
                RebakeVisible();
            }

            OnSpreadChanged?.Invoke(leftPage, rightPage);

            TryDequeue();
        }

        private void TryDequeue()
        {
            if (!_queuedDir.HasValue || _state != BookState.Open) return;
            int next = _queuedDir.Value;
            _queuedDir = null;
            if (CanTurn(next)) StartCoroutine(Turn(next));
        }

        // ══════════════════════════════════════════════════════════
        // Baking helpers
        // ══════════════════════════════════════════════════════════
        private void RebakeVisible()
        {
            ResolveZones();
            BakeZonePage(_zStackL, LeftPage(_spread));
            BakeZonePage(_zStackR, RightPage(_spread));
            UpdateStackVisibility(_spread);
        }

        private void BakeZonePage(TextZone zone, int pageIndex)
        {
            if (zone == null || surface == null) return;

            // Body text from paginator (full page zone — layout never depends on numbers).
            string text = pageIndex < 0 ? "" : _pages.GetPageText(pageIndex);
            var texts = new Dictionary<string, string>(4) { { zone.id, text } };
            var list = new List<TextZone>(4) { zone };

            // Optional user-authored PageNumber zones on the same renderer + material slot.
            if (pageIndex >= 0 && _pages != null && _pages.PageHasText(pageIndex))
            {
                string num = FormatPageNumber(pageIndex);
                foreach (var nz in FindPageNumberZonesFor(zone))
                {
                    texts[nz.id] = num;
                    list.Add(nz);
                }
            }

            surface.BakeZones(texts, list, locale);
        }

        /// <summary>Display string for a logical page index (offset +1 by convention via format helper).</summary>
        private string FormatPageNumber(int pageIndex)
        {
            // 1-based display by default (page 0 → "1").
            return (pageIndex + 1).ToString();
        }

        /// <summary>
        /// PageNumber zones that share this page slot's renderer and material index.
        /// Authored freely in the UV editor / inspector (not auto-generated).
        /// </summary>
        private List<TextZone> FindPageNumberZonesFor(TextZone pageZone)
        {
            var result = new List<TextZone>();
            if (surface == null || pageZone == null) return result;

            // Prefer explicit parent link (child PageNumber of this PageSlot).
            foreach (var z in surface.GetPageNumberChildren(pageZone))
            {
                surface.ApplyPageNumberDefaults(z);
                // Keep renderer/slot in sync with parent page.
                z.targetRenderer = pageZone.targetRenderer;
                z.materialIndex = pageZone.materialIndex;
                result.Add(z);
            }

            // Legacy fallback: same renderer+slot, no parent set.
            if (result.Count == 0)
            {
                var pageR = surface.GetZoneRenderer(pageZone);
                foreach (var z in surface.zones)
                {
                    if (z == null || z.role != TextZoneRole.PageNumber) continue;
                    if (!string.IsNullOrEmpty(z.parentZoneId) && z.parentZoneId != pageZone.id)
                        continue;
                    if (surface.GetZoneRenderer(z) != pageR) continue;
                    if (z.materialIndex != pageZone.materialIndex) continue;
                    if (string.IsNullOrEmpty(z.parentZoneId))
                        z.parentZoneId = pageZone.id; // heal link once
                    surface.ApplyPageNumberDefaults(z);
                    result.Add(z);
                }
            }
            return result;
        }


        /// <summary>
        /// Measure zone for a logical page: even pages use the LEFT template, odd the RIGHT.
        /// All left surfaces (stack L, flip back) must share one zone size; same for right.
        /// When page numbers are on, returns a height-reduced proxy so the number line is reserved.
        /// </summary>
        private TextZone PageZoneForMeasure(int pageIndex)
        {
            ResolveZones();
            bool isLeft = ((pageIndex & 1) == 0) != firstPageOnRight;
            // Full zone — page numbers are an overlay strip and must not change body pagination,
            // otherwise flipper and stacks measure/bake different line breaks.
            return isLeft ? (_zStackL ?? _zFlipBack) : (_zStackR ?? _zFlipFront);
        }


        // ══════════════════════════════════════════════════════════
        // Rig helpers
        // ══════════════════════════════════════════════════════════
        private void ResolveZones()
        {
            if (_zonesResolved || surface == null) return;

            var flipZones = new List<TextZone>();
            foreach (var z in surface.GetPageSlots())
            {
                var r = surface.GetZoneRenderer(z);
                if (r == stackL && stackL != null) _zStackL = z;
                else if (r == stackR && stackR != null) _zStackR = z;
                else if (r == flipSheet && flipSheet != null) flipZones.Add(z);
            }

            // Front/back of the flip sheet: either two material slots (0 = front, 1 = back)
            // or a single slot with both pages side by side in one texture — then the
            // zones' Order decides: lower orderIndex = front.
            if (flipZones.Count >= 2)
            {
                flipZones.Sort((a, b) => a.materialIndex != b.materialIndex
                    ? a.materialIndex.CompareTo(b.materialIndex)
                    : a.orderIndex.CompareTo(b.orderIndex));
                _zFlipFront = flipZones[0];
                _zFlipBack = flipZones[1];
            }
            else if (flipZones.Count == 1)
            {
                _zFlipFront = flipZones[0];
            }

            if (_zStackL == null || _zStackR == null || _zFlipFront == null || _zFlipBack == null)
            {
                Debug.LogWarning(
                    "[MeshTextBaker] BookController: expected 4 PageSlot zones bound to " +
                    "PageStack_L, PageStack_R and FlipSheet (materialIndex 0 = front, 1 = back). " +
                    $"Found: L={( _zStackL != null)}, R={(_zStackR != null)}, " +
                    $"F={(_zFlipFront != null)}, B={(_zFlipBack != null)}.", this);
            }

            if (_zStackL != null && _zStackR != null)
            {
                if (_zStackL.materialIndex == edgeMaterialIndexL)
                {
                    Debug.LogWarning(
                        $"[MeshTextBaker] BookController: Left page zone Material Index ({_zStackL.materialIndex}) conflicts " +
                        $"with Edge Slot Left Stack ({edgeMaterialIndexL})! They should be different slots.", this);
                }
                if (_zStackR.materialIndex == edgeMaterialIndexR)
                {
                    Debug.LogWarning(
                        $"[MeshTextBaker] BookController: Right page zone Material Index ({_zStackR.materialIndex}) conflicts " +
                        $"with Edge Slot Right Stack ({edgeMaterialIndexR})! They should be different slots.", this);
                }
            }

            _zonesResolved = true;
        }

        /// <summary>
        /// Fires an Animator trigger if a Trigger parameter with that name exists; otherwise
        /// plays the string as a state name directly (bare, unconnected states are fine).
        /// If the state runs at negative speed, playback starts from its end so reversed
        /// clips (Close = Open at speed -1) work out of the box.
        /// </summary>
        private void PlayTriggerOrState(string name)
        {
            if (animator == null || string.IsNullOrEmpty(name)) return;

            foreach (var p in animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Trigger && p.name == name)
                {
                    animator.SetTrigger(name);
                    return;
                }
            }

            int hash = Animator.StringToHash(name);
            if (!animator.HasState(openCloseLayer, hash))
            {
                Debug.LogWarning("[MeshTextBaker] Animator has neither a Trigger parameter nor " +
                                 "a state named '" + name + "'.", animator);
                return;
            }
            animator.Play(hash, openCloseLayer, 0f);
            animator.Update(0f); // apply so the state's speed is readable
            if (animator.GetCurrentAnimatorStateInfo(openCloseLayer).speed < 0f)
                animator.Play(hash, openCloseLayer, 1f); // negative speed: run from the end
        }

        /// <summary>
        /// Optional auto-hide of empty stacks — ONLY while fully Open and only when
        /// <see cref="autoHideEmptyStacks"/> is on. Never forces renderers ON: the user
        /// (and Open/Close animation clips) fully own the initial / animated visibility.
        /// </summary>
        private void ApplyClosedStackPresentation()
        {
            // Closed book presentation is deterministic: only the right/full stack remains.
            if (stackL != null) stackL.enabled = false;
            if (stackR != null) stackR.enabled = true;
            SetFlipVisible(false);
            if (dynamicStackThickness && stackL != null && stackR != null)
                ApplyStackThicknessWeights(minStackScale, 1f);
        }

        private void UpdateStackVisibility(int spreadIndex)
        {
            if (!autoHideEmptyStacks) return;
            if (_state != BookState.Open) return; // leave Opening/Closing/Closed alone

            if (stackL != null) stackL.enabled = LeftPage(spreadIndex) >= 0;
            if (stackR != null) stackR.enabled = _pages != null && _pages.PageHasText(Mathf.Max(0, RightPage(spreadIndex)));
        }

        private void SetFlipVisible(bool visible)
        {
            if (flipSheet != null) flipSheet.enabled = visible;
        }

        private void SetFlipAngle(float degrees)
        {
            if (flipPivot != null) flipPivot.localRotation = Quaternion.Euler(0f, 0f, degrees);
        }

        private void SetState(BookState next)
        {
            bool wasBusy = isBusy;
            _state = next;
            if (wasBusy != isBusy) OnBusyChanged?.Invoke(isBusy);
        }
    }
}
