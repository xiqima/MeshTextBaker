// Mesh Text Baker — ITextLocalizationProvider.cs
// Interface for text localization. Swap implementations to integrate different systems.

namespace MeshTextBaker
{
    /// <summary>
    /// Abstraction over text localization sources.
    /// Default implementation reads from <see cref="MeshTextAsset"/>.
    /// Optional: implement this to bridge another system. For deterministic per-locale
    /// fallback, locale discovery and change events implement ITextLocalizationProviderV2.
    /// </summary>
    public interface ITextLocalizationProvider
    {
        /// <summary>Returns localized text for the given key and locale. Returns empty string if not found.</summary>
        string GetText(string key, string locale);
    }
}
