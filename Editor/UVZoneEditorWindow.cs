// Mesh Text Baker — UVZoneEditorWindow.cs
// EditorWindow for editing text zones in UV-space (move / resize / rotate),
// with rotated zone rendering, a rotation handle, and overflow-link arrows.
//
// Editor correctness:
//   - _surface survives domain reloads (restored from instance id in OnEnable).
//   - Canvas texture falls back to the material's albedo when baseAlbedoTexture is unset.
//   - All zone mutations go through Undo.RecordObject + SetDirty + prefab modifications.
//   - Destructive list actions (delete / create sub-zone) are DEFERRED to after the draw
//     loop to avoid GUILayout Begin/End mismatches.

using System.Collections.Generic;
using UnityEngine;
using MeshTextBaker;
using UnityEditor;

namespace MeshTextBaker.Editor
{
    public class UVZoneEditorWindow : EditorWindow
    {
        // Serialized directly: EditorWindow fields marked [SerializeField] survive domain
        // reloads, including scene-object references. This avoids GetInstanceID()/
        // InstanceIDToObject() (replaced by entity-id APIs in Unity 6.x), so the same code
        // compiles on 2022.3 LTS through Unity 6.x.
        [SerializeField] private MeshTextSurface _surface;

        private Vector2 _scrollPos;
        private int _selectedZoneIndex = -1;

        private bool _isDragging = false;
        private int _dragCorner = -1;
        private bool _isRotating = false;
        private bool _dragDirty = false;
        private Vector2 _dragStart;
        private Rect _dragOriginalRect;
        private float _dragOriginalRotation;

        private bool _isCreating = false;
        private Vector2 _createStart;
        private Vector2 _createEnd;

        // Deferred actions (applied after the draw loop; -1 = none).
        private int _pendingDeleteIndex = -1;
        private int _pendingCreateSubIndex = -1;
        private int _pendingPageNumIndex = -1;

        // Snap / match-size helpers
        private bool _snapEnabled = false;
        private float _snapStep = 0.01f; // UV units (0.01 = 1% of texture)
        private int _matchSizeSourceIndex = -1; // zone whose size is copied onto the selection

        // Canvas zoom / pan
        private float _zoom = 1f;              // 1 = fit to canvas
        private Vector2 _pan;                  // UV-space pan when zoomed
        private bool _isPanning;
        private Vector2 _panStartMouse;
        private Vector2 _panStartOffset;

        const float TOOLBAR_HEIGHT = 24f;
        const float ROT_HANDLE_DIST = 28f;

        public static void ShowWindow(MeshTextSurface surface)
        {
            var window = GetWindow<UVZoneEditorWindow>("UV Zone Editor");
            window.SetSurface(surface);
            window.minSize = new Vector2(400, 400);
            window.Show();
        }

        private void SetSurface(MeshTextSurface surface)
        {
            _surface = surface;
        }

        private void OnEnable()
        {
            // _surface is a [SerializeField] on the window — Unity restores it automatically
            // after domain reloads; nothing to resolve manually here.
            Undo.undoRedoPerformed += Repaint;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= Repaint;
        }

        /// <summary>Records the surface for Undo and marks it dirty (call BEFORE mutating).</summary>
        private void RecordSurface(string actionName)
        {
            Undo.RecordObject(_surface, actionName);
        }

        private void FlushDirty()
        {
            EditorUtility.SetDirty(_surface);
            PrefabUtility.RecordPrefabInstancePropertyModifications(_surface);
        }

        private void OnGUI()
        {
            wantsMouseMove = true;
            if (_surface == null)
            {
                EditorGUILayout.HelpBox("No Mesh Text Surface assigned.", MessageType.Warning);
                var picked = (MeshTextSurface)EditorGUILayout.ObjectField(
                    "Surface", null, typeof(MeshTextSurface), true);
                if (picked != null) SetSurface(picked);
                return;
            }

            DrawToolbar();

            float available = position.height - TOOLBAR_HEIGHT - 4f;
            // Reserve a fixed bottom panel so zone list never draws over the canvas.
            const float listPanelH = 220f;
            float canvasHeight = Mathf.Max(120f, available - listPanelH);

            Rect canvasRect = GUILayoutUtility.GetRect(
                Mathf.Max(position.width - 8, 200), canvasHeight, GUILayout.ExpandWidth(true));

            // Clip zone overlays / handles to the canvas so they cannot paint over the list.
            GUI.BeginGroup(canvasRect);
            var localCanvas = new Rect(0, 0, canvasRect.width, canvasRect.height);
            DrawCanvas(localCanvas);
            HandleCanvasInput(localCanvas);
            GUI.EndGroup();

            DrawZoneList(listPanelH);
            ApplyPendingActions();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("UV Zone Editor", EditorStyles.boldLabel);

            if (GUILayout.Button("Add Zone", EditorStyles.toolbarButton))
            {
                _isCreating = true;
                Debug.Log("[MeshTextBaker] Click and drag on the canvas to create a new zone.");
            }

            _snapEnabled = GUILayout.Toggle(_snapEnabled, new GUIContent("Snap",
                "Grid snap while moving/resizing. Hold Ctrl for temporary grid snap + magnet to other zone corners."),
                EditorStyles.toolbarButton, GUILayout.Width(44));
            using (new EditorGUI.DisabledScope(!_snapEnabled))
            {
                GUILayout.Label("Step", EditorStyles.miniLabel, GUILayout.Width(28));
                _snapStep = EditorGUILayout.FloatField(_snapStep, GUILayout.Width(40));
                _snapStep = Mathf.Clamp(_snapStep, 0.001f, 0.25f);
            }

            using (new EditorGUI.DisabledScope(
                _surface == null || _selectedZoneIndex < 0 || _selectedZoneIndex >= _surface.zones.Count))
            {
                if (GUILayout.Button(new GUIContent("Match Size",
                        "Copy width/height from the zone chosen in the dropdown onto the selected zone " +
                        "(keeps the selected zone's center)."),
                    EditorStyles.toolbarButton, GUILayout.Width(78)))
                {
                    ApplyMatchSize();
                }
            }

            if (_surface != null && _surface.zones.Count > 0)
            {
                var names = new string[_surface.zones.Count];
                for (int i = 0; i < names.Length; i++)
                {
                    var z = _surface.zones[i];
                    string n = string.IsNullOrEmpty(z.displayName) ? $"Zone {i}" : z.displayName;
                    if (z.role == TextZoneRole.PageNumber) n = "# " + n;
                    else if (z.role == TextZoneRole.PageSlot) n = "P " + n;
                    names[i] = n;
                }
                if (_matchSizeSourceIndex < 0 || _matchSizeSourceIndex >= names.Length)
                    _matchSizeSourceIndex = 0;
                _matchSizeSourceIndex = EditorGUILayout.Popup(_matchSizeSourceIndex, names, GUILayout.Width(110));
            }

            GUILayout.Space(6);
            if (GUILayout.Button(new GUIContent("-", "Zoom out"), EditorStyles.toolbarButton, GUILayout.Width(22)))
                SetZoom(_zoom / 1.25f);
            GUILayout.Label($"{_zoom * 100f:0.#}%", EditorStyles.miniLabel, GUILayout.Width(40));
            if (GUILayout.Button(new GUIContent("+", "Zoom in"), EditorStyles.toolbarButton, GUILayout.Width(22)))
                SetZoom(_zoom * 1.25f);
            if (GUILayout.Button(new GUIContent("1:1", "Reset zoom and pan"), EditorStyles.toolbarButton, GUILayout.Width(30)))
            {
                _zoom = 1f;
                _pan = Vector2.zero;
                Repaint();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("Scroll=zoom · MMB/Alt+LMB/Shift+LMB=pan · Ctrl=snap · RMB=pick",
                EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void SetZoom(float z)
        {
            _zoom = Mathf.Clamp(z, 0.25f, 8f);
            Repaint();
        }

        private void ApplyMatchSize()
        {
            if (_surface == null) return;
            if (_selectedZoneIndex < 0 || _selectedZoneIndex >= _surface.zones.Count) return;
            if (_matchSizeSourceIndex < 0 || _matchSizeSourceIndex >= _surface.zones.Count) return;
            if (_matchSizeSourceIndex == _selectedZoneIndex) return;

            var target = _surface.zones[_selectedZoneIndex];
            var source = _surface.zones[_matchSizeSourceIndex];
            RecordSurface("Match Zone Size");
            Rect r = target.uvRect;
            Vector2 center = r.center;
            r.width = source.uvRect.width;
            r.height = source.uvRect.height;
            r.center = center;
            r.x = Mathf.Clamp(r.x, 0f, 1f - r.width);
            r.y = Mathf.Clamp(r.y, 0f, 1f - r.height);
            target.uvRect = r;
            FlushDirty();
            Repaint();
        }

        /// <summary>Grid snap active when toolbar toggle is on OR Ctrl is held.</summary>
        private bool SnapActiveNow()
        {
            Event e = Event.current;
            bool ctrl = e != null && (e.control || e.command);
            return _snapEnabled || ctrl;
        }

        private float Snap(float v)
        {
            if (!SnapActiveNow() || _snapStep <= 0f) return v;
            return Mathf.Round(v / _snapStep) * _snapStep;
        }

        /// <summary>
        /// Snap consistently: first snap position (x,y), then snap size (w,h).
        /// Snapping both min and max independently made equal-sized zones land on
        /// different grids depending on origin — they could no longer edge-align.
        /// </summary>
        private Rect SnapRect(Rect r)
        {
            if (!SnapActiveNow() || _snapStep <= 0f) return r;
            float x = Snap(r.x);
            float y = Snap(r.y);
            float w = Mathf.Max(_snapStep, Snap(r.width));
            float h = Mathf.Max(_snapStep, Snap(r.height));
            x = Mathf.Clamp(x, 0f, 1f - w);
            y = Mathf.Clamp(y, 0f, 1f - h);
            return new Rect(x, y, w, h);
        }

        /// <summary>
        /// While Ctrl is held, magnet the dragged corner (or whole rect on move) to nearby
        /// corners of other zones. Returns possibly adjusted rect.
        /// </summary>
        private Rect MagnetToOtherCorners(Rect r, int excludeIndex, int dragCorner)
        {
            Event e = Event.current;
            if (e == null || !(e.control || e.command) || _surface == null) return r;

            const float thresh = 0.02f;
            float xMin = r.xMin, yMin = r.yMin, xMax = r.xMax, yMax = r.yMax;

            // Collect other zones' edge coordinates.
            var xs = new List<float>();
            var ys = new List<float>();
            for (int i = 0; i < _surface.zones.Count; i++)
            {
                if (i == excludeIndex) continue;
                Rect o = _surface.zones[i].uvRect;
                xs.Add(o.xMin); xs.Add(o.xMax);
                ys.Add(o.yMin); ys.Add(o.yMax);
            }

            float SnapEdge(float v, List<float> edges)
            {
                float best = thresh;
                float result = v;
                for (int i = 0; i < edges.Count; i++)
                {
                    float d = Mathf.Abs(v - edges[i]);
                    if (d < best) { best = d; result = edges[i]; }
                }
                return result;
            }

            if (dragCorner >= 0)
            {
                // Resize: snap the free edges (the ones that move for this corner).
                // Corner indices from GetZoneCanvasCorners: 0=bl, 1=br, 2=tr, 3=tl in canvas
                // but uvRect resize switch uses 0..3 on unrotated local - keep all edges snappable.
                xMin = SnapEdge(xMin, xs);
                xMax = SnapEdge(xMax, xs);
                yMin = SnapEdge(yMin, ys);
                yMax = SnapEdge(yMax, ys);
                if (xMax - xMin < 0.01f) xMax = xMin + 0.01f;
                if (yMax - yMin < 0.01f) yMax = yMin + 0.01f;
            }
            else
            {
                // Move: snap whole rect by the nearest edge delta.
                float dx = 0f, dy = 0f, bestX = thresh, bestY = thresh;
                float[] myX = { xMin, xMax };
                float[] myY = { yMin, yMax };
                for (int a = 0; a < 2; a++)
                {
                    for (int i = 0; i < xs.Count; i++)
                    {
                        float d = Mathf.Abs(myX[a] - xs[i]);
                        if (d < bestX) { bestX = d; dx = xs[i] - myX[a]; }
                    }
                    for (int i = 0; i < ys.Count; i++)
                    {
                        float d = Mathf.Abs(myY[a] - ys[i]);
                        if (d < bestY) { bestY = d; dy = ys[i] - myY[a]; }
                    }
                }
                xMin += dx; xMax += dx;
                yMin += dy; yMax += dy;
            }

            var result = Rect.MinMaxRect(
                Mathf.Clamp01(Mathf.Min(xMin, xMax)),
                Mathf.Clamp01(Mathf.Min(yMin, yMax)),
                Mathf.Clamp01(Mathf.Max(xMin, xMax)),
                Mathf.Clamp01(Mathf.Max(yMin, yMax)));
            if (result.width < 0.01f) result.width = 0.01f;
            if (result.height < 0.01f) result.height = 0.01f;
            result.x = Mathf.Clamp(result.x, 0f, 1f - result.width);
            result.y = Mathf.Clamp(result.y, 0f, 1f - result.height);
            return result;
        }


        /// <summary>Canvas background: explicit override, else the material's albedo texture.</summary>
        private Texture GetCanvasTexture()
        {
            Texture tex = _surface.bakeSettings.baseAlbedoTexture;
            if (tex != null) return tex;

            // Fallback: same detection the baker uses (URP/HDRP/Built-in property names).
            // Prefer the selected zone's own renderer/slot so multi-renderer books preview right.
            Renderer renderer = _surface.targetRenderer;
            int matIdx = 0;
            if (_selectedZoneIndex >= 0 && _selectedZoneIndex < _surface.zones.Count)
            {
                var zone = _surface.zones[_selectedZoneIndex];
                renderer = _surface.GetZoneRenderer(zone);
                matIdx = zone.materialIndex;
            }
            return MeshTextBakerCore.GetAlbedoTexture(renderer, matIdx);
        }

        private Rect ComputeTexRect(Rect canvasRect, Texture baseTex)
        {
            // Fit-to-canvas base size (aspect preserved when a texture is present).
            float displayW = canvasRect.width;
            float displayH = canvasRect.height;
            if (baseTex != null)
            {
                float aspect = (float)baseTex.width / baseTex.height;
                if (aspect > canvasRect.width / canvasRect.height) displayH = displayW / aspect;
                else displayW = displayH * aspect;
            }

            // Zoom around canvas center, then apply pan (in canvas pixels).
            float z = Mathf.Max(0.25f, _zoom);
            displayW *= z;
            displayH *= z;

            // Pan is stored in UV space (0–1); convert to pixels for this display size.
            float panPxX = _pan.x * displayW;
            float panPxY = -_pan.y * displayH; // UI Y is top-down; UV V is bottom-up

            return new Rect(
                canvasRect.x + (canvasRect.width - displayW) / 2 + panPxX,
                canvasRect.y + (canvasRect.height - displayH) / 2 + panPxY,
                displayW, displayH);
        }

        private void DrawCanvas(Rect canvasRect)
        {
            EditorGUI.DrawRect(canvasRect, new Color(0.2f, 0.2f, 0.2f, 1f));

            Texture baseTex = GetCanvasTexture();
            Rect texRect = ComputeTexRect(canvasRect, baseTex);

            if (baseTex != null)
                GUI.DrawTexture(texRect, baseTex, ScaleMode.StretchToFill, true);
            else
                EditorGUI.DropShadowLabel(canvasRect,
                    "No texture found on the material.\nZones are edited on the UV 0–1 square.");

            DrawUVGrid(texRect);
            DrawSubZoneLinks(texRect);

            for (int i = 0; i < _surface.zones.Count; i++)
                DrawZone(texRect, _surface.zones[i], i == _selectedZoneIndex);

            if (_isCreating)
            {
                Rect uvRect = Rect.MinMaxRect(
                    Mathf.Min(_createStart.x, _createEnd.x), Mathf.Min(_createStart.y, _createEnd.y),
                    Mathf.Max(_createStart.x, _createEnd.x), Mathf.Max(_createStart.y, _createEnd.y));
                Rect pixelRect = UVToCanvas(texRect, uvRect);
                EditorGUI.DrawRect(pixelRect, new Color(1f, 1f, 0f, 0.3f));
                DrawRectOutline(pixelRect, Color.yellow);
            }

            DrawBakeSizeOverlay(canvasRect);
        }

        /// <summary>
        /// Bottom-left readout of the WxH the selected zone's target actually bakes at:
        /// Resolution is the long side, the aspect follows the background texture.
        /// </summary>
        private void DrawBakeSizeOverlay(Rect canvasRect)
        {
            if (_surface == null) return;
            Renderer renderer = _surface.targetRenderer;
            int matIdx = 0;
            if (_selectedZoneIndex >= 0 && _selectedZoneIndex < _surface.zones.Count)
            {
                TextZone zone = _surface.zones[_selectedZoneIndex];
                if (zone == null) return;
                renderer = _surface.GetZoneRenderer(zone);
                matIdx = zone.materialIndex;
            }
            BakeSettings settings = _surface.bakeSettings;
            if (settings == null) return;
            string texProp = string.IsNullOrEmpty(settings.texturePropertyName)
                ? MeshTextBakerCore.DetectTexturePropertyName()
                : settings.texturePropertyName;
            MeshTextBakerCore.ResolveBakeTargetSize(_surface, renderer, matIdx,
                settings, texProp, out int w, out int h);
            GUI.Label(new Rect(canvasRect.x + 6, canvasRect.yMax - 20, 420, 16),
                $"Bake target: {w}\u00d7{h}  (Resolution {(int)settings.resolution} = long side)",
                EditorStyles.miniLabel);
        }

        private void DrawUVGrid(Rect texRect)
        {
            Handles.color = new Color(1, 1, 1, 0.15f);
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f;
                float x = texRect.x + texRect.width * t;
                Handles.DrawLine(new Vector3(x, texRect.y), new Vector3(x, texRect.y + texRect.height));
                float y = texRect.y + texRect.height * t;
                Handles.DrawLine(new Vector3(texRect.x, y), new Vector3(texRect.x + texRect.width, y));
            }
        }

        private Vector2[] GetZoneCanvasCorners(Rect texRect, TextZone zone)
        {
            Rect aabb = UVToCanvas(texRect, zone.uvRect);
            Vector2 c = new Vector2(aabb.center.x, aabb.center.y);

            Vector2 bl = new Vector2(aabb.xMin, aabb.yMax);
            Vector2 br = new Vector2(aabb.xMax, aabb.yMax);
            Vector2 tr = new Vector2(aabb.xMax, aabb.yMin);
            Vector2 tl = new Vector2(aabb.xMin, aabb.yMin);

            float rad = -zone.rotation * Mathf.Deg2Rad;
            return new[]
            {
                RotateAround(bl, c, rad), RotateAround(br, c, rad),
                RotateAround(tr, c, rad), RotateAround(tl, c, rad),
            };
        }

        private static Vector2 RotateAround(Vector2 p, Vector2 pivot, float rad)
        {
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            Vector2 d = p - pivot;
            return new Vector2(d.x * cos - d.y * sin, d.x * sin + d.y * cos) + pivot;
        }

        private Vector2 GetZoneCanvasCenter(Rect texRect, TextZone zone)
        {
            Rect aabb = UVToCanvas(texRect, zone.uvRect);
            return new Vector2(aabb.center.x, aabb.center.y);
        }

        private Vector2 GetRotationHandlePos(Rect texRect, TextZone zone)
        {
            var corners = GetZoneCanvasCorners(texRect, zone);
            Vector2 topMid = (corners[2] + corners[3]) * 0.5f;
            Vector2 center = GetZoneCanvasCenter(texRect, zone);
            Vector2 dir = (topMid - center).normalized;
            if (dir.sqrMagnitude < 1e-4f) dir = new Vector2(0, -1);
            return topMid + dir * ROT_HANDLE_DIST;
        }

        private void DrawZone(Rect texRect, TextZone zone, bool selected)
        {
            var corners = GetZoneCanvasCorners(texRect, zone);
            Color border = selected ? Color.white : new Color(1, 1, 1, 0.6f);

            Handles.color = zone.previewColor;
            Handles.DrawAAConvexPolygon(corners[0], corners[1], corners[2], corners[3]);

            Handles.color = border;
            for (int i = 0; i < 4; i++)
                Handles.DrawLine(corners[i], corners[(i + 1) % 4]);

            var labelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = selected ? Color.yellow : Color.white },
                fontSize = 10
            };
            string label = zone.role == TextZoneRole.OverflowOnly ? "↳ " + zone.displayName
                : zone.role == TextZoneRole.PageNumber ? "# " + zone.displayName
                : zone.displayName;
            if (zone.flipHorizontal) label += " \u21d4";
            if (zone.flipVertical) label += " \u21d5";
            GUI.Label(new Rect(corners[3].x + 2, corners[3].y, 200, 16), label, labelStyle);

            if (selected)
            {
                Handles.color = Color.cyan;
                foreach (var cpt in corners)
                    Handles.DrawSolidDisc(cpt, Vector3.forward, 3.5f);

                Vector2 topMid = (corners[2] + corners[3]) * 0.5f;
                Vector2 rotPos = GetRotationHandlePos(texRect, zone);
                Handles.color = new Color(0.4f, 1f, 0.4f, 0.9f);
                Handles.DrawLine(topMid, rotPos);
                Handles.DrawSolidDisc(rotPos, Vector3.forward, 6f);

                Handles.color = Color.white;
                GUI.Label(new Rect(rotPos.x + 8, rotPos.y - 8, 60, 16), $"{zone.rotation:F0}\u00b0", EditorStyles.miniLabel);
            }
        }

        private void DrawSubZoneLinks(Rect texRect)
        {
            foreach (var zone in _surface.zones)
            {
                if (zone.overflowBehavior != TextZoneOverflow.ContinueToSubZones) continue;
                if (string.IsNullOrEmpty(zone.continueToZoneId)) continue;

                var target = _surface.FindZoneById(zone.continueToZoneId);
                if (target == null) continue;

                Vector2 from = GetZoneCanvasCenter(texRect, zone);
                Vector2 to = GetZoneCanvasCenter(texRect, target);
                Handles.color = new Color(1f, 0.6f, 0.1f, 0.9f);
                Handles.DrawLine(from, to);
                DrawArrowHead(from, to);
            }
        }

        private void DrawArrowHead(Vector2 from, Vector2 to)
        {
            Vector2 dir = (to - from).normalized;
            if (dir.sqrMagnitude < 1e-4f) return;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float size = 8f;
            Vector2 tip = to - dir * 6f;
            Vector2 a = tip - dir * size + perp * size * 0.5f;
            Vector2 b = tip - dir * size - perp * size * 0.5f;
            Handles.DrawAAConvexPolygon(tip, a, b);
        }

        private void DrawRectOutline(Rect rect, Color color)
        {
            float t = 1f;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, t), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - t, rect.width, t), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, t, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - t, rect.y, t, rect.height), color);
        }

        private Rect UVToCanvas(Rect texRect, Rect uvRect)
        {
            float x = texRect.x + uvRect.x * texRect.width;
            float y = texRect.y + (1f - uvRect.y - uvRect.height) * texRect.height;
            float w = uvRect.width * texRect.width;
            float h = uvRect.height * texRect.height;
            return new Rect(x, y, w, h);
        }

        private Vector2 CanvasPixelToUV(Rect texRect, Vector2 canvasPixel)
        {
            float u = (canvasPixel.x - texRect.x) / texRect.width;
            float v = 1f - (canvasPixel.y - texRect.y) / texRect.height;
            return new Vector2(u, v);
        }

        private void HandleCanvasInput(Rect canvasRect)
        {
            Event e = Event.current;

            // Pan end (MMB / Alt+LMB / Shift+LMB) — allow outside canvas.
            if (e.type == EventType.MouseUp && _isPanning)
            {
                _isPanning = false;
                e.Use();
                return;
            }
            if (e.type == EventType.MouseDrag && _isPanning)
            {
                Texture panTex = GetCanvasTexture();
                Rect panRect = ComputeTexRect(canvasRect, panTex);
                // Delta mouse → UV pan (invert Y for UI).
                Vector2 d = e.mousePosition - _panStartMouse;
                if (panRect.width > 1f && panRect.height > 1f)
                {
                    _pan = _panStartOffset + new Vector2(d.x / panRect.width, -d.y / panRect.height);
                }
                Repaint();
                e.Use();
                return;
            }

            // MouseUp must be handled even OUTSIDE the canvas, otherwise a drag that ends
            // off-canvas leaves _isDragging stuck and the zone keeps following the mouse.
            if (e.type == EventType.MouseUp && _isDragging)
            {
                _isDragging = false; _isRotating = false; _dragCorner = -1;
                if (_dragDirty) FlushDirty();
                _dragDirty = false;
                e.Use();
                return;
            }
            if (e.type == EventType.MouseUp && _isCreating && !canvasRect.Contains(e.mousePosition))
            {
                _isCreating = false; // cancel off-canvas zone creation
                Repaint(); e.Use();
                return;
            }

            if (!canvasRect.Contains(e.mousePosition)) return;

            Texture baseTex = GetCanvasTexture();
            Rect texRect = ComputeTexRect(canvasRect, baseTex);

            // Mouse wheel zoom toward cursor.
            if (e.type == EventType.ScrollWheel)
            {
                float oldZ = _zoom;
                float factor = e.delta.y > 0 ? 1f / 1.15f : 1.15f;
                float newZ = Mathf.Clamp(oldZ * factor, 0.25f, 8f);
                if (!Mathf.Approximately(oldZ, newZ) && oldZ > 0.001f)
                {
                    // Keep the UV under the cursor stable while zooming.
                    Vector2 uvAtCursor = CanvasPixelToUV(texRect, e.mousePosition);
                    _zoom = newZ;
                    Rect newTex = ComputeTexRect(canvasRect, baseTex);
                    Vector2 uvAfter = CanvasPixelToUV(newTex, e.mousePosition);
                    _pan += uvAtCursor - uvAfter;
                }
                e.Use();
                Repaint();
                return;
            }

            // Middle mouse, Alt+LMB or Shift+LMB — pan.
            bool panButton = (e.type == EventType.MouseDown && e.button == 2) ||
                             (e.type == EventType.MouseDown && e.button == 0 && (e.alt || e.shift));
            if (panButton)
            {
                _isPanning = true;
                _panStartMouse = e.mousePosition;
                _panStartOffset = _pan;
                e.Use();
                return;
            }

            if (_isCreating && e.type == EventType.MouseDown && e.button == 0)
            {
                _createStart = CanvasPixelToUV(texRect, e.mousePosition);
                _createEnd = _createStart;
                e.Use(); return;
            }
            if (_isCreating && e.type == EventType.MouseDrag && e.button == 0)
            {
                _createEnd = CanvasPixelToUV(texRect, e.mousePosition);
                Repaint(); e.Use(); return;
            }
            if (_isCreating && e.type == EventType.MouseUp && e.button == 0)
            {
                _createEnd = CanvasPixelToUV(texRect, e.mousePosition);
                Rect uvRect = Rect.MinMaxRect(
                    Mathf.Min(_createStart.x, _createEnd.x), Mathf.Min(_createStart.y, _createEnd.y),
                    Mathf.Max(_createStart.x, _createEnd.x), Mathf.Max(_createStart.y, _createEnd.y));

                if (uvRect.width > 0.01f && uvRect.height > 0.01f)
                {
                    RecordSurface("Add Zone");
                    var zone = _surface.AddZone();
                    zone.uvRect = SnapRect(uvRect);
                    zone.displayName = $"Zone {_surface.zones.Count}";
                    _selectedZoneIndex = _surface.zones.Count - 1;
                    FlushDirty();
                }
                _isCreating = false;
                Repaint(); e.Use(); return;
            }

            // Right-click: context menu listing ALL zones under the cursor (overlap-friendly).
            if (e.type == EventType.MouseDown && e.button == 1)
            {
                var under = new System.Collections.Generic.List<int>();
                for (int i = _surface.zones.Count - 1; i >= 0; i--)
                    if (PointInRotatedZone(texRect, _surface.zones[i], e.mousePosition))
                        under.Add(i);
                if (under.Count > 0)
                {
                    var menu = new GenericMenu();
                    foreach (int zi in under)
                    {
                        int captured = zi;
                        var z = _surface.zones[zi];
                        string label = string.IsNullOrEmpty(z.displayName) ? $"Zone {zi}" : z.displayName;
                        if (zi == _selectedZoneIndex) label = "\u25cf " + label;
                        menu.AddItem(new GUIContent(label), zi == _selectedZoneIndex, () =>
                        {
                            _selectedZoneIndex = captured;
                            Repaint();
                        });
                    }
                    menu.AddSeparator("");
                    int top = under[0];
                    menu.AddItem(new GUIContent("Use As Match-Size Source"), false, () =>
                    {
                        _matchSizeSourceIndex = top;
                        Repaint();
                    });
                    if (_selectedZoneIndex >= 0 && _selectedZoneIndex < _surface.zones.Count
                        && _selectedZoneIndex != top)
                    {
                        menu.AddItem(new GUIContent("Match Size From This \u2192 Selection"), false, () =>
                        {
                            _matchSizeSourceIndex = top;
                            ApplyMatchSize();
                        });
                        menu.AddItem(new GUIContent("Match Size From Selection \u2192 This"), false, () =>
                        {
                            int prevSel = _selectedZoneIndex;
                            _matchSizeSourceIndex = prevSel;
                            _selectedZoneIndex = top;
                            ApplyMatchSize();
                            _selectedZoneIndex = prevSel;
                        });
                    }
                    menu.ShowAsContext();
                    e.Use();
                    return;
                }
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (_selectedZoneIndex >= 0 && _selectedZoneIndex < _surface.zones.Count)
                {
                    var sel = _surface.zones[_selectedZoneIndex];

                    Vector2 rotPos = GetRotationHandlePos(texRect, sel);
                    if (Vector2.Distance(rotPos, e.mousePosition) <= 9f)
                    {
                        _isDragging = true; _isRotating = true; _dragCorner = -1; _dragDirty = false;
                        _dragStart = e.mousePosition; _dragOriginalRotation = sel.rotation;
                        Repaint(); e.Use(); return;
                    }

                    int corner = HitTestRotatedCorner(texRect, sel, e.mousePosition, 9f);
                    if (corner >= 0)
                    {
                        _isDragging = true; _isRotating = false; _dragCorner = corner; _dragDirty = false;
                        _dragStart = e.mousePosition; _dragOriginalRect = sel.uvRect; _dragOriginalRotation = sel.rotation;
                        Repaint(); e.Use(); return;
                    }
                }

                int clicked = -1;
                for (int i = _surface.zones.Count - 1; i >= 0; i--)
                    if (PointInRotatedZone(texRect, _surface.zones[i], e.mousePosition)) { clicked = i; break; }
                _selectedZoneIndex = clicked;

                if (_selectedZoneIndex >= 0)
                {
                    _isDragging = true; _isRotating = false; _dragCorner = -1; _dragDirty = false;
                    _dragStart = e.mousePosition;
                    _dragOriginalRect = _surface.zones[_selectedZoneIndex].uvRect;
                    _dragOriginalRotation = _surface.zones[_selectedZoneIndex].rotation;
                }
                Repaint(); e.Use();
            }

            if (e.type == EventType.MouseDrag && _isDragging && _selectedZoneIndex >= 0)
            {
                var zone = _surface.zones[_selectedZoneIndex];

                // Record once per drag gesture so Ctrl+Z reverts the whole gesture.
                if (!_dragDirty)
                {
                    RecordSurface(_isRotating ? "Rotate Zone" : (_dragCorner >= 0 ? "Resize Zone" : "Move Zone"));
                    _dragDirty = true;
                }

                if (_isRotating)
                {
                    Vector2 center = GetZoneCanvasCenter(texRect, zone);
                    Vector2 v0 = _dragStart - center;
                    Vector2 v1 = e.mousePosition - center;
                    float a0 = Mathf.Atan2(v0.y, v0.x) * Mathf.Rad2Deg;
                    float a1 = Mathf.Atan2(v1.y, v1.x) * Mathf.Rad2Deg;
                    float delta = -(a1 - a0);
                    float newRot = _dragOriginalRotation + delta;
                    if (e.shift) newRot = Mathf.Round(newRot / 15f) * 15f;
                    while (newRot > 180f) newRot -= 360f;
                    while (newRot < -180f) newRot += 360f;
                    zone.rotation = newRot;
                }
                else if (_dragCorner >= 0)
                {
                    Vector2 startUV = CanvasPixelToUV(texRect, _dragStart);
                    Vector2 curUV = CanvasPixelToUV(texRect, e.mousePosition);
                    Vector2 deltaUV = curUV - startUV;
                    float rad = -zone.rotation * Mathf.Deg2Rad;
                    float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
                    Vector2 localDelta = new Vector2(deltaUV.x * cos - deltaUV.y * sin, deltaUV.x * sin + deltaUV.y * cos);

                    Rect r = _dragOriginalRect;
                    float xMin = r.xMin, yMin = r.yMin, xMax = r.xMax, yMax = r.yMax;
                    switch (_dragCorner)
                    {
                        case 0: xMin += localDelta.x; yMin += localDelta.y; break;
                        case 1: xMax += localDelta.x; yMin += localDelta.y; break;
                        case 2: xMax += localDelta.x; yMax += localDelta.y; break;
                        case 3: xMin += localDelta.x; yMax += localDelta.y; break;
                    }

                    const float minSize = 0.01f;
                    if (xMax - xMin < minSize) { if (_dragCorner == 0 || _dragCorner == 3) xMin = xMax - minSize; else xMax = xMin + minSize; }
                    if (yMax - yMin < minSize) { if (_dragCorner == 0 || _dragCorner == 1) yMin = yMax - minSize; else yMax = yMin + minSize; }

                    Rect resized = Rect.MinMaxRect(
                        Mathf.Clamp01(xMin), Mathf.Clamp01(yMin), Mathf.Clamp01(xMax), Mathf.Clamp01(yMax));
                    resized = SnapRect(resized);
                    zone.uvRect = MagnetToOtherCorners(resized, _selectedZoneIndex, _dragCorner);
                }
                else
                {
                    Vector2 startUV = CanvasPixelToUV(texRect, _dragStart);
                    Vector2 curUV = CanvasPixelToUV(texRect, e.mousePosition);
                    Vector2 delta = curUV - startUV;
                    Rect newRect = _dragOriginalRect;
                    newRect.position += delta;
                    newRect.x = Mathf.Clamp(newRect.x, 0f, 1f - newRect.width);
                    newRect.y = Mathf.Clamp(newRect.y, 0f, 1f - newRect.height);
                    newRect = SnapRect(newRect);
                    zone.uvRect = MagnetToOtherCorners(newRect, _selectedZoneIndex, -1);
                }

                Repaint(); e.Use();
            }
            // MouseUp is handled at the top of this method (works outside the canvas too).
        }

        private int HitTestRotatedCorner(Rect texRect, TextZone zone, Vector2 mouse, float tol)
        {
            var corners = GetZoneCanvasCorners(texRect, zone);
            for (int i = 0; i < corners.Length; i++)
                if (Vector2.Distance(corners[i], mouse) <= tol) return i;
            return -1;
        }

        private bool PointInRotatedZone(Rect texRect, TextZone zone, Vector2 mouse)
        {
            var c = GetZoneCanvasCorners(texRect, zone);
            return PointInConvexQuad(mouse, c[0], c[1], c[2], c[3]);
        }

        private static bool PointInConvexQuad(Vector2 p, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float s1 = Cross(a, b, p), s2 = Cross(b, c, p), s3 = Cross(c, d, p), s4 = Cross(d, a, p);
            bool hasNeg = (s1 < 0) || (s2 < 0) || (s3 < 0) || (s4 < 0);
            bool hasPos = (s1 > 0) || (s2 > 0) || (s3 > 0) || (s4 > 0);
            return !(hasNeg && hasPos);
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 p)
        {
            return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        }

        private void DrawZoneList(float panelHeight)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Zones", EditorStyles.boldLabel);

            float scrollH = Mathf.Max(80f, panelHeight - 28f);
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(scrollH));

            // Build parent → children (PageNumber by parentZoneId, overflow by continueTo).
            var drawn = new HashSet<int>();

            for (int i = 0; i < _surface.zones.Count; i++)
            {
                var zone = _surface.zones[i];
                // Nested page numbers are drawn under their parent PageSlot.
                if (zone.role == TextZoneRole.PageNumber && !string.IsNullOrEmpty(zone.parentZoneId))
                    continue;
                // Overflow-only children drawn under parent when linked.
                bool isOverflowChild = false;
                foreach (var p in _surface.zones)
                {
                    if (p.overflowBehavior == TextZoneOverflow.ContinueToSubZones &&
                        p.continueToZoneId == zone.id) { isOverflowChild = true; break; }
                }
                if (isOverflowChild) continue;

                DrawZoneListItem(i, indent: 0);
                drawn.Add(i);

                // Overflow child
                if (zone.overflowBehavior == TextZoneOverflow.ContinueToSubZones &&
                    !string.IsNullOrEmpty(zone.continueToZoneId))
                {
                    int ci = IndexOfZoneId(zone.continueToZoneId);
                    if (ci >= 0 && drawn.Add(ci))
                        DrawZoneListItem(ci, indent: 1);
                }

                // Page number children
                for (int j = 0; j < _surface.zones.Count; j++)
                {
                    var ch = _surface.zones[j];
                    if (ch.role == TextZoneRole.PageNumber && ch.parentZoneId == zone.id && drawn.Add(j))
                        DrawZoneListItem(j, indent: 1);
                }
            }

            // Orphans (page numbers / overflow not linked)
            for (int i = 0; i < _surface.zones.Count; i++)
            {
                if (drawn.Contains(i)) continue;
                DrawZoneListItem(i, indent: 0);
            }

            EditorGUILayout.EndScrollView();
        }

        private int IndexOfZoneId(string id)
        {
            if (string.IsNullOrEmpty(id) || _surface == null) return -1;
            for (int i = 0; i < _surface.zones.Count; i++)
                if (_surface.zones[i] != null && _surface.zones[i].id == id) return i;
            return -1;
        }

        private void DrawZoneListItem(int i, int indent)
        {
            if (i < 0 || i >= _surface.zones.Count) return;
            var zone = _surface.zones[i];

            EditorGUILayout.BeginHorizontal();
            if (indent > 0) GUILayout.Space(18f * indent);

            bool selected = (i == _selectedZoneIndex);
            var style = selected
                ? new GUIStyle(EditorStyles.helpBox) { normal = { background = Texture2D.grayTexture } }
                : EditorStyles.helpBox;

            EditorGUILayout.BeginVertical(style);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();
            string prefix = indent > 0
                ? (zone.role == TextZoneRole.PageNumber ? "# " : "\u21b3 ")
                : "";
            string newName = EditorGUILayout.TextField(prefix + "Name", zone.displayName);
            if (GUILayout.Button(selected ? "\u25cf" : "\u25cb", GUILayout.Width(24)))
                _selectedZoneIndex = i;
            EditorGUILayout.EndHorizontal();

            if (zone.role == TextZoneRole.PageNumber)
                EditorGUILayout.LabelField("PageNumber child", EditorStyles.miniLabel);

            Rect newUvRect = EditorGUILayout.RectField("UV Rect", zone.uvRect);
            float newRotation = EditorGUILayout.Slider("Rotation (\u00b0)", zone.rotation, -180f, 180f);
            bool newFlipH = EditorGUILayout.Toggle("Flip Horizontal", zone.flipHorizontal);
            bool newFlipV = EditorGUILayout.Toggle("Flip Vertical", zone.flipVertical);
            float newLineSpacing = EditorGUILayout.FloatField("Line Spacing (px)", zone.lineSpacing);

            if (EditorGUI.EndChangeCheck())
            {
                RecordSurface("Edit Zone");
                zone.displayName = newName;
                zone.uvRect = newUvRect;
                zone.rotation = newRotation;
                zone.flipHorizontal = newFlipH;
                zone.flipVertical = newFlipV;
                zone.lineSpacing = newLineSpacing;
                FlushDirty();
            }

            EditorGUILayout.BeginHorizontal();
            if (zone.role != TextZoneRole.PageNumber &&
                GUILayout.Button("\u271a Create Sub-Zone"))
                _pendingCreateSubIndex = i;
            if (zone.role == TextZoneRole.PageSlot &&
                GUILayout.Button(new GUIContent("\u271a Page #",
                    "Create a child PageNumber zone (same renderer/slot). Move it freely on the canvas.")))
                _pendingPageNumIndex = i;
            if (GUILayout.Button("Delete Zone")) _pendingDeleteIndex = i;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }


        /// <summary>Deferred destructive actions — applied OUTSIDE the layout loop.</summary>
        private void ApplyPendingActions()
        {
            if (_pendingCreateSubIndex >= 0 && _pendingCreateSubIndex < _surface.zones.Count)
            {
                RecordSurface("Create Sub-Zone");
                var sub = _surface.CreateSubZone(_surface.zones[_pendingCreateSubIndex]);
                _selectedZoneIndex = _surface.zones.IndexOf(sub);
                FlushDirty();
            }
            _pendingCreateSubIndex = -1;

            if (_pendingPageNumIndex >= 0 && _pendingPageNumIndex < _surface.zones.Count)
            {
                RecordSurface("Create Page Number Zone");
                var pn = _surface.CreatePageNumberZone(_surface.zones[_pendingPageNumIndex]);
                if (pn != null)
                    _selectedZoneIndex = _surface.zones.IndexOf(pn);
                FlushDirty();
            }
            _pendingPageNumIndex = -1;

            if (_pendingDeleteIndex >= 0 && _pendingDeleteIndex < _surface.zones.Count)
            {
                RecordSurface("Delete Zone");
                _surface.RemoveZone(_surface.zones[_pendingDeleteIndex]);
                if (_selectedZoneIndex >= _surface.zones.Count)
                    _selectedZoneIndex = _surface.zones.Count - 1;
                FlushDirty();
            }
            _pendingDeleteIndex = -1;
        }
    }
}
