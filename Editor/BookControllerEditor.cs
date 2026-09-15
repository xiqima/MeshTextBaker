// Mesh Text Baker — BookControllerEditor.cs
// Custom inspector for BookController to hide/show fields dynamically depending on modes,
// simplifying the interface and avoiding clutter for the user.

using UnityEditor;
using UnityEngine;
using MeshTextBaker;

namespace MeshTextBaker.Editor
{
    [CustomEditor(typeof(BookController))]
    public class BookControllerEditor : UnityEditor.Editor
    {
        private SerializedProperty _surface;
        private SerializedProperty _animator;
        private SerializedProperty _stackL;
        private SerializedProperty _stackR;
        private SerializedProperty _flipSheet;
        private SerializedProperty _flipPivot;

        private SerializedProperty _openTrigger;
        private SerializedProperty _closeTrigger;
        private SerializedProperty _openCloseLayer;

        private SerializedProperty _flipMode;
        private SerializedProperty _flipStateName;
        private SerializedProperty _flipLayer;

        private SerializedProperty _dynamicStackThickness;
        private SerializedProperty _thicknessMode;
        private SerializedProperty _thicknessAxis;
        private SerializedProperty _thicknessBlendShape;
        private SerializedProperty _invertThicknessShape;
        private SerializedProperty _useFlipLiftShapes;
        private SerializedProperty _flipLiftShapeR;
        private SerializedProperty _flipLiftShapeL;
        private SerializedProperty _flipLiftScaleR;
        private SerializedProperty _flipLiftScaleL;
        private SerializedProperty _flipLiftTakeoffBias;
        private SerializedProperty _flipLiftLandingBias;
        private SerializedProperty _flipLiftMaxWeight;
        private SerializedProperty _minStackScale;
        private SerializedProperty _compensateEdgeTiling;
        private SerializedProperty _edgeMaterialIndexL;
        private SerializedProperty _edgeMaterialIndexR;
        private SerializedProperty _edgeUvAxis;

        private SerializedProperty _sleepWhileClosed;
        private SerializedProperty _firstPageOnRight;
        private SerializedProperty _autoHideEmptyStacks;
        private SerializedProperty _openAt;
        private SerializedProperty _bookmarkJumpMode;
        private SerializedProperty _bookmarkMultiFlipDuration;
        private SerializedProperty _locale;
        private SerializedProperty _turnDuration;
        private SerializedProperty _turnCurve;
        private SerializedProperty _openDuration;

        private void OnEnable()
        {
            _surface = serializedObject.FindProperty("surface");
            _animator = serializedObject.FindProperty("animator");
            _stackL = serializedObject.FindProperty("stackL");
            _stackR = serializedObject.FindProperty("stackR");
            _flipSheet = serializedObject.FindProperty("flipSheet");
            _flipPivot = serializedObject.FindProperty("flipPivot");

            _openTrigger = serializedObject.FindProperty("openTrigger");
            _closeTrigger = serializedObject.FindProperty("closeTrigger");
            _openCloseLayer = serializedObject.FindProperty("openCloseLayer");

            _flipMode = serializedObject.FindProperty("flipMode");
            _flipStateName = serializedObject.FindProperty("flipStateName");
            _flipLayer = serializedObject.FindProperty("flipLayer");

            _dynamicStackThickness = serializedObject.FindProperty("dynamicStackThickness");
            _thicknessMode = serializedObject.FindProperty("thicknessMode");
            _thicknessAxis = serializedObject.FindProperty("thicknessAxis");
            _thicknessBlendShape = serializedObject.FindProperty("thicknessBlendShape");
            _invertThicknessShape = serializedObject.FindProperty("invertThicknessShape");
            _useFlipLiftShapes = serializedObject.FindProperty("useFlipLiftShapes");
            _flipLiftShapeR = serializedObject.FindProperty("flipLiftShapeR");
            _flipLiftShapeL = serializedObject.FindProperty("flipLiftShapeL");
            _flipLiftScaleR = serializedObject.FindProperty("flipLiftScaleR");
            _flipLiftScaleL = serializedObject.FindProperty("flipLiftScaleL");
            _flipLiftTakeoffBias = serializedObject.FindProperty("flipLiftTakeoffBias");
            _flipLiftLandingBias = serializedObject.FindProperty("flipLiftLandingBias");
            _flipLiftMaxWeight = serializedObject.FindProperty("flipLiftMaxWeight");
            _minStackScale = serializedObject.FindProperty("minStackScale");
            _compensateEdgeTiling = serializedObject.FindProperty("compensateEdgeTiling");
            _edgeMaterialIndexL = serializedObject.FindProperty("edgeMaterialIndexL");
            _edgeMaterialIndexR = serializedObject.FindProperty("edgeMaterialIndexR");
            _edgeUvAxis = serializedObject.FindProperty("edgeUvAxis");

            _sleepWhileClosed = serializedObject.FindProperty("sleepWhileClosed");
            _firstPageOnRight = serializedObject.FindProperty("firstPageOnRight");
            _autoHideEmptyStacks = serializedObject.FindProperty("autoHideEmptyStacks");
            _openAt = serializedObject.FindProperty("openAt");
            _bookmarkJumpMode = serializedObject.FindProperty("bookmarkJumpMode");
            _bookmarkMultiFlipDuration = serializedObject.FindProperty("bookmarkMultiFlipDuration");
            _locale = serializedObject.FindProperty("locale");
            _turnDuration = serializedObject.FindProperty("turnDuration");
            _turnCurve = serializedObject.FindProperty("turnCurve");
            _openDuration = serializedObject.FindProperty("openDuration");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Book Controller", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("This controller manages page layout, thickness, flip mechanics, and animator integration.", MessageType.Info);
            EditorGUILayout.Space(2);

            // ── References ──
            EditorGUILayout.LabelField("References", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_surface);
            EditorGUILayout.PropertyField(_animator);
            EditorGUILayout.PropertyField(_stackL);
            EditorGUILayout.PropertyField(_stackR);
            EditorGUILayout.PropertyField(_flipSheet);
            EditorGUILayout.PropertyField(_flipPivot);
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4);

            // ── Animator integration (Only shown if Animator reference is assigned) ──
            if (_animator.objectReferenceValue != null)
            {
                EditorGUILayout.LabelField("Animator Integration", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_openTrigger, new GUIContent("Open Trigger / State"));
                EditorGUILayout.PropertyField(_closeTrigger, new GUIContent("Close Trigger / State"));
                EditorGUILayout.PropertyField(_openCloseLayer, new GUIContent("Open/Close Layer"));
                EditorGUI.indentLevel--;
                EditorGUILayout.Space(4);
            }

            // ── Page flip ──
            EditorGUILayout.LabelField("Page Flip Settings", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_flipMode, new GUIContent("Flip Mode"));
            if ((FlipMode)_flipMode.enumValueIndex == FlipMode.Animator)
            {
                EditorGUILayout.PropertyField(_flipStateName, new GUIContent("Flip State Name"));
                if (_animator.objectReferenceValue != null)
                {
                    EditorGUILayout.PropertyField(_flipLayer, new GUIContent("Page Flip Layer"));
                }
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4);

            // ── Dynamic Stack Thickness ──
            EditorGUILayout.LabelField("Dynamic Stack Thickness", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_dynamicStackThickness, new GUIContent("Enable Dynamic Thickness"));
            if (_dynamicStackThickness.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_thicknessMode, new GUIContent("Thickness Mode"));

                if ((StackThicknessMode)_thicknessMode.enumValueIndex == StackThicknessMode.TransformScale)
                {
                    EditorGUILayout.PropertyField(_thicknessAxis, new GUIContent("Thickness Axis"));
                }
                else if ((StackThicknessMode)_thicknessMode.enumValueIndex == StackThicknessMode.BlendShape)
                {
                    EditorGUILayout.PropertyField(_thicknessBlendShape, new GUIContent("Blend Shape Name"));
                    EditorGUILayout.PropertyField(_invertThicknessShape, new GUIContent("Invert Shape Weight"));
                }

                EditorGUILayout.PropertyField(_minStackScale, new GUIContent("Min Stack Scale", "Minimum thickness multiplier when a stack is empty."));

                // Flip lift shapes (only available in blendshape thickness mode or both, let's keep it under dynamic thickness as in the original class)
                EditorGUILayout.PropertyField(_useFlipLiftShapes, new GUIContent("Use Flip Lift Shapes"));
                if (_useFlipLiftShapes.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_flipLiftShapeR, new GUIContent("Lift Shape R"));
                    EditorGUILayout.PropertyField(_flipLiftShapeL, new GUIContent("Lift Shape L"));
                    EditorGUILayout.PropertyField(_flipLiftScaleR, new GUIContent("Lift Scale R",
                        "Multiply the computed right-side lift weight (1 = Blender authoring)."));
                    EditorGUILayout.PropertyField(_flipLiftScaleL, new GUIContent("Lift Scale L"));
                    EditorGUILayout.PropertyField(_flipLiftTakeoffBias, new GUIContent("Takeoff Bias",
                        "Added to take-off lift fraction before scale. Tweak without re-exporting."));
                    EditorGUILayout.PropertyField(_flipLiftLandingBias, new GUIContent("Landing Bias"));
                    EditorGUILayout.PropertyField(_flipLiftMaxWeight, new GUIContent("Max Weight (0–100)"));
                    EditorGUI.indentLevel--;
                }

                // Compensate edge tiling
                EditorGUILayout.PropertyField(_compensateEdgeTiling, new GUIContent("Compensate Edge Tiling"));
                if (_compensateEdgeTiling.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(_edgeMaterialIndexL, new GUIContent("Edge Slot Left Stack"));
                    EditorGUILayout.PropertyField(_edgeMaterialIndexR, new GUIContent("Edge Slot Right Stack"));
                    EditorGUILayout.PropertyField(_edgeUvAxis, new GUIContent("Edge UV Axis"));
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4);

            // ── Performance & Flow ──
            EditorGUILayout.LabelField("Performance & Flow", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_sleepWhileClosed, new GUIContent("Sleep While Closed", "Frees RenderTextures when the book is closed to save VRAM."));
            EditorGUILayout.PropertyField(_firstPageOnRight, new GUIContent("First Page On Right", "Real books start with page 1 on the right half."));
            EditorGUILayout.PropertyField(_autoHideEmptyStacks, new GUIContent("Auto Hide Empty Stacks",
                "While fully Open, hides a stack renderer with 0 pages. Never during Open/Close or while Closed."));
            EditorGUILayout.PropertyField(_openAt, new GUIContent("Open At",
                "Start = first spread. ResumeLast = the spread from the last Close()."));
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bookmark Jump", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_bookmarkJumpMode, new GUIContent("Mode",
                "Instant / one flip to target / page-by-page multi flip."));
            if (_bookmarkJumpMode != null &&
                _bookmarkJumpMode.enumValueIndex == (int)BookmarkJumpMode.MultiFlip)
            {
                EditorGUILayout.PropertyField(_bookmarkMultiFlipDuration, new GUIContent("Multi Flip Sec/Page",
                    "Turn duration while jumping through many pages (normal Next/Prev use Turn Duration)."));
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Timing & Locale", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_locale, new GUIContent("Current Locale"));
            EditorGUILayout.PropertyField(_turnDuration, new GUIContent("Turn Duration (sec)"));
            EditorGUILayout.PropertyField(_turnCurve, new GUIContent("Turn Curve"));
            EditorGUILayout.PropertyField(_openDuration, new GUIContent("Open/Close Duration (sec)"));
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4);

            serializedObject.ApplyModifiedProperties();

            // ── Play Mode controls ──
            if (UnityEngine.Application.isPlaying)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Play Mode", EditorStyles.boldLabel);
                var book = (BookController)target;
                EditorGUILayout.LabelField("State", book.state.ToString());
                EditorGUILayout.LabelField("Spread", book.spread.ToString());
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(book.state != BookState.Closed))
                    if (GUILayout.Button("Open")) book.Open();
                using (new EditorGUI.DisabledScope(book.state != BookState.Open))
                    if (GUILayout.Button("Close")) book.Close();
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(book.isBusy || book.state != BookState.Open))
                {
                    if (GUILayout.Button("◀ Prev")) book.PreviousPage();
                    if (GUILayout.Button("Next ▶")) book.NextPage();
                }
                EditorGUILayout.EndHorizontal();
                if (GUILayout.Button("Invalidate Zone Bindings"))
                    book.InvalidateZoneBindings();

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Bookmarks", EditorStyles.boldLabel);
                var ids = book.GetBookmarkIds();
                if (ids == null || ids.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No <bookmark=id> tags in the current book text. " +
                        "Insert e.g. <bookmark=chapter2> in the MeshTextAsset.",
                        MessageType.None);
                }
                else
                {
                    foreach (var id in ids)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(id, GUILayout.MinWidth(80));
                        EditorGUILayout.LabelField(
                            $"page {book.GetBookmarkPage(id)} / spread {book.GetBookmarkSpread(id)}",
                            EditorStyles.miniLabel);
                        if (GUILayout.Button("Go", GUILayout.Width(40)))
                            book.GoToBookmark(id);
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
        }
    }
}
