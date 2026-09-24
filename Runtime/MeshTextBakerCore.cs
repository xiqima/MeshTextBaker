// Mesh Text Baker — MeshTextBakerCore.cs
// Composite pipeline. For each (renderer, materialIndex) group of zones, draws the material's
// background and each zone's TMP mesh directly into a pooled RenderTexture via GL + DrawMeshNow
// (camera-free, SRP-independent). The material samples that RenderTexture directly — no ReadPixels.
//
// Pipeline:
// 1. Resolve text per zone (localizationKey → provider → textAsset; page slots from the map).
// 2. Apply the zone's overflow behavior (ShrinkToFit / Clip / Ellipsis / ContinueToSubZones).
// 3. Group zones by (renderer, materialIndex).
// 4. Per group: get pooled RT, blit material albedo as background, draw each zone's TMP mesh.
// 5. Return the RTs; MeshTextSurface assigns them to material instances.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MeshTextBaker
{
    public static class MeshTextBakerCore
    {
        /// <summary>One baked texture for one (renderer, material slot) target.</summary>
        public struct MaterialBake
        {
            public Renderer renderer;       // which renderer this texture goes to
            public int materialIndex;       // which material slot on that renderer
            public RenderTexture texture;   // the baked albedo RenderTexture (owned by the surface's pool)
            public RenderTexture emission;  // optional baked emission RT (null when no zone glows)
            public float emissionIntensity; // HDR multiplier for the material's emissive color (>= 1)
            public RenderTexture pbrMask;   // optional baked mask RT (metallic/smoothness), null if unused
            public RenderTexture height;    // optional signed height/parallax data RT
            public float heightAmplitude;   // full map amplitude/strength used by the material
        }

        public class BakeResult
        {
            /// <summary>Generated textures, one entry per (renderer, material slot) that had zones.</summary>
            public List<MaterialBake> bakes = new List<MaterialBake>();
            public List<string> warnings = new List<string>();
            public bool success;
        }

        private static readonly List<Vector2> s_ShineVariants = new List<Vector2>(4);
        private static readonly List<float> s_HeightVariants = new List<float>(4);
        private static readonly List<MeshTextBlendMode> s_BlendVariants = new List<MeshTextBlendMode>(4);

        /// <summary>
        /// Effective PBR switch: PageSlot zones follow the surface-wide Book Use PBR toggle
        /// (one switch for the whole book); all other zones use their own Use PBR.
        /// </summary>
        private static bool EffectiveUsePbr(MeshTextSurface surface, TextZone zone)
            => zone.role == TextZoneRole.PageSlot ? surface.bookUsePbr : zone.usePbr;

        // Inline-image filtering for PBR passes: which images (by surrounding tag scope)
        // are drawn into the emission / mask-coverage targets.
        private const int ImgAll = 0;           // every image
        private const int ImgGlowScoped = 1;    // only images inside <emission> scopes
        private const int ImgShineVariant = 2;  // only images inside <surface> matching (m,s)
        private const int ImgHeightVariant = 3; // only images inside <surface> matching h

        private const int BlendImagesAll = 0;
        private const int BlendImagesUnscoped = 1;
        private const int BlendImagesVariant = 2;

        /// <summary>Per-zone prepared render data (final text + optional font size override).</summary>
        private struct PreparedZone
        {
            public string text;
            public float pixelFontSizeOverride; // <= 0 → use zone's own size
        }

        /// <summary>
        /// Dictionary/set key for one (renderer, material slot) bake target. Uses object
        /// reference equality + Object.GetHashCode() — deliberately NO GetInstanceID (removed
        /// from newer Unity APIs) and NO GetEntityId (does not exist before Unity 6), so the
        /// same code compiles from 2022.3 LTS through Unity 6.x.
        /// </summary>
        private struct RendererSlot : IEquatable<RendererSlot>
        {
            public readonly Renderer renderer;
            public readonly int materialIndex;
            public RendererSlot(Renderer r, int i) { renderer = r; materialIndex = i; }
            public bool Equals(RendererSlot o) => renderer == o.renderer && materialIndex == o.materialIndex;
            public override bool Equals(object o) => o is RendererSlot s && Equals(s);
            public override int GetHashCode()
                => ((renderer != null ? renderer.GetHashCode() : 0) * 397) ^ materialIndex;
        }

        public static BakeResult Bake(MeshTextSurface surface, string locale)
            => Bake(surface, locale, null, null);

        public static BakeResult Bake(MeshTextSurface surface, string locale, Dictionary<string, string> pageSlotTexts)
            => Bake(surface, locale, pageSlotTexts, null);

        /// <summary>
        /// Bakes zones. If <paramref name="pageSlotTexts"/> is provided, zones whose role is
        /// PageSlot take their text from that map (id → text) instead of their own textAsset.
        /// If <paramref name="zoneFilter"/> is non-null, ONLY those zones are baked (used by the
        /// book controller to re-bake just the zones of the target spread, leaving the other
        /// pages' baked textures untouched). When null, all zones are baked.
        /// Zones with <see cref="TextZone.bakeEnabled"/> false are removed from the queue even
        /// if they were explicitly filtered in; a texture that only they occupied is rebuilt
        /// from the background so the previous bake does not linger.
        /// </summary>
        public static BakeResult Bake(MeshTextSurface surface, string locale,
            Dictionary<string, string> pageSlotTexts, ICollection<TextZone> zoneFilter)
        {
            var result = new BakeResult { success = false };

            if (surface == null)
            {
                result.warnings.Add("MeshTextSurface is null.");
                return result;
            }

            var settings = surface.GetBakeSettings();

            string texProp = string.IsNullOrEmpty(settings.texturePropertyName)
                ? DetectTexturePropertyName()
                : settings.texturePropertyName;

            // ── Select zones (optionally filtered), in render order. ──
            // NOTE on filtering: a bake REDRAWS the whole texture of every touched
            // (renderer, materialIndex) target (background blit + zones). If we drew only the
            // filtered zones, any OTHER zone sharing that texture would be wiped to background.
            // So the filter selects which TEXTURES to re-bake, and we then expand the selection
            // with all sibling zones living on those textures.
            var zones = new List<TextZone>();
            if (zoneFilter != null)
            {
                var filterSet = zoneFilter as HashSet<TextZone> ?? new HashSet<TextZone>(zoneFilter);

                // Which (renderer, matIdx) targets are affected by the requested zones
                // (surface zones AND ephemeral overlays passed only via the filter, e.g. page numbers).
                var affectedTargets = new HashSet<RendererSlot>();
                foreach (var z in filterSet)
                {
                    if (z == null) continue;
                    Renderer zr = surface.GetZoneRenderer(z);
                    if (zr == null) continue;
                    affectedTargets.Add(new RendererSlot(zr, z.materialIndex));
                }
                foreach (var z in surface.zones)
                {
                    if (z == null || !filterSet.Contains(z)) continue;
                    Renderer zr = surface.GetZoneRenderer(z);
                    if (zr == null) continue;
                    affectedTargets.Add(new RendererSlot(zr, z.materialIndex));
                }

                // Always include every filter zone (even if not stored on the surface).
                foreach (var z in filterSet)
                {
                    if (z == null) continue;
                    if (!zones.Contains(z)) zones.Add(z);
                }

                // Plus every sibling zone that shares an affected texture.
                foreach (var z in surface.zones)
                {
                    if (z == null || zones.Contains(z)) continue;
                    Renderer zr = surface.GetZoneRenderer(z);
                    if (zr == null) continue;
                    if (affectedTargets.Contains(new RendererSlot(zr, z.materialIndex)))
                        zones.Add(z);
                }
            }
            else
            {
                zones.AddRange(surface.zones);
            }
            // The public list is mutable for backwards compatibility. Treat null entries as
            // absent rather than allowing Sort/prepare/grouping to throw at runtime.
            zones.RemoveAll(z => z == null);
            // Strip after sibling expansion so a disabled zone still marks its texture, then
            // leaves the draw list. Siblings that remain are redrawn; a texture with nothing
            // left is queued for a background-only rebuild.
            HashSet<RendererSlot> clearTargets = CollectAndRemoveDisabledZones(surface, zones);
            zones.Sort((a, b) => a.orderIndex.CompareTo(b.orderIndex));

            // ── Resolve final text per zone (overflow routing + overflow behavior). ──
            // Multi-pass so ContinueToSubZones works even when a child zone sorts BEFORE its
            // parent by orderIndex (manual reorder). Each pass only prepares zones whose text
            // is already known (primary sources, or overflow already routed into overflowTexts).
            var overflowTexts = new Dictionary<string, string>();
            var prepared = new Dictionary<string, PreparedZone>();

            int safety = zones.Count + 2;
            while (safety-- > 0)
            {
                bool progress = false;
                foreach (var zone in zones)
                {
                    if (zone.role == TextZoneRole.Image) continue;
                    if (prepared.ContainsKey(zone.id)) continue;

                    bool isOverflowInput = overflowTexts.ContainsKey(zone.id);

                    // OverflowOnly zones wait until a parent routes text into them.
                    if (zone.role == TextZoneRole.OverflowOnly && !isOverflowInput)
                        continue;

                    if (zone.role == TextZoneRole.PageNumber)
                        surface.ApplyPageNumberDefaults(zone);

                    string text = ResolveText(surface, zone, locale, overflowTexts, pageSlotTexts);
                    if (string.IsNullOrEmpty(text))
                    {
                        // Still mark prepared so we do not spin forever on empty OverflowOnly.
                        if (zone.role != TextZoneRole.OverflowOnly || isOverflowInput)
                        {
                            prepared[zone.id] = new PreparedZone { text = "", pixelFontSizeOverride = -1f };
                            progress = true;
                        }
                        continue;
                    }

                    // PageSlot / overflow input is already markdown+PBR-preprocessed by the parent path.
                    string processed = (zone.role == TextZoneRole.PageSlot || zone.role == TextZoneRole.PageNumber || isOverflowInput)
                        ? text
                        : (zone.markdownEnabled ? MarkdownParser.ConvertToRichText(text) : text);

                    // Convert custom layout-safe scopes once. ALWAYS strip/convert them for
                    // rich-text zones even when the corresponding effect is disabled; raw tags
                    // must never leak into visible text.
                    if (zone.richTextEnabled && zone.role != TextZoneRole.PageSlot && zone.role != TextZoneRole.PageNumber && !isOverflowInput)
                    {
                        processed = RichTextPbrTags.Preprocess(processed);
                        processed = RichTextBlendTags.Preprocess(processed);
                        processed = RichTextOpacityTags.Preprocess(processed);
                        processed = InlineFontResolver.Process(processed, settings.fontLibrary, result.warnings);
                    }

                    // Measure with the target this zone will actually be drawn into.
                    Renderer prepRenderer = surface.GetZoneRenderer(zone);
                    ResolveBakeTargetSize(surface, prepRenderer, zone.materialIndex,
                        settings, texProp, out int prepW, out int prepH);
                    prepared[zone.id] = PrepareZoneText(
                        processed, zone, settings, prepW, prepH, overflowTexts, result.warnings);
                    progress = true;
                }
                if (!progress) break;
            }

            // Any OverflowOnly that never received text → empty.
            foreach (var zone in zones)
            {
                if (zone.role == TextZoneRole.Image) continue;
                if (!prepared.ContainsKey(zone.id))
                    prepared[zone.id] = new PreparedZone { text = "", pixelFontSizeOverride = -1f };
            }

            // ── Group zones by (target renderer, materialIndex). ──
            // Keyed by RendererSlot (reference equality) — no GetInstanceID/GetEntityId, so this
            // compiles unchanged on Unity 2022.3 LTS through Unity 6.x.
            var groups = new Dictionary<RendererSlot, List<TextZone>>();

            foreach (var zone in zones)
            {
                Renderer r = surface.GetZoneRenderer(zone);
                if (r == null) continue;
                var key = new RendererSlot(r, zone.materialIndex);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<TextZone>();
                    groups[key] = list;
                }
                list.Add(zone);
            }

            // Disabled-only targets are not in `zones` anymore. Rebuild them from the
            // background so the previous bake of that zone does not stay on the material.
            foreach (RendererSlot slot in clearTargets)
            {
                if (!groups.ContainsKey(slot))
                    groups[slot] = new List<TextZone>();
            }

            foreach (var kv in groups)
            {
                var groupZones = kv.Value;
                Renderer r = kv.Key.renderer;
                int matIdx = kv.Key.materialIndex;

                // Non-square targets (custom NPOT background): WxH follows the background
                // aspect with Resolution as the long side. RTs, layout and GL all use it.
                ResolveBakeTargetSize(surface, r, matIdx, settings, texProp,
                    out int bakeW, out int bakeH);

                // Pooled RenderTexture reused across bakes; material samples it directly.
                var targetRT = surface.GetOrCreatePooledRT(r, matIdx, bakeW, bakeH);

                // Background from this renderer's material slot.
                // CRITICAL: on a re-bake the slot already holds the baked instance whose
                // albedo IS the pooled RT — reading it back would compose the new text over
                // the previous one (the "text bakes onto existing text" bug). Always prefer
                // the recorded immutable SOURCE material of this (renderer, slot).
                Material originalMat = surface.GetBakeSourceMaterial(r, matIdx);
                if (originalMat == null)
                {
                    Material[] shared = r != null ? r.sharedMaterials : null;
                    if (shared != null && matIdx >= 0 && matIdx < shared.Length) originalMat = shared[matIdx];
                }
                Texture baseTex = settings.baseAlbedoTexture;
                if (baseTex == null)
                {
                    if (originalMat != null) baseTex = GetTextureFromMaterial(originalMat, texProp);
                    if (baseTex == null) baseTex = GetMaterialTextureFrom(r, matIdx, texProp);
                    if (baseTex == targetRT) baseTex = null;
                }
                InitializeAlbedoRT(targetRT, baseTex, GetMaterialAlbedoColor(originalMat));

                // Optional EMISSION pass: a second pooled RT for this target, black background
                // (black = no glow). Created if a zone glows entirely (bakeEmission) OR its
                // text contains <glow> scopes. The HDR intensity of zone emission colors is
                // carried separately (the map stores LDR): the material multiplies by it.
                RenderTexture emissionRT = null;
                float emissionIntensity = 1f;
                foreach (var zone in groupZones)
                {
                    bool pbrOn = EffectiveUsePbr(surface, zone);
                    bool zoneGlows = (pbrOn && zone.bakeEmission) ||
                        (pbrOn && prepared.TryGetValue(zone.id, out var pz) && RichTextPbrTags.HasGlow(pz.text));
                    if (!zoneGlows) continue;
                    if (emissionRT == null)
                    {
                        emissionRT = surface.GetOrCreatePooledEmissionRT(r, matIdx, bakeW, bakeH);
                        var prevA = RenderTexture.active;
                        RenderTexture.active = emissionRT;
                        GL.Clear(true, true, Color.black);
                        RenderTexture.active = prevA;
                    }
                    float zoneIntensity = GetHdrIntensity(zone.emissionColor);
                    if (prepared.TryGetValue(zone.id, out var pgi))
                        zoneIntensity *= RichTextPbrTags.GetMaxGlowIntensity(pgi.text);
                    emissionIntensity = Mathf.Max(emissionIntensity, zoneIntensity);
                }

                // Optional PBR MASK pass: metallic/smoothness under the zone's pixels.
                // Created if a zone is fully masked (bakePbrMask) OR contains <surface> scopes.
                // Initialized from the material's existing mask map (or its scalar
                // metallic/smoothness values), then each zone composites by glyph coverage.
                RenderTexture maskRT = null;
                foreach (var zone in groupZones)
                {
                    bool pbrOn2 = EffectiveUsePbr(surface, zone);
                    bool zoneShines = (pbrOn2 && zone.bakePbrMask) ||
                        (pbrOn2 && prepared.TryGetValue(zone.id, out var pz) && RichTextPbrTags.HasShine(pz.text));
                    if (!zoneShines) continue;
                    maskRT = surface.GetOrCreatePooledMaskRT(r, matIdx, bakeW, bakeH);
                    InitializeMaskRT(maskRT, surface.GetBakeSourceMaterial(r, matIdx), r, matIdx);
                    break;
                }

                // Optional signed HEIGHT pass. h= is normalized per scope; the zone's
                // Height Amplitude converts it to physical/parallax strength. One material slot
                // needs one shared full amplitude, so use the strongest contribution in group.
                RenderTexture heightRT = null;
                float heightAmplitude = GetOriginalHeightAmplitude(surface.GetBakeSourceMaterial(r, matIdx));
                foreach (var zone in groupZones)
                {
                    if (!EffectiveUsePbr(surface, zone)) continue;
                    float maxNormalized = zone.bakeHeight ? Mathf.Abs(zone.heightValue) : 0f;
                    if (prepared.TryGetValue(zone.id, out var hp) && RichTextPbrTags.HasHeight(hp.text))
                    {
                        s_HeightVariants.Clear();
                        RichTextPbrTags.GetHeightVariants(hp.text, s_HeightVariants);
                        for (int h = 0; h < s_HeightVariants.Count; h++)
                            maxNormalized = Mathf.Max(maxNormalized, Mathf.Abs(s_HeightVariants[h]));
                    }
                    // h scales the material's own Height/Parallax slider. Do not double the
                    // amplitude: the previous 2x mapping turned the default 0.01 into 0.02 and
                    // shifted thin URP glyphs completely outside their albedo strokes.
                    heightAmplitude = Mathf.Max(heightAmplitude,
                        maxNormalized * Mathf.Max(0.0001f, zone.heightAmplitude));
                }
                if (heightAmplitude > 0f)
                {
                    bool anyHeight = false;
                    foreach (var zone in groupZones)
                    {
                        if (!EffectiveUsePbr(surface, zone)) continue;
                        if (zone.bakeHeight || (prepared.TryGetValue(zone.id, out var hp) &&
                            RichTextPbrTags.HasHeight(hp.text))) { anyHeight = true; break; }
                    }
                    if (anyHeight)
                    {
                        heightRT = surface.GetOrCreatePooledHeightRT(r, matIdx, bakeW, bakeH);
                        InitializeHeightRT(heightRT, surface.GetBakeSourceMaterial(r, matIdx), r, matIdx);
                    }
                }

                foreach (var zone in groupZones)
                {
                    // Image zones: draw a textured quad instead of TMP glyphs.
                    if (zone.role == TextZoneRole.Image)
                    {
                        if (zone.GetImageTexture() == null) continue;
                        DrawImageZoneAlbedo(targetRT, zone, bakeW, bakeH);
                        if (EffectiveUsePbr(surface, zone) && zone.bakeEmission && emissionRT != null)
                        {
                            Color emissionTint = GetLdrColor(zone.imageTint * zone.emissionColor);
                            emissionTint.a *= Mathf.Clamp01(zone.opacity);
                            DrawImageIntoTarget(emissionRT, zone, bakeW, bakeH, emissionTint);
                        }
                        if (EffectiveUsePbr(surface, zone) && zone.bakePbrMask && maskRT != null)
                            CompositeZoneIntoMask(maskRT, zone, bakeW, bakeH, settings, null, -1f);
                        if (EffectiveUsePbr(surface, zone) && zone.bakeHeight && heightRT != null)
                        {
                            float encoded = EncodeHeight(zone.heightValue, zone.heightAmplitude, heightAmplitude);
                            CompositeZoneIntoData(heightRT, zone, bakeW, bakeH, settings, null, -1f,
                                new Vector4(encoded, encoded, encoded, encoded), ImgAll, Vector2.zero, float.NaN);
                        }
                        continue;
                    }

                    if (!prepared.TryGetValue(zone.id, out var prep) || string.IsNullOrEmpty(prep.text))
                        continue;

                    DrawAlbedoText(targetRT, prep.text, zone, settings, bakeW, bakeH,
                        prep.pixelFontSizeOverride, result.warnings);

                    // Emission pass: same text laid out again with the emission color as
                    // vertex color (an LDR-safe base; HDR intensity is carried in the bake
                    // and applied to the material by ApplyBakeResult).
                    // Whole-zone glow renders everything; <glow> scopes render only the tagged
                    // words (the rest is hidden via zero-width <alpha=#00> — layout identical).
                    if (emissionRT != null)
                    {
                        string glowText = null;
                        bool zonePbr = EffectiveUsePbr(surface, zone);
                        if (zonePbr && zone.bakeEmission) glowText = prep.text;
                        else if (zonePbr && RichTextPbrTags.HasGlow(prep.text))
                            glowText = RichTextPbrTags.BuildGlowMarkup(prep.text,
                                RichTextPbrTags.GetMaxGlowIntensity(prep.text));

                        if (glowText != null)
                        {
                            var glowLayout = TextLayoutEngine.BuildZoneExact(
                                glowText, zone, settings, bakeW, bakeH, prep.pixelFontSizeOverride,
                                GetLdrColor(zone.emissionColor));
                            try
                            {
                                bool hasMeshes = glowLayout.subMeshes != null && glowLayout.subMeshes.Length > 0;
                                bool hasImages = glowLayout.inlineImages != null && glowLayout.inlineImages.Count > 0;
                                if (hasMeshes || hasImages)
                                {
                                    // Whole-zone glow: every image glows (zone-tinted).
                                    // Tag-based glow: ONLY images inside <glow> scopes.
                                    int imgMode = (zonePbr && zone.bakeEmission) ? ImgAll : ImgGlowScoped;
                                    DrawZoneIntoTarget(emissionRT, glowLayout, zone, bakeW, bakeH,
                                        imgMode, Vector2.zero, GetLdrColor(zone.emissionColor),
                                        RichTextPbrTags.GetMaxGlowIntensity(prep.text));
                                }
                            }
                            finally
                            {
                                TextLayoutEngine.DisposeLayout(glowLayout);
                            }
                        }
                    }

                    // PBR mask pass: glyph coverage → metallic/smoothness under the text.
                    // Whole zone, or per-<shine>-scope. Scopes may carry their OWN m/s
                    // (<shine m=1 s=0.9>): each distinct (m,s) variant is composited as a
                    // separate pass with its own target values.
                    if (maskRT != null)
                    {
                        bool zonePbrM = EffectiveUsePbr(surface, zone);
                        if (zonePbrM && zone.bakePbrMask)
                        {
                            CompositeZoneIntoMask(maskRT, zone, bakeW, bakeH, settings, prep.text,
                                prep.pixelFontSizeOverride, zone.maskMetallic, zone.maskSmoothness,
                                ImgAll, Vector2.zero);
                        }
                        else if (zonePbrM && RichTextPbrTags.HasShine(prep.text))
                        {
                            s_ShineVariants.Clear();
                            RichTextPbrTags.GetShineVariants(prep.text, s_ShineVariants);
                            for (int v = 0; v < s_ShineVariants.Count; v++)
                            {
                                Vector2 variant = s_ShineVariants[v];
                                string variantText = RichTextPbrTags.BuildShineMarkupForVariant(prep.text, variant);
                                float m = variant.x >= 0f ? variant.x : zone.maskMetallic;
                                float sm = variant.y >= 0f ? variant.y : zone.maskSmoothness;
                                // Images inside matching <shine> scopes contribute their
                                // alpha to THIS variant's coverage only.
                                CompositeZoneIntoMask(maskRT, zone, bakeW, bakeH, settings, variantText,
                                    prep.pixelFontSizeOverride, m, sm, ImgShineVariant, variant);
                            }
                        }
                    }

                    if (heightRT != null && EffectiveUsePbr(surface, zone))
                    {
                        if (zone.bakeHeight)
                        {
                            float encoded = EncodeHeight(zone.heightValue, zone.heightAmplitude, heightAmplitude);
                            CompositeZoneIntoData(heightRT, zone, bakeW, bakeH, settings, prep.text,
                                prep.pixelFontSizeOverride,
                                new Vector4(encoded, encoded, encoded, encoded), ImgAll,
                                Vector2.zero, float.NaN);
                        }
                        else if (RichTextPbrTags.HasHeight(prep.text))
                        {
                            s_HeightVariants.Clear();
                            RichTextPbrTags.GetHeightVariants(prep.text, s_HeightVariants);
                            for (int h = 0; h < s_HeightVariants.Count; h++)
                            {
                                float variant = s_HeightVariants[h];
                                float encoded = EncodeHeight(variant, zone.heightAmplitude, heightAmplitude);
                                string heightText = RichTextPbrTags.BuildHeightMarkupForVariant(prep.text, variant);
                                CompositeZoneIntoData(heightRT, zone, bakeW, bakeH, settings, heightText,
                                    prep.pixelFontSizeOverride,
                                    new Vector4(encoded, encoded, encoded, encoded), ImgHeightVariant,
                                    Vector2.zero, variant);
                            }
                        }
                    }
                }

                result.bakes.Add(new MaterialBake
                {
                    renderer = r,
                    materialIndex = matIdx,
                    texture = targetRT,
                    emission = emissionRT,
                    emissionIntensity = emissionIntensity,
                    pbrMask = maskRT,
                    height = heightRT,
                    heightAmplitude = heightAmplitude
                });
            }

            result.success = true;
            return result;
        }

        /// <summary>
        /// Removes zones that opted out of baking and records their targets so the next bake
        /// can clear stale pixels. Call only after the candidate list (filter + siblings) is complete.
        /// </summary>
        private static HashSet<RendererSlot> CollectAndRemoveDisabledZones(
            MeshTextSurface surface, List<TextZone> zones)
        {
            var clearTargets = new HashSet<RendererSlot>();
            for (int i = zones.Count - 1; i >= 0; i--)
            {
                TextZone zone = zones[i];
                if (zone == null || zone.bakeEnabled) continue;
                Renderer renderer = surface.GetZoneRenderer(zone);
                if (renderer != null)
                    clearTargets.Add(new RendererSlot(renderer, zone.materialIndex));
                zones.RemoveAt(i);
            }
            return clearTargets;
        }

        /// <summary>
        /// Applies the zone's overflow behavior to already-processed (markdown/rich) text.
        /// ContinueToSubZones routes the remainder into <paramref name="overflowTexts"/> with
        /// rich-text tag repair on the boundary.
        /// </summary>
        private static PreparedZone PrepareZoneText(string processed, TextZone zone,
            BakeSettings settings, int bakeW, int bakeH,
            Dictionary<string, string> overflowTexts, List<string> warnings)
        {
            var prep = new PreparedZone { text = processed, pixelFontSizeOverride = -1f };

            // Page slots are pre-sliced by the paginator — render verbatim.
            if (zone.role == TextZoneRole.PageSlot || zone.role == TextZoneRole.PageNumber) return prep;

            switch (zone.overflowBehavior)
            {
                case TextZoneOverflow.ShrinkToFit:
                {
                    int fit = TextLayoutEngine.MeasureFittingChars(processed, zone, settings, bakeW, bakeH);
                    if (fit < processed.Length)
                        prep.pixelFontSizeOverride =
                            TextLayoutEngine.FindShrinkToFitPixelSize(processed, zone, settings, bakeW, bakeH);
                    break;
                }

                case TextZoneOverflow.Clip:
                {
                    int fit = TextLayoutEngine.MeasureFittingChars(processed, zone, settings, bakeW, bakeH);
                    if (fit < processed.Length)
                    {
                        RichTextTagRepair.Split(processed, fit, out string head, out _);
                        prep.text = zone.richTextEnabled ? head : processed.Substring(0, fit);
                    }
                    break;
                }

                case TextZoneOverflow.Ellipsis:
                {
                    int fit = TextLayoutEngine.MeasureFittingChars(processed, zone, settings, bakeW, bakeH);
                    if (fit < processed.Length)
                    {
                        // Reserve a little room so the ellipsis itself still fits on the last line.
                        int cut = Mathf.Max(0, fit - 1);
                        // Never split a surrogate pair (emoji etc.).
                        if (cut > 0 && char.IsLowSurrogate(processed[cut])) cut--;
                        while (cut > 0 && char.IsWhiteSpace(processed[cut - 1])) cut--;

                        if (zone.richTextEnabled)
                        {
                            // Insert the ellipsis BEFORE the closing tags so it inherits styling.
                            string headText = processed.Substring(0, cut);
                            prep.text = headText + "\u2026" + RichTextTagRepair.GetClosingSuffix(headText);
                        }
                        else
                        {
                            prep.text = processed.Substring(0, cut) + "\u2026";
                        }
                    }
                    break;
                }

                case TextZoneOverflow.ContinueToSubZones:
                {
                    int fit = TextLayoutEngine.MeasureFittingChars(processed, zone, settings, bakeW, bakeH);
                    if (fit < processed.Length)
                    {
                        string head, tail;
                        if (zone.richTextEnabled)
                            RichTextTagRepair.Split(processed, fit, out head, out tail);
                        else
                        {
                            head = processed.Substring(0, fit);
                            tail = processed.Substring(fit);
                        }

                        prep.text = head;

                        string remaining = tail != null ? tail.TrimStart('\n', ' ') : null;
                        if (!string.IsNullOrEmpty(remaining))
                        {
                            if (!string.IsNullOrEmpty(zone.continueToZoneId))
                                overflowTexts[zone.continueToZoneId] = remaining;
                            else
                                warnings.Add(
                                    $"Zone '{zone.displayName}' overflowed with no continuation zone. " +
                                    $"{remaining.Length} chars dropped.");
                        }
                    }
                    break;
                }
            }

            return prep;
        }

        // DrawMeshNow inherits several pieces of state from Camera.current. In the Editor that
        // can be the SceneView camera, so merely focusing Scene used to inject its view matrix,
        // viewport and _ScreenParams into a bake. TMP's SDF shader explicitly uses
        // UNITY_MATRIX_P * _ScreenParams to calculate glyph sharpness, hence the focus-dependent
        // distorted/faint text and, after switching views, occasionally empty pages.
        //
        // Keep every immediate draw in a fully specified, camera-neutral state and restore the
        // host view afterwards. Camera.SetupCurrent(null) also works around Unity issue IN-38022
        // (Scene view camera hijacks GL view matrix with Graphics.DrawMeshNow).
        private static readonly int ScreenParamsId = Shader.PropertyToID("_ScreenParams");
        private static readonly int ScaledScreenParamsId = Shader.PropertyToID("_ScaledScreenParams");
        private static readonly int ProjectionParamsId = Shader.PropertyToID("_ProjectionParams");
        private static readonly int OrthoParamsId = Shader.PropertyToID("unity_OrthoParams");

        private struct ImmediateDrawState
        {
            public RenderTexture active;
            public Camera camera;
            public Rect viewport;
            public bool invertCulling;
            public bool wireframe;
            public bool srgbWrite;
            public Vector4 screenParams;
            public Vector4 scaledScreenParams;
            public Vector4 projectionParams;
            public Vector4 orthoParams;
        }

        private static ImmediateDrawState BeginImmediateDraw(RenderTexture target, int bakeW, int bakeH)
        {
            Camera current = Camera.current;
            Rect previousViewport;
            if (current != null)
                previousViewport = current.pixelRect;
            else if (RenderTexture.active != null)
                previousViewport = new Rect(0f, 0f, RenderTexture.active.width, RenderTexture.active.height);
            else
                previousViewport = new Rect(0f, 0f, Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));

            var state = new ImmediateDrawState
            {
                active = RenderTexture.active,
                camera = current,
                viewport = previousViewport,
                invertCulling = GL.invertCulling,
                wireframe = GL.wireframe,
                srgbWrite = GL.sRGBWrite,
                screenParams = Shader.GetGlobalVector(ScreenParamsId),
                scaledScreenParams = Shader.GetGlobalVector(ScaledScreenParamsId),
                projectionParams = Shader.GetGlobalVector(ProjectionParamsId),
                orthoParams = Shader.GetGlobalVector(OrthoParamsId)
            };

            // Save host matrices BEFORE detaching Camera.current; SetupCurrent can replace the
            // active view matrix on affected Editor versions.
            GL.PushMatrix();

            // DrawMeshNow otherwise silently multiplies our matrices by the current Scene/Game
            // camera on affected Unity versions. There is no camera involved in this bake.
            Camera.SetupCurrent(null);
            Graphics.SetRenderTarget(target);
            GL.Viewport(new Rect(0f, 0f, bakeW, bakeH));
            GL.invertCulling = false;
            GL.wireframe = false;
            GL.sRGBWrite = target != null && target.sRGB;

            // Pixel-space ortho: x in [0..bakeW], y in [0..bakeH], origin bottom-left (matches Texture2D).
            // GL.LoadProjectionMatrix performs the platform-specific D3D/Metal adjustments.
            GL.LoadProjectionMatrix(Matrix4x4.Ortho(0, bakeW, 0, bakeH, -100f, 100f));
            GL.modelview = Matrix4x4.identity;

            float invW = 1f / Mathf.Max(1, bakeW);
            float invH = 1f / Mathf.Max(1, bakeH);
            Vector4 screen = new Vector4(bakeW, bakeH, 1f + invW, 1f + invH);
            Shader.SetGlobalVector(ScreenParamsId, screen);
            Shader.SetGlobalVector(ScaledScreenParamsId, screen);
            Shader.SetGlobalVector(ProjectionParamsId, new Vector4(1f, -100f, 100f, 0.01f));
            Shader.SetGlobalVector(OrthoParamsId, new Vector4(bakeW, bakeH, 0f, 1f));
            return state;
        }

        private static void EndImmediateDraw(ImmediateDrawState state)
        {
            GL.PopMatrix();

            // Restore the render destination/current camera first: either operation may update
            // built-in GPU state, so exact saved flags/globals are written back afterwards.
            Graphics.SetRenderTarget(state.active);
            GL.Viewport(state.viewport);
            Camera.SetupCurrent(state.camera);

            GL.invertCulling = state.invertCulling;
            GL.wireframe = state.wireframe;
            GL.sRGBWrite = state.srgbWrite;
            Shader.SetGlobalVector(ScreenParamsId, state.screenParams);
            Shader.SetGlobalVector(ScaledScreenParamsId, state.scaledScreenParams);
            Shader.SetGlobalVector(ProjectionParamsId, state.projectionParams);
            Shader.SetGlobalVector(OrthoParamsId, state.orthoParams);
        }

        // ── Albedo initialization / blending ─────────────────────
        private static Material s_AlbedoInitMat;

        private static Color GetMaterialAlbedoColor(Material material)
        {
            if (material == null) return Color.white;
            if (material.HasProperty("_BaseColor")) return material.GetColor("_BaseColor");
            if (material.HasProperty("_Color")) return material.GetColor("_Color");
            return Color.white;
        }

        private static Material GetAlbedoInitMaterial()
        {
            if (s_AlbedoInitMat != null) return s_AlbedoInitMat;
            Shader shader = Shader.Find("Hidden/MeshTextBaker/AlbedoInit");
            if (shader == null) return null;
            s_AlbedoInitMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return s_AlbedoInitMat;
        }

        private static void InitializeAlbedoRT(RenderTexture target, Texture source, Color tint)
        {
            RenderTexture previous = RenderTexture.active;
            if (source != null)
            {
                Material material = GetAlbedoInitMaterial();
                if (material != null)
                {
                    material.SetColor("_Tint", tint);
                    Graphics.Blit(source, target, material);
                }
                else Graphics.Blit(source, target);
            }
            else
            {
                RenderTexture.active = target;
                GL.Clear(true, true, tint);
            }
            RenderTexture.active = previous;
        }

        /// <summary>Generated albedo already contains the original material tint.</summary>
        public static void SetMaterialAlbedoColorWhite(Material material)
        {
            if (material == null) return;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        }

        private static Material s_BlendCompositeMat;
        private static RenderTexture s_BlendLayerRT;

        private static Material GetBlendCompositeMaterial()
        {
            if (s_BlendCompositeMat != null) return s_BlendCompositeMat;
            Shader shader = Shader.Find("Hidden/MeshTextBaker/BlendComposite");
            if (shader == null)
            {
                Debug.LogError("[MeshTextBaker] BlendComposite shader not found; custom blend modes disabled.");
                return null;
            }
            s_BlendCompositeMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return s_BlendCompositeMat;
        }

        private static RenderTexture GetBlendLayer(int bakeW, int bakeH)
        {
            if (s_BlendLayerRT != null && s_BlendLayerRT.width == bakeW && s_BlendLayerRT.height == bakeH)
                return s_BlendLayerRT;
            if (s_BlendLayerRT != null)
            {
                s_BlendLayerRT.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(s_BlendLayerRT);
                else UnityEngine.Object.DestroyImmediate(s_BlendLayerRT);
            }
            s_BlendLayerRT = new RenderTexture(bakeW, bakeH, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "MTB_BlendLayer",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            s_BlendLayerRT.Create();
            return s_BlendLayerRT;
        }

        private static void ClearTransparent(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = previous;
        }

        private static void CompositeBlendLayer(RenderTexture target, RenderTexture layer,
            MeshTextBlendMode mode, int bakeW, int bakeH)
        {
            Material mat = GetBlendCompositeMaterial();
            if (mat == null) { Graphics.Blit(layer, target); return; }
            RenderTexture temp = RenderTexture.GetTemporary(bakeW, bakeH, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(target, temp);
            mat.SetTexture("_LayerTex", layer);
            mat.SetInt("_Mode", (int)mode);
            Graphics.Blit(temp, target, mat);
            RenderTexture.ReleaseTemporary(temp);
        }

        private static void DrawAlbedoText(RenderTexture target, string text, TextZone zone,
            BakeSettings settings, int bakeW, int bakeH, float fontSizeOverride, List<string> warnings)
        {
            bool hasScopes = RichTextBlendTags.HasBlendScopes(text);
            if (!hasScopes)
            {
                DrawAlbedoTextPass(target, text, zone, settings, bakeW, bakeH, fontSizeOverride,
                    zone.blendMode, BlendImagesAll, MeshTextBlendMode.Normal, warnings);
                return;
            }

            string baseText = RichTextBlendTags.BuildBaseMarkup(text);
            DrawAlbedoTextPass(target, baseText, zone, settings, bakeW, bakeH, fontSizeOverride,
                zone.blendMode, BlendImagesUnscoped, MeshTextBlendMode.Normal, warnings);

            s_BlendVariants.Clear();
            RichTextBlendTags.GetVariants(text, s_BlendVariants);
            for (int i = 0; i < s_BlendVariants.Count; i++)
            {
                MeshTextBlendMode variant = s_BlendVariants[i];
                string variantText = RichTextBlendTags.BuildVariantMarkup(text, variant);
                DrawAlbedoTextPass(target, variantText, zone, settings, bakeW, bakeH, fontSizeOverride,
                    variant, BlendImagesVariant, variant, warnings);
            }
        }

        private static void DrawAlbedoTextPass(RenderTexture target, string text, TextZone zone,
            BakeSettings settings, int bakeW, int bakeH, float fontSizeOverride, MeshTextBlendMode mode,
            int blendImageMode, MeshTextBlendMode blendVariant, List<string> warnings)
        {
            ZoneLayoutResult layout = TextLayoutEngine.BuildZoneExact(
                text, zone, settings, bakeW, bakeH, fontSizeOverride);
            try
            {
                if (layout.warnings != null) warnings.AddRange(layout.warnings);
                bool hasMeshes = layout.subMeshes != null && layout.subMeshes.Length > 0;
                bool hasImages = layout.inlineImages != null && layout.inlineImages.Count > 0;
                if (!hasMeshes && !hasImages) return;
                if (mode == MeshTextBlendMode.Normal)
                {
                    DrawZoneIntoTarget(target, layout, zone, bakeW, bakeH, ImgAll, Vector2.zero,
                        Color.white, 1f, blendImageMode, blendVariant, float.NaN);
                }
                else
                {
                    RenderTexture layer = GetBlendLayer(bakeW, bakeH);
                    ClearTransparent(layer);
                    DrawZoneIntoTarget(layer, layout, zone, bakeW, bakeH, ImgAll, Vector2.zero,
                        Color.white, 1f, blendImageMode, blendVariant, float.NaN);
                    CompositeBlendLayer(target, layer, mode, bakeW, bakeH);
                }
            }
            finally
            {
                TextLayoutEngine.DisposeLayout(layout);
            }
        }

        private static void DrawImageZoneAlbedo(RenderTexture target, TextZone zone, int bakeW, int bakeH)
        {
            Color tint = zone.imageTint;
            tint.a *= Mathf.Clamp01(zone.opacity);
            if (zone.blendMode == MeshTextBlendMode.Normal)
            {
                DrawImageIntoTarget(target, zone, bakeW, bakeH, tint);
                return;
            }
            RenderTexture layer = GetBlendLayer(bakeW, bakeH);
            ClearTransparent(layer);
            DrawImageIntoTarget(layer, zone, bakeW, bakeH, tint);
            CompositeBlendLayer(target, layer, zone.blendMode, bakeW, bakeH);
        }

        /// <summary>
        /// Draws one zone's TMP sub-meshes into the target RT, positioned by a model matrix
        /// (zone center in pixels, rotation, 1 unit == 1 pixel). Alpha-blends over the background.
        /// </summary>
        private static void DrawZoneIntoTarget(RenderTexture targetRT, ZoneLayoutResult layout, TextZone zone, int bakeW, int bakeH)
            => DrawZoneIntoTarget(targetRT, layout, zone, bakeW, bakeH, ImgAll, Vector2.zero, Color.white, 1f);

        private static void DrawZoneIntoTarget(RenderTexture targetRT, ZoneLayoutResult layout, TextZone zone, int bakeW, int bakeH,
            int imageMode, Vector2 shineVariant, Color glowTint, float maxGlowIntensity)
            => DrawZoneIntoTarget(targetRT, layout, zone, bakeW, bakeH, imageMode, shineVariant,
                glowTint, maxGlowIntensity, BlendImagesAll, MeshTextBlendMode.Normal, float.NaN);

        private static void DrawZoneIntoTarget(RenderTexture targetRT, ZoneLayoutResult layout, TextZone zone, int bakeW, int bakeH,
            int imageMode, Vector2 shineVariant, Color glowTint, float maxGlowIntensity,
            int blendImageMode, MeshTextBlendMode blendVariant, float heightVariant)
        {
            Vector2 center = zone.GetPixelCenter(bakeW, bakeH);
            Quaternion rot = Quaternion.Euler(0, 0, zone.rotation);
            // Per-zone mirror: negative model scale flips glyphs AND inline-image quads
            // (they share this matrix). An odd mirror count reverses triangle winding, so
            // the cull state is counter-flipped — Begin/EndImmediateDraw save & restore it.
            Vector3 mirrorScale = new Vector3(
                zone.flipHorizontal ? -1f : 1f, zone.flipVertical ? -1f : 1f, 1f);
            Matrix4x4 model = Matrix4x4.TRS(new Vector3(center.x, center.y, 0f), rot, mirrorScale);

            ImmediateDrawState drawState = BeginImmediateDraw(targetRT, bakeW, bakeH);
            if (zone.flipHorizontal ^ zone.flipVertical) GL.invertCulling = true;
            try
            {
                // Inline images with under=true draw BEFORE the glyphs (image behind the text).
                DrawInlineImages(layout, model, true, imageMode, shineVariant, glowTint,
                    maxGlowIntensity, blendImageMode, blendVariant, heightVariant);

                for (int i = 0; i < layout.subMeshes.Length; i++)
                {
                    var sm = layout.subMeshes[i];
                    if (sm.mesh == null || sm.material == null) continue;
                    if (sm.material.SetPass(0))
                        Graphics.DrawMeshNow(sm.mesh, model);
                }

                // Regular inline images draw over the glyph layer at the tag position.
                DrawInlineImages(layout, model, false, imageMode, shineVariant, glowTint,
                    maxGlowIntensity, blendImageMode, blendVariant, heightVariant);
            }
            finally
            {
                EndImmediateDraw(drawState);
            }
        }

        /// <summary>Textured quads in the SAME local space as the glyphs (mesh-local rects
        /// from the layout engine) — zone rotation/centering applies identically.</summary>
        private static void DrawInlineImages(ZoneLayoutResult layout, Matrix4x4 model, bool underLayer,
            int imageMode, Vector2 shineVariant, Color glowTint, float maxGlowIntensity,
            int blendImageMode, MeshTextBlendMode blendVariant, float heightVariant)
        {
            if (layout.inlineImages == null) return;
            Material mat = null;
            for (int i = 0; i < layout.inlineImages.Count; i++)
            {
                var img = layout.inlineImages[i];
                if (img.texture == null || img.underText != underLayer) continue;

                if (blendImageMode == BlendImagesUnscoped && img.hasBlendMode) continue;
                if (blendImageMode == BlendImagesVariant &&
                    (!img.hasBlendMode || img.blendMode != blendVariant)) continue;

                // PBR passes draw an image ONLY when the tag sits inside a matching scope
                // (albedo/whole-zone passes draw everything).
                Color tint = Color.white;
                if (imageMode == ImgGlowScoped)
                {
                    if (img.scope != ImagePbrScope.Glow) continue;
                    // Scope color (or the zone glow tint) dimmed by i/maxI — same relative
                    // brightness rule as glyphs.
                    tint = glowTint;
                    if (!string.IsNullOrEmpty(img.scopeColorHex) &&
                        ColorUtility.TryParseHtmlString("#" + img.scopeColorHex, out var sc))
                        tint = sc;
                    float dim = maxGlowIntensity > 0f
                        ? Mathf.Min(1f, img.scopeIntensity / maxGlowIntensity) : 1f;
                    tint *= dim;
                    tint.a = 1f;
                }
                else if (imageMode == ImgShineVariant)
                {
                    if (img.scope != ImagePbrScope.Shine) continue;
                    if (!Mathf.Approximately(img.scopeMetallic, shineVariant.x) ||
                        !Mathf.Approximately(img.scopeSmoothness, shineVariant.y)) continue;
                    // Coverage pass: alpha matters; keep the image's own alpha, white RGB.
                }
                else if (imageMode == ImgHeightVariant)
                {
                    if (img.scope != ImagePbrScope.Shine || float.IsNaN(img.scopeHeight) ||
                        !Mathf.Approximately(img.scopeHeight, heightVariant)) continue;
                }

                tint.a *= Mathf.Clamp01(img.opacity);

                if (mat == null) mat = GetImageBlitMaterial();
                mat.mainTexture = img.texture;
                mat.color = Color.white;
                if (mat.SetPass(0))
                {
                    Rect rct = img.localRect;
                    GL.Begin(GL.QUADS);
                    GL.Color(tint);
                    GL.TexCoord2(0, 0); GLVertexLocal(model, rct.xMin, rct.yMin);
                    GL.TexCoord2(1, 0); GLVertexLocal(model, rct.xMax, rct.yMin);
                    GL.TexCoord2(1, 1); GLVertexLocal(model, rct.xMax, rct.yMax);
                    GL.TexCoord2(0, 1); GLVertexLocal(model, rct.xMin, rct.yMax);
                    GL.End();
                }
            }
        }

        private static void GLVertexLocal(Matrix4x4 model, float x, float y)
        {
            Vector3 p = model.MultiplyPoint3x4(new Vector3(x, y, 0f));
            GL.Vertex3(p.x, p.y, p.z);
        }

        private static string ResolveText(MeshTextSurface surface, TextZone zone, string locale,
            Dictionary<string, string> overflowTexts, Dictionary<string, string> pageSlotTexts)
        {
            // Book flow: page-slot and page-number zones get streamed text from the map.
            if (zone.role == TextZoneRole.PageSlot || zone.role == TextZoneRole.PageNumber)
            {
                if (pageSlotTexts != null && pageSlotTexts.TryGetValue(zone.id, out string pageText))
                    return pageText;
                return "";
            }

            if (overflowTexts != null && overflowTexts.TryGetValue(zone.id, out string overflow))
                return overflow;
            if (zone.role == TextZoneRole.OverflowOnly)
                return "";

            // Explicit key wins; otherwise a MeshTextAsset key lets external files / Unity
            // String Tables override embedded authoring text without changing every zone.
            string key = !string.IsNullOrWhiteSpace(zone.localizationKey) ? zone.localizationKey
                : (zone.textAsset != null ? zone.textAsset.key : "");
            LocalizedTextResult resolved = surface.ResolveLocalizedText(key, zone.textAsset, locale);
            return resolved.found ? resolved.text : "";
        }

        private static Texture GetMaterialTextureFrom(Renderer mr, int materialIndex, string preferredProp)
        {
            if (mr == null) return null;

            var mats = mr.sharedMaterials;
            if (materialIndex < 0 || materialIndex >= mats.Length) return null;

            return GetTextureFromMaterial(mats[materialIndex], preferredProp);
        }

        private static Texture GetTextureFromMaterial(Material mat, string preferredProp)
        {
            if (mat == null) return null;

            if (!string.IsNullOrEmpty(preferredProp) && mat.HasProperty(preferredProp))
            {
                var t = mat.GetTexture(preferredProp);
                if (t != null) return t;
            }

            string[] fallbacks = { "_BaseColorMap", "_BaseMap", "_MainTex" };
            foreach (var p in fallbacks)
            {
                if (p == preferredProp) continue;
                if (mat.HasProperty(p))
                {
                    var t = mat.GetTexture(p);
                    if (t != null) return t;
                }
            }
            return mat.mainTexture;
        }

        /// <summary>
        /// Resolves the WxH bake target for one (renderer, materialIndex) target. The LONG side
        /// comes from Bake Settings Resolution; the aspect follows the background texture
        /// (Base Albedo Texture override, else the source material's texture, else the live
        /// slot's texture). Square when no background texture exists. Pagination, measurement
        /// and drawing all share this so pixels always agree.
        /// </summary>
        public static void ResolveBakeTargetSize(MeshTextSurface surface, Renderer renderer,
            int materialIndex, BakeSettings settings, string texProp, out int bakeW, out int bakeH)
        {
            int longSide = Mathf.Max(8, (int)settings.resolution);
            bakeW = longSide;
            bakeH = longSide;

            Texture baseTex = settings.baseAlbedoTexture;
            if (baseTex == null && surface != null)
            {
                Material src = surface.GetBakeSourceMaterial(renderer, materialIndex);
                if (src != null) baseTex = GetTextureFromMaterial(src, texProp);
                if (baseTex == null)
                    baseTex = GetMaterialTextureFrom(renderer, materialIndex, texProp);
            }

            if (baseTex != null && baseTex.width > 0 && baseTex.height > 0)
            {
                if (baseTex.width >= baseTex.height)
                {
                    bakeW = longSide;
                    bakeH = Mathf.Max(8, Mathf.RoundToInt(
                        (float)longSide * baseTex.height / baseTex.width));
                }
                else
                {
                    bakeH = longSide;
                    bakeW = Mathf.Max(8, Mathf.RoundToInt(
                        (float)longSide * baseTex.width / baseTex.height));
                }
            }
        }

        /// <summary>Best-guess albedo texture of a renderer's material slot (public for editor tools).</summary>
        public static Texture GetAlbedoTexture(Renderer renderer, int materialIndex, string preferredProp = null)
        {
            if (string.IsNullOrEmpty(preferredProp)) preferredProp = DetectTexturePropertyName();
            return GetMaterialTextureFrom(renderer, materialIndex, preferredProp);
        }

        /// <summary>Normalizes an HDR color to LDR for vertex-color use (intensity clamped).</summary>
        private static Color GetLdrColor(Color hdr)
        {
            float m = Mathf.Max(hdr.r, Mathf.Max(hdr.g, hdr.b));
            if (m <= 1f) return new Color(hdr.r, hdr.g, hdr.b, 1f);
            return new Color(hdr.r / m, hdr.g / m, hdr.b / m, 1f);
        }

        /// <summary>HDR intensity of a color (max channel above 1, else 1).</summary>
        private static float GetHdrIntensity(Color hdr)
            => Mathf.Max(1f, Mathf.Max(hdr.r, Mathf.Max(hdr.g, hdr.b)));

        // ── PBR mask compositing ─────────────────────────────────

        private static Material s_MaskCompositeMat;
        private static RenderTexture s_CoverageRT; // scratch coverage buffer, resized on demand

        private static Material GetMaskCompositeMaterial()
        {
            if (s_MaskCompositeMat != null) return s_MaskCompositeMat;
            var shader = Shader.Find("Hidden/MeshTextBaker/MaskComposite");
            if (shader == null)
            {
                Debug.LogError("[MeshTextBaker] Shader 'Hidden/MeshTextBaker/MaskComposite' not found " +
                               "(should live in a Resources folder). PBR mask baking disabled.");
                return null;
            }
            s_MaskCompositeMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return s_MaskCompositeMat;
        }

        /// <summary>
        /// Fills the mask RT with the material's existing mask map. When there is no map,
        /// encodes the material's scalar metallic/smoothness values into the background so
        /// enabling a generated map changes only covered glyphs, not the rest of the sheet.
        /// HDRP's untouched channels stay neutral: AO=1, detail=0.5.
        /// </summary>
        private static void InitializeMaskRT(RenderTexture maskRT, Material originalMat, Renderer r, int matIdx)
        {
            // Same re-bake feedback hazard as the albedo background: prefer the original
            // material; the live slot may hold the baked instance whose mask IS maskRT.
            Texture existing = null;
            string maskProp = null;
            var mats = r != null ? r.sharedMaterials : null;
            Material m = originalMat;
            if (m == null && mats != null && matIdx >= 0 && matIdx < mats.Length) m = mats[matIdx];
            if (m != null)
            {
                maskProp = DetectMaskPropertyName(m);
                if (maskProp != null && m.HasProperty(maskProp)) existing = m.GetTexture(maskProp);
            }
            if (existing == maskRT) existing = null; // self-feedback guard

            var prevA = RenderTexture.active;
            if (existing != null)
            {
                Graphics.Blit(existing, maskRT);
            }
            else
            {
                RenderTexture.active = maskRT;

                float metallic = GetMaterialFloat(m, "_Metallic", 0f);
                float smoothness;
                if (m != null && m.HasProperty("_Smoothness"))       // URP / HDRP Lit
                    smoothness = m.GetFloat("_Smoothness");
                else if (m != null && m.HasProperty("_Glossiness")) // Built-in Standard
                    smoothness = m.GetFloat("_Glossiness");
                else
                    smoothness = 0.5f;

                // _MaskMap: R=metallic, G=AO, B=detail, A=smoothness.
                // _MetallicGlossMap uses R/A and safely ignores G/B.
                GL.Clear(true, true, new Color(
                    Mathf.Clamp01(metallic), 1f, 0.5f, Mathf.Clamp01(smoothness)));
            }
            RenderTexture.active = prevA;
        }

        private static float GetMaterialFloat(Material mat, string property, float fallback)
            => mat != null && mat.HasProperty(property) ? mat.GetFloat(property) : fallback;

        /// <summary>
        /// Renders the zone's coverage (glyphs in white, or the image's alpha) into a scratch
        /// buffer, then composites (metallic, AO, detail, smoothness) into the mask RT wherever
        /// coverage is non-zero. Text zones pass <paramref name="preparedText"/>; image zones
        /// pass null (the image is drawn instead).
        /// </summary>
        private static void CompositeZoneIntoMask(RenderTexture maskRT, TextZone zone, int bakeW, int bakeH,
            BakeSettings settings, string preparedText, float pixelFontSizeOverride)
            => CompositeZoneIntoMask(maskRT, zone, bakeW, bakeH, settings, preparedText,
                pixelFontSizeOverride, zone.maskMetallic, zone.maskSmoothness);

        private static void CompositeZoneIntoMask(RenderTexture maskRT, TextZone zone, int bakeW, int bakeH,
            BakeSettings settings, string preparedText, float pixelFontSizeOverride,
            float metallic, float smoothness)
            => CompositeZoneIntoMask(maskRT, zone, bakeW, bakeH, settings, preparedText,
                pixelFontSizeOverride, metallic, smoothness, ImgAll, Vector2.zero);

        private static void CompositeZoneIntoMask(RenderTexture maskRT, TextZone zone, int bakeW, int bakeH,
            BakeSettings settings, string preparedText, float pixelFontSizeOverride,
            float metallic, float smoothness, int imageMode, Vector2 shineVariant)
            => CompositeZoneIntoData(maskRT, zone, bakeW, bakeH, settings, preparedText,
                pixelFontSizeOverride, new Vector4(metallic, 1f, 0.5f, smoothness),
                imageMode, shineVariant, float.NaN);

        private static void CompositeZoneIntoData(RenderTexture dataRT, TextZone zone, int bakeW, int bakeH,
            BakeSettings settings, string preparedText, float pixelFontSizeOverride,
            Vector4 targetValues, int imageMode, Vector2 shineVariant, float heightVariant)
        {
            var compositeMat = GetMaskCompositeMaterial();
            if (compositeMat == null) return;

            // 1. Coverage buffer (transparent black; alpha = glyph/image coverage).
            if (s_CoverageRT == null || s_CoverageRT.width != bakeW || s_CoverageRT.height != bakeH)
            {
                if (s_CoverageRT != null) { s_CoverageRT.Release(); UnityEngine.Object.DestroyImmediate(s_CoverageRT); }
                s_CoverageRT = new RenderTexture(bakeW, bakeH, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                { hideFlags = HideFlags.DontSave };
                s_CoverageRT.Create();
            }

            var prevA = RenderTexture.active;
            RenderTexture.active = s_CoverageRT;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = prevA;

            if (zone.role == TextZoneRole.Image)
            {
                Color coverageTint = Color.white;
                coverageTint.a = Mathf.Clamp01(zone.opacity);
                DrawImageIntoTarget(s_CoverageRT, zone, bakeW, bakeH, coverageTint);
            }
            else if (!string.IsNullOrEmpty(preparedText))
            {
                var layout = TextLayoutEngine.BuildZoneExact(
                    preparedText, zone, settings, bakeW, bakeH, pixelFontSizeOverride, Color.white);
                try
                {
                    bool hasMeshes = layout.subMeshes != null && layout.subMeshes.Length > 0;
                    bool hasImages = layout.inlineImages != null && layout.inlineImages.Count > 0;
                    if (hasMeshes || hasImages)
                        DrawZoneIntoTarget(s_CoverageRT, layout, zone, bakeW, bakeH,
                            imageMode, shineVariant, Color.white, 1f,
                            BlendImagesAll, MeshTextBlendMode.Normal, heightVariant);
                }
                finally
                {
                    TextLayoutEngine.DisposeLayout(layout);
                }
            }

            // 2. Composite: mask = lerp(mask, target, coverage). Ping-pong via a temp RT
            //    because a texture cannot be read and written in the same blit.
            var temp = RenderTexture.GetTemporary(bakeW, bakeH, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(dataRT, temp);

            compositeMat.SetTexture("_CoverTex", s_CoverageRT);
            compositeMat.SetVector("_TargetValues", targetValues);
            Graphics.Blit(temp, dataRT, compositeMat);

            RenderTexture.ReleaseTemporary(temp);
        }

        private static float EncodeHeight(float normalizedHeight, float zoneAmplitude, float fullAmplitude)
        {
            if (fullAmplitude <= 0.000001f) return 0.5f;
            float signedStrength = Mathf.Clamp(normalizedHeight, -1f, 1f) * Mathf.Max(0f, zoneAmplitude);
            // 0.5 is neutral. The strongest signed contribution reaches 0 or 1 while the
            // material slider remains exactly Height Amplitude (rather than the old 2x value).
            return Mathf.Clamp01(0.5f + 0.5f * signedStrength / fullAmplitude);
        }

        private static float GetOriginalHeightAmplitude(Material mat)
        {
            if (mat == null) return 0f;
            string prop = DetectHeightPropertyName(mat);
            if (string.IsNullOrEmpty(prop) || !mat.HasProperty(prop) || mat.GetTexture(prop) == null)
                return 0f;
            if (mat.HasProperty("_HeightAmplitude")) return Mathf.Abs(mat.GetFloat("_HeightAmplitude"));
            if (mat.HasProperty("_Parallax")) return Mathf.Abs(mat.GetFloat("_Parallax"));
            if (mat.HasProperty("_HeightScale")) return Mathf.Abs(mat.GetFloat("_HeightScale"));
            return 0.02f;
        }

        private static void InitializeHeightRT(RenderTexture heightRT, Material originalMat,
            Renderer renderer, int materialIndex)
        {
            Material mat = originalMat;
            if (mat == null && renderer != null)
            {
                Material[] materials = renderer.sharedMaterials;
                if (materialIndex >= 0 && materialIndex < materials.Length) mat = materials[materialIndex];
            }
            Texture existing = null;
            string prop = DetectHeightPropertyName(mat);
            if (mat != null && !string.IsNullOrEmpty(prop) && mat.HasProperty(prop))
                existing = mat.GetTexture(prop);
            if (existing == heightRT) existing = null;

            RenderTexture previous = RenderTexture.active;
            if (existing != null) Graphics.Blit(existing, heightRT);
            else
            {
                RenderTexture.active = heightRT;
                // Signed neutral height: (sample - 0.5) * amplitude == 0.
                GL.Clear(true, true, new Color(0.5f, 0.5f, 0.5f, 0.5f));
            }
            RenderTexture.active = previous;
        }

        /// <summary>Height property for HDRP/custom shaders or Built-in Standard parallax.</summary>
        public static string DetectHeightPropertyName(Material mat)
        {
            if (mat != null)
            {
                if (mat.HasProperty("_HeightMap")) return "_HeightMap";
                if (mat.HasProperty("_ParallaxMap")) return "_ParallaxMap";
            }
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != null && rp.GetType().Name.Contains("HD")) return "_HeightMap";
            return "_ParallaxMap";
        }

        /// <summary>Mask map property for the material's shader (HDRP vs URP/Built-in).</summary>
        public static string DetectMaskPropertyName(Material mat)
        {
            if (mat != null)
            {
                if (mat.HasProperty("_MaskMap")) return "_MaskMap";               // HDRP Lit
                if (mat.HasProperty("_MetallicGlossMap")) return "_MetallicGlossMap"; // URP/Standard
            }
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != null && rp.GetType().Name.Contains("HD")) return "_MaskMap";
            return "_MetallicGlossMap";
        }

        // Reusable unlit-transparent material for image quads (GL pass).
        private static Material s_ImageBlitMat;

        /// <summary>Destroys shared GL materials and scratch RTs (domain reload / cleanup).</summary>
        public static void ReleaseSharedResources()
        {
            DestroyShared(ref s_ImageBlitMat);
            DestroyShared(ref s_MaskCompositeMat);
            DestroyShared(ref s_BlendCompositeMat);
            DestroyShared(ref s_AlbedoInitMat);
            if (s_BlendLayerRT != null)
            {
                s_BlendLayerRT.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(s_BlendLayerRT);
                else UnityEngine.Object.DestroyImmediate(s_BlendLayerRT);
                s_BlendLayerRT = null;
            }
            if (s_CoverageRT != null)
            {
                s_CoverageRT.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(s_CoverageRT);
                else UnityEngine.Object.DestroyImmediate(s_CoverageRT);
                s_CoverageRT = null;
            }
        }

        private static void DestroyShared(ref Material mat)
        {
            if (mat == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(mat);
            else UnityEngine.Object.DestroyImmediate(mat);
            mat = null;
        }

        private static Material GetImageBlitMaterial()
        {
            if (s_ImageBlitMat != null) return s_ImageBlitMat;
            // Built-in "Sprites/Default" exists in every pipeline install and does exactly
            // what we need: tint * texture with standard alpha blending, no lighting.
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            s_ImageBlitMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return s_ImageBlitMat;
        }

        /// <summary>
        /// Draws an image zone as a textured quad into the target RT (same pixel-space ortho
        /// setup as text). Honors zone rotation and optional aspect-ratio preservation.
        /// </summary>
        private static void DrawImageIntoTarget(RenderTexture targetRT, TextZone zone, int bakeW, int bakeH, Color tint)
        {
            var tex = zone.GetImageTexture();
            if (tex == null) return;

            Rect uv = zone.GetImageUvRect(); // sprite atlas sub-rect or full texture
            Vector2 srcSize = zone.GetImagePixelSize();

            Rect padded = zone.GetPaddedPixelRect(bakeW, bakeH);
            float w = padded.width, h = padded.height;

            if (zone.imagePreserveAspect && srcSize.x > 0f && srcSize.y > 0f)
            {
                float texAspect = srcSize.x / srcSize.y;
                float zoneAspect = w / Mathf.Max(1f, h);
                if (texAspect > zoneAspect) h = w / texAspect;  // limited by width
                else w = h * texAspect;                          // limited by height
            }

            // Tint is passed ONCE via GL.Color (vertex color). Material color stays white —
            // Sprites/Default multiplies vertex color by _Color, so setting both would
            // apply the tint twice (squared).
            var mat = GetImageBlitMaterial();
            mat.mainTexture = tex;
            mat.color = Color.white;

            Vector2 center = zone.GetPixelCenter(bakeW, bakeH);
            Quaternion rot = Quaternion.Euler(0, 0, zone.rotation);
            // Same zone mirror as the glyph path (see DrawZoneIntoTarget).
            Vector3 mirrorScale = new Vector3(
                zone.flipHorizontal ? -1f : 1f, zone.flipVertical ? -1f : 1f, 1f);
            Matrix4x4 model = Matrix4x4.TRS(new Vector3(center.x, center.y, 0f), rot, mirrorScale);

            ImmediateDrawState drawState = BeginImmediateDraw(targetRT, bakeW, bakeH);
            if (zone.flipHorizontal ^ zone.flipVertical) GL.invertCulling = true;
            try
            {
                if (mat.SetPass(0))
                {
                    float hw = w * 0.5f, hh = h * 0.5f;
                    GL.Begin(GL.QUADS);
                    GL.Color(tint);
                    GL.TexCoord2(uv.xMin, uv.yMin); GLVertex(model, -hw, -hh);
                    GL.TexCoord2(uv.xMax, uv.yMin); GLVertex(model, hw, -hh);
                    GL.TexCoord2(uv.xMax, uv.yMax); GLVertex(model, hw, hh);
                    GL.TexCoord2(uv.xMin, uv.yMax); GLVertex(model, -hw, hh);
                    GL.End();
                }
            }
            finally
            {
                EndImmediateDraw(drawState);
            }
        }

        private static void GLVertex(Matrix4x4 model, float x, float y)
        {
            Vector3 p = model.MultiplyPoint3x4(new Vector3(x, y, 0f));
            GL.Vertex3(p.x, p.y, p.z);
        }

        public static string DetectEmissionPropertyName()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != null && rp.GetType().Name.Contains("HD"))
                return "_EmissiveColorMap";
            return "_EmissionMap"; // Built-in Standard and URP Lit
        }

        public static string DetectTexturePropertyName()
        {
            var rp = GraphicsSettings.currentRenderPipeline;
            if (rp != null)
            {
                string typeName = rp.GetType().Name;
                if (typeName.Contains("Universal") || typeName.Contains("URP"))
                    return "_BaseMap";
                if (typeName.Contains("HDRenderPipeline") || typeName.Contains("HDRP"))
                    return "_BaseColorMap";
            }
            return "_MainTex";
        }

        public static Material CreateMaterialInstance(Material source, Texture newTexture, string textureProperty)
        {
            if (source == null)
            {
                Debug.LogError("[MeshTextBaker] Source material is null.");
                return null;
            }

            var instance = new Material(source);
            instance.name = source.name + " (TextBaked)";
            // Never persist generated instances into the scene/asset files.
            instance.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

            if (!instance.HasProperty(textureProperty))
            {
                Debug.LogWarning($"[MeshTextBaker] Material '{source.name}' has no property '{textureProperty}'. " +
                                 $"Auto-detected name would be '{DetectTexturePropertyName()}'. Baked texture may not show.");
            }
            instance.SetTexture(textureProperty, newTexture);
            return instance;
        }
    }
}
