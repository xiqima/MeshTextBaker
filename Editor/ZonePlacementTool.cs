// Mesh Text Baker — ZonePlacementTool.cs
// SceneView tool for placing text zones on meshes via raycasting (RaycastHit.textureCoord → UV).
//
// Performance: a temporary MeshCollider is created ONCE when entering placement mode and removed
// on exit (previously it was added/destroyed on every mouse event). SkinnedMeshRenderer targets
// get a BakeMesh() snapshot for accurate raycasts against the current pose.

using UnityEngine;
using MeshTextBaker;
using UnityEditor;

namespace MeshTextBaker.Editor
{
    /// <summary>Handles zone placement mode in SceneView.</summary>
    [InitializeOnLoad]
    public static class ZonePlacementTool
    {
        private static MeshTextSurface s_ActiveSurface;
        private static Renderer s_TargetRenderer; // optional: place on a child renderer (book pages)
        private static bool s_IsActive = false;
        private static int s_PlacementStep = 0;
        private static Vector2 s_FirstUV;
        private static Vector2 s_CurrentUV;
        private static int s_SelectedZoneIndex = -1;

        // Temporary raycast collider, created ONCE on EnterPlacementMode, removed on exit.
        // It lives on a separate hidden holder object (ported from a parallel review): adding
        // a MeshCollider directly to the user's GameObject can conflict with physics setups
        // (e.g. a Rigidbody + non-convex MeshCollider logs errors) and pollutes their object.
        private static GameObject s_ColliderHolder;
        private static MeshCollider s_TempCollider;
        private static Mesh s_BakedSkinnedMesh; // owned snapshot for SkinnedMeshRenderer
        private static MeshCollider s_ExistingCollider; // pre-existing MeshCollider, if any

        public static bool isActive => s_IsActive;
        public static MeshTextSurface activeSurface => s_ActiveSurface;
        public static Renderer placementTarget => s_TargetRenderer;
        public static int selectedZoneIndex => s_SelectedZoneIndex;

        static ZonePlacementTool()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += ExitPlacementMode;
        }

        public static void EnterPlacementMode(MeshTextSurface surface)
            => EnterPlacementMode(surface, null);

        /// <summary>
        /// Enter placement mode. When <paramref name="targetRenderer"/> is set (e.g. a book
        /// page child), raycasts and new zones target that renderer instead of the surface root.
        /// </summary>
        public static void EnterPlacementMode(MeshTextSurface surface, Renderer targetRenderer)
        {
            ExitPlacementMode(); // clean any previous session

            s_ActiveSurface = surface;
            s_TargetRenderer = targetRenderer != null ? targetRenderer : surface != null ? surface.targetRenderer : null;
            s_IsActive = true;
            s_PlacementStep = 0;
            s_SelectedZoneIndex = -1;

            var mesh = s_TargetRenderer != null
                ? MeshTextSurface.GetMeshOfRenderer(s_TargetRenderer)
                : surface.sharedMesh;
            if (mesh != null && !mesh.isReadable)
            {
                Debug.LogWarning(
                    $"[MeshTextBaker] Mesh '{mesh.name}' is not Read/Write enabled. " +
                    "UV raycasting needs a readable mesh — enable Read/Write in the model import settings.",
                    mesh);
            }

            SetupRaycastCollider(surface, s_TargetRenderer);

            Selection.activeGameObject = surface.gameObject;
            SceneView.RepaintAll();
            string targetName = s_TargetRenderer != null ? s_TargetRenderer.name : surface.name;
            Debug.Log($"[MeshTextBaker] Placement mode active on '{targetName}'. Click on the mesh to place zone corners. Press Escape to exit.");
        }

        public static void ExitPlacementMode()
        {
            CleanupRaycastCollider();
            s_IsActive = false;
            s_ActiveSurface = null;
            s_TargetRenderer = null;
            s_PlacementStep = 0;
            s_SelectedZoneIndex = -1;
            SceneView.RepaintAll();
        }

        private static void SetupRaycastCollider(MeshTextSurface surface, Renderer targetRenderer)
        {
            // RaycastHit.textureCoord ONLY works with MeshCollider — a Box/Sphere/Capsule
            // collider would silently return UV (0,0) for every click. Reuse an existing
            // collider only if it is a MeshCollider on the SAME target (and the mesh is not
            // skinned — a stale MeshCollider would not match the current pose).
            var smr = targetRenderer as SkinnedMeshRenderer;
            bool isSkinned = smr != null && smr.sharedMesh != null;
            if (!isSkinned && targetRenderer != null)
            {
                s_ExistingCollider = targetRenderer.GetComponent<MeshCollider>();
                if (s_ExistingCollider != null) return; // reuse what's there
            }

            Mesh raycastMesh = targetRenderer != null
                ? MeshTextSurface.GetMeshOfRenderer(targetRenderer)
                : surface.sharedMesh;

            // Skinned meshes: bake the current pose so clicks land where the user sees the mesh.
            if (isSkinned)
            {
                s_BakedSkinnedMesh = new Mesh { name = "__MTB_BakedSkin", hideFlags = HideFlags.HideAndDontSave };
                smr.BakeMesh(s_BakedSkinnedMesh);
                raycastMesh = s_BakedSkinnedMesh;
            }

            if (raycastMesh == null) return;

            // Hidden holder mirrors the renderer's world transform. NOTE: BakeMesh output is
            // already scaled by the renderer's transform scale, so the holder must NOT apply
            // lossyScale again for skinned meshes.
            Transform t = targetRenderer != null ? targetRenderer.transform : surface.transform;
            s_ColliderHolder = new GameObject("__MTB_TempCollider") { hideFlags = HideFlags.HideAndDontSave };
            s_ColliderHolder.transform.position = t.position;
            s_ColliderHolder.transform.rotation = t.rotation;
            s_ColliderHolder.transform.localScale = isSkinned ? Vector3.one : t.lossyScale;

            s_TempCollider = s_ColliderHolder.AddComponent<MeshCollider>();
            s_TempCollider.sharedMesh = raycastMesh;
        }

        private static void CleanupRaycastCollider()
        {
            if (s_ColliderHolder != null)
            {
                Object.DestroyImmediate(s_ColliderHolder);
                s_ColliderHolder = null;
            }
            s_TempCollider = null;
            if (s_BakedSkinnedMesh != null)
            {
                Object.DestroyImmediate(s_BakedSkinnedMesh);
                s_BakedSkinnedMesh = null;
            }
            s_ExistingCollider = null;
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!s_IsActive || s_ActiveSurface == null) return;

            Event e = Event.current;

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                ExitPlacementMode();
                e.Use();
                return;
            }

            Mesh mesh = s_TargetRenderer != null
                ? MeshTextSurface.GetMeshOfRenderer(s_TargetRenderer)
                : s_ActiveSurface.sharedMesh;
            if (mesh == null) return;
            Transform meshTransform = s_TargetRenderer != null
                ? s_TargetRenderer.transform
                : s_ActiveSurface.transform;

            for (int i = 0; i < s_ActiveSurface.zones.Count; i++)
            {
                bool selected = (i == s_SelectedZoneIndex);
                var z = s_ActiveSurface.zones[i];
                Mesh zm = s_ActiveSurface.GetZoneMesh(z) ?? mesh;
                Transform zt = s_ActiveSurface.GetZoneRenderer(z) != null
                    ? s_ActiveSurface.GetZoneRenderer(z).transform
                    : meshTransform;
                ZoneHandles.DrawZoneOverlay(z, zm, zt, selected);
            }

            if (s_PlacementStep == 1)
            {
                Rect inProgressRect = Rect.MinMaxRect(
                    Mathf.Min(s_FirstUV.x, s_CurrentUV.x),
                    Mathf.Min(s_FirstUV.y, s_CurrentUV.y),
                    Mathf.Max(s_FirstUV.x, s_CurrentUV.x),
                    Mathf.Max(s_FirstUV.y, s_CurrentUV.y));
                var tempZone = new TextZone { uvRect = inProgressRect, previewColor = Color.yellow };
                ZoneHandles.DrawZoneOverlay(tempZone, mesh, meshTransform, true);
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Vector2? uvHit = RaycastMeshUV(e);
                if (uvHit.HasValue)
                {
                    if (s_PlacementStep == 0)
                    {
                        s_FirstUV = uvHit.Value;
                        s_CurrentUV = uvHit.Value;
                        s_PlacementStep = 1;
                        e.Use();
                    }
                    else if (s_PlacementStep == 1)
                    {
                        s_CurrentUV = uvHit.Value;
                        Rect zoneRect = Rect.MinMaxRect(
                            Mathf.Min(s_FirstUV.x, s_CurrentUV.x),
                            Mathf.Min(s_FirstUV.y, s_CurrentUV.y),
                            Mathf.Max(s_FirstUV.x, s_CurrentUV.x),
                            Mathf.Max(s_FirstUV.y, s_CurrentUV.y));

                        Undo.RecordObject(s_ActiveSurface, "Add Zone");
                        var zone = s_ActiveSurface.AddZone();
                        zone.uvRect = zoneRect;
                        zone.displayName = $"Zone {s_ActiveSurface.zones.Count}";
                        // Bind to the placement target when it is not the surface's own renderer.
                        if (s_TargetRenderer != null && s_TargetRenderer != s_ActiveSurface.targetRenderer)
                            zone.targetRenderer = s_TargetRenderer;
                        s_PlacementStep = 0;
                        s_SelectedZoneIndex = s_ActiveSurface.zones.Count - 1;
                        EditorUtility.SetDirty(s_ActiveSurface);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(s_ActiveSurface);
                        e.Use();
                        Debug.Log($"[MeshTextBaker] Created zone '{zone.displayName}' at UV {zoneRect}");
                    }
                }
            }

            if (e.type == EventType.MouseMove && s_PlacementStep == 1)
            {
                Vector2? uvHit = RaycastMeshUV(e);
                if (uvHit.HasValue)
                {
                    s_CurrentUV = uvHit.Value;
                    SceneView.RepaintAll();
                }
            }

            Handles.BeginGUI();
            string instruction = s_PlacementStep == 0
                ? "Click on mesh to set first corner of text zone"
                : "Click to set second corner (Esc to cancel)";
            GUILayout.Label(instruction, new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 14,
                normal = { textColor = Color.white },
                padding = new RectOffset(8, 8, 6, 6)
            });
            Handles.EndGUI();
        }

        private static Vector2? RaycastMeshUV(Event e)
        {
            Collider collider = s_ExistingCollider != null ? (Collider)s_ExistingCollider : s_TempCollider;
            if (collider == null) return null;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (collider.Raycast(ray, out RaycastHit hit, Mathf.Infinity))
                return hit.textureCoord;
            return null;
        }
    }
}
