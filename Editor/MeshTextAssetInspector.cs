// Mesh Text Baker — MeshTextAssetInspector.cs
// Adds a prominent "Open Text Editor" button on top of the default MeshTextAsset inspector.
// (Double-clicking the asset also opens the editor window.)

using UnityEditor;
using UnityEngine;

namespace MeshTextBaker.Editor
{
    [CustomEditor(typeof(MeshTextAsset))]
    public class MeshTextAssetInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            GUI.backgroundColor = new Color(0.6f, 0.85f, 1f);
            if (GUILayout.Button("\u270e Open Text Editor", GUILayout.Height(30)))
                MeshTextAssetEditorWindow.ShowWindow((MeshTextAsset)target);
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);
            DrawDefaultInspector();
        }
    }
}
