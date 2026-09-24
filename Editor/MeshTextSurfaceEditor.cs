// Mesh Text Baker — MeshTextSurfaceEditor.cs
// Custom inspector for MeshTextSurface: zone management, placement, bake controls, per-zone settings.

using UnityEngine;
using MeshTextBaker;
using UnityEditor;
using System.Collections.Generic;
using TMPro;

namespace MeshTextBaker.Editor
{
    [CustomEditor(typeof(MeshTextSurface))]
    public class MeshTextSurfaceEditor : UnityEditor.Editor
    {
        private SerializedProperty _bakeSettingsProp;
        private SerializedProperty _bakeModeProp;
        private SerializedProperty _previewLocaleProp;
        private SerializedProperty _localizationProviderProp;
        private SerializedProperty _ignoreSceneDefaultProviderProp;
        private SerializedProperty _zonesProp;

        private bool _showBakeSettings = true;
        private bool _showDefaultValues = true;
        private bool _showBookMode = true;
        private bool _showPageNumberDefaults = true;
        private bool _showZones = true;
        // private bool _showBookDebug = false; // debug section commented out (see DrawBookControls).
        private bool _showLocalization = true;
        // Multiple zones can be expanded at once (keyed by zone id — stable across reorders).
        private readonly HashSet<string> _expandedZoneIds = new HashSet<string>();

        // Inspector drag-and-drop ordering. orderIndex is derived from list position.
        private const string ZoneDragDataKey = "MeshTextBaker.ZoneDragId";
        private int _pendingZoneMoveFrom = -1;
        private int _pendingZoneMoveTo = -1;
        private int _zoneDragCandidate = -1;

        // Book test cursor (editor-only; for the debug "bake single page" section below — currently commented out).
        // private int _testCursor = 0;
        // private int _testZoneIndex = 0;
        private SerializedProperty _bookTextProp;
        private SerializedProperty _bookLocalizationKeyProp;
        private SerializedProperty _bookMarkdownProp;
        private SerializedProperty _bookUsePbrProp;
        private SerializedProperty _pageNumberFontProp;
        private SerializedProperty _pageNumberFontSizeModeProp;
        private SerializedProperty _pageNumberFontSizeProp;
        private SerializedProperty _pageNumberAlignmentProp;
        private SerializedProperty _pageNumberUseDefaultTextColorProp;
        private SerializedProperty _pageNumberTextColorProp;
        private SerializedProperty _pageNumberPaddingProp;


        private void OnEnable()
        {
            _bakeSettingsProp = serializedObject.FindProperty("_bakeSettings");
            _bakeModeProp = serializedObject.FindProperty("_bakeMode");
            _previewLocaleProp = serializedObject.FindProperty("_previewLocale");
            _localizationProviderProp = serializedObject.FindProperty("_localizationProviderBehaviour");
            _ignoreSceneDefaultProviderProp = serializedObject.FindProperty("_ignoreSceneDefaultProvider");
            _zonesProp = serializedObject.FindProperty("_zones");
            _bookTextProp = serializedObject.FindProperty("_bookText");
            _bookLocalizationKeyProp = serializedObject.FindProperty("_bookLocalizationKey");
            _bookMarkdownProp = serializedObject.FindProperty("_bookMarkdownEnabled");
            _bookUsePbrProp = serializedObject.FindProperty("_bookUsePbr");
            _pageNumberFontProp = serializedObject.FindProperty("_pageNumberFont");
            _pageNumberFontSizeModeProp = serializedObject.FindProperty("_pageNumberFontSizeMode");
            _pageNumberFontSizeProp = serializedObject.FindProperty("_pageNumberFontSize");
            _pageNumberAlignmentProp = serializedObject.FindProperty("_pageNumberAlignment");
            _pageNumberUseDefaultTextColorProp = serializedObject.FindProperty("_pageNumberUseDefaultTextColor");
            _pageNumberTextColorProp = serializedObject.FindProperty("_pageNumberTextColor");
            _pageNumberPaddingProp = serializedObject.FindProperty("_pageNumberPadding");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var surface = (MeshTextSurface)target;

            DrawPlacementControls(surface);
            DrawBakeControls(surface);
            EditorGUILayout.Space(4);
            DrawBakeSettings();
            EditorGUILayout.Space(4);
            DrawLocalization(surface);
            EditorGUILayout.Space(4);
            DrawBookControls(surface);
            EditorGUILayout.Space(4);
            DrawDefaultValues();
            EditorGUILayout.Space(4);
            DrawZonesList(surface);
            EditorGUILayout.Space(4);
            DrawStatus(surface);

            serializedObject.ApplyModifiedProperties();
        }


                private void DrawPlacementControls(MeshTextSurface surface)
        {
            EditorGUILayout.LabelField("Zone Placement", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.45f, 0.85f, 1f);
            if (GUILayout.Button(new GUIContent("Open UV Editor",
                    "Precise UV-space placement, resize, rotation, snap and preview."),
                GUILayout.Height(30)))
                UVZoneEditorWindow.ShowWindow(surface);
            GUI.backgroundColor = previous;

            if (!ZonePlacementTool.isActive)
            {
                if (GUILayout.Button("Enter Placement Mode", GUILayout.Height(30)))
                    ZonePlacementTool.EnterPlacementMode(surface);
            }
            else
            {
                if (GUILayout.Button("Exit Placement Mode", GUILayout.Height(30)))
                    ZonePlacementTool.ExitPlacementMode();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawBakeControls(MeshTextSurface surface)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Baking", EditorStyles.boldLabel);

            // EditorBake mode: re-bake automatically whenever the preview locale changes.
            string localeBefore = _previewLocaleProp.stringValue;
            EditorGUILayout.PropertyField(_previewLocaleProp, new GUIContent("Preview Locale"));
            EditorGUILayout.PropertyField(_bakeModeProp, new GUIContent("Bake Mode",
                "Manual — bake ONLY when you press Test Bake or call BakeForLocale() from code. " +
                "Nothing happens automatically.\n\n" +
                "EditorBake — same as Manual, PLUS the editor re-bakes automatically whenever " +
                "you change the Preview Locale in this inspector. Convenient while authoring.\n\n" +
                "RuntimeBakeOnStart — the surface bakes itself in Start() when the game runs " +
                "(uses Preview Locale). Use for dynamic/localized text that must match the " +
                "player's language at startup."));

            if ((BakeMode)_bakeModeProp.enumValueIndex == BakeMode.EditorBake
                && _previewLocaleProp.stringValue != localeBefore
                && !string.IsNullOrEmpty(_previewLocaleProp.stringValue))
            {
                serializedObject.ApplyModifiedProperties();
                surface.BakeForLocale(_previewLocaleProp.stringValue);
                SceneView.RepaintAll();
            }

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("Test Bake", GUILayout.Height(32)))
            {
                string locale = _previewLocaleProp.stringValue;
                if (string.IsNullOrEmpty(locale))
                {
                    EditorUtility.DisplayDialog("Error", "Please enter a locale code (e.g., 'en', 'ru').", "OK");
                }
                else
                {
                    // Note: no Undo here on purpose — a bake mutates RTs/material instances
                    // which Undo can't track. Use "Clear Bake" to revert.
                    surface.BakeForLocale(locale);
                    SceneView.RepaintAll();
                }
            }
            GUI.backgroundColor = Color.white;

            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("Clear Bake", GUILayout.Height(32)))
            {
                surface.ClearBake();
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // Bake to Asset: persistent PNG + material in the project — zero runtime cost
            // for static text (no RTs, no bake on load, platform texture compression).
            if (GUILayout.Button(new GUIContent("Bake to Asset (PNG + Material)",
                "Saves the baked textures as PNG assets, creates persistent materials and " +
                "assigns them. Recommended for static text: removes all runtime baking cost."),
                GUILayout.Height(24)))
            {
                string locale = _previewLocaleProp.stringValue;
                if (string.IsNullOrEmpty(locale))
                    EditorUtility.DisplayDialog("Error", "Please enter a locale code first.", "OK");
                else
                {
                    // Folder picker (remembers the last choice); cancelling aborts the bake.
                    BakeToAssetUtility.BakeToAssetsInteractive(surface, locale);
                    SceneView.RepaintAll();
                }
            }

            if (GUILayout.Button(new GUIContent("Force Clear Restore Records",
                "Forcefully clears stale serialized restore records (_touchedSlots). " +
                "Use this if you have modified your mesh material slots and the material " +
                "assignments are messed up upon entering Play Mode."),
                GUILayout.Height(24)))
            {
                var so = new SerializedObject(surface);
                var touchedProp = so.FindProperty("_touchedSlots");
                if (touchedProp != null && touchedProp.isArray)
                {
                    touchedProp.ClearArray();
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(surface);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
                    Debug.Log($"[MeshTextBaker] Cleared stale restore records on '{surface.name}'.", surface);
                }
            }
        }

        private void DrawBookControls(MeshTextSurface surface)
        {
            int slotCount = 0;
            int pageNumberCount = 0;
            foreach (var z in surface.zones)
            {
                if (z.role == TextZoneRole.PageSlot) slotCount++;
                else if (z.role == TextZoneRole.PageNumber) pageNumberCount++;
            }

            if (slotCount > 0)
            {
                _showBookMode = EditorGUILayout.Foldout(_showBookMode, "Book Settings", true);
                if (_showBookMode)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_bookTextProp, new GUIContent("Book Text",
                        "Embedded authoring/fallback text. Use <newpage> to force a break."));
                    if (_bookLocalizationKeyProp != null)
                        EditorGUILayout.PropertyField(_bookLocalizationKeyProp, new GUIContent("Book Localization Key",
                            "External/Unity localization key. Empty = use the Book Text asset key."));
                    if (_bookMarkdownProp != null)
                        EditorGUILayout.PropertyField(_bookMarkdownProp, new GUIContent("Book Markdown",
                            "Convert markdown to TMP rich text ONCE, before pagination."));
                    MeshTextAsset bookAsset = _bookTextProp.objectReferenceValue as MeshTextAsset;
                    if (bookAsset != null)
                    {
                        EditorGUILayout.HelpBox(
                            $"Book TextAsset authoring: Rich Text {(bookAsset.richTextEnabled ? "On" : "Off")} · " +
                            $"Markdown {(bookAsset.markdownEnabled ? "On" : "Off")}", MessageType.None);
                        if (bookAsset.markdownEnabled && _bookMarkdownProp != null && !_bookMarkdownProp.boolValue)
                            EditorGUILayout.HelpBox("Book TextAsset expects Markdown, but Book Markdown is off.",
                                MessageType.Warning);
                        if (bookAsset.richTextEnabled)
                        {
                            foreach (TextZone z in surface.zones)
                            {
                                if (z.role == TextZoneRole.PageSlot && !z.richTextEnabled)
                                {
                                    EditorGUILayout.HelpBox(
                                        $"PageSlot '{z.displayName}' has Rich Text disabled, but the Book TextAsset expects it.",
                                        MessageType.Warning);
                                    break;
                                }
                            }
                        }
                    }
                    if (_bookUsePbrProp != null)
                        EditorGUILayout.PropertyField(_bookUsePbrProp, new GUIContent("Book Use PBR",
                            "Enable PBR tags and per-zone PBR settings for all PageSlot zones."));
                    SerializedProperty measureWindowProp = _bakeSettingsProp != null
                        ? _bakeSettingsProp.FindPropertyRelative("measureWindow") : null;
                    if (measureWindowProp != null)
                        EditorGUILayout.PropertyField(measureWindowProp, new GUIContent("Measure Window",
                            "Maximum characters considered per page-layout pass."));
                    EditorGUILayout.HelpBox(
                        $"Page slots: {slotCount}. Test Bake fills them in reading order; " +
                        "BookController drives runtime paging.", MessageType.Info);
                    EditorGUI.indentLevel--;
                }
            }

            if (pageNumberCount > 0)
            {
                _showPageNumberDefaults = EditorGUILayout.Foldout(
                    _showPageNumberDefaults, "Page Number Defaults", true);
                if (_showPageNumberDefaults)
                {
                    EditorGUI.indentLevel++;
                    if (_pageNumberFontProp != null)
                        EditorGUILayout.PropertyField(_pageNumberFontProp, new GUIContent("Font"));
                    if (_pageNumberFontSizeModeProp != null)
                        EditorGUILayout.PropertyField(_pageNumberFontSizeModeProp, new GUIContent("Font Size Mode"));
                    if (_pageNumberFontSizeProp != null)
                        EditorGUILayout.PropertyField(_pageNumberFontSizeProp, new GUIContent("Font Size"));
                    if (_pageNumberAlignmentProp != null)
                        EditorGUILayout.PropertyField(_pageNumberAlignmentProp, new GUIContent("Alignment"));
                    if (_pageNumberUseDefaultTextColorProp != null)
                        EditorGUILayout.PropertyField(_pageNumberUseDefaultTextColorProp,
                            new GUIContent("Use Default Text Color"));
                    if (_pageNumberTextColorProp != null)
                    {
                        using (new EditorGUI.DisabledScope(
                            _pageNumberUseDefaultTextColorProp != null &&
                            _pageNumberUseDefaultTextColorProp.boolValue))
                            EditorGUILayout.PropertyField(_pageNumberTextColorProp, new GUIContent("Text Color"));
                    }
                    if (_pageNumberPaddingProp != null)
                        EditorGUILayout.PropertyField(_pageNumberPaddingProp, new GUIContent("Padding"));
                    EditorGUI.indentLevel--;
                }
            }

            // Debug-only "bake single page" section is commented out. Delete the /* and */ lines to re-enable.
            /*
            if (slotCount == 0) return;
            _showBookDebug = EditorGUILayout.Foldout(_showBookDebug, "Debug: bake single page", true);
            if (!_showBookDebug) return;

            EditorGUI.indentLevel++;
            using (new EditorGUI.DisabledScope(surface.bookText == null &&
                string.IsNullOrWhiteSpace(surface.bookLocalizationKey)))
            {
                _testZoneIndex = EditorGUILayout.IntField("Test Zone Index (reading order)",
                    Mathf.Max(0, _testZoneIndex));
                _testCursor = EditorGUILayout.IntField("Test Start Char", Mathf.Max(0, _testCursor));
                EditorGUILayout.BeginHorizontal();
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
                if (GUILayout.Button("Bake Page Into Zone", GUILayout.Height(24)))
                {
                    string locale = _previewLocaleProp.stringValue;
                    var slots = surface.GetPageSlots();
                    if (_testZoneIndex >= 0 && _testZoneIndex < slots.Count)
                    {
                        var zone = slots[_testZoneIndex];
                        string full = surface.GetBookText(locale);
                        int next = _testCursor;
                        string pageText = string.IsNullOrEmpty(full) ? ""
                            : surface.GetPageText(full, _testCursor, zone, out next);
                        var texts = new Dictionary<string, string> { { zone.id, pageText } };
                        var one = new List<TextZone> { zone };
                        surface.BakeZones(texts, one, locale);
                        _testCursor = next;
                        SceneView.RepaintAll();
                    }
                    else Debug.LogWarning($"[MeshTextBaker] Zone index {_testZoneIndex} out of range.");
                }
                GUI.backgroundColor = prev;
                EditorGUILayout.EndHorizontal();
                if (GUILayout.Button("Reset Test (zone 0, char 0)"))
                {
                    _testZoneIndex = 0;
                    _testCursor = 0;
                }
            }
            EditorGUI.indentLevel--;
            */
        }

        private void DrawBakeSettings()
        {
            _showBakeSettings = EditorGUILayout.Foldout(_showBakeSettings, "Bake Settings", true);
            if (!_showBakeSettings) return;

            EditorGUI.indentLevel++;
            var s = _bakeSettingsProp;
            EditorGUILayout.PropertyField(s.FindPropertyRelative("resolution"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("texturePropertyName"),
                new GUIContent("Texture Property Name (optional)",
                    "Leave empty to auto-detect per render pipeline " +
                    "(_MainTex Built-in, _BaseMap URP, _BaseColorMap HDRP)."));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("emissionPropertyName"),
                new GUIContent("Emission Property Name (optional)",
                    "Leave empty to auto-detect (_EmissionMap Built-in/URP, _EmissiveColorMap HDRP)."));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("heightPropertyName"),
                new GUIContent("Height Property Name (optional)",
                    "Leave empty to auto-detect (_HeightMap HDRP/custom, _ParallaxMap URP/Built-in)."));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("baseAlbedoTexture"),
                new GUIContent("Base Albedo Texture (optional)",
                    "Leave empty to auto-use the texture already on the material."));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("generateMipMaps"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("imageLibrary"),
                new GUIContent("Image Library",
                    "Named textures usable inside text via <image=name> tags. Optional " +
                    "params: h=NNN, w=NNN, displace=false, blend=multiply, opacity=0.5."), true);
            EditorGUILayout.PropertyField(s.FindPropertyRelative("fontLibrary"),
                new GUIContent("Font Library",
                    "Named TMP fonts usable via <font=name>. Loose TTF/OTF: " +
                    "<font=file:fonts/MyFont.ttf>."), true);
            EditorGUI.indentLevel--;
        }

        private void DrawLocalization(MeshTextSurface surface)
        {
            _showLocalization = EditorGUILayout.Foldout(_showLocalization, "Localization", true);
            if (!_showLocalization) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_localizationProviderProp, new GUIContent(
                "Localization Provider (optional)",
                "Explicit provider for THIS surface. Empty = automatic: the scene's default " +
                "provider (e.g. one External Override for the whole scene), else embedded " +
                "MeshTextAsset fallback."));
            if (_ignoreSceneDefaultProviderProp != null)
                EditorGUILayout.PropertyField(_ignoreSceneDefaultProviderProp, new GUIContent(
                    "Ignore Scene Default",
                    "Ignore the scene's default provider on THIS surface: with no explicit provider " +
                    "assigned, always use embedded MeshTextAsset text."));
            DrawLocalizationProviderStatus(surface);
            EditorGUI.indentLevel--;
        }

        private void DrawDefaultValues()
        {
            _showDefaultValues = EditorGUILayout.Foldout(_showDefaultValues, "Default Values", true);
            if (!_showDefaultValues) return;
            EditorGUI.indentLevel++;
            var s = _bakeSettingsProp;
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultFont"), new GUIContent("Default Font"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultFontSizeMode"),
                new GUIContent("Default Font Size Mode"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultFontSize"),
                new GUIContent("Default Font Size"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultTextColor"),
                new GUIContent("Default Text Color"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultAlignment"),
                new GUIContent("Default Alignment"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultRichTextEnabled"),
                new GUIContent("Default Rich Text"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultMarkdownEnabled"),
                new GUIContent("Default Markdown"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultUsePbr"),
                new GUIContent("Default Use PBR"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultBlendMode"),
                new GUIContent("Default Blend Mode"));
            EditorGUILayout.PropertyField(s.FindPropertyRelative("defaultOpacity"),
                new GUIContent("Default Opacity"));
            EditorGUILayout.HelpBox("Alignment/processing/PBR/blend/opacity defaults are assigned to newly created zones.",
                MessageType.None);
            EditorGUI.indentLevel--;
        }

        private void DrawZonesList(MeshTextSurface surface)
        {
            int bakeCount = 0;
            for (int zi = 0; zi < surface.zones.Count; zi++)
                if (surface.zones[zi] != null && surface.zones[zi].bakeEnabled) bakeCount++;
            string zonesTitle = bakeCount == surface.zones.Count
                ? $"Zones ({surface.zones.Count})"
                : $"Zones ({surface.zones.Count}, {bakeCount} baked)";
            _showZones = EditorGUILayout.Foldout(_showZones, zonesTitle, true);
            if (!_showZones) return;

            SyncZoneOrderToList(surface, recordUndo: false);
            _pendingZoneMoveFrom = -1;
            _pendingZoneMoveTo = -1;

            string previewLocale = _previewLocaleProp.stringValue;
            for (int i = 0; i < surface.zones.Count; i++)
                DrawZoneItem(surface, i, previewLocale);

            if (_pendingZoneMoveFrom >= 0 && _pendingZoneMoveTo >= 0)
            {
                int from = _pendingZoneMoveFrom;
                int to = Mathf.Clamp(_pendingZoneMoveTo, 0, surface.zones.Count);
                if (from >= 0 && from < surface.zones.Count)
                {
                    Undo.RecordObject(surface, "Reorder Text Zone");
                    TextZone moved = surface.zones[from];
                    surface.zones.RemoveAt(from);
                    if (to > from) to--;
                    surface.zones.Insert(Mathf.Clamp(to, 0, surface.zones.Count), moved);
                    SyncZoneOrderToList(surface, recordUndo: false);
                    surface.MarkZonesChanged();
                    EditorUtility.SetDirty(surface);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
                    GUI.changed = true;
                }
            }
        }

        private static void SyncZoneOrderToList(MeshTextSurface surface, bool recordUndo)
        {
            bool changed = false;
            for (int i = 0; i < surface.zones.Count; i++)
                if (surface.zones[i] != null && surface.zones[i].orderIndex != i) { changed = true; break; }
            if (!changed) return;
            if (recordUndo) Undo.RecordObject(surface, "Normalize Zone Order");
            for (int i = 0; i < surface.zones.Count; i++)
                if (surface.zones[i] != null) surface.zones[i].orderIndex = i;
            surface.MarkZonesChanged();
            EditorUtility.SetDirty(surface);
            PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
        }

        /// <summary>
        /// Draw through SerializedProperty so TMP's own TextAlignmentOptions property drawer
        /// (field + visual alignment button grid) is used consistently with Page Number Defaults.
        /// </summary>
        private TextAlignmentOptions DrawZoneAlignmentField(int zoneIndex, string label,
            TextAlignmentOptions fallback)
        {
            if (_zonesProp == null || zoneIndex < 0 || zoneIndex >= _zonesProp.arraySize)
                return (TextAlignmentOptions)EditorGUILayout.EnumPopup(label, fallback);
            SerializedProperty zoneProp = _zonesProp.GetArrayElementAtIndex(zoneIndex);
            SerializedProperty alignmentProp = zoneProp.FindPropertyRelative("alignment");
            if (alignmentProp == null)
                return (TextAlignmentOptions)EditorGUILayout.EnumPopup(label, fallback);
            EditorGUILayout.PropertyField(alignmentProp, new GUIContent(label));
            return (TextAlignmentOptions)alignmentProp.intValue;
        }

        /// <summary>
        /// Right-click "Reset" for hand-drawn zone fields (manual IMGUI fields don't get
        /// Unity's built-in property context menu). Call right after drawing a field.
        /// </summary>
        private void FieldResetMenu(MeshTextSurface surface, string label, System.Action reset)
        {
            var rect = GUILayoutUtility.GetLastRect();
            var e = Event.current;
            if (e.type != EventType.ContextClick || !rect.Contains(e.mousePosition)) return;

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent($"Reset {label}"), false, () =>
            {
                Undo.RecordObject(surface, $"Reset {label}");
                reset();
                EditorUtility.SetDirty(surface);
                PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
            });
            menu.ShowAsContext();
            e.Use();
        }

        private void DrawZoneItem(MeshTextSurface surface, int index, string locale)
        {
            var zone = surface.zones[index];
            bool isExpanded = _expandedZoneIds.Contains(zone.id);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();

            Rect dragRect = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18), GUILayout.Height(18));
            GUI.Label(dragRect, new GUIContent("☰", "Drag to reorder; bake Order follows inspector order."),
                EditorStyles.centeredGreyMiniLabel);
            EditorGUIUtility.AddCursorRect(dragRect, MouseCursor.Pan);
            Event dragEvent = Event.current;
            if (dragEvent.type == EventType.MouseDown && dragEvent.button == 0 && dragRect.Contains(dragEvent.mousePosition))
            {
                _zoneDragCandidate = index;
                dragEvent.Use();
            }
            else if (dragEvent.type == EventType.MouseDrag && _zoneDragCandidate == index)
            {
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData(ZoneDragDataKey, zone.id);
                DragAndDrop.StartDrag("Move zone: " + zone.displayName);
                _zoneDragCandidate = -1;
                dragEvent.Use();
            }
            else if (dragEvent.type == EventType.MouseUp && _zoneDragCandidate == index)
            {
                _zoneDragCandidate = -1;
            }

            var colorRect = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
            EditorGUI.DrawRect(colorRect, zone.previewColor);

            Color foldoutColor = GUI.color;
            if (!zone.bakeEnabled) GUI.color = new Color(1f, 1f, 1f, 0.55f);
            bool newExpanded = EditorGUILayout.Foldout(isExpanded, zone.displayName, true);
            GUI.color = foldoutColor;
            if (newExpanded != isExpanded)
            {
                // Independent expansion: opening one zone does NOT close the others.
                if (newExpanded) _expandedZoneIds.Add(zone.id);
                else _expandedZoneIds.Remove(zone.id);
            }

            var roleStyle = new GUIStyle(EditorStyles.miniLabel);
            if (!zone.bakeEnabled) roleStyle.normal.textColor = Color.gray;
            else if (zone.role == TextZoneRole.OverflowOnly) roleStyle.normal.textColor = Color.yellow;
            else if (zone.role == TextZoneRole.PageNumber) roleStyle.normal.textColor = new Color(1f, 0.6f, 0.15f);
            else if (zone.role == TextZoneRole.PageSlot) roleStyle.normal.textColor = new Color(0.4f, 0.75f, 1f);
            else roleStyle.normal.textColor = Color.cyan;
            GUILayout.Label(zone.role.ToString(), roleStyle, GUILayout.Width(90));

            bool newBakeEnabled = GUILayout.Toggle(zone.bakeEnabled, BakeToggleContent,
                GUILayout.Width(18));
            if (newBakeEnabled != zone.bakeEnabled)
            {
                Undo.RecordObject(surface, "Toggle Zone Bake");
                surface.SetZoneBakeEnabled(zone, newBakeEnabled);
                EditorUtility.SetDirty(surface);
                PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
            }

            if (GUILayout.Button(DuplicateZoneContent(), IconButtonStyle, GUILayout.Width(22), GUILayout.Height(18)))
            {
                Undo.RecordObject(surface, "Duplicate Zone");
                TextZone copy = surface.DuplicateZone(zone);
                if (copy != null) _expandedZoneIds.Add(copy.id);
                EditorUtility.SetDirty(surface);
                PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }

            if (GUILayout.Button("\u00d7", GUILayout.Width(20)))
            {
                Undo.RecordObject(surface, "Remove Zone");
                _expandedZoneIds.Remove(zone.id);
                surface.RemoveZone(zone);
                EditorUtility.SetDirty(surface);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return;
            }
            EditorGUILayout.EndHorizontal();

            Rect headerRect = GUILayoutUtility.GetLastRect();
            string draggedId = DragAndDrop.GetGenericData(ZoneDragDataKey) as string;
            if (!string.IsNullOrEmpty(draggedId) && headerRect.Contains(Event.current.mousePosition))
            {
                int insertAt = Event.current.mousePosition.y > headerRect.center.y ? index + 1 : index;
                Rect marker = new Rect(headerRect.x, insertAt > index ? headerRect.yMax - 1f : headerRect.y,
                    headerRect.width, 2f);
                EditorGUI.DrawRect(marker, new Color(0.25f, 0.7f, 1f));
                if (Event.current.type == EventType.DragUpdated)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Move;
                    Event.current.Use();
                }
                else if (Event.current.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    _pendingZoneMoveFrom = surface.zones.FindIndex(z => z != null && z.id == draggedId);
                    _pendingZoneMoveTo = insertAt;
                    DragAndDrop.SetGenericData(ZoneDragDataKey, null);
                    Event.current.Use();
                }
            }

            if (newExpanded)
            {
                EditorGUI.indentLevel++;

                if (!zone.bakeEnabled)
                    EditorGUILayout.HelpBox(
                        "Bake is off. This zone stays in the list but is removed from the next bake queue.",
                        MessageType.None);

                // Draw into temps; commit through Undo.RecordObject only when something changed.
                EditorGUI.BeginChangeCheck();

                string newDisplayName = EditorGUILayout.TextField("Name", zone.displayName);
                var newTargetRenderer = (Renderer)EditorGUILayout.ObjectField(
                    new GUIContent("Target Renderer",
                        "Optional. The renderer this zone bakes onto (e.g. a child book page). " +
                        "Leave empty to use this object's own renderer. Supports MeshRenderer and SkinnedMeshRenderer."),
                    zone.targetRenderer, typeof(Renderer), true);
                int newMaterialIndex = EditorGUILayout.IntField(
                    new GUIContent("Material Index",
                        "Which material slot / texture this zone bakes into on the target renderer. " +
                        "Same renderer+index = shared texture; different = separate textures."),
                    zone.materialIndex);
                var newRole = (TextZoneRole)EditorGUILayout.EnumPopup("Role", zone.role);
                Color newPreviewColor = zone.previewColor;
                if (newRole != zone.role && newPreviewColor == TextZone.DefaultPreviewColor(zone.role))
                    newPreviewColor = TextZone.DefaultPreviewColor(newRole); // keep default in sync with role
                newPreviewColor = EditorGUILayout.ColorField("Preview Color", newPreviewColor);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("UV Rect", EditorStyles.miniBoldLabel);
                Rect newUvRect = EditorGUILayout.RectField("UV Rect", zone.uvRect);
                FieldResetMenu(surface, "UV Rect", () => zone.uvRect = new Rect(0.1f, 0.1f, 0.8f, 0.35f));
                float newRotation = EditorGUILayout.Slider("Rotation (\u00b0)", zone.rotation, -180f, 180f);
                FieldResetMenu(surface, "Rotation", () => zone.rotation = 0f);
                bool newFlipH = EditorGUILayout.Toggle("Flip Horizontal", zone.flipHorizontal);
                FieldResetMenu(surface, "Flip Horizontal", () => zone.flipHorizontal = false);
                bool newFlipV = EditorGUILayout.Toggle("Flip Vertical", zone.flipVertical);
                FieldResetMenu(surface, "Flip Vertical", () => zone.flipVertical = false);
                float newPadding = EditorGUILayout.Slider("Padding", zone.padding, 0f, 0.1f);
                FieldResetMenu(surface, "Padding", () => zone.padding = 0.01f);

                bool isImageZone = newRole == TextZoneRole.Image;

                // Image zone fields (role == Image) — text sections are hidden.
                Texture2D newImageTexture = zone.imageTexture;
                Sprite newImageSprite = zone.imageSprite;
                Color newImageTint = zone.imageTint;
                bool newImagePreserveAspect = zone.imagePreserveAspect;
                if (isImageZone)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Image", EditorStyles.miniBoldLabel);
                    newImageSprite = (Sprite)EditorGUILayout.ObjectField(
                        new GUIContent("Sprite", "Takes priority over Image Texture. Atlas " +
                            "sub-rects are honored (Rectangle packing)."),
                        zone.imageSprite, typeof(Sprite), false);
                    newImageTexture = (Texture2D)EditorGUILayout.ObjectField(
                        new GUIContent("Image Texture", "Used when no Sprite is assigned."),
                        zone.imageTexture, typeof(Texture2D), false);
                    newImageTint = EditorGUILayout.ColorField("Tint", zone.imageTint);
                    newImagePreserveAspect = EditorGUILayout.Toggle(
                        new GUIContent("Preserve Aspect", "Fit inside the zone; off = stretch."),
                        zone.imagePreserveAspect);
                    if (newImageSprite == null && newImageTexture == null)
                        EditorGUILayout.HelpBox("No sprite or texture assigned — this zone will bake nothing.", MessageType.Warning);
                    if (newImageSprite != null && newImageSprite.packed &&
                        newImageSprite.packingMode == SpritePackingMode.Tight)
                        EditorGUILayout.HelpBox("Tight-packed sprite: the full bounding rect will be " +
                            "baked (may include neighboring atlas pixels). Use Rectangle packing.", MessageType.Warning);
                }

                string newLocalizationKey = zone.localizationKey;
                MeshTextAsset newTextAsset = zone.textAsset;
                TMP_FontAsset newFontOverride = zone.fontOverride;
                FontSizeMode newFontSizeMode = zone.fontSizeMode;
                float newFontSize = zone.fontSize;
                float newFontSizeMin = zone.fontSizeMin;
                float newFontSizeMax = zone.fontSizeMax;
                float newLineSpacing = zone.lineSpacing;
                TextAlignmentOptions newAlignment = zone.alignment;
                bool newUseDefaultTextColor = zone.useDefaultTextColor;
                Color newTextColor = zone.textColor;
                MeshTextBlendMode newBlendMode = zone.blendMode;
                float newOpacity = zone.opacity;
                bool newRichTextEnabled = zone.richTextEnabled;
                bool newMarkdownEnabled = zone.markdownEnabled;
                TextZoneOverflow newOverflowBehavior = zone.overflowBehavior;

                bool isPageNumber = newRole == TextZoneRole.PageNumber;
                bool newOverridePn = zone.overridePageNumberStyle;

                if (isPageNumber)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.HelpBox(
                        "Child of a PageSlot. Position freely in UV. Digit is filled by BookController at bake. " +
                        "Style comes from surface Page Number Defaults unless Override is on.",
                        MessageType.Info);
                    newOverridePn = EditorGUILayout.Toggle(
                        new GUIContent("Override Style", "Use this zone's font/size/color instead of Page Number Defaults."),
                        zone.overridePageNumberStyle);
                    if (!string.IsNullOrEmpty(zone.parentZoneId))
                    {
                        var parent = surface.FindZoneById(zone.parentZoneId);
                        EditorGUILayout.LabelField("Parent PageSlot",
                            parent != null ? parent.displayName : zone.parentZoneId);
                    }

                    if (newOverridePn)
                    {
                        EditorGUILayout.Space(2);
                        EditorGUILayout.LabelField("Font & Layout (override)", EditorStyles.miniBoldLabel);
                        newFontOverride = (TMP_FontAsset)EditorGUILayout.ObjectField("Font Override", zone.fontOverride, typeof(TMP_FontAsset), false);
                        newFontSizeMode = (FontSizeMode)EditorGUILayout.EnumPopup("Font Size Mode", zone.fontSizeMode);
                        newFontSize = EditorGUILayout.FloatField("Font Size", zone.fontSize);
                        newAlignment = DrawZoneAlignmentField(index, "Alignment", zone.alignment);
                        newUseDefaultTextColor = EditorGUILayout.Toggle("Use Default Text Color", zone.useDefaultTextColor);
                        using (new EditorGUI.DisabledScope(newUseDefaultTextColor))
                            newTextColor = EditorGUILayout.ColorField("Text Color", newTextColor);
                        newPadding = EditorGUILayout.Slider("Padding", zone.padding, 0f, 0.1f);
                    }
                }

                if (!isImageZone && !isPageNumber)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Text Source", EditorStyles.miniBoldLabel);
                    newLocalizationKey = EditorGUILayout.TextField(
                        new GUIContent("Localization Key",
                            "Resolved through the Localization Provider. Takes priority over Text Asset."),
                        zone.localizationKey);
                    newTextAsset = (MeshTextAsset)EditorGUILayout.ObjectField("Text Asset", zone.textAsset, typeof(MeshTextAsset), false);

                    if (newTextAsset != null)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(EditorGUI.indentLevel * 15f);
                        if (GUILayout.Button(new GUIContent("\u270e Edit Text\u2026",
                            "Open this Text Asset in the Text Editor window."), GUILayout.Height(20)))
                            MeshTextAssetEditorWindow.ShowWindow(newTextAsset);
                        EditorGUILayout.EndHorizontal();

                        if (!string.IsNullOrEmpty(locale))
                        {
                            string previewKey = !string.IsNullOrWhiteSpace(newLocalizationKey)
                                ? newLocalizationKey : newTextAsset.key;
                            LocalizedTextResult previewResult = surface.ResolveLocalizedText(
                                previewKey, newTextAsset, locale);
                            string previewText = previewResult.found ? previewResult.text : "";
                            if (!string.IsNullOrEmpty(previewText))
                            {
                                EditorGUILayout.Space(2);
                                EditorGUILayout.LabelField(
                                    $"Preview ({locale}, {previewResult.source}, {previewResult.resolvedLocale}):",
                                    EditorStyles.miniLabel);
                                // Read-only: edits here silently went nowhere before. Use
                                // the Edit Text… button to change the asset.
                                EditorGUILayout.SelectableLabel(
                                    previewText.Length > 500 ? previewText.Substring(0, 500) + "\u2026" : previewText,
                                    EditorStyles.textArea, GUILayout.MaxHeight(60));
                            }
                        }

                        EditorGUILayout.HelpBox(
                            $"TextAsset authoring: Rich Text {(newTextAsset.richTextEnabled ? "On" : "Off")} · " +
                            $"Markdown {(newTextAsset.markdownEnabled ? "On" : "Off")}",
                            MessageType.None);
                        bool effectiveMarkdown = newRole == TextZoneRole.PageSlot
                            ? (_bookMarkdownProp != null && _bookMarkdownProp.boolValue)
                            : newMarkdownEnabled;
                        if (newTextAsset.richTextEnabled && !newRichTextEnabled)
                            EditorGUILayout.HelpBox("This TextAsset expects Rich Text, but the zone's Rich Text checkbox is off.",
                                MessageType.Warning);
                        if (newTextAsset.markdownEnabled && !effectiveMarkdown)
                            EditorGUILayout.HelpBox(newRole == TextZoneRole.PageSlot
                                    ? "This TextAsset expects Markdown, but Book Markdown is off."
                                    : "This TextAsset expects Markdown, but the zone's Markdown checkbox is off.",
                                MessageType.Warning);
                        if (newTextAsset.markdownEnabled && !newRichTextEnabled)
                            EditorGUILayout.HelpBox("Markdown converts to TMP tags; enable Rich Text on this zone as well.",
                                MessageType.Warning);
                        if (!newTextAsset.richTextEnabled && newTextAsset.ContainsRichTextMarkup())
                            EditorGUILayout.HelpBox("Rich-text tags were detected although TextAsset Rich Text authoring is off.",
                                MessageType.Warning);
                        if (!newTextAsset.markdownEnabled && newTextAsset.ContainsMarkdownMarkup())
                            EditorGUILayout.HelpBox("Markdown markers were detected although TextAsset Markdown authoring is off.",
                                MessageType.Warning);
                    }

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Font & Layout", EditorStyles.miniBoldLabel);
                    newFontOverride = (TMP_FontAsset)EditorGUILayout.ObjectField("Font Override", zone.fontOverride, typeof(TMP_FontAsset), false);
                    newFontSizeMode = (FontSizeMode)EditorGUILayout.EnumPopup("Font Size Mode", zone.fontSizeMode);
                    string sizeLabel = newFontSizeMode == FontSizeMode.PercentOfZoneHeight
                        ? "Font Size (% of zone height)"
                        : "Font Size (pts @1024)";
                    newFontSize = EditorGUILayout.FloatField(sizeLabel, zone.fontSize);
                    FieldResetMenu(surface, "Font Size", () => zone.fontSize = 0f);
                    newLineSpacing = EditorGUILayout.FloatField(
                        new GUIContent("Line Spacing (px)",
                            "Extra space between lines in bake-target pixels. 0 = font default; " +
                            "positive loosens, negative tightens. Pagination and overflow adapt automatically."),
                        zone.lineSpacing);
                    FieldResetMenu(surface, "Line Spacing", () => zone.lineSpacing = 0f);
                    if (newFontSize <= 0f)
                        EditorGUILayout.HelpBox("Font Size = 0 uses Default Font Size and Default Font Size Mode.",
                            MessageType.None);
                    if (zone.overflowBehavior == TextZoneOverflow.ShrinkToFit)
                    {
                        newFontSizeMin = EditorGUILayout.FloatField("Min Font Size (ShrinkToFit)", zone.fontSizeMin);
                        newFontSizeMax = EditorGUILayout.FloatField("Max Font Size (ShrinkToFit)", zone.fontSizeMax);
                    }
                    newAlignment = DrawZoneAlignmentField(index, "Alignment", zone.alignment);
                    FieldResetMenu(surface, "Alignment", () => zone.alignment = TextAlignmentOptions.TopLeft);
                    newUseDefaultTextColor = EditorGUILayout.Toggle("Use Default Text Color", zone.useDefaultTextColor);
                    using (new EditorGUI.DisabledScope(newUseDefaultTextColor))
                        newTextColor = EditorGUILayout.ColorField("Text Color", newTextColor);

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Text Processing", EditorStyles.miniBoldLabel);
                    newRichTextEnabled = EditorGUILayout.Toggle("Rich Text", zone.richTextEnabled);
                    newMarkdownEnabled = EditorGUILayout.Toggle("Markdown", zone.markdownEnabled);
                    if (zone.role == TextZoneRole.PageSlot && newMarkdownEnabled)
                        EditorGUILayout.HelpBox(
                            "PageSlot zones ignore per-zone Markdown: book text is converted once " +
                            "before pagination (see 'Book Markdown' on the surface).", MessageType.Info);
                }

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Albedo Compositing", EditorStyles.miniBoldLabel);
                newBlendMode = (MeshTextBlendMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Blend Mode", "Whole-zone Photoshop-style blend. Rich text can " +
                        "override selected content with <blend=multiply|screen|overlay|add|subtract>."),
                    zone.blendMode);
                newOpacity = EditorGUILayout.Slider(
                    new GUIContent("Opacity", "Whole-zone opacity. Inline override: " +
                        "<opacity=0.5>...</opacity> or <image=... opacity=0.5>."),
                    zone.opacity, 0f, 1f);

                // PBR (both text and image zones): emission, mask and height,
                // all behind one master switch.
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("PBR", EditorStyles.miniBoldLabel);
                bool newUsePbr = zone.usePbr;
                if (zone.role == TextZoneRole.PageSlot)
                {
                    EditorGUILayout.HelpBox("PageSlot zones follow the surface-wide " +
                        "'Book Use PBR' toggle (Book Settings section above).", MessageType.None);
                }
                else
                {
                    newUsePbr = EditorGUILayout.Toggle(
                        new GUIContent("Use PBR", "Master switch: emission, surface mask and " +
                            "the <emission>/<surface> text tags."),
                        zone.usePbr);
                }

                bool newBakeEmission = zone.bakeEmission;
                Color newEmissionColor = zone.emissionColor;
                bool newBakePbrMask = zone.bakePbrMask;
                float newMaskMetallic = zone.maskMetallic;
                float newMaskSmoothness = zone.maskSmoothness;
                bool newBakeHeight = zone.bakeHeight;
                float newHeightValue = zone.heightValue;
                float newHeightAmplitude = zone.heightAmplitude;

                if (newUsePbr)
                {
                    EditorGUI.indentLevel++;

                    newBakeEmission = EditorGUILayout.Toggle(
                        new GUIContent("Emission (whole zone)", "Bake the entire zone into the " +
                            "emission map. For single words use <emission> tags instead."),
                        zone.bakeEmission);
                    newEmissionColor = EditorGUILayout.ColorField(
                        new GUIContent("Emission Color", "Used by whole-zone emission AND " +
                            "<emission> tags without their own color. HDR intensity above 1 raises " +
                            "the material's emissive intensity (HDRP nits / URP multiplier)."),
                        zone.emissionColor, true, false, true);
                    FieldResetMenu(surface, "Emission Color", () => zone.emissionColor = Color.white);

                    newBakePbrMask = EditorGUILayout.Toggle(
                        new GUIContent("PBR Mask (whole zone)", "Bake the entire zone into the " +
                            "metallic/smoothness map. For single words use <surface> tags instead."),
                        zone.bakePbrMask);
                    newMaskMetallic = EditorGUILayout.Slider(
                        new GUIContent("Metallic", "Applies to whole-zone mask AND <surface> words."),
                        zone.maskMetallic, 0f, 1f);
                    FieldResetMenu(surface, "Metallic", () => zone.maskMetallic = 1f);
                    newMaskSmoothness = EditorGUILayout.Slider(
                        new GUIContent("Smoothness", "Applies to whole-zone mask AND <surface> words."),
                        zone.maskSmoothness, 0f, 1f);
                    FieldResetMenu(surface, "Smoothness", () => zone.maskSmoothness = 0.85f);

                    newBakeHeight = EditorGUILayout.Toggle(
                        new GUIContent("Height (whole zone)", "Bake signed height/parallax for the " +
                            "whole zone. For selected words/images use <surface h=...>."),
                        zone.bakeHeight);
                    newHeightValue = EditorGUILayout.Slider(
                        new GUIContent("Height Value", "-1 = engraved, 0 = neutral, +1 = raised."),
                        zone.heightValue, -1f, 1f);
                    newHeightAmplitude = EditorGUILayout.Slider(
                        new GUIContent("Height Amplitude", "Maximum relief/parallax strength. For thin " +
                            "URP/Built-in text start at 0.001-0.005; high values shift albedo outside " +
                            "glyph strokes. HDRP uses world units."),
                        zone.heightAmplitude, 0.0001f, 0.08f);

                    if (!isImageZone)
                    {
                        if (zone.richTextEnabled)
                            EditorGUILayout.HelpBox(
                                "Per-word tags: <emission>word</emission>, " +
                                "<emission=#00ffcc i=2>word</emission>, " +
                                "<surface m=1 s=0.9 h=1>word</surface> — work even with the " +
                                "whole-zone toggles off. Bare <surface> is intentionally neutral.",
                                MessageType.None);
                        else
                            EditorGUILayout.HelpBox(
                                "Enable Rich Text to use <emission>/<surface> tags in this zone's text.",
                                MessageType.Info);
                    }

                    EditorGUI.indentLevel--;
                }

                if (!isImageZone)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Overflow", EditorStyles.miniBoldLabel);
                    newOverflowBehavior = (TextZoneOverflow)EditorGUILayout.EnumPopup("Overflow Behavior", zone.overflowBehavior);
                }

                string newContinueToZoneId = zone.continueToZoneId;
                if (newOverflowBehavior == TextZoneOverflow.ContinueToSubZones)
                {
                    EditorGUILayout.LabelField("Overflow \u2192 Continue To", EditorStyles.miniBoldLabel);
                    EditorGUI.indentLevel++;

                    // Single continuation target chosen by name from existing zones.
                    var candidates = new List<TextZone>();
                    var names = new List<string> { "(none)" };
                    int currentIdx = 0;
                    foreach (var oz in surface.zones)
                    {
                        if (oz.id == zone.id) continue;
                        candidates.Add(oz);
                        names.Add(oz.displayName);
                        if (oz.id == zone.continueToZoneId) currentIdx = candidates.Count; // +1 offset for "(none)"
                    }

                    int picked = EditorGUILayout.Popup("Continue To Zone", currentIdx, names.ToArray());
                    newContinueToZoneId = (picked <= 0) ? "" : candidates[picked - 1].id;

                    if (!string.IsNullOrEmpty(newContinueToZoneId) && surface.FindZoneById(newContinueToZoneId) == null)
                        EditorGUILayout.HelpBox("Continuation zone is missing.", MessageType.Warning);

                    EditorGUI.indentLevel--;
                }

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(surface, "Edit Zone");
                    zone.displayName = newDisplayName;
                    zone.orderIndex = index;
                    zone.targetRenderer = newTargetRenderer;
                    zone.materialIndex = newMaterialIndex;
                    if (zone.role != newRole) surface.MarkZonesChanged(); // page-slot cache
                    zone.role = newRole;
                    zone.previewColor = newPreviewColor;
                    zone.uvRect = newUvRect;
                    zone.rotation = newRotation;
                    zone.flipHorizontal = newFlipH;
                    zone.flipVertical = newFlipV;
                    zone.padding = newPadding;
                    zone.localizationKey = newLocalizationKey;
                    zone.textAsset = newTextAsset;
                    zone.fontOverride = newFontOverride;
                    zone.fontSizeMode = newFontSizeMode;
                    zone.fontSize = newFontSize;
                    zone.lineSpacing = newLineSpacing;
                    zone.fontSizeMin = newFontSizeMin;
                    zone.fontSizeMax = newFontSizeMax;
                    zone.alignment = newAlignment;
                    zone.useDefaultTextColor = newUseDefaultTextColor;
                    zone.textColor = newTextColor;
                    zone.blendMode = newBlendMode;
                    zone.opacity = newOpacity;
                    zone.richTextEnabled = newRichTextEnabled;
                    zone.markdownEnabled = newMarkdownEnabled;
                    zone.overflowBehavior = newOverflowBehavior;
                    zone.continueToZoneId = newContinueToZoneId;
                    zone.imageTexture = newImageTexture;
                    zone.imageSprite = newImageSprite;
                    zone.imageTint = newImageTint;
                    zone.imagePreserveAspect = newImagePreserveAspect;
                    zone.usePbr = newUsePbr;
                    if (zone.role == TextZoneRole.PageNumber)
                        zone.overridePageNumberStyle = newOverridePn;
                    zone.bakeEmission = newBakeEmission;
                    zone.emissionColor = newEmissionColor;
                    zone.bakePbrMask = newBakePbrMask;
                    zone.maskMetallic = newMaskMetallic;
                    zone.maskSmoothness = newMaskSmoothness;
                    zone.bakeHeight = newBakeHeight;
                    zone.heightValue = newHeightValue;
                    zone.heightAmplitude = newHeightAmplitude;
                    EditorUtility.SetDirty(surface);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
                }

                if (zone.overflowBehavior == TextZoneOverflow.ContinueToSubZones)
                {
                    if (GUILayout.Button("✚ Create Sub-Zone (copy style)"))
                    {
                        Undo.RecordObject(surface, "Create Sub-Zone");
                        surface.CreateSubZone(zone);
                        EditorUtility.SetDirty(surface);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
                    }
                }

                if (zone.role == TextZoneRole.PageSlot)
                {
                    if (GUILayout.Button(new GUIContent("✚ Add Page Number Zone",
                        "Creates a small movable PageNumber zone on the same renderer/material. " +
                        "Position it freely in the UV Editor; BookController fills the digit when baking.")))
                    {
                        Undo.RecordObject(surface, "Create Page Number Zone");
                        surface.CreatePageNumberZone(zone);
                        EditorUtility.SetDirty(surface);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
                    }
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawStatus(MeshTextSurface surface)
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

            if (surface.currentBakedLocale != null)
                EditorGUILayout.HelpBox($"Currently baked for locale: \"{surface.currentBakedLocale}\"", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Not baked yet.", MessageType.Info);

            if (surface.zones.Count == 0)
                EditorGUILayout.HelpBox("No zones defined. Use Placement Mode or Add Zone to create text zones.", MessageType.Warning);

            // Overflow chain order: continuation zones must sort after their parent (Core walks by orderIndex).
            foreach (var z in surface.zones)
            {
                if (z.overflowBehavior != TextZoneOverflow.ContinueToSubZones) continue;
                if (string.IsNullOrEmpty(z.continueToZoneId)) continue;
                var child = surface.FindZoneById(z.continueToZoneId);
                if (child == null) continue;
                if (child.orderIndex <= z.orderIndex)
                {
                    EditorGUILayout.HelpBox(
                        $"Overflow link '{z.displayName}' → '{child.displayName}': child Order ({child.orderIndex}) " +
                        $"must be greater than parent Order ({z.orderIndex}), or the overflow text will be dropped.",
                        MessageType.Warning);
                }
            }

            // Duplicate id detection (can happen after copy-pasting zones in the inspector).
            var seenIds = new HashSet<string>();
            bool hasDuplicates = false;
            foreach (var z in surface.zones)
                if (!seenIds.Add(z.id)) { hasDuplicates = true; break; }
            if (hasDuplicates)
            {
                EditorGUILayout.HelpBox("Duplicate zone IDs detected (copy-pasted zones?). " +
                    "Overflow links and book baking may break.", MessageType.Error);
                if (GUILayout.Button("Fix Duplicate IDs"))
                {
                    Undo.RecordObject(surface, "Fix Duplicate Zone IDs");
                    seenIds.Clear();
                    foreach (var z in surface.zones)
                        if (!seenIds.Add(z.id)) { z.RegenerateId(); seenIds.Add(z.id); }
                    EditorUtility.SetDirty(surface);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(surface);
                }
            }

            foreach (var zone in surface.zones)
            {
                if (zone == null || !zone.bakeEnabled) continue;
                if (zone.role == TextZoneRole.Main && zone.textAsset == null && string.IsNullOrEmpty(zone.localizationKey))
                    EditorGUILayout.HelpBox($"Zone \"{zone.displayName}\" has no text source assigned.", MessageType.Warning);
                if (zone.role == TextZoneRole.Image && zone.GetImageTexture() == null)
                    EditorGUILayout.HelpBox($"Image zone \"{zone.displayName}\" has no texture assigned.", MessageType.Warning);
            }

            if (surface.bakeSettings.defaultFont == null)
            {
                bool anyZoneWithoutFont = false;
                foreach (var z in surface.zones) if (z.fontOverride == null) { anyZoneWithoutFont = true; break; }
                if (anyZoneWithoutFont)
                    EditorGUILayout.HelpBox("No default font set in Bake Settings. Assign a TMP font asset.", MessageType.Warning);
            }

        }

        private void DrawLocalizationProviderStatus(MeshTextSurface surface)
        {
            MonoBehaviour assigned =
                _localizationProviderProp.objectReferenceValue as MonoBehaviour;
            bool assignedValid = assigned is ITextLocalizationProvider;
            ITextLocalizationProvider effective = surface.localizationProvider;

            if (assigned != null && !assignedValid)
                EditorGUILayout.HelpBox("Assigned component does not implement " +
                    "ITextLocalizationProvider — it is ignored.", MessageType.Warning);

            if (assignedValid)
            {
                EditorGUILayout.HelpBox("Using explicit provider: " +
                    DescribeProvider(assigned) + ".", MessageType.None);
            }
            else if (effective is DefaultTextLocalizationProvider)
            {
                bool autoOff = _ignoreSceneDefaultProviderProp != null &&
                               _ignoreSceneDefaultProviderProp.boolValue;
                EditorGUILayout.HelpBox(autoOff
                        ? "Automatic discovery is off: using embedded MeshTextAsset text."
                        : "No provider in the scene: using embedded MeshTextAsset text. Add one " +
                          "External Override Localization Provider anywhere in the scene to enable " +
                          "key overrides automatically (no per-surface wiring).",
                    MessageType.None);
            }
            else
            {
                bool isSceneDefault =
                    ReferenceEquals(effective, MeshTextLocalizationRuntime.DefaultProvider);
                EditorGUILayout.HelpBox("Using " +
                    (isSceneDefault ? "scene default: " : "auto-detected scene provider: ") +
                    DescribeProvider(effective as MonoBehaviour) + ".", MessageType.None);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Refresh Auto Provider",
                    "Re-detect the scene provider (automatic mode only; explicit assignment wins)."),
                GUILayout.Height(22)))
            {
                surface.RefreshLocalizationProvider();
                Repaint();
            }
            using (new EditorGUI.DisabledScope(!(effective is MonoBehaviour) || assignedValid))
            {
                if (GUILayout.Button(new GUIContent("Pin to Field",
                        "Write the auto-detected provider into the field above (explicit assignment)."),
                    GUILayout.Height(22)))
                {
                    Undo.RecordObject(surface, "Pin Localization Provider");
                    _localizationProviderProp.objectReferenceValue = effective as MonoBehaviour;
                    serializedObject.ApplyModifiedProperties();
                }
            }
            if (assigned != null)
            {
                if (GUILayout.Button(new GUIContent("Clear",
                        "Remove the explicit assignment (back to automatic)."),
                    GUILayout.Height(22)))
                {
                    Undo.RecordObject(surface, "Clear Localization Provider");
                    _localizationProviderProp.objectReferenceValue = null;
                    serializedObject.ApplyModifiedProperties();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string DescribeProvider(MonoBehaviour provider)
        {
            if (provider == null) return "(missing)";
            return provider.GetType().Name + " on '" + provider.gameObject.name + "'";
        }

        private static readonly GUIContent BakeToggleContent = new GUIContent("",
            "Include this zone in the next bake. Uncheck to remove it from the bake queue. " +
            "The zone stays in the list.");

        private static GUIStyle _iconButtonStyle;
        private static GUIContent _duplicateContent;
        private static bool _duplicateContentProSkin;

        private static GUIStyle IconButtonStyle
        {
            get
            {
                if (_iconButtonStyle == null)
                {
                    _iconButtonStyle = new GUIStyle(EditorStyles.miniButton)
                    {
                        padding = new RectOffset(2, 2, 2, 2),
                        alignment = TextAnchor.MiddleCenter
                    };
                }
                return _iconButtonStyle;
            }
        }

        /// <summary>
        /// Unity's built-in duplicate icon (two overlapping pages). Falls back to a glyph
        /// only if that icon is missing from this editor build.
        /// </summary>
        private static GUIContent DuplicateZoneContent()
        {
            if (_duplicateContent != null && _duplicateContentProSkin == EditorGUIUtility.isProSkin)
                return _duplicateContent;

            _duplicateContentProSkin = EditorGUIUtility.isProSkin;
            string iconName = _duplicateContentProSkin ? "d_TreeEditor.Duplicate" : "TreeEditor.Duplicate";
            GUIContent icon = EditorGUIUtility.IconContent(iconName);
            if (icon == null || icon.image == null)
                icon = EditorGUIUtility.IconContent("TreeEditor.Duplicate");

            _duplicateContent = icon != null && icon.image != null
                ? new GUIContent(icon.image, "Duplicate this zone")
                : new GUIContent("\u29c9", "Duplicate this zone");
            return _duplicateContent;
        }

        private void OnSceneGUI()
        {
            var surface = (MeshTextSurface)target;
            if (surface == null) return;

            for (int i = 0; i < surface.zones.Count; i++)
            {
                var zone = surface.zones[i];
                Mesh mesh = surface.GetZoneMesh(zone);
                if (mesh == null) continue;
                var r = surface.GetZoneRenderer(zone);
                Transform t = r != null ? r.transform : surface.transform;
                ZoneHandles.DrawZoneOverlay(zone, mesh, t, false);
            }
        }
    }
}
