// Mesh Text Baker — MeshTextAsset.cs
// ScriptableObject holding localized text for a single entry.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeshTextBaker
{
    /// <summary>A single translation entry: locale code → text body.</summary>
    [Serializable]
    public class LocalizedTextEntry
    {
        [Tooltip("ISO locale code, e.g. 'en', 'ru', 'de', 'ja'.")]
        public string locale = "en";

        [Tooltip("Localized text body. Supports TMP rich text and optional markdown.")]
        [TextArea(3, 20)]
        public string text = "";
    }

    /// <summary>
    /// Embedded authoring/fallback text for <see cref="MeshTextSurface"/>. Its key can be
    /// overridden by an assigned Unity/custom/external localization provider.
    /// </summary>
    [CreateAssetMenu(fileName = "MeshTextAsset", menuName = "Mesh Text Baker/Text Asset")]
    public class MeshTextAsset : ScriptableObject
    {
        [Tooltip("Unique key identifying this text entry.")]
        public string key;

        [Tooltip("Locale to use when the requested locale is not found.")]
        public string fallbackLocale = "en";

        [Tooltip("Authoring metadata: this asset is expected to contain TMP/custom rich-text tags.")]
        public bool richTextEnabled = true;

        [Tooltip("Authoring metadata: this asset is expected to contain lightweight Markdown.")]
        public bool markdownEnabled = true;

        [Tooltip("List of locale → text mappings.")]
        public List<LocalizedTextEntry> translations = new List<LocalizedTextEntry>();

        public bool ContainsRichTextMarkup()
        {
            for (int i = 0; i < translations.Count; i++)
                if (MeshTextMarkupUtility.ContainsRichText(translations[i].text)) return true;
            return false;
        }

        public bool ContainsMarkdownMarkup()
        {
            for (int i = 0; i < translations.Count; i++)
                if (MeshTextMarkupUtility.ContainsMarkdown(translations[i].text)) return true;
            return false;
        }

        /// <summary>Returns only an exact locale match; does not apply fallback.</summary>
        public bool TryGetTextExact(string locale, out string text)
        {
            for (int i = 0; i < translations.Count; i++)
            {
                if (!string.Equals(translations[i].locale, locale, StringComparison.OrdinalIgnoreCase)) continue;
                text = translations[i].text;
                return !string.IsNullOrEmpty(text);
            }
            text = null;
            return false;
        }

        /// <summary>Returns localized text for the given locale, or fallback, or empty string.</summary>
        public string GetText(string locale)
        {
            if (TryGetTextExact(locale, out string exact)) return exact;

            for (int i = 0; i < translations.Count; i++)
                if (string.Equals(translations[i].locale, fallbackLocale, StringComparison.OrdinalIgnoreCase))
                    return translations[i].text;

            if (translations.Count > 0)
                return translations[0].text;

            return "";
        }
    }
}
