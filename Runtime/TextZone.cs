// Mesh Text Baker — TextZone.cs
// Serializable data class representing a text zone in UV-space on a mesh.
// MVP: rectangular UV zones (with rotation). Architecture allows future polygon zones.

using System;
using UnityEngine;
using TMPro;

namespace MeshTextBaker
{
    /// <summary>How the zone's font size value is interpreted.</summary>
    public enum FontSizeMode
    {
        /// <summary>Font size = percent of the zone HEIGHT (resolution-independent). e.g. 10 = ~10% of zone height.</summary>
        PercentOfZoneHeight,
        /// <summary>Font size in TMP points at a 1024 reference resolution (auto-scales with bake resolution; non-square bakes scale by texture height).</summary>
        Absolute
    }

    /// <summary>Overflow behavior when text doesn't fit in a zone.</summary>
    public enum TextZoneOverflow
    {
        ShrinkToFit,
        Clip,
        Ellipsis,
        ContinueToSubZones
    }

    /// <summary>Role of a zone.</summary>
    public enum TextZoneRole
    {
        /// <summary>Primary text area (notes/letters).</summary>
        Main,
        /// <summary>Receives overflow text from a linked zone.</summary>
        OverflowOnly,
        /// <summary>A book page side. Receives streamed text from the book flow API.</summary>
        PageSlot,
        /// <summary>
        /// Optional page-number label for a book page. Place freely in UV space.
        /// BookController fills it with the current page number when baking the
        /// matching PageSlot (same target renderer + material index).
        /// </summary>
        PageNumber,
        /// <summary>Bakes an image (stamp, seal, illustration) instead of text.</summary>
        Image
    }

    /// <summary>
    /// A single text zone on a mesh surface, defined in UV-space.
    /// Stored as a serialized sub-object within <see cref="MeshTextSurface"/>.
    /// </summary>
    [Serializable]
    public class TextZone
    {
        [Tooltip("Unique identifier for this zone.")]
        public string id = Guid.NewGuid().ToString("N");

        /// <summary>Assigns a fresh unique id (used by editors to fix duplicated zones).</summary>
        public void RegenerateId() => id = Guid.NewGuid().ToString("N");

        [Tooltip("Human-readable name shown in the inspector.")]
        public string displayName = "New Zone";

        // ── UV Geometry ──────────────────────────────────────────
        // TODO: Replace Rect with a polygon representation for non-rectangular zones.
        [Tooltip("Rectangular region in UV-space (0-1). x,y = bottom-left corner.")]
        public Rect uvRect = new Rect(0.1f, 0.1f, 0.8f, 0.35f);

        [Tooltip("Rotation of the zone in degrees, counter-clockwise, around the zone center (UV space).")]
        public float rotation = 0f;

        [Tooltip("Mirror the zone content horizontally (around the zone center, before rotation).")]
        public bool flipHorizontal = false;

        [Tooltip("Mirror the zone content vertically (around the zone center, before rotation).")]
        public bool flipVertical = false;

        [Tooltip("Color used to draw the zone overlay in the editor.")]
        public Color previewColor = new Color(0f, 1f, 0.3f, 0.3f);

        // ── Text Source ──────────────────────────────────────────
        [Tooltip("Direct reference to a MeshTextAsset. Used if localizationKey is empty.")]
        public MeshTextAsset textAsset;

        [Tooltip("Localization key. Takes priority over textAsset if non-empty.")]
        public string localizationKey;

        // ── Layout ───────────────────────────────────────────────
        [Tooltip("Rendering order (lower = rendered first).")]
        public int orderIndex;

        [Tooltip("Which material slot (and therefore which texture) this zone is baked into. " +
                 "Zones sharing a materialIndex are baked into the same texture (atlas). " +
                 "Different values bake into separate textures (e.g. one per book page side).")]
        public int materialIndex = 0;

        [Tooltip("Optional. The renderer this zone bakes onto (e.g. a child page of a book). " +
                 "If null, the MeshTextSurface's own renderer is used. UVs are read from this " +
                 "renderer's mesh. Supports MeshRenderer and SkinnedMeshRenderer.")]
        public Renderer targetRenderer;

        [Tooltip("Padding inside the zone in UV-space (0-1). Applied inward from all edges.")]
        public float padding = 0.01f;

        [Tooltip("Font override for this zone. If null, uses the default from MeshTextSurface.")]
        public TMP_FontAsset fontOverride;

        [Tooltip("How 'Font Size' is interpreted. PercentOfZoneHeight is resolution-independent and recommended.")]
        public FontSizeMode fontSizeMode = FontSizeMode.PercentOfZoneHeight;

        [Tooltip("Font size. 0 = use Default Font Size from Bake Settings. Meaning depends on Font Size Mode.")]
        public float fontSize = 0f;

        [Tooltip("Minimum font size when auto-sizing (ShrinkToFit).")]
        public float fontSizeMin = 6f;

        [Tooltip("Maximum font size when auto-sizing (ShrinkToFit).")]
        public float fontSizeMax = 48f;

        [Tooltip("Extra space between lines in bake-target pixels. 0 = the font's default " +
                 "leading; positive loosens, negative tightens. Inline <line-spacing> tags " +
                 "add on top of this.")]
        public float lineSpacing = 0f;

        [Tooltip("Text alignment within the zone.")]
        public TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft;

        [Tooltip("Color of the rendered text. If 'Use Default Text Color' is on, this is ignored.")]
        public Color textColor = Color.black;

        [Tooltip("Photoshop-style albedo blend used by this whole text/image zone. Inline " +
                 "<blend=...> scopes can override it for selected words or images.")]
        public MeshTextBlendMode blendMode = MeshTextBlendMode.Normal;

        [Range(0f, 1f)]
        [Tooltip("Opacity of this zone's glyphs/images and their PBR coverage. Inline " +
                 "<opacity=0..1> scopes multiply it for selected content.")]
        public float opacity = 1f;

        [Tooltip("When true, the zone uses Default Text Color from Bake Settings instead of its own Text Color.")]
        public bool useDefaultTextColor = true;

        [Tooltip("Enable TMP rich text tags (bold, italic, color, size, etc.).")]
        public bool richTextEnabled = true;

        [Tooltip("Enable lightweight markdown pre-processing before passing to TMP.")]
        public bool markdownEnabled = true;

        // ── Overflow ─────────────────────────────────────────────
        [Tooltip("What to do when text doesn't fit in this zone.")]
        public TextZoneOverflow overflowBehavior = TextZoneOverflow.ShrinkToFit;

        [Tooltip("ID of the zone to continue overflow text into. Used with ContinueToSubZones. " +
                 "Chains are built link-by-link: A→B, then B→C.")]
        public string continueToZoneId = "";

        [Tooltip("Parent zone id. PageNumber zones reference their PageSlot parent.")]
        public string parentZoneId = "";

        [Tooltip("PageNumber only: when false, font/layout/color come from surface Page Number Defaults.")]
        public bool overridePageNumberStyle = false;

        [Tooltip("Whether this zone is a main content area or only receives overflow.")]
        public TextZoneRole role = TextZoneRole.Main;

        // ── Image zone (role == Image) ───────────────────────────
        [Tooltip("Image zones: the texture baked into the zone rect instead of text.")]
        public Texture2D imageTexture;

        [Tooltip("Image zones: sprite alternative to Image Texture (takes priority when set). " +
                 "Atlas sprites are supported (Rectangle packing; Tight packing falls back to " +
                 "the full rect).")]
        public Sprite imageSprite;

        [Tooltip("Image zones: tint multiplied over the image (white = unchanged).")]
        public Color imageTint = Color.white;

        [Tooltip("Image zones: preserve the image aspect ratio (fit inside the zone). " +
                 "Off = stretch to fill the zone rect.")]
        public bool imagePreserveAspect = true;

        // ── PBR (emission / mask) ────────────────────────────────
        [Tooltip("Master switch for PBR baking on this zone: emission and " +
                 "metallic/smoothness mask, including the <emission>/<surface> text tags.")]
        public bool usePbr = false;

        [Tooltip("Bake the WHOLE zone into the material's EMISSION map. For single words " +
                 "use <emission>…</emission> tags in the text instead.")]
        public bool bakeEmission = false;

        [Tooltip("Emission color multiplied over the zone's rendered pixels. Use HDR intensity " +
                 "for bloom (values above 1 glow with post-processing).")]
        [ColorUsage(false, true)]
        public Color emissionColor = Color.white;

        // ── PBR mask (metallic / smoothness) ─────────────────────
        [Tooltip("Bake the WHOLE zone into the material's PBR mask map. For single words use " +
                 "<surface>…</surface> tags. HDRP: _MaskMap. URP/Built-in: _MetallicGlossMap.")]
        public bool bakePbrMask = false;

        [Range(0f, 1f)]
        [Tooltip("Metallic value under this zone's pixels (glyph/image coverage).")]
        public float maskMetallic = 1f;

        [Range(0f, 1f)]
        [Tooltip("Smoothness value under this zone's pixels (glyph/image coverage).")]
        public float maskSmoothness = 0.85f;

        [Tooltip("Bake signed height under the WHOLE zone. For selected words use " +
                 "<surface h=...>. Height Value is normalized: -1 = engraved, 0 = neutral, " +
                 "+1 = fully raised.")]
        public bool bakeHeight = false;

        [Range(-1f, 1f)]
        [Tooltip("Signed normalized relief for whole-zone height baking.")]
        public float heightValue = 1f;

        [Range(0.0001f, 0.08f)]
        [Tooltip("Maximum height/parallax strength for this zone. Thin text usually needs " +
                 "0.001-0.005; larger values can shift URP/Built-in albedo outside the glyph. " +
                 "HDRP interprets this in world units.")]
        public float heightAmplitude = 0.005f;

        /// <summary>Effective flags: PBR features act only when the master switch is on.</summary>
        public bool EmissionEnabled => usePbr && bakeEmission;
        public bool PbrMaskEnabled => usePbr && bakePbrMask;
        public bool HeightEnabled => usePbr && bakeHeight;
        /// <summary>
        /// Whether &lt;emission&gt;/&lt;surface&gt; tags trigger PBR passes. NOTE: the tags are
        /// always STRIPPED from rich text (never shown as literal characters) even when this
        /// is false — they just have no PBR effect then.
        /// </summary>
        public bool PbrTagsEnabled => usePbr && richTextEnabled;

        /// <summary>Pixel rect within a texture of given size.</summary>
        public Rect GetPixelRect(int textureSize)
            => GetPixelRect(textureSize, textureSize);

        /// <summary>Pixel rect within a texture of given width and height.</summary>
        public Rect GetPixelRect(int textureWidth, int textureHeight)
        {
            return new Rect(
                uvRect.x * textureWidth,
                uvRect.y * textureHeight,
                uvRect.width * textureWidth,
                uvRect.height * textureHeight);
        }

        /// <summary>Returns the inner rect after applying padding.</summary>
        public Rect GetPaddedPixelRect(int textureSize)
            => GetPaddedPixelRect(textureSize, textureSize);

        /// <summary>Returns the inner rect after applying padding.</summary>
        public Rect GetPaddedPixelRect(int textureWidth, int textureHeight)
        {
            // Padding is defined in UV-space: per-axis so the UV inset stays uniform.
            float px = padding * textureWidth;
            float py = padding * textureHeight;
            return new Rect(
                uvRect.x * textureWidth + px,
                uvRect.y * textureHeight + py,
                Mathf.Max(1f, uvRect.width * textureWidth - px * 2f),
                Mathf.Max(1f, uvRect.height * textureHeight - py * 2f));
        }

        /// <summary>Center of the zone in pixel space.</summary>
        public Vector2 GetPixelCenter(int textureSize)
            => GetPixelCenter(textureSize, textureSize);

        /// <summary>Center of the zone in pixel space.</summary>
        public Vector2 GetPixelCenter(int textureWidth, int textureHeight)
        {
            return new Vector2(
                (uvRect.x + uvRect.width * 0.5f) * textureWidth,
                (uvRect.y + uvRect.height * 0.5f) * textureHeight);
        }

        /// <summary>Image zones: the effective texture (sprite's texture takes priority).</summary>
        public Texture2D GetImageTexture()
        {
            if (imageSprite != null && imageSprite.texture != null) return imageSprite.texture;
            return imageTexture;
        }

        /// <summary>
        /// Image zones: normalized UV rect of the image within its texture. Full rect for a
        /// plain Texture2D; the sprite's atlas sub-rect for sprites (Rectangle packing — Tight
        /// packed sprites render their bounding rect).
        /// </summary>
        public Rect GetImageUvRect()
        {
            if (imageSprite != null && imageSprite.texture != null)
            {
                Rect tr = imageSprite.textureRect;
                float w = imageSprite.texture.width;
                float h = imageSprite.texture.height;
                return new Rect(tr.x / w, tr.y / h, tr.width / w, tr.height / h);
            }
            return new Rect(0f, 0f, 1f, 1f);
        }

        /// <summary>Image zones: source pixel size (sprite sub-rect or full texture).</summary>
        public Vector2 GetImagePixelSize()
        {
            if (imageSprite != null && imageSprite.texture != null)
                return imageSprite.textureRect.size;
            if (imageTexture != null)
                return new Vector2(imageTexture.width, imageTexture.height);
            return Vector2.zero;
        }

        /// <summary>Default preview overlay color for a given role.</summary>
        public static Color DefaultPreviewColor(TextZoneRole role)
        {
            switch (role)
            {
                case TextZoneRole.OverflowOnly: return new Color(1f, 0.85f, 0.1f, 0.3f);   // yellow
                case TextZoneRole.PageSlot:     return new Color(0.2f, 0.6f, 1f, 0.3f);     // blue
                case TextZoneRole.PageNumber:   return new Color(1f, 0.55f, 0.1f, 0.35f);   // orange
                case TextZoneRole.Image:        return new Color(1f, 0.3f, 0.8f, 0.3f);     // magenta
                case TextZoneRole.Main:
                default:                        return new Color(0f, 1f, 0.3f, 0.3f);       // green
            }
        }

        /// <summary>
        /// Copies STYLE-only fields from another zone. Does NOT copy geometry (uvRect, rotation),
        /// id, displayName, role, links, or the text source. Flip flags ARE copied so overflow
        /// continuations keep the parent's orientation.
        /// </summary>
        public void CopyStyleFrom(TextZone src)
        {
            if (src == null) return;
            padding = src.padding;
            fontOverride = src.fontOverride;
            fontSize = src.fontSize;
            fontSizeMode = src.fontSizeMode;
            fontSizeMin = src.fontSizeMin;
            fontSizeMax = src.fontSizeMax;
            lineSpacing = src.lineSpacing;
            alignment = src.alignment;
            textColor = src.textColor;
            blendMode = src.blendMode;
            opacity = src.opacity;
            useDefaultTextColor = src.useDefaultTextColor;
            richTextEnabled = src.richTextEnabled;
            markdownEnabled = src.markdownEnabled;
            overflowBehavior = src.overflowBehavior;
            previewColor = src.previewColor;
            flipHorizontal = src.flipHorizontal;
            flipVertical = src.flipVertical;
            usePbr = src.usePbr;
            bakeEmission = src.bakeEmission;
            emissionColor = src.emissionColor;
            bakePbrMask = src.bakePbrMask;
            maskMetallic = src.maskMetallic;
            maskSmoothness = src.maskSmoothness;
            bakeHeight = src.bakeHeight;
            heightValue = src.heightValue;
            heightAmplitude = src.heightAmplitude;
        }
    }
}
