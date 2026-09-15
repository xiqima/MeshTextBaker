// Mesh Text Baker — ZoneHandles.cs
// Utility for drawing zone overlays in SceneView (projected onto the mesh).
//
// Performance: mesh.uv / mesh.vertices / mesh.triangles each ALLOCATE a fresh managed array on
// every access, and UV→world lookup is O(triangles). Both were previously paid per repaint per
// line segment. We now cache the arrays per mesh (invalidated by a small LRU) so a repaint costs
// zero allocations, and the triangle scan runs on cached data.

using System.Collections.Generic;
using UnityEngine;
using MeshTextBaker;
using UnityEditor;

namespace MeshTextBaker.Editor
{
    /// <summary>Draws TextZone rectangles in SceneView projected onto the mesh.</summary>
    public static class ZoneHandles
    {
        private class MeshCache
        {
            public Vector2[] uvs;
            public Vector3[] vertices;
            public int[] triangles;
            public int vertexCount;
            public int dirtyCount; // catches UV/vertex/index edits that keep the same vertex count
        }

        private const int MaxCachedMeshes = 8;
        private static readonly Dictionary<Mesh, MeshCache> s_Cache = new Dictionary<Mesh, MeshCache>();
        private static readonly List<Mesh> s_CacheOrder = new List<Mesh>();

        private static MeshCache GetCache(Mesh mesh)
        {
            if (s_Cache.TryGetValue(mesh, out var c))
            {
                if (c.vertexCount == mesh.vertexCount &&
                    c.dirtyCount == EditorUtility.GetDirtyCount(mesh)) return c;
                s_Cache.Remove(mesh);
                s_CacheOrder.Remove(mesh);
            }

            var cache = new MeshCache
            {
                uvs = mesh.uv,
                vertices = mesh.vertices,
                triangles = mesh.triangles,
                vertexCount = mesh.vertexCount,
                dirtyCount = EditorUtility.GetDirtyCount(mesh)
            };

            if (s_CacheOrder.Count >= MaxCachedMeshes)
            {
                s_Cache.Remove(s_CacheOrder[0]);
                s_CacheOrder.RemoveAt(0);
            }
            s_Cache[mesh] = cache;
            s_CacheOrder.Add(mesh);
            return cache;
        }

        /// <summary>Clears cached mesh data (e.g. when meshes are re-imported).</summary>
        public static void ClearCache()
        {
            s_Cache.Clear();
            s_CacheOrder.Clear();
        }

        public static void DrawZoneOverlay(TextZone zone, Mesh mesh, Transform transform, bool isSelected)
        {
            if (mesh == null || !mesh.isReadable) return;
            Color color = zone.previewColor;
            if (!isSelected) color.a *= 0.5f;
            DrawUVRectOnMesh(zone.uvRect, mesh, transform, color, zone.displayName);
        }

        private static void DrawUVRectOnMesh(Rect uvRect, Mesh mesh, Transform transform, Color color, string label)
        {
            var cache = GetCache(mesh);
            if (cache.uvs == null || cache.uvs.Length == 0) return;

            using (new Handles.DrawingScope(color))
            {
                DrawUVLineOnMesh(new Vector2(uvRect.xMin, uvRect.yMax), new Vector2(uvRect.xMax, uvRect.yMax), cache, transform, color);
                DrawUVLineOnMesh(new Vector2(uvRect.xMin, uvRect.yMin), new Vector2(uvRect.xMax, uvRect.yMin), cache, transform, color);
                DrawUVLineOnMesh(new Vector2(uvRect.xMin, uvRect.yMin), new Vector2(uvRect.xMin, uvRect.yMax), cache, transform, color);
                DrawUVLineOnMesh(new Vector2(uvRect.xMax, uvRect.yMin), new Vector2(uvRect.xMax, uvRect.yMax), cache, transform, color);

                Vector3 center = FindWorldPositionForUV(uvRect.center, cache, transform);
                if (center != Vector3.zero)
                {
                    Handles.Label(center, label, new GUIStyle(EditorStyles.label)
                    {
                        normal = { textColor = color },
                        fontSize = 12,
                        fontStyle = FontStyle.Bold
                    });
                }
            }
        }

        private static void DrawUVLineOnMesh(Vector2 uvStart, Vector2 uvEnd, MeshCache cache, Transform transform, Color color)
        {
            int segments = 32;
            Vector3 prevWorld = Vector3.zero;
            bool hasPrev = false;

            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                Vector2 uv = Vector2.Lerp(uvStart, uvEnd, t);
                Vector3 worldPos = FindWorldPositionForUV(uv, cache, transform);

                if (worldPos != Vector3.zero)
                {
                    if (hasPrev)
                    {
                        Handles.color = color;
                        Handles.DrawLine(prevWorld, worldPos, 2f);
                    }
                    prevWorld = worldPos;
                    hasPrev = true;
                }
                else hasPrev = false;
            }
        }

        public static Vector3 FindWorldPositionForUV(Vector2 targetUV, Mesh mesh, Transform transform)
        {
            if (mesh == null || !mesh.isReadable) return Vector3.zero;
            return FindWorldPositionForUV(targetUV, GetCache(mesh), transform);
        }

        private static Vector3 FindWorldPositionForUV(Vector2 targetUV, MeshCache cache, Transform transform)
        {
            var uvs = cache.uvs;
            var vertices = cache.vertices;
            var triangles = cache.triangles;
            if (uvs == null || vertices == null || triangles == null) return Vector3.zero;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int i0 = triangles[i], i1 = triangles[i + 1], i2 = triangles[i + 2];
                Vector3 bary = Barycentric(targetUV, uvs[i0], uvs[i1], uvs[i2]);
                if (bary.x >= -0.001f && bary.y >= -0.001f && bary.z >= -0.001f)
                {
                    Vector3 localPos = vertices[i0] * bary.x + vertices[i1] * bary.y + vertices[i2] * bary.z;
                    return transform.TransformPoint(localPos);
                }
            }
            return Vector3.zero;
        }

        private static Vector3 Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 v0 = c - a, v1 = b - a, v2 = p - a;
            float dot00 = Vector2.Dot(v0, v0);
            float dot01 = Vector2.Dot(v0, v1);
            float dot02 = Vector2.Dot(v0, v2);
            float dot11 = Vector2.Dot(v1, v1);
            float dot12 = Vector2.Dot(v1, v2);
            float invDenom = 1f / (dot00 * dot11 - dot01 * dot01 + 0.00001f);
            float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
            float v = (dot00 * dot12 - dot01 * dot02) * invDenom;
            float w = 1f - u - v;
            return new Vector3(w, v, u);
        }
    }
}
