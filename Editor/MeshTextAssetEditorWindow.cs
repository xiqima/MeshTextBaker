// Mesh Text Baker — MeshTextAssetEditorWindow.cs  (v6 — scroll + paste newline normalization)
// UI Toolkit writing environment for MeshTextAsset.
//
// ARCHITECTURE (based on the consensus of 4 external reviews of AGENT_TextEditorWindow_Issue.md):
// In Unity 6 the TextField is only a wrapper — ALL input handling (pointer, keyboard,
// context menu, select-all-on-focus) lives in the inner "unity-text-input" TextElement,
// which is created during attach (NOT at construction) and processes events before
// anything reaches callbacks registered on the wrapper. Therefore:
//   1. Selection flags (textSelection.selectAllOnFocus = false etc.) are applied AFTER
//      AttachToPanelEvent and re-applied one tick later (the inner element can be
//      re-initialized during the first layout, wiping flags set at construction).
//   2. All input callbacks are registered ON THE INNER ELEMENT (TrickleDown).
//   3. The context menu uses the canonical ContextualMenuManipulator targeted at the
//      inner element (the engine displays it itself — correct at any UI scale).
//   4. Focus() is NEVER called when the field is already focused — refocusing re-runs
//      the engine's select-all path (that was the "next keystroke erases all" bug).
//      Focus and caret placement are separated by a scheduler tick.
//   5. Enter/Shift+Enter are NOT intercepted: Unity 6 multiline handles both natively;
//      the old manual insertion fought the built-in handler.
//   6. Safety net: on inner-element FocusIn, a full-text selection (buggy flag path)
//      is collapsed back to the caret on the next tick.
// All text mutations flow through ONE entry point (ReplaceRange) that maintains the undo
// stack, the caret, dirty state, preview and counters. The TextField is only a view.

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MeshTextBaker.Editor
{
    public class MeshTextAssetEditorWindow : EditorWindow
    {
        [SerializeField] private MeshTextAsset _asset;
        [SerializeField] private bool _autoSave;

        // ── Document state ──
        private int _localeIndex;
        private int _workingLocaleIndex = -1;
        private string _workingLocaleCode;
        private string _workingText = "";
        private bool _dirty;
        // Last serialized asset state observed by this window. Unity's undo callback is global;
        // comparing snapshots prevents an unrelated scene/object Undo from resetting our draft.
        private string _observedAssetState;

        // ── Undo/redo (own stacks; text-only snapshots, grouped by time) ──
        private readonly List<string> _undoStack = new List<string>();
        private readonly List<string> _redoStack = new List<string>();
        private const int MaxUndoDepth = 200;
        private double _lastEditTime;
        private const double UndoGroupSeconds = 0.7;

        // ── Input state ──
        private bool _fieldFocused;
        private bool _swallowNextNewlineChar;
        private Color _pickedColor = new Color(1f, 0.35f, 0f);
        private int _sizePercent = 120;

        // The REAL input element inside the TextField (Unity 6: all pointer/keyboard handling
        // happens there, before anything bubbles to the TextField wrapper). Resolved after
        // AttachToPanel — it does not reliably exist right after construction.
        private VisualElement _textInput;
        private bool _inputCallbacksRegistered;

        // ── UI ──
        private ObjectField _assetField;
        private Toolbar _localeBar;
        private ToolbarButton _saveButton;
        private ToolbarToggle _markdownToggle;
        private ToolbarToggle _richTextToggle;
        private VisualElement _commonFormattingControls;
        private VisualElement _markdownControls;
        private VisualElement _richTextControls;
        private Toolbar _markupToolbar;
        private HelpBox _noLocaleNotice;
        private TextField _textField;
        private Label _previewLabel;
        private VisualElement _previewBox;
        private VisualElement _previewSection;
        private Label _counterLabel;
        private Label _stateLabel;
        private VisualElement _editorRoot;
        private VisualElement _helpPanel;
        private bool _helpVisible;

        // ── Find (Ctrl+F) ──
        private VisualElement _findBar;
        private TextField _findField;
        private Label _findCounter;
        private int _findIndex = -1; // char index of the current match
        private int _findSelectionGeneration;
        private IVisualElementScheduledItem _findDebounce;
        private Vector2 _guardedScroll;
        private int _suppressScrollGuard; // ticks to skip after intentional jumps (find, reset)
        private ScrollView _guardedScrollView;
        private double _lastUserScrollInputTime = -1000d;
        private bool _scrollbarPointerActive;
        private const double ScrollGuardUserInputGraceSeconds = 0.45d;
        private const double ScrollbarDragStaleSeconds = 2d;
        private VisualElement _textAreaHost; // resized by the corner grip
        private float _previewHeight = 160f; // drag splitter between text and preview

        private const string AutoSavePref = "MeshTextBaker.TextEditor.AutoSave";

        // ══════════════════════════════════════════════════════════
        // Opening
        // ══════════════════════════════════════════════════════════
        [MenuItem("Window/Mesh Text Baker/Text Editor")]
        public static void ShowEmpty() => ShowWindow(null);

        public static void ShowWindow(MeshTextAsset asset)
        {
            var window = GetWindow<MeshTextAssetEditorWindow>("Text Editor");
            window.minSize = new Vector2(560, 420);
            if (asset != null) window.SetAsset(asset);
            window.Show();
        }

        [OnOpenAsset]
        private static bool OnOpenAsset(int instanceId, int line)
        {
            var asset = EditorCompatUtility.ObjectFromId(instanceId) as MeshTextAsset;
            if (asset == null) return false;
            ShowWindow(asset);
            return true;
        }

        private void OnEnable()
        {
            _autoSave = EditorPrefs.GetBool(AutoSavePref, false);
            _observedAssetState = CaptureAssetState();
            Undo.undoRedoPerformed += OnAssetUndoRedo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnAssetUndoRedo;
        }

        private void OnAssetUndoRedo()
        {
            string currentState = CaptureAssetState();

            // undoRedoPerformed is global. If this asset did not change, touching the working
            // buffer would lose an unrelated, unsaved draft and its custom undo history.
            if (currentState == _observedAssetState) return;

            // If this asset did change while a draft is open, preserve the draft. Saving it is
            // an explicit user decision; silently reloading from the asset is never safe.
            if (_dirty)
            {
                _observedAssetState = currentState;
                UpdateUnsavedState();
                RefreshStateRow();
                return;
            }

            _observedAssetState = currentState;
            _workingLocaleIndex = -1;
            _undoStack.Clear();
            _redoStack.Clear();
            RefreshAll();
        }

        private string CaptureAssetState()
            => _asset != null ? EditorJsonUtility.ToJson(_asset) : null;

        private void SetAsset(MeshTextAsset asset)
        {
            if (_dirty)
            {
                SaveToAsset();
                if (_dirty)
                {
                    _assetField?.SetValueWithoutNotify(_asset);
                    return;
                }
            }
            _asset = asset;
            _localeIndex = 0;
            _workingLocaleIndex = -1;
            _workingLocaleCode = null;
            _dirty = false;
            _undoStack.Clear();
            _redoStack.Clear();
            _observedAssetState = CaptureAssetState();
            if (_assetField != null) _assetField.SetValueWithoutNotify(asset);
            RefreshAll();
        }

        // ── Native 3-button close dialog (Save / Cancel / Discard) ──
        public override void SaveChanges()
        {
            SaveToAsset();
            if (_dirty)
                throw new System.InvalidOperationException(
                    "The draft locale no longer exists, so the draft could not be saved. " +
                    "The draft remains open; copy it or restore the locale before closing.");
            base.SaveChanges();
        }
        public override void DiscardChanges() { _dirty = false; base.DiscardChanges(); }

        private void UpdateUnsavedState()
        {
            hasUnsavedChanges = _dirty && !_autoSave && _asset != null;
            saveChangesMessage = _asset != null
                ? $"'{_asset.name}' has unsaved text changes for locale '{CurrentLocaleName()}'.\n\nSave them?"
                : "";
        }

        // ══════════════════════════════════════════════════════════
        // UI construction
        // ══════════════════════════════════════════════════════════
        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();

            root.Add(BuildHeader());

            _editorRoot = new VisualElement { style = { flexGrow = 1 } };
            root.Add(_editorRoot);

            _localeBar = new Toolbar();
            _editorRoot.Add(_localeBar);
            _editorRoot.Add(BuildMarkupToolbar());
            // Ctrl+F works anywhere in the window (not only inside the text field).
            root.RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.ctrlKey || e.commandKey) && e.keyCode == KeyCode.F)
                {
                    ShowFindBar();
                    e.StopImmediatePropagation();
                }
            }, TrickleDown.TrickleDown);

            _editorRoot.Add(BuildHelpPanel());
            _editorRoot.Add(BuildFindBar());
            _editorRoot.Add(BuildTextArea());
            _editorRoot.Add(BuildSplitter());
            BuildPreview(_editorRoot);
            _editorRoot.Add(BuildFooter());

            RefreshAll();
        }

        private Toolbar BuildHeader()
        {
            var header = new Toolbar();

            _assetField = new ObjectField { objectType = typeof(MeshTextAsset), allowSceneObjects = false };
            _assetField.style.minWidth = 220;
            _assetField.style.flexGrow = 1;
            _assetField.SetValueWithoutNotify(_asset);
            _assetField.RegisterValueChangedCallback(e => SetAsset(e.newValue as MeshTextAsset));
            header.Add(_assetField);

            _markdownToggle = new ToolbarToggle { text = "Markdown" };
            _markdownToggle.tooltip = "Enable Markdown authoring tools and preview processing for this TextAsset.";
            _markdownToggle.RegisterValueChangedCallback(e => ChangeProcessingMode(markdown: true, e.newValue));
            header.Add(_markdownToggle);

            _richTextToggle = new ToolbarToggle { text = "Rich Text" };
            _richTextToggle.tooltip = "Enable TMP/custom tag authoring tools and preview processing for this TextAsset.";
            _richTextToggle.RegisterValueChangedCallback(e => ChangeProcessingMode(markdown: false, e.newValue));
            header.Add(_richTextToggle);

            var autoSaveToggle = new ToolbarToggle { text = "Auto Save", value = _autoSave };
            autoSaveToggle.tooltip = "Commit every change to the asset immediately.";
            autoSaveToggle.RegisterValueChangedCallback(e =>
            {
                _autoSave = e.newValue;
                EditorPrefs.SetBool(AutoSavePref, _autoSave);
                if (_autoSave && _dirty) SaveToAsset();
                UpdateUnsavedState();
                RefreshStateRow();
            });
            header.Add(autoSaveToggle);

            _saveButton = new ToolbarButton(SaveToAsset) { text = "Saved" };
            _saveButton.style.width = 60;
            header.Add(_saveButton);

            header.Add(new ToolbarButton(() => { if (_asset != null) EditorGUIUtility.PingObject(_asset); })
            { text = "Ping" });

            return header;
        }

        private void RefreshProcessingUI()
        {
            if (_asset == null) return;
            _markdownToggle?.SetValueWithoutNotify(_asset.markdownEnabled);
            _richTextToggle?.SetValueWithoutNotify(_asset.richTextEnabled);
            if (_commonFormattingControls != null)
                _commonFormattingControls.style.display = (_asset.markdownEnabled || _asset.richTextEnabled)
                    ? DisplayStyle.Flex : DisplayStyle.None;
            if (_markdownControls != null)
                _markdownControls.style.display = _asset.markdownEnabled ? DisplayStyle.Flex : DisplayStyle.None;
            if (_richTextControls != null)
                _richTextControls.style.display = _asset.richTextEnabled ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void ChangeProcessingMode(bool markdown, bool enabled)
        {
            if (_asset == null) return;
            bool previous = markdown ? _asset.markdownEnabled : _asset.richTextEnabled;
            if (previous == enabled) return;

            bool contains = markdown ? ContainsMarkdownIncludingDraft() : ContainsRichTextIncludingDraft();
            bool removeMarkup = false;
            if (!enabled && contains)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    markdown ? "Disable Markdown" : "Disable Rich Text",
                    $"This asset contains {(markdown ? "Markdown markers" : "rich-text tags")} in one or more locales.\n\n" +
                    "Keep Markup leaves the source unchanged (MeshTextSurface will warn when processing is off).\n" +
                    "Remove Markup removes formatting markup from ALL locales. This can be undone.",
                    "Keep Markup", "Cancel", "Remove Markup");
                if (choice == 1)
                {
                    if (markdown) _markdownToggle?.SetValueWithoutNotify(true);
                    else _richTextToggle?.SetValueWithoutNotify(true);
                    return;
                }
                removeMarkup = choice == 2;
            }

            if (_dirty)
            {
                SaveToAsset();
                if (_dirty)
                {
                    if (markdown) _markdownToggle?.SetValueWithoutNotify(previous);
                    else _richTextToggle?.SetValueWithoutNotify(previous);
                    return;
                }
            }
            Undo.RecordObject(_asset, markdown ? "Change Markdown Authoring" : "Change Rich Text Authoring");
            if (removeMarkup)
            {
                for (int i = 0; i < _asset.translations.Count; i++)
                {
                    string text = _asset.translations[i].text ?? "";
                    _asset.translations[i].text = markdown ? RemoveMarkdownMarkup(text) : RemoveRichTextMarkup(text);
                }
                _workingLocaleIndex = -1;
                _undoStack.Clear();
                _redoStack.Clear();
            }
            if (markdown) _asset.markdownEnabled = enabled;
            else _asset.richTextEnabled = enabled;
            EditorUtility.SetDirty(_asset);
            RefreshAll();
        }

        private bool ContainsMarkdownIncludingDraft()
        {
            if (MeshTextMarkupUtility.ContainsMarkdown(_workingText)) return true;
            return _asset != null && _asset.ContainsMarkdownMarkup();
        }

        private bool ContainsRichTextIncludingDraft()
        {
            if (MeshTextMarkupUtility.ContainsRichText(_workingText)) return true;
            return _asset != null && _asset.ContainsRichTextMarkup();
        }

        private static string RemoveRichTextMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>", "\n",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text,
                @"<\s*/?\s*(b|i|u|s|color|size|align|font|alpha|mark|sub|sup|emission|surface|blend|opacity|noparse|link|voffset|space|cspace|indent|line-height|nobr|image|sprite)\b[^>]*>", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return System.Text.RegularExpressions.Regex.Replace(text, @"<#[0-9a-fA-F]{3,8}>", "");
        }

        private static string RemoveMarkdownMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                if (trimmed.Contains("|") && trimmed.Replace(":", "").Replace("|", "")
                    .Replace("-", "").Trim().Length == 0)
                    continue; // Markdown table separator row.
                line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*#{1,6}\s+", "");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*[-*]\s+", "");
                if (line.Contains("|")) line = line.Trim().Trim('|').Replace("|", "\t");
                line = line.Replace("**", "").Replace("*", "").Replace("`", "");
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }
            return sb.ToString();
        }

        private Toolbar BuildMarkupToolbar()
        {
            var bar = new Toolbar();
            _markupToolbar = bar;
            _commonFormattingControls = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _markdownControls = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _richTextControls = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            bar.Add(_commonFormattingControls);
            bar.Add(_markdownControls);
            bar.Add(_richTextControls);

            AddButton(_commonFormattingControls, "B", "Bold (Markdown ** or Rich Text <b>)", WrapBold, 26);
            AddButton(_commonFormattingControls, "I", "Italic (Markdown * or Rich Text <i>)", WrapItalic, 26);
            AddButton(_richTextControls, "U", "Underline — <u>text</u>", () => WrapSelection("<u>", "</u>"), 26);
            AddButton(_richTextControls, "S", "Strikethrough — <s>text</s>", () => WrapSelection("<s>", "</s>"), 26);
            AddButton(_markdownControls, "H1", "Heading — # text", () => PrefixLine("# "), 30);
            AddButton(_markdownControls, "H2", "Subheading — ## text", () => PrefixLine("## "), 30);
            AddButton(_markdownControls, "\u2022", "List item — - text", () => PrefixLine("- "), 26);

            _richTextControls.Add(new ToolbarSpacer());

            var colorField = new ColorField { value = _pickedColor, showAlpha = false, showEyeDropper = false };
            colorField.style.width = 46;
            colorField.tooltip = "Color used by Color / Glow (colored) and the right-click menu";
            colorField.RegisterValueChangedCallback(e => _pickedColor = e.newValue);
            _richTextControls.Add(colorField);

            AddButton(_richTextControls, "Color", "<color=#\u2026> with the picked color",
                () => WrapSelection($"<color=#{PickedHex()}>", "</color>"), 46);

            // Visual gap so the size number clearly belongs to Size, not to Color.
            _richTextControls.Add(new VisualElement { style = { width = 12 } });

            // Size: numeric percent field + apply button (<size=NNN%>).
            var sizeField = new IntegerField { value = _sizePercent, isDelayed = true };
            sizeField.style.width = 44;
            sizeField.tooltip = "Font size in percent for the Size button (e.g. 120 = <size=120%>)";
            sizeField.RegisterValueChangedCallback(e =>
            {
                _sizePercent = Mathf.Clamp(e.newValue, 10, 500);
                sizeField.SetValueWithoutNotify(_sizePercent);
            });
            _richTextControls.Add(sizeField);
            AddButton(_richTextControls, "Size", "Wrap selection in <size=NNN%> using the field on the left",
                () => WrapSelection($"<size={_sizePercent}%>", "</size>"), 40);

            _richTextControls.Add(new ToolbarSpacer());

            // Alignment: one dropdown (TMP <align> applies until the next align tag).
            var alignMenu = new ToolbarMenu { text = "Align" };
            alignMenu.tooltip = "Text alignment — <align=left/center/right>";
            alignMenu.menu.AppendAction("Left", _ => WrapAlign("left"));
            alignMenu.menu.AppendAction("Center", _ => WrapAlign("center"));
            alignMenu.menu.AppendAction("Right", _ => WrapAlign("right"));
            _richTextControls.Add(alignMenu);

            _richTextControls.Add(new ToolbarSpacer());

            // PBR: one dropdown button instead of three separate buttons.
            var pbrMenu = new ToolbarMenu { text = "PBR" };
            pbrMenu.tooltip = "Per-word PBR tags (require 'Use PBR' on the zone). " +
                              "Glow brightness: i= multiplier; Shine: per-word metallic/smoothness.";
            pbrMenu.menu.AppendAction("Emission/Default  <emission>", _ => WrapSelection("<emission>", "</emission>"));
            pbrMenu.menu.AppendAction("Emission/Picked color", _ => WrapSelection($"<emission=#{PickedHex()}>", "</emission>"));
            pbrMenu.menu.AppendAction("Emission/Picked color, bright ×2", _ => WrapSelection($"<emission=#{PickedHex()} i=2>", "</emission>"));
            pbrMenu.menu.AppendAction("Emission/Picked color, bright ×4", _ => WrapSelection($"<emission=#{PickedHex()} i=4>", "</emission>"));
            pbrMenu.menu.AppendAction("Emission/Dim ×0.5", _ => WrapSelection("<emission i=0.5>", "</emission>"));
            pbrMenu.menu.AppendAction("Surface/Neutral (no-op)  <surface>", _ => WrapSelection("<surface>", "</surface>"));
            pbrMenu.menu.AppendAction("Surface/Gold (m=1 s=0.9)", _ => WrapSelection("<surface m=1 s=0.9>", "</surface>"));
            pbrMenu.menu.AppendAction("Surface/Glossy ink (m=0 s=0.95)", _ => WrapSelection("<surface m=0 s=0.95>", "</surface>"));
            pbrMenu.menu.AppendAction("Surface/Brushed metal (m=1 s=0.4)", _ => WrapSelection("<surface m=1 s=0.4>", "</surface>"));
            pbrMenu.menu.AppendAction("Height/Raised (h=1)", _ => WrapSelection("<surface h=1>", "</surface>"));
            pbrMenu.menu.AppendAction("Height/Engraved (h=-1)", _ => WrapSelection("<surface h=-1>", "</surface>"));
            _richTextControls.Add(pbrMenu);

            var blendMenu = new ToolbarMenu { text = "Blend" };
            foreach (MeshTextBlendMode mode in System.Enum.GetValues(typeof(MeshTextBlendMode)))
            {
                MeshTextBlendMode captured = mode;
                string tag = MeshTextBlendModeUtility.ToTagName(mode);
                blendMenu.menu.AppendAction(mode.ToString(), _ => WrapSelection($"<blend={tag}>", "</blend>"));
            }
            _richTextControls.Add(blendMenu);

            var opacityMenu = new ToolbarMenu { text = "Opacity" };
            opacityMenu.menu.AppendAction("25%", _ => WrapSelection("<opacity=0.25>", "</opacity>"));
            opacityMenu.menu.AppendAction("50%", _ => WrapSelection("<opacity=0.5>", "</opacity>"));
            opacityMenu.menu.AppendAction("75%", _ => WrapSelection("<opacity=0.75>", "</opacity>"));
            _richTextControls.Add(opacityMenu);

            AddButton(_richTextControls, "Font", "Inline font — <font=name> or <font=file:fonts/My.ttf>",
                () => WrapSelection("<font=name>", "</font>"), 38);
            AddButton(_richTextControls, "Img", "Inline image — <image=name h=100> (name from the surface's Image Library)",
                () => InsertText("<image=name h=100>"), 34);
            AddButton(_markdownControls, "Table", "Insert a simple Markdown pipe table",
                () => InsertText("\n| Column A | Column B |\n|---|---|\n| Value A | Value B |\n"), 42);
            AddButton(bar, "Page", "Force page break — <newpage>", () => InsertText("\n<newpage>\n"), 42);
            AddButton(bar, "Mark", "Named bookmark — <bookmark=id> (jump via BookController.GoToBookmark)",
                () => InsertText("<bookmark=id>"), 42);

            bar.Add(new ToolbarSpacer { style = { flexGrow = 1 } });

            var previewToggle = new ToolbarToggle { text = "Preview", value = true };
            previewToggle.RegisterValueChangedCallback(e =>
            {
                var vis = e.newValue ? DisplayStyle.Flex : DisplayStyle.None;
                if (_previewSection != null) _previewSection.style.display = vis;
                else if (_previewBox != null) _previewBox.style.display = vis;
                var split = _editorRoot != null ? _editorRoot.Q("mtb-splitter") : null;
                if (split != null) split.style.display = vis;
            });
            bar.Add(previewToggle);

            var helpBtn = new ToolbarToggle { text = "?" };
            helpBtn.tooltip = "Tag reference";
            helpBtn.style.width = 28;
            helpBtn.RegisterValueChangedCallback(e =>
            {
                _helpVisible = e.newValue;
                if (_helpPanel != null)
                    _helpPanel.style.display = _helpVisible ? DisplayStyle.Flex : DisplayStyle.None;
            });
            bar.Add(helpBtn);

            return bar;
        }

        private void WrapBold()
        {
            if (_asset != null && _asset.markdownEnabled) WrapSelection("**", "**");
            else WrapSelection("<b>", "</b>");
        }

        private void WrapItalic()
        {
            if (_asset != null && _asset.markdownEnabled) WrapSelection("*", "*");
            else WrapSelection("<i>", "</i>");
        }

        /// <summary>
        /// Alignment: wraps the selection in <align=...> … </align>. Without a selection,
        /// inserts <align=...> at the start of the current line (TMP applies alignment from
        /// the tag to the next align tag / end of text).
        /// </summary>
        private void WrapAlign(string mode)
        {
            if (GetSelection(out _, out _))
                WrapSelection($"<align={mode}>", "</align>");
            else
                PrefixLine($"<align={mode}>");
        }

        private static void AddButton(VisualElement bar, string text, string tooltip, System.Action action, float width)
        {
            var b = new ToolbarButton(action) { text = text, tooltip = tooltip };
            if (width > 0) b.style.width = width;
            bar.Add(b);
        }

        private VisualElement BuildTextArea()
        {
            // No outer ScrollView: the TextField scrolls its own content (see
            // verticalScrollerVisibility below); the host just sizes the field.
            var scroll = new VisualElement { style = { flexGrow = 2 } };

            _textField = new TextField { multiline = true };
            _textField.style.flexGrow = 1;
            _textField.style.fontSize = 13;
            _textField.style.whiteSpace = WhiteSpace.Normal;

            // Selection flags are applied AFTER AttachToPanel (and once more a tick later):
            // in Unity 6 the inner text element may be (re)initialized during attach and
            // flags set at construction time get wiped (consensus of external reviews).
            _textField.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                _textField.schedule.Execute(() =>
                {
                    ResolveTextInput();
                    ApplySelectionFlags();
                    RegisterTextInputCallbacks();
                });
                // Once more after the first layout, in case the inner element was recreated.
                _textField.schedule.Execute(ApplySelectionFlags).ExecuteLater(1);
            });

            _textField.RegisterCallback<FocusInEvent>(_ => _fieldFocused = true);
            _textField.RegisterCallback<FocusOutEvent>(_ => _fieldFocused = false);
            _textField.RegisterValueChangedCallback(OnTyped);

            scroll.Add(_textField);
            scroll.style.flexGrow = 1;
            _textField.style.flexGrow = 1;
            _textField.style.height = Length.Percent(100);

            // The grip sits INSIDE the TextField's own corner and resizes the FIELD itself
            // (CSS-textarea behavior): position:relative on the field, absolute grip child.
            _textField.style.position = Position.Relative;

            // Scrolling happens INSIDE the field itself (its own vertical scroller):
            // the wheel works natively over the text content, the caret stays in view
            // while typing, and the grip only changes the field's outer height.
            _textField.verticalScrollerVisibility = ScrollerVisibility.Auto;

            // ── Permanent scroll guard ──
            // The text engine teleports the scroller to 0 on several internal paths
            // (buffer swap re-layout, focus restore on click, view-data restore). A one-shot
            // restore can't catch them all. Instead we track the last offset the USER chose
            // (wheel / scrollbar drag / caret navigation set it via legitimate deltas) and
            // veto sudden engine-side jumps to the very top.
            _textField.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                _textField.schedule.Execute(() =>
                {
                    var sc = _textField.Q<ScrollView>();
                    if (sc == null) return;
                    RegisterScrollGuardInputCallbacks(sc);
                    _guardedScroll = sc.scrollOffset;
                    _textField.schedule.Execute(() =>
                    {
                        Vector2 cur = sc.scrollOffset;
                        if (_suppressScrollGuard > 0)
                        {
                            _suppressScrollGuard--;
                            _guardedScroll = cur;
                            return;
                        }
                        // Legitimate movement: small delta (wheel step, caret line jump,
                        // drag) or ANY move away from 0 → adopt it. A direct jump to y=0
                        // is vetoed only when it was NOT caused by recent user scroll input;
                        // otherwise dragging the scrollbar/thumb or using a high-resolution
                        // wheel/trackpad to reach the real top gets incorrectly bounced down.
                        float dy = Mathf.Abs(cur.y - _guardedScroll.y);
                        bool jumpToTop = cur.y <= 0.5f && _guardedScroll.y > 40f && dy > 40f;
                        if (jumpToTop && !IsUserScrollInProgress())
                            sc.scrollOffset = _guardedScroll; // engine reset — veto
                        else
                            _guardedScroll = cur;             // user/engine legitimate move
                    }).Every(50);
                });
            });

            _textAreaHost = new VisualElement
            {
                style = { flexGrow = 1, flexShrink = 1, minHeight = 80 }
            };
            _noLocaleNotice = new HelpBox(
                "Create and select a locale before writing text.",
                HelpBoxMessageType.Warning);
            _noLocaleNotice.style.display = DisplayStyle.None;
            _textAreaHost.Add(_noLocaleNotice);
            _textAreaHost.Add(scroll);
            return _textAreaHost;
        }

        private void RegisterScrollGuardInputCallbacks(ScrollView sc)
        {
            if (sc == null || ReferenceEquals(_guardedScrollView, sc)) return;
            _guardedScrollView = sc;

            // Wheel/trackpad scrolling over the text content is a legitimate way to reach
            // y=0, so the guard must not treat it as an engine-side teleport.
            sc.RegisterCallback<WheelEvent>(_ => NoteUserScrollInput(), TrickleDown.TrickleDown);

            // Scrollbar drags are reported on the vertical scroller (and its children).
            // Track the drag separately because the final scrollOffset update can arrive
            // after the PointerMove/PointerUp event that produced it.
            var vertical = sc.verticalScroller;
            if (vertical == null) return;
            vertical.RegisterCallback<PointerDownEvent>(_ =>
            {
                _scrollbarPointerActive = true;
                NoteUserScrollInput();
            }, TrickleDown.TrickleDown);
            vertical.RegisterCallback<PointerMoveEvent>(_ =>
            {
                if (_scrollbarPointerActive) NoteUserScrollInput();
            }, TrickleDown.TrickleDown);
            vertical.RegisterCallback<PointerUpEvent>(_ =>
            {
                NoteUserScrollInput();
                _scrollbarPointerActive = false;
            }, TrickleDown.TrickleDown);
            vertical.RegisterCallback<PointerCaptureOutEvent>(_ =>
            {
                NoteUserScrollInput();
                _scrollbarPointerActive = false;
            }, TrickleDown.TrickleDown);
        }

        private void NoteUserScrollInput()
        {
            _lastUserScrollInputTime = EditorApplication.timeSinceStartup;
        }

        private bool IsUserScrollInProgress()
        {
            double age = EditorApplication.timeSinceStartup - _lastUserScrollInputTime;
            if (_scrollbarPointerActive && age > ScrollbarDragStaleSeconds)
                _scrollbarPointerActive = false;
            return _scrollbarPointerActive || age <= ScrollGuardUserInputGraceSeconds;
        }

        /// <summary>Finds the inner editable element (created during attach, not construction).</summary>
        private void ResolveTextInput()
        {
            _textInput = _textField?.Q<VisualElement>("unity-text-input");
            if (_textInput != null)
            {
                _textInput.style.minHeight = 220;
                _textInput.style.paddingLeft = 8;
                _textInput.style.paddingRight = 8;
                _textInput.style.paddingTop = 6;
                _textInput.style.paddingBottom = 6;
            }
        }

        private void ApplySelectionFlags()
        {
            if (_textField == null) return;
#if UNITY_2023_1_OR_NEWER
            var sel = _textField.textSelection;
            sel.selectAllOnFocus = false;
            sel.selectAllOnMouseUp = false;
            sel.doubleClickSelectsWord = true;
            sel.tripleClickSelectsLine = true;
#else
            _textField.selectAllOnFocus = false;
            _textField.selectAllOnMouseUp = false;
            _textField.doubleClickSelectsWord = true;
            _textField.tripleClickSelectsLine = true;
#endif
        }

        /// <summary>
        /// All input callbacks go on the INNER input element: in Unity 6 the text engine
        /// handles pointer/keyboard there, before anything reaches the TextField wrapper —
        /// TrickleDown on the wrapper is simply too late.
        /// </summary>
        private void RegisterTextInputCallbacks()
        {
            if (_inputCallbacksRegistered) return;
            var target = _textInput ?? (VisualElement)_textField;
            if (target == null) return;
            _inputCallbacksRegistered = true;

            target.RegisterCallback<FocusInEvent>(OnInputFocusIn, TrickleDown.TrickleDown);
            target.RegisterCallback<FocusOutEvent>(_ => _fieldFocused = false, TrickleDown.TrickleDown);
            target.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            target.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            // Keep the caret cache fresh: continuous polling while the field is focused
            // (event-based capture proved unreliable — events fire BEFORE the engine moves
            // the caret). 50 ms is imperceptible and the poll is two int reads.
            _caretPoller = _textField.schedule.Execute(CaptureCaretState).Every(50);
            _caretPoller.Pause();
            target.RegisterCallback<FocusInEvent>(_ => _caretPoller?.Resume());
            target.RegisterCallback<FocusOutEvent>(_ => _caretPoller?.Pause());

            // Canonical UITK context menu: populate on the INNER element and let the engine
            // display it (correct coordinates at any UI scale). No GenericMenu, no
            // StopPropagation — the manipulator shows the (replaced) menu itself.
            var menuManipulator = new ContextualMenuManipulator(evt =>
            {
                evt.menu.ClearItems();
                PopulateDropdownMenu(evt.menu);
            });
            target.AddManipulator(menuManipulator);
        }

        private void OnInputFocusIn(FocusInEvent evt)
        {
            _fieldFocused = true;

            // Belt-and-braces against select-all-on-focus (covers versions where the flag
            // is buggy): if the engine selected everything, collapse to the caret next tick.
            _textField.schedule.Execute(() =>
            {
                ApplySelectionFlags();
                if (SelectionCoversAllText())
                {
                    int c = CursorIndex();
                    SelectRange(c, c);
                }
            });
        }

        /// <summary>
        /// Horizontal drag bar between the text field and the Preview panel.
        /// Dragging resizes the Preview height; text area takes the remaining space.
        /// Hidden when Preview is off (text field fills the window).
        /// </summary>
        private VisualElement BuildSplitter()
        {
            var bar = new VisualElement
            {
                name = "mtb-splitter",
                style =
                {
                    height = 6,
                    flexGrow = 0,
                    flexShrink = 0,
                    marginLeft = 4,
                    marginRight = 4,
                    backgroundColor = new Color(1f, 1f, 1f, 0.08f),
                }
            };
            bar.tooltip = "Drag to resize Preview";

            // Center grip indicator
            var handle = new VisualElement
            {
                style =
                {
                    height = 2,
                    width = 40,
                    marginTop = 2,
                    alignSelf = Align.Center,
                    backgroundColor = new Color(1f, 1f, 1f, 0.25f),
                },
                pickingMode = PickingMode.Ignore
            };
            bar.Add(handle);

            bool dragging = false;
            float startY = 0f, startH = 0f;

            bar.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                dragging = true;
                startY = e.position.y;
                startH = _previewHeight;
                bar.CapturePointer(e.pointerId);
                e.StopImmediatePropagation();
            });
            bar.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                // Dragging the bar DOWN grows the text area → shrinks preview (and vice versa).
                float dy = e.position.y - startY;
                _previewHeight = Mathf.Clamp(startH - dy, 60f, 600f);
                ApplyPreviewHeight();
                e.StopImmediatePropagation();
            });
            bar.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false;
                bar.ReleasePointer(e.pointerId);
                e.StopImmediatePropagation();
            });
            bar.RegisterCallback<ClickEvent>(e =>
            {
                if (e.clickCount == 2)
                {
                    _previewHeight = 160f;
                    ApplyPreviewHeight();
                    e.StopImmediatePropagation();
                }
            });

            return bar;
        }

        private void ApplyPreviewHeight()
        {
            if (_previewBox == null) return;
            _previewBox.style.height = _previewHeight;
        }

        private void BuildPreview(VisualElement parent)
        {
            _previewSection = new VisualElement { style = { flexGrow = 0, flexShrink = 0 } };
            _previewSection.Add(new Label("Preview")
            { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 11, marginLeft = 6, marginTop = 2 } });

            var scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                style =
                {
                    flexGrow = 0,
                    flexShrink = 0,
                    height = _previewHeight,
                    marginLeft = 4, marginRight = 4, marginBottom = 2,
                    backgroundColor = new Color(0, 0, 0, 0.18f),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4
                }
            };
            _previewLabel = new Label
            {
                enableRichText = true,
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    fontSize = 13,
                    paddingLeft = 8, paddingRight = 8, paddingTop = 6, paddingBottom = 6
                }
            };
            scroll.Add(_previewLabel);
            _previewBox = scroll;
            _previewSection.Add(scroll);
            parent.Add(_previewSection);
        }


        private VisualElement BuildHelpPanel()
        {
            _helpPanel = new ScrollView(ScrollViewMode.Vertical)
            {
                style =
                {
                    display = DisplayStyle.None,
                    flexGrow = 0,
                    flexShrink = 0,
                    height = 240,
                    marginLeft = 4, marginRight = 4, marginTop = 2, marginBottom = 2,
                    backgroundColor = new Color(0.12f, 0.12f, 0.14f, 0.95f),
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    paddingLeft = 10, paddingRight = 10, paddingTop = 8, paddingBottom = 8
                }
            };

            void Line(string text)
            {
                var lab = new Label(text)
                {
                    style =
                    {
                        whiteSpace = WhiteSpace.Normal,
                        fontSize = 11,
                        color = new Color(0.88f, 0.88f, 0.88f),
                        marginBottom = 2,
                        unityFontStyleAndWeight = FontStyle.Normal
                    }
                };
                // CRITICAL: UITK Label parses rich text by default — that ate our tag examples.
#if UNITY_2021_2_OR_NEWER
                lab.enableRichText = false;
#endif
                _helpPanel.Add(lab);
            }

            Line("Markdown / Rich Text toggles control authoring buttons and preview processing.");
            Line("B/I buttons use Markdown markers when Markdown is on, otherwise TMP <b>/<i> tags.");
            Line("");
            Line("Markdown shortcuts");
            Line("**bold** - bold (may span line breaks)");
            Line("*italic* - italic (may span line breaks)");
            Line("`code` - literal text; emitted as <noparse>...</noparse>");
            Line("# text / ## text - heading / subheading");
            Line("- item / * item - bullet item");
            Line("| A | B | plus separator row |---|---| - simple table");
            Line("");
            Line("TMP / Rich Text basics");
            Line("<b>...</b>, <i>...</i>, <u>...</u>, <s>...</s> - bold / italic / underline / strike");
            Line("<color=#RRGGBB>...</color> or <#RRGGBB>...</color> - color");
            Line("<size=120%>...</size> - font size");
            Line("<align=left|center|right>...</align> - alignment");
            Line("<br> - line break");
            Line("<noparse>...</noparse> - show tags literally");
            Line("");
            Line("PBR / material scopes (require Rich Text and zone PBR support)");
            Line("<emission>...</emission> - glow using the zone emission color");
            Line("<emission=#RRGGBB>...</emission> - glow with custom color");
            Line("<emission=#RRGGBB i=2>...</emission> - glow color + intensity multiplier");
            Line("<surface>...</surface> - neutral/no-op PBR scope");
            Line("<surface m=1 s=0.9>...</surface> - metallic (m 0..1) / smoothness (s 0..1)");
            Line("<surface h=1>...</surface> / <surface h=-1>...</surface> - raised / engraved height (-1..1)");
            Line("");
            Line("Blend and opacity");
            Line("<blend=normal|multiply|screen|overlay|add|subtract>...</blend> - blend scope");
            Line("Aliases: blend=mul, blend=sub, blend=linear-dodge");
            Line("<opacity=0.5>...</opacity> - opacity scope (0..1)");
            Line("");
            Line("Inline images");
            Line("<image=name> - image from the surface Image Library (default h=100, auto width)");
            Line("<image=res:Path/To/Tex> - Texture2D from Resources, path without extension");
            Line("<image=file:images/pic.png> - loose PNG/JPG from persistentDataPath or StreamingAssets");
            Line("Image source can be quoted when needed: <image=\"name with spaces\">");
            Line("Image params (space-separated after source):");
            Line("  h=150 - height as % of current font size (1..2000, default 100)");
            Line("  w=200 - width as % of current font size; omit for texture aspect ratio");
            Line("  displace=false - overlay image without moving following text (default true)");
            Line("  under=true - draw image under glyphs / behind text (default false)");
            Line("  pivot=bl|br|tl|tr|c|center - anchor point, default bl on baseline");
            Line("  blend=multiply|screen|overlay|add|subtract|normal - direct image blend mode");
            Line("  opacity=0.5 - direct image opacity (0..1)");
            Line("Example: <image=seal h=140 w=160 pivot=c under=true opacity=0.75>");
            Line("Images inherit surrounding <emission>/<surface>, <blend>, and <opacity> scopes unless overridden by direct image params.");
            Line("");
            Line("Inline fonts");
            Line("<font=name>...</font> - Font Library alias or TMP Resources fallback");
            Line("<font=res:Path/Font>...</font> - TMP_FontAsset or Font from Resources");
            Line("<font=file:fonts/My.ttf>...</font> - loose runtime TTF/OTF");
            Line("");
            Line("Book/navigation tags");
            Line("<newpage> - force book page break");
            Line("<bookmark=id>, <bookmark id=\"id\">, <bookmark name=\"id\"> - zero-width jump target");
            Line("PageNumber zone - UV Editor / PageSlot: Add Page Number Zone");

            return _helpPanel;
        }

        private Toolbar BuildFooter()
        {
            var footer = new Toolbar();
            _counterLabel = new Label("") { style = { fontSize = 10, unityTextAlign = TextAnchor.MiddleLeft } };
            footer.Add(_counterLabel);
            footer.Add(new ToolbarSpacer { style = { flexGrow = 1 } });
            _stateLabel = new Label("") { style = { fontSize = 10, unityTextAlign = TextAnchor.MiddleRight } };
            footer.Add(_stateLabel);
            return footer;
        }

        // ══════════════════════════════════════════════════════════
        // Input interception
        // ══════════════════════════════════════════════════════════
        private void OnPointerDown(PointerDownEvent evt)
        {
            // Right-click is fully handled by the ContextualMenuManipulator on the inner
            // input — nothing to do here for button 1, and we must NOT interfere with it.

            // Select-all fallback: let the engine place the caret from the click first,
            // THEN collapse a full-text selection if one appeared (buggy flag path).
            if (evt.button == 0)
            {
                bool hadFocus = _fieldFocused;
                _textField.schedule.Execute(() =>
                {
                    if ((!hadFocus || SelectionCoversAllText()) && SelectionCoversAllText())
                    {
                        int c = CursorIndex();
                        SelectRange(c, c);
                    }
                }).ExecuteLater(1);
            }
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            // Shift+Enter: Unity 6's multiline field treats it as "commit/blur" (the caret
            // vanishes) instead of inserting a newline. Intercept it on the INNER input
            // (we run before the engine here) and insert the newline ourselves. Both the
            // keycode event and the paired character event ('\n' with KeyCode.None) must be
            // swallowed, otherwise the engine still runs its blur path.
            bool isReturnKey = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
            bool isNewlineChar = evt.keyCode == KeyCode.None && (evt.character == '\n' || evt.character == '\r');

            if (_swallowNextNewlineChar && isNewlineChar)
            {
                _swallowNextNewlineChar = false;
                evt.StopImmediatePropagation();
                return;
            }
            if (!isReturnKey && !isNewlineChar) _swallowNextNewlineChar = false;

            if (isReturnKey && evt.shiftKey)
            {
                InsertText("\n");
                _swallowNextNewlineChar = true; // the paired character event follows
                evt.StopImmediatePropagation();
                return;
            }
            // Plain Enter works natively — don't touch it.

            if (IsScrollNavigationKey(evt)) NoteUserScrollInput();

            bool cmd = evt.ctrlKey || evt.commandKey;
            if (!cmd) return;

            if (evt.keyCode == KeyCode.S)
            {
                SaveToAsset();
                evt.StopImmediatePropagation();
            }
            else if (evt.keyCode == KeyCode.F)
            {
                ShowFindBar();
                evt.StopImmediatePropagation();
            }
            else if ((evt.keyCode == KeyCode.Z && evt.shiftKey) || evt.keyCode == KeyCode.Y)
            {
                RedoText();
                evt.StopImmediatePropagation();
            }
            else if (evt.keyCode == KeyCode.Z)
            {
                UndoText();
                evt.StopImmediatePropagation();
            }
        }

        private static bool IsScrollNavigationKey(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.PageUp:
                case KeyCode.PageDown:
                case KeyCode.Home:
                case KeyCode.End:
                    return true;
                default:
                    return false;
            }
        }

        private void OnTyped(ChangeEvent<string> e)
        {
            if (_asset == null || _workingLocaleIndex < 0 ||
                _workingLocaleIndex >= _asset.translations.Count)
            {
                _textField?.SetValueWithoutNotify("");
                return;
            }

            string rawNewValue = e.newValue ?? "";
            string normalizedNewValue = NormalizeEditorText(rawNewValue);
            bool normalized = !string.Equals(rawNewValue, normalizedNewValue,
                System.StringComparison.Ordinal);

            // If text was pasted from a browser/website, the clipboard can contain CRLF or
            // Unicode line/paragraph separators. UITK's editable TextElement measures
            // selection/caret positions in its normalized view, while our tag insertion uses
            // _workingText indices. Keep the source buffer in the same LF-only coordinate
            // space immediately on input so toolbar tags wrap the selected text correctly.
            int rawCursor = 0, rawSelect = 0;
            if (normalized && _textField != null)
            {
                rawCursor = LiveCursorIndex();
                rawSelect = LiveSelectIndex();
            }

            PushUndoSnapshot(NormalizeEditorText(e.previousValue));
            _workingText = normalizedNewValue;

            if (normalized && _textField != null)
            {
                int cursor = NormalizeEditorTextSelectionIndex(rawNewValue, normalizedNewValue, rawCursor);
                int select = NormalizeEditorTextSelectionIndex(rawNewValue, normalizedNewValue, rawSelect);
                _textField.SetValueWithoutNotify(_workingText);
                _textField.schedule.Execute(() => SelectRange(cursor, select));
            }

            MarkEdited();
        }

        private static string NormalizeEditorText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";

            bool needsNormalization = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == (char)0x000D || c == (char)0x0085 || c == (char)0x2028 || c == (char)0x2029)
                {
                    needsNormalization = true;
                    break;
                }
            }
            if (!needsNormalization) return text;

            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == (char)0x000D)
                {
                    if (i + 1 < text.Length && text[i + 1] == (char)0x000A) i++;
                    sb.Append((char)0x000A);
                }
                else if (c == (char)0x0085 || c == (char)0x2028 || c == (char)0x2029)
                {
                    sb.Append((char)0x000A);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static int NormalizeEditorTextSelectionIndex(string rawText, string normalizedText, int index)
        {
            // Depending on the Unity version/path, the live selection index can already be in
            // the TextElement's normalized coordinate space. If it fits the normalized string,
            // keep it; only map down indices that are clearly from the raw CRLF string.
            int normalizedLength = (normalizedText ?? "").Length;
            if (index <= normalizedLength) return Mathf.Clamp(index, 0, normalizedLength);
            if (string.IsNullOrEmpty(rawText)) return 0;
            index = Mathf.Clamp(index, 0, rawText.Length);

            int normalizedIndex = 0;
            for (int i = 0; i < index; i++)
            {
                char c = rawText[i];
                if (c == (char)0x000D)
                {
                    if (i + 1 < rawText.Length && rawText[i + 1] == (char)0x000A && i + 1 < index) i++;
                    normalizedIndex++;
                }
                else
                {
                    normalizedIndex++;
                }
            }
            return Mathf.Clamp(normalizedIndex, 0, normalizedLength);
        }

        // ══════════════════════════════════════════════════════════
        // Find (Ctrl+F)
        // ══════════════════════════════════════════════════════════
        private VisualElement BuildFindBar()
        {
            _findBar = new Toolbar { style = { display = DisplayStyle.None } };

            _findBar.Add(new Label("Find:")
            { style = { unityTextAlign = TextAnchor.MiddleLeft, marginLeft = 4, marginRight = 2 } });

            _findField = new TextField();
            _findField.style.flexGrow = 1;
            _findField.style.maxWidth = 260;
#if UNITY_2023_1_OR_NEWER
            _findField.textSelection.selectAllOnFocus = true; // convenient for retyping a query
#endif
            // Debounced live search: wait until the user stops typing (350 ms), and do NOT
            // steal focus from the query field — only highlight and count.
            _findField.RegisterValueChangedCallback(_ =>
            {
                _findIndex = -1;
                _findSelectionGeneration++;
                if (_findDebounce == null)
                {
                    _findDebounce = _findField.schedule.Execute(() => FindStep(true, true));
                    _findDebounce.Pause();
                }
                _findDebounce.ExecuteLater(350);
            });
            _findField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    if (e.shiftKey) FindPrevious(); else FindNext();
                    e.StopImmediatePropagation();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    HideFindBar();
                    e.StopImmediatePropagation();
                }
            }, TrickleDown.TrickleDown);
            _findBar.Add(_findField);

            var prevButton = new ToolbarButton(FindPrevious) { text = "\u25b2", tooltip = "Previous (Shift+Enter)" };
            prevButton.style.width = 24;
            _findBar.Add(prevButton);

            var nextButton = new ToolbarButton(FindNext) { text = "\u25bc", tooltip = "Next (Enter)" };
            nextButton.style.width = 24;
            _findBar.Add(nextButton);

            _findCounter = new Label("")
            { style = { fontSize = 10, unityTextAlign = TextAnchor.MiddleLeft, marginLeft = 4, minWidth = 70 } };
            _findBar.Add(_findCounter);

            _findBar.Add(new ToolbarSpacer { style = { flexGrow = 1 } });

            var closeButton = new ToolbarButton(HideFindBar) { text = "\u00d7", tooltip = "Close (Esc)" };
            closeButton.style.width = 24;
            _findBar.Add(closeButton);

            return _findBar;
        }

        private void ShowFindBar()
        {
            if (_findBar == null) return;
            _findBar.style.display = DisplayStyle.Flex;
            _findIndex = -1;
            _findSelectionGeneration++;

            // Pre-fill with the current selection (standard find-bar behavior).
            if (GetSelection(out int selStart, out int selEnd))
                _findField.SetValueWithoutNotify((_workingText ?? "").Substring(selStart, selEnd - selStart));

            _findField.schedule.Execute(() => _findField.Focus());
        }

        private void HideFindBar()
        {
            if (_findBar == null) return;
            _findBar.style.display = DisplayStyle.None;
            _findCounter.text = "";
            FocusTextInput();
        }

        private void FindNext() { FindStep(true, false); }
        private void FindPrevious() { FindStep(false, false); }

        private void FindStep(bool forward, bool keepFocusInQuery)
        {
            string query = _findField != null ? _findField.value : null;
            string t = _workingText ?? "";
            if (string.IsNullOrEmpty(query) || t.Length == 0)
            {
                if (_findCounter != null) _findCounter.text = "";
                return;
            }

            // Wrap-around search, case-insensitive.
            int hit;
            if (forward)
            {
                int from = _findIndex < 0 ? 0 : Mathf.Clamp(_findIndex + query.Length, 0, t.Length);
                hit = from < t.Length
                    ? t.IndexOf(query, from, System.StringComparison.OrdinalIgnoreCase) : -1;
                if (hit < 0) hit = t.IndexOf(query, 0, System.StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                int from = (_findIndex < 0 ? t.Length : _findIndex) - 1;
                hit = from >= 0
                    ? t.LastIndexOf(query, Mathf.Min(from, t.Length - 1), System.StringComparison.OrdinalIgnoreCase) : -1;
                if (hit < 0) hit = t.LastIndexOf(query, t.Length - 1, System.StringComparison.OrdinalIgnoreCase);
            }

            if (hit < 0)
            {
                _findCounter.text = "0 matches";
                return;
            }

            _findIndex = hit;
            UpdateFindCounter(t, query, hit);

            // Ignore stale delayed selections from an older query/find step.
            int generation = ++_findSelectionGeneration;
            string expectedQuery = query;
            System.Action selectMatch = () =>
            {
                if (generation != _findSelectionGeneration || _findField == null ||
                    !string.Equals(_findField.value, expectedQuery, System.StringComparison.Ordinal)) return;
                SelectRange(hit, hit + expectedQuery.Length);
                _suppressScrollGuard = 6;
                ScrollCaretIntoView(hit);
            };

            if (keepFocusInQuery)
            {
                _textField.schedule.Execute(selectMatch);
            }
            else
            {
                // Focus and selection must happen on separate ticks; focusing the inner input can
                // reset its live selection during the same event.
                _textField.schedule.Execute(FocusTextInput);
                _textField.schedule.Execute(selectMatch).ExecuteLater(1);
            }
        }

        private void UpdateFindCounter(string t, string query, int currentHit)
        {
            int total = 0, current = 0, idx = 0;
            while (idx <= t.Length - query.Length &&
                   (idx = t.IndexOf(query, idx, System.StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                total++;
                if (idx == currentHit) current = total;
                idx += Mathf.Max(1, query.Length);
            }
            _findCounter.text = $"{current}/{total}";
        }

        /// <summary>Approximate scroll-to-line: positions the field's inner scroller so the
        /// line containing <paramref name="charIndex"/> is roughly centered.</summary>
        private void ScrollCaretIntoView(int charIndex)
        {
            var innerScroll = _textField != null ? _textField.Q<ScrollView>() : null;
            if (innerScroll == null) return;

            string t = _workingText ?? "";
            int line = 0, totalLines = 1;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] != '\n') continue;
                totalLines++;
                if (i < charIndex) line++;
            }
            float contentH = innerScroll.contentContainer.resolvedStyle.height;
            float viewH = innerScroll.resolvedStyle.height;
            if (contentH <= viewH || totalLines <= 1) return;

            float lineY = contentH * line / totalLines;
            innerScroll.scrollOffset = new Vector2(0f, Mathf.Max(0f, lineY - viewH * 0.5f));
        }

        // ══════════════════════════════════════════════════════════
        // Context menu — via ContextualMenuManipulator on the inner input (canonical UITK
        // path: the engine shows the menu itself, correct at any UI scale). GenericMenu
        // and manual right-click interception were removed.
        // ══════════════════════════════════════════════════════════
        private void PopulateDropdownMenu(DropdownMenu menu)
        {
            bool hasSel = GetSelection(out _, out _);
            var on = DropdownMenuAction.Status.Normal;
            var off = DropdownMenuAction.Status.Disabled;

            menu.AppendAction("Cut", _ => CutSelection(), hasSel ? on : off);
            menu.AppendAction("Copy", _ => CopySelection(), hasSel ? on : off);
            menu.AppendAction("Paste", _ => PasteClipboard(),
                string.IsNullOrEmpty(EditorGUIUtility.systemCopyBuffer) ? off : on);
            menu.AppendAction("Select All", _ => SelectAllText(),
                (_workingText ?? "").Length > 0 ? on : off);
            menu.AppendSeparator();
            if (_asset != null && (_asset.markdownEnabled || _asset.richTextEnabled))
            {
                string boldLabel = _asset.markdownEnabled ? "Bold  **…**" : "Bold  <b>";
                string italicLabel = _asset.markdownEnabled ? "Italic  *…*" : "Italic  <i>";
                menu.AppendAction(boldLabel, _ => WrapBold(), hasSel ? on : off);
                menu.AppendAction(italicLabel, _ => WrapItalic(), hasSel ? on : off);
            }
            if (_asset != null && _asset.markdownEnabled)
            {
                menu.AppendAction("Heading  #", _ => PrefixLine("# "), on);
                menu.AppendAction("List item  -", _ => PrefixLine("- "), on);
                menu.AppendAction("Markdown table", _ => InsertText(
                    "\n| Column A | Column B |\n|---|---|\n| Value A | Value B |\n"), on);
                menu.AppendSeparator();
            }
            if (_asset != null && _asset.richTextEnabled)
            {
                menu.AppendAction("Underline  <u>", _ => WrapSelection("<u>", "</u>"), hasSel ? on : off);
                menu.AppendAction("Strikethrough  <s>", _ => WrapSelection("<s>", "</s>"), hasSel ? on : off);
                menu.AppendAction("Color  <color>", _ => WrapSelection($"<color=#{PickedHex()}>", "</color>"), hasSel ? on : off);
                menu.AppendAction($"Size  <size={_sizePercent}%>", _ => WrapSelection($"<size={_sizePercent}%>", "</size>"), hasSel ? on : off);
                menu.AppendAction("Align left", _ => WrapAlign("left"), on);
                menu.AppendAction("Align center", _ => WrapAlign("center"), on);
                menu.AppendAction("Align right", _ => WrapAlign("right"), on);
                menu.AppendAction("Emission  <emission>", _ => WrapSelection("<emission>", "</emission>"), hasSel ? on : off);
                menu.AppendAction("Emission (picked color)", _ => WrapSelection($"<emission=#{PickedHex()}>", "</emission>"), hasSel ? on : off);
                menu.AppendAction("Surface neutral  <surface>", _ => WrapSelection("<surface>", "</surface>"), hasSel ? on : off);
                menu.AppendAction("Surface height raised  h=1", _ => WrapSelection("<surface h=1>", "</surface>"), hasSel ? on : off);
                menu.AppendAction("Surface height engraved  h=-1", _ => WrapSelection("<surface h=-1>", "</surface>"), hasSel ? on : off);
                menu.AppendAction("Blend/Multiply", _ => WrapSelection("<blend=multiply>", "</blend>"), hasSel ? on : off);
                menu.AppendAction("Blend/Screen", _ => WrapSelection("<blend=screen>", "</blend>"), hasSel ? on : off);
                menu.AppendAction("Blend/Overlay", _ => WrapSelection("<blend=overlay>", "</blend>"), hasSel ? on : off);
                menu.AppendAction("Blend/Add", _ => WrapSelection("<blend=add>", "</blend>"), hasSel ? on : off);
                menu.AppendAction("Blend/Subtract", _ => WrapSelection("<blend=subtract>", "</blend>"), hasSel ? on : off);
                menu.AppendAction("Opacity/25%", _ => WrapSelection("<opacity=0.25>", "</opacity>"), hasSel ? on : off);
                menu.AppendAction("Opacity/50%", _ => WrapSelection("<opacity=0.5>", "</opacity>"), hasSel ? on : off);
                menu.AppendAction("Opacity/75%", _ => WrapSelection("<opacity=0.75>", "</opacity>"), hasSel ? on : off);
                menu.AppendAction("Font  <font=name>", _ => WrapSelection("<font=name>", "</font>"), hasSel ? on : off);
                menu.AppendSeparator();
            }
            menu.AppendAction("Page break  <newpage>", _ => InsertText("\n<newpage>\n"), on);
            menu.AppendSeparator();
            menu.AppendAction("Undo  (Ctrl+Z)", _ => UndoText(),
                _undoStack.Count > 0 ? on : off);
            menu.AppendAction("Redo  (Ctrl+Shift+Z)", _ => RedoText(),
                _redoStack.Count > 0 ? on : off);
        }

        // ══════════════════════════════════════════════════════════
        // Selection / caret (version-safe accessors)
        // ══════════════════════════════════════════════════════════
        // Last known caret/selection, captured while the field IS focused. When a toolbar
        // button is clicked the field loses focus and the engine resets the live selection
        // to 0 — which made toolbar buttons insert tags at the very start of the text
        // (the right-click menu worked because focus never left the field). When the field
        // is not focused we fall back to this cache.
        private int _cachedCursor;
        private int _cachedSelect;
        private IVisualElementScheduledItem _caretPoller;

        private int LiveCursorIndex()
        {
#if UNITY_2023_1_OR_NEWER
            return _textField.textSelection.cursorIndex;
#else
            return _textField.cursorIndex;
#endif
        }

        private int LiveSelectIndex()
        {
#if UNITY_2023_1_OR_NEWER
            return _textField.textSelection.selectIndex;
#else
            return _textField.selectIndex;
#endif
        }

        /// <summary>Snapshots the live caret/selection into the cache (call while focused).</summary>
        private void CaptureCaretState()
        {
            if (_textField == null || !_fieldFocused) return;
            _cachedCursor = LiveCursorIndex();
            _cachedSelect = LiveSelectIndex();
        }

        private int CursorIndex() => _fieldFocused ? LiveCursorIndex() : _cachedCursor;
        private int SelectIndex() => _fieldFocused ? LiveSelectIndex() : _cachedSelect;

        private void SelectRange(int a, int b)
        {
            int len = (_workingText ?? "").Length;
            a = Mathf.Clamp(a, 0, len);
            b = Mathf.Clamp(b, 0, len);
            _cachedCursor = a;
            _cachedSelect = b;
#if UNITY_2023_1_OR_NEWER
            _textField.textSelection.SelectRange(a, b);
#else
            _textField.SelectRange(a, b);
#endif
        }

        private bool GetSelection(out int start, out int end)
        {
            int len = (_workingText ?? "").Length;
            start = Mathf.Clamp(Mathf.Min(CursorIndex(), SelectIndex()), 0, len);
            end = Mathf.Clamp(Mathf.Max(CursorIndex(), SelectIndex()), 0, len);
            return end > start;
        }

        private bool SelectionCoversAllText()
        {
            int len = (_workingText ?? "").Length;
            return len > 0 && GetSelection(out int s, out int e) && s == 0 && e == len;
        }

        // ══════════════════════════════════════════════════════════
        // The single mutation entry point
        // ══════════════════════════════════════════════════════════
        /// <summary>Replaces [start, end) with <paramref name="replacement"/>; caret lands
        /// at <paramref name="caretAfter"/> (or end of the replacement when negative).</summary>
        private void ReplaceRange(int start, int end, string replacement, int caretAfter = -1)
        {
            if (_asset == null || _workingLocaleIndex < 0 ||
                _workingLocaleIndex >= _asset.translations.Count) return;
            string t = _workingText ?? "";
            replacement = NormalizeEditorText(replacement);
            start = Mathf.Clamp(start, 0, t.Length);
            end = Mathf.Clamp(end, start, t.Length);

            PushUndoSnapshot(t);
            _workingText = t.Substring(0, start) + replacement + t.Substring(end);

            int caret = caretAfter >= 0 ? caretAfter : start + replacement.Length;
            ApplyBufferToField(caret);
            MarkEdited();
        }

        /// <summary>
        /// Pushes the buffer into the view and restores the caret.
        /// CRITICAL (external review consensus): do NOT call Focus() when the field is
        /// already focused — refocusing re-triggers the engine's select-all path, which
        /// was the root of the "next keystroke erases everything" bug. Focus (only if
        /// needed) and caret are separated by a tick so a deferred select-all cannot
        /// overwrite the caret afterwards.
        /// </summary>
        private void ApplyBufferToField(int caret)
        {
            if (_textField == null) return;

            // The permanent scroll guard (see BuildTextArea) vetoes the engine's
            // reset-to-top after the buffer swap — nothing extra needed here.
            _textField.SetValueWithoutNotify(_workingText);
            int clamped = Mathf.Clamp(caret, 0, (_workingText ?? "").Length);

            _textField.schedule.Execute(() =>
            {
                if (!_fieldFocused) FocusTextInput();
            });
            _textField.schedule.Execute(() => SelectRange(clamped, clamped)).ExecuteLater(1);
        }

        /// <summary>Focuses the inner input element (focusing the wrapper routes through
        /// extra focus logic in Unity 6); falls back to the wrapper.</summary>
        private void FocusTextInput()
        {
            if (_textInput != null) _textInput.Focus();
            else _textField?.Focus();
        }

        private void WrapSelection(string prefix, string suffix)
        {
            if (GetSelection(out int start, out int end))
            {
                string body = (_workingText ?? "").Substring(start, end - start);
                ReplaceRange(start, end, prefix + body + suffix);
            }
            else
            {
                int at = Mathf.Clamp(CursorIndex(), 0, (_workingText ?? "").Length);
                // Caret between the tags — type the word right away.
                ReplaceRange(at, at, prefix + suffix, at + prefix.Length);
            }
        }

        private void PrefixLine(string linePrefix)
        {
            string t = _workingText ?? "";
            int at = Mathf.Clamp(CursorIndex(), 0, t.Length);
            int lineStart = t.LastIndexOf('\n', Mathf.Max(0, at - 1)) + 1;
            ReplaceRange(lineStart, lineStart, linePrefix, at + linePrefix.Length);
        }

        private void InsertText(string insert)
        {
            if (GetSelection(out int start, out int end))
                ReplaceRange(start, end, insert);
            else
            {
                int at = Mathf.Clamp(CursorIndex(), 0, (_workingText ?? "").Length);
                ReplaceRange(at, at, insert);
            }
        }

        private void CutSelection()
        {
            if (!GetSelection(out int start, out int end)) return;
            EditorGUIUtility.systemCopyBuffer = (_workingText ?? "").Substring(start, end - start);
            ReplaceRange(start, end, "");
        }

        private void CopySelection()
        {
            if (!GetSelection(out int start, out int end)) return;
            EditorGUIUtility.systemCopyBuffer = (_workingText ?? "").Substring(start, end - start);
        }

        private void PasteClipboard()
        {
            string clip = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(clip)) return;
            InsertText(clip);
        }

        private void SelectAllText()
        {
            int len = (_workingText ?? "").Length;
            _textField.schedule.Execute(() =>
            {
                if (!_fieldFocused) FocusTextInput();
            });
            _textField.schedule.Execute(() => SelectRange(0, len)).ExecuteLater(1);
        }

        // ══════════════════════════════════════════════════════════
        // Undo / redo
        // ══════════════════════════════════════════════════════════
        private void PushUndoSnapshot(string previous)
        {
            double now = EditorApplication.timeSinceStartup;
            if (_undoStack.Count > 0 && now - _lastEditTime < UndoGroupSeconds)
            {
                _lastEditTime = now;
                _redoStack.Clear();
                return;
            }
            _undoStack.Add(previous ?? "");
            if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
            _redoStack.Clear();
            _lastEditTime = now;
        }

        private void UndoText()
        {
            if (_undoStack.Count == 0) return;
            _redoStack.Add(_workingText ?? "");
            _workingText = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            // The next keystroke starts a new undo group from the restored state.
            _lastEditTime = double.NegativeInfinity;
            ApplyBufferToField((_workingText ?? "").Length);
            MarkEdited();
        }

        private void RedoText()
        {
            if (_redoStack.Count == 0) return;
            _undoStack.Add(_workingText ?? "");
            _workingText = _redoStack[_redoStack.Count - 1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            // The next keystroke starts a new undo group from the restored state.
            _lastEditTime = double.NegativeInfinity;
            ApplyBufferToField((_workingText ?? "").Length);
            MarkEdited();
        }

        // ══════════════════════════════════════════════════════════
        // Persistence / refresh
        // ══════════════════════════════════════════════════════════
        private void MarkEdited()
        {
            _dirty = true;
            if (_autoSave) SaveToAsset();
            // Everything below is visual/bookkeeping — debounced together with the preview.
            // (UpdateUnsavedState builds an interpolated string and RefreshStateRow relayouts
            // two toolbar labels; doing that per keystroke measurably slowed long documents.)
            SchedulePreviewRefresh();
        }

        // Preview/counters are DEBOUNCED: converting markdown + PBR tags + sanitizing the
        // WHOLE text on every keystroke made typing sluggish on long documents. One refresh
        // 300 ms after the last edit is imperceptible and O(1) per burst of typing.
        private IVisualElementScheduledItem _previewRefreshItem;

        private void SchedulePreviewRefresh()
        {
            if (_previewRefreshItem == null && rootVisualElement != null)
            {
                _previewRefreshItem = rootVisualElement.schedule.Execute(() =>
                {
                    RefreshPreview();
                    RefreshCounters();
                    UpdateUnsavedState();
                    RefreshStateRow();
                });
                _previewRefreshItem.Pause();
            }
            _previewRefreshItem?.ExecuteLater(300);
        }

        private void SaveToAsset()
        {
            if (_asset == null) return;

            int targetIndex = _workingLocaleIndex;
            bool indexMatchesLocale = targetIndex >= 0 && targetIndex < _asset.translations.Count &&
                string.Equals(_asset.translations[targetIndex].locale, _workingLocaleCode,
                    System.StringComparison.Ordinal);
            if (!indexMatchesLocale)
            {
                targetIndex = -1;
                for (int i = 0; i < _asset.translations.Count; i++)
                {
                    if (!string.Equals(_asset.translations[i].locale, _workingLocaleCode,
                        System.StringComparison.Ordinal)) continue;
                    targetIndex = i;
                    break;
                }
            }
            if (targetIndex < 0)
            {
                // The locale may have been deleted/renamed by Unity Undo while a draft was
                // open. Keep the draft dirty instead of silently discarding or misrouting it.
                Debug.LogWarning("[MeshTextBaker] Cannot save the draft because its locale no " +
                    "longer exists in the asset. The draft remains open in the Text Editor.", _asset);
                UpdateUnsavedState();
                RefreshStateRow();
                return;
            }

            _workingLocaleIndex = targetIndex;
            Undo.RecordObject(_asset, "Edit Text");
            _asset.translations[targetIndex].text = _workingText ?? "";
            EditorUtility.SetDirty(_asset);
            _dirty = false;
            _observedAssetState = CaptureAssetState();
            UpdateUnsavedState();
            RefreshStateRow();
        }

        private string CurrentLocaleName()
        {
            if (!string.IsNullOrEmpty(_workingLocaleCode)) return _workingLocaleCode;
            if (_asset == null || _workingLocaleIndex < 0 ||
                _workingLocaleIndex >= _asset.translations.Count) return "?";
            return _asset.translations[_workingLocaleIndex].locale;
        }

        private void RefreshAll()
        {
            if (_editorRoot == null) return;

            bool hasAsset = _asset != null;
            _editorRoot.style.display = hasAsset ? DisplayStyle.Flex : DisplayStyle.None;
            if (!hasAsset) return;

            SyncWorkingBuffer();
            RefreshLocaleTabs();
            RefreshProcessingUI();
            bool hasLocale = _workingLocaleIndex >= 0 && _workingLocaleIndex < _asset.translations.Count &&
                !string.IsNullOrWhiteSpace(_asset.translations[_workingLocaleIndex].locale);
            if (_textField != null)
            {
                _textField.SetEnabled(hasLocale);
                _textField.SetValueWithoutNotify(hasLocale ? _workingText : "");
            }
            if (_noLocaleNotice != null)
                _noLocaleNotice.style.display = hasLocale ? DisplayStyle.None : DisplayStyle.Flex;
            _markupToolbar?.SetEnabled(hasLocale);
            RefreshPreview();
            RefreshCounters();
            RefreshStateRow();
            UpdateUnsavedState();
            _observedAssetState = CaptureAssetState();
        }

        private void SyncWorkingBuffer()
        {
            if (_asset == null || _asset.translations.Count == 0)
            {
                _workingText = "";
                _workingLocaleIndex = -1;
                _workingLocaleCode = null;
                _localeIndex = 0;
                _dirty = false;
                return;
            }
            _localeIndex = Mathf.Clamp(_localeIndex, 0, _asset.translations.Count - 1);
            if (_workingLocaleIndex != _localeIndex)
            {
                _workingText = NormalizeEditorText(_asset.translations[_localeIndex].text);
                _workingLocaleIndex = _localeIndex;
                _workingLocaleCode = _asset.translations[_localeIndex].locale;
                _dirty = false;
            }
        }

        private void RefreshLocaleTabs()
        {
            _localeBar.Clear();
            var translations = _asset.translations;

            for (int i = 0; i < translations.Count; i++)
            {
                int idx = i;
                var toggle = new ToolbarToggle
                {
                    text = string.IsNullOrEmpty(translations[i].locale) ? "?" : translations[i].locale,
                    value = i == _localeIndex
                };
                toggle.style.minWidth = 36;
                toggle.RegisterValueChangedCallback(e =>
                {
                    if (!e.newValue) { toggle.SetValueWithoutNotify(true); return; }
                    if (idx == _localeIndex) return;
                    if (_dirty)
                    {
                        SaveToAsset(); // switching tabs never loses text
                        if (_dirty) { toggle.SetValueWithoutNotify(false); return; }
                    }
                    _localeIndex = idx;
                    _workingLocaleIndex = -1;
                    _undoStack.Clear();
                    _redoStack.Clear();
                    RefreshAll();
                });
                _localeBar.Add(toggle);
            }

            var addButton = new ToolbarButton(() =>
            {
                if (_dirty)
                {
                    SaveToAsset();
                    if (_dirty) return;
                }
                Undo.RecordObject(_asset, "Add Locale");
                string localeName = _asset.translations.Count == 0 ? "en" : "new";
                _asset.translations.Add(new LocalizedTextEntry { locale = localeName, text = "" });
                _localeIndex = _asset.translations.Count - 1;
                _workingLocaleIndex = -1;
                _undoStack.Clear();
                _redoStack.Clear();
                EditorUtility.SetDirty(_asset);
                RefreshAll();
            }) { text = "+" };
            addButton.style.width = 26;
            _localeBar.Add(addButton);

            _localeBar.Add(new ToolbarSpacer { style = { flexGrow = 1 } });

            if (translations.Count > 0 && _localeIndex < translations.Count)
            {
                _localeBar.Add(new Label("Locale:")
                { style = { fontSize = 10, unityTextAlign = TextAnchor.MiddleLeft, marginRight = 2 } });

                var rename = new TextField { value = translations[_localeIndex].locale, isDelayed = true };
                rename.style.width = 64;
                rename.RegisterValueChangedCallback(e =>
                {
                    if (_dirty)
                    {
                        SaveToAsset();
                        if (_dirty)
                        {
                            rename.SetValueWithoutNotify(_workingLocaleCode ?? "");
                            return;
                        }
                    }
                    Undo.RecordObject(_asset, "Rename Locale");
                    translations[_localeIndex].locale = e.newValue;
                    if (_workingLocaleIndex == _localeIndex) _workingLocaleCode = e.newValue;
                    EditorUtility.SetDirty(_asset);
                    RefreshAll();
                });
                _localeBar.Add(rename);

                var deleteButton = new ToolbarButton(() =>
                {
                    if (!EditorUtility.DisplayDialog("Delete locale",
                        $"Delete locale '{translations[_localeIndex].locale}' and its text?", "Delete", "Cancel"))
                        return;
                    Undo.RecordObject(_asset, "Delete Locale");
                    translations.RemoveAt(_localeIndex);
                    _localeIndex = Mathf.Max(0, _localeIndex - 1);
                    _workingLocaleIndex = -1;
                    _dirty = false;
                    _undoStack.Clear();
                    _redoStack.Clear();
                    EditorUtility.SetDirty(_asset);
                    RefreshAll();
                }) { text = "\u00d7" };
                deleteButton.style.width = 26;
                _localeBar.Add(deleteButton);
            }
        }

        private void RefreshPreview()
        {
            if (_previewLabel == null) return;
            bool markdown = _asset != null && _asset.markdownEnabled;
            bool richText = _asset != null && _asset.richTextEnabled;
            string converted = markdown
                ? MarkdownParser.ConvertToRichText(_workingText ?? "")
                : (_workingText ?? "");
            if (richText)
            {
                converted = RichTextPbrTags.Preprocess(converted);
                // Blend markers are zero-width; preview keeps their content visible without trying
                // to reproduce the final texture composite inside UI Toolkit.
                converted = RichTextBlendTags.Preprocess(converted);
                converted = RichTextOpacityTags.Preprocess(converted);
                converted = RichTextOpacityTags.Apply(converted);
            }
            _previewLabel.enableRichText = richText;
            _previewLabel.text = richText ? SanitizeForPreview(converted) : converted;
        }

        private void RefreshCounters()
        {
            if (_counterLabel == null) return;
            string t = _workingText ?? "";
            _counterLabel.text = $"  {t.Length} chars   \u00b7   {CountWords(t)} words";
        }

        private void RefreshStateRow()
        {
            if (_saveButton != null)
            {
                _saveButton.text = _dirty && !_autoSave ? "Save*" : "Saved";
                _saveButton.SetEnabled(_dirty && !_autoSave);
            }
            if (_stateLabel != null)
            {
                _stateLabel.text = _autoSave
                    ? "Auto Save on  "
                    : (_dirty ? "Unsaved (Ctrl+S)   \u00b7   Ctrl+Z / Ctrl+Shift+Z  " : "");
            }
        }

        private string PickedHex() => ColorUtility.ToHtmlStringRGB(_pickedColor).ToLowerInvariant();

        private static string SanitizeForPreview(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close > i && close - i < 64)
                    {
                        string inner = text.Substring(i + 1, close - i - 1).ToLowerInvariant();
                        bool keep = inner == "b" || inner == "/b" ||
                                    inner == "i" || inner == "/i" ||
                                    inner == "u" || inner == "/u" ||
                                    inner == "s" || inner == "/s" ||
                                    inner.StartsWith("mark") || inner == "/mark" ||
                                    inner.StartsWith("color") || inner == "/color" ||
                                    inner.StartsWith("size") || inner == "/size" ||
                                    inner.StartsWith("align") || inner == "/align" ||
                                    inner.StartsWith("alpha") ||
                                    inner.StartsWith("pos") || inner == "nobr" || inner == "/nobr" ||
                                    inner.StartsWith("#");
                        if (keep) sb.Append(text, i, close - i + 1);
                        else if (inner == "newpage") sb.Append("\n\u2014\u2014\u2014 page break \u2014\u2014\u2014\n");
                        else if (inner.StartsWith("image")) sb.Append("\u25a3"); // ▣ image placeholder
                        i = close + 1;
                        continue;
                    }
                }
                sb.Append(text[i]);
                i++;
            }
            return sb.ToString();
        }

        private static int CountWords(string t)
        {
            if (string.IsNullOrEmpty(t)) return 0;
            int words = 0;
            bool inWord = false;
            for (int i = 0; i < t.Length; i++)
            {
                bool isSpace = char.IsWhiteSpace(t[i]);
                if (!isSpace && !inWord) { words++; inWord = true; }
                else if (isSpace) inWord = false;
            }
            return words;
        }
    }
}
