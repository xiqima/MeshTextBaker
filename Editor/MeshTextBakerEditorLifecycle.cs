// Mesh Text Baker — MeshTextBakerEditorLifecycle.cs
// Editor bakes use RenderTextures and material instances marked DontSave: they do not survive
// domain reloads or play-mode transitions. To avoid pink/black textures and leaked material
// instances stuck in sharedMaterials, we clear all bakes right BEFORE those events.

using UnityEditor;
using UnityEngine;

namespace MeshTextBaker.Editor
{
    [InitializeOnLoad]
    public static class MeshTextBakerEditorLifecycle
    {
        static MeshTextBakerEditorLifecycle()
        {
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorSceneManagerHooks();
        }

        private static void EditorSceneManagerHooks()
        {
            // Clear before a scene is saved so generated instances never end up in the file.
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += (scene, path) => ClearAllBakes();
        }

        private static void OnBeforeReload() => ClearAllBakes();

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                ClearAllBakes();
        }

        private static void ClearAllBakes()
        {
            // Cross-version lookup (include inactive; order irrelevant for clearing bakes).
            //   2023.1+ — FindObjectsByType; the sort-mode overload was deprecated mid-Unity-6
            //             (InstanceID → EntityId transition), so the warning is suppressed
            //             rather than #if-branching on an exact minor version.
            //   2022.3  — legacy FindObjectsOfType.
#if UNITY_2023_1_OR_NEWER
#pragma warning disable 618
            var surfaces = Object.FindObjectsByType<MeshTextSurface>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
#pragma warning restore 618
#else
            var surfaces = Object.FindObjectsOfType<MeshTextSurface>(true);
#endif
            foreach (var s in surfaces)
            {
                if (s == null) continue;
                s.ClearBake();
            }

            // Hidden shared TMP instances hold references into the reloading domain — drop them.
            TextLayoutEngine.DestroyHiddenInstances();
            MeshTextBakerCore.ReleaseSharedResources();
            InlineImageParser.ReleaseFileCache();
        }
    }
}
