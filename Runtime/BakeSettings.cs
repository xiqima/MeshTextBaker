// Mesh Text Baker — BakeSettings.cs
// Shared bake configuration data.

using UnityEngine;

namespace MeshTextBaker
{
    /// <summary>Bake resolution for generated textures.</summary>
    public enum BakeResolution
    {
        _512 = 512,
        _1024 = 1024,
        _2048 = 2048,
        _4096 = 4096
    }

    /// <summary>When baking should occur.</summary>
    public enum BakeMode
    {
        /// <summary>
        /// Bake ONLY when the user presses "Bake Now" or code calls BakeForLocale().
        /// Nothing happens automatically — full manual control.
        /// </summary>
        Manual,
        /// <summary>
        /// Like Manual, plus the EDITOR re-bakes automatically whenever the Preview Locale
        /// field changes in the inspector. Handy while authoring localized content.
        /// </summary>
        EditorBake,
        /// <summary>
        /// The surface bakes itself in Start() at RUNTIME using the Preview Locale.
        /// Use for text that must match the player's language when the game launches.
        /// </summary>
        RuntimeBakeOnStart
    }

    /// <summary>
    /// Settings used by the baker. Populated from <see cref="MeshTextSurface"/> at bake time.
    /// </summary>
    [System.Serializable]
    public class BakeSettings
    {
        [Tooltip("Output texture size: the LONG side in pixels. When the material (or Base Albedo " +
                 "Texture) has a non-square texture, the bake matches its aspect; otherwise the bake is square.")]
        public BakeResolution resolution = BakeResolution._1024;

        [Tooltip("Name of the texture property on the material. Leave empty to auto-detect per render pipeline.")]
        public string texturePropertyName = "";

        [Tooltip("Name of the EMISSION map property on the material. Leave empty to auto-detect " +
                 "(_EmissionMap for Built-in/URP, _EmissiveColorMap for HDRP). Used by zones " +
                 "with 'Bake Emission' enabled.")]
        public string emissionPropertyName = "";

        [Tooltip("Name of the HEIGHT/PARALLAX map property. Leave empty to auto-detect " +
                 "(_HeightMap for HDRP/custom shaders, _ParallaxMap for URP/Built-in Lit). " +
                 "URP availability depends on the Lit shader version.")]
        public string heightPropertyName = "";

        [Tooltip("Optional override. If empty, the texture is taken automatically from the " +
                 "material's albedo slot. If the material has none either, a white background is used.")]
        public Texture2D baseAlbedoTexture;

        [Tooltip("Default font used when a zone has no override.")]
        public TMPro.TMP_FontAsset defaultFont;

        [Tooltip("How Default Font Size is interpreted for zones whose Font Size is 0.")]
        public FontSizeMode defaultFontSizeMode = FontSizeMode.PercentOfZoneHeight;

        [Tooltip("Default font size when a zone has no override.")]
        public float defaultFontSize = 8f;

        [Tooltip("Default text color when a zone has no override.")]
        public Color defaultTextColor = Color.black;

        [Tooltip("Alignment assigned to newly created zones.")]
        public TMPro.TextAlignmentOptions defaultAlignment = TMPro.TextAlignmentOptions.TopLeft;

        [Tooltip("Rich Text setting assigned to newly created zones.")]
        public bool defaultRichTextEnabled = true;

        [Tooltip("Markdown setting assigned to newly created zones.")]
        public bool defaultMarkdownEnabled = true;

        [Tooltip("Use PBR setting assigned to newly created non-book zones.")]
        public bool defaultUsePbr = false;

        [Tooltip("Blend mode assigned to newly created zones.")]
        public MeshTextBlendMode defaultBlendMode = MeshTextBlendMode.Normal;

        [Range(0f, 1f)]
        [Tooltip("Opacity assigned to newly created zones.")]
        public float defaultOpacity = 1f;

        [Tooltip("Generate mipmaps for baked textures (recommended when the surface is viewed " +
                 "at a distance; costs ~33% extra VRAM).")]
        public bool generateMipMaps = true;

        [Tooltip("Book mode: max characters measured per page-layout pass. Increase for very " +
                 "large page slots holding more than this many characters.")]
        public int measureWindow = 2000;

        [Tooltip("Named textures usable inside text via <image=name> tags (inline images). " +
                 "Optional parameters: h=NNN, w=NNN, displace=false, blend=multiply, " +
                 "opacity=0.5.")]
        public System.Collections.Generic.List<NamedImage> imageLibrary =
            new System.Collections.Generic.List<NamedImage>();

        [Tooltip("Named TMP fonts usable through <font=name>...</font>. External runtime fonts " +
                 "can be loaded with <font=file:fonts/MyFont.ttf> from persistentDataPath or " +
                 "StreamingAssets, using the same search order as loose inline images.")]
        public System.Collections.Generic.List<NamedFont> fontLibrary =
            new System.Collections.Generic.List<NamedFont>();
    }
}
