using MeshTextBaker;
using UnityEditor;
using UnityEngine;

namespace MeshTextBaker.Editor
{
    [CustomEditor(typeof(ExternalOverrideLocalizationProvider))]
    public class ExternalOverrideLocalizationProviderEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty baseProvider = serializedObject.FindProperty("baseProviderBehaviour");
            EditorGUILayout.PropertyField(baseProvider, new GUIContent("Base Provider",
                "Unity Localization adapter, I2 bridge, or any ITextLocalizationProvider. " +
                "External files override this provider only for MeshTextBaker consumers."));
            if (baseProvider.objectReferenceValue != null &&
                !(baseProvider.objectReferenceValue is ITextLocalizationProvider))
                EditorGUILayout.HelpBox("Assigned component does not implement ITextLocalizationProvider.", MessageType.Error);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("currentLocale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("fallbackLocale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("localizationRootOverride"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("initializeOnAwake"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("defaultPriorityOffset"),
                new GUIContent("Default Priority Offset",
                    "Added to this decorator's built-in priority (100). Higher wins when " +
                    "several providers register as scene default."));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "MeshTextBaker external overlay: <Game executable>/Localization/<locale>/...\n" +
                "It decorates the assigned provider; it is not a replacement localization system for UI/dialogues.",
                MessageType.Info);

            var provider = (ExternalOverrideLocalizationProvider)target;
            bool isSceneDefault =
                ReferenceEquals(MeshTextLocalizationRuntime.DefaultProvider, provider);
            EditorGUILayout.LabelField("Scene Default",
                isSceneDefault ? "Yes (auto-used by surfaces)" : "No");
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Game Root", provider.GameRoot ?? "(not initialized)");
                EditorGUILayout.LabelField("Localization Root", provider.LocalizationRoot ?? "(not initialized)");
                if (GUILayout.Button("Reload External Overrides Once")) provider.ReloadLocalization();
            }
        }
    }
}
