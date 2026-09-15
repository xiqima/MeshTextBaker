// Mesh Text Baker — DefaultTextLocalizationProvider.cs
// Default implementation that reads text from MeshTextAsset ScriptableObjects.

namespace MeshTextBaker
{
    /// <summary>
    /// Built-in provider used when no custom <see cref="ITextLocalizationProvider"/> is assigned.
    /// <para>
    /// <b>Key resolution:</b> <see cref="GetText"/> returns empty — keys are only meaningful when
    /// a custom provider (Unity Localization, I2, …) is assigned on the surface. The default
    /// path for zone text is <see cref="ResolveTextForZone"/>, which reads
    /// <see cref="TextZone.textAsset"/>.
    /// </para>
    /// </summary>
    public class DefaultTextLocalizationProvider : ITextLocalizationProvider
    {
        public string GetText(string key, string locale)
        {
            // Default provider has no global key registry. Assign a custom
            // ITextLocalizationProvider on MeshTextSurface for key-based lookup.
            return "";
        }

        /// <summary>
        /// Resolves text for a zone: falls back to the zone's textAsset.
        /// </summary>
        public static string ResolveTextForZone(TextZone zone, string locale)
        {
            if (zone.textAsset != null)
                return zone.textAsset.GetText(locale);
            return "";
        }
    }
}
