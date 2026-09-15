// Mesh Text Baker — MeshTextMarkupUtility.cs
// Lightweight authoring diagnostics; rendering remains owned by MarkdownParser/TMP.

using System;

namespace MeshTextBaker
{
    public static class MeshTextMarkupUtility
    {
        private static readonly string[] RichPrefixes =
        {
            "<b", "</b", "<i", "</i", "<u", "</u", "<s", "</s", "<color", "</color",
            "<size", "</size", "<align", "</align", "<font", "</font", "<alpha", "<mark",
            "<sub", "</sub", "<sup", "</sup", "<emission", "</emission", "<surface",
            "</surface", "<blend", "</blend", "<opacity", "</opacity", "<noparse", "<link",
            "<voffset", "<space", "<cspace", "<indent", "<line-height", "<nobr", "<br",
            "<image", "<sprite"
        };

        public static bool ContainsRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < RichPrefixes.Length; i++)
                if (text.IndexOf(RichPrefixes[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public static bool ContainsMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf("**", StringComparison.Ordinal) >= 0 || text.IndexOf('`') >= 0) return true;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("# ", StringComparison.Ordinal) ||
                    line.StartsWith("## ", StringComparison.Ordinal) ||
                    line.StartsWith("- ", StringComparison.Ordinal) ||
                    line.StartsWith("* ", StringComparison.Ordinal)) return true;
                if (line.IndexOf('|') >= 0 && i + 1 < lines.Length &&
                    lines[i + 1].IndexOf("---", StringComparison.Ordinal) >= 0) return true;
                // A paired single-star sequence on one or multiple lines.
                int star = line.IndexOf('*');
                if (star >= 0 && line.IndexOf('*', star + 1) > star) return true;
            }
            // Multiline italic: opening star on one line and closing star later.
            int first = text.IndexOf('*');
            return first >= 0 && text.IndexOf('*', first + 1) > first;
        }
    }
}
