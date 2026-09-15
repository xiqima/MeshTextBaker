// Mesh Text Baker — BookPaginator.cs
// Pure pagination logic for book mode: maps logical page indices to character positions in the
// (markdown-converted) book text. No scene/animation knowledge — fully testable in EditMode.
//
// Pagination model: page N starts where page N-1 ended. Page ends are measured with
// MeshTextSurface.MeasurePageEnd, which shares its layout code with the actual bake
// (single source of truth), so cursors can never drift.

using System.Collections.Generic;
using UnityEngine;

namespace MeshTextBaker
{
    /// <summary>
    /// Lazily computes and caches the start character of every logical page for one locale.
    /// Invalidate on locale or text change.
    /// </summary>
    public class BookPaginator
    {
        private readonly MeshTextSurface _surface;
        private readonly List<int> _pageStarts = new List<int> { 0 };

        // LRU cache of sliced page texts (pageIndex → text). GetPageText runs a full TMP
        // measuring pass; during page turns the same pages are requested repeatedly
        // (flip front = current right, etc.), so caching pays off immediately.
        private readonly Dictionary<int, string> _pageTextCache = new Dictionary<int, string>(16);
        private readonly LinkedList<int> _pageTextLru = new LinkedList<int>();
        private const int PageTextCacheCapacity = 16;

        private string _locale;
        private string _fullText = "";

        /// <summary>Zone used to measure a given page (left/right slots may differ in size).</summary>
        public System.Func<int, TextZone> measureZoneSelector;

        public BookPaginator(MeshTextSurface surface)
        {
            _surface = surface;
        }

        public string locale => _locale;
        public string fullText => _fullText;

        /// <summary>Sets the locale and resets the cache. Must be called before first use.</summary>
        public void SetLocale(string locale)
        {
            _locale = locale;
            InvalidateCache();
        }

        /// <summary>Re-reads the book text and clears cached page starts (locale/text change).</summary>
        public void InvalidateCache()
        {
            if (_surface != null) _surface.InvalidateBookCache();
            _fullText = _surface != null ? (_surface.GetBookText(_locale) ?? "") : "";
            _pageStarts.Clear();
            _pageStarts.Add(0);
            _pageTextCache.Clear();
            _pageTextLru.Clear();
        }

        /// <summary>
        /// Start character of the given logical page (0-based). Pages beyond the text end all
        /// return the text length. Computed lazily and cached.
        /// </summary>
        public int GetPageStart(int pageIndex)
        {
            if (pageIndex <= 0) return 0;
            if (string.IsNullOrEmpty(_fullText)) return 0;

            while (_pageStarts.Count <= pageIndex)
            {
                int prevPage = _pageStarts.Count - 1;
                int prevStart = _pageStarts[prevPage];

                if (prevStart >= _fullText.Length)
                {
                    _pageStarts.Add(_fullText.Length);
                    continue;
                }

                TextZone zone = GetMeasureZone(prevPage);
                if (zone == null)
                {
                    _pageStarts.Add(_fullText.Length);
                    continue;
                }

                int end = _surface.MeasurePageEnd(_fullText, prevStart, zone);
                // Guarantee forward progress even on degenerate zones (avoid infinite loop).
                if (end <= prevStart) end = prevStart + 1;
                _pageStarts.Add(Mathf.Clamp(end, 0, _fullText.Length));
            }
            return _pageStarts[pageIndex];
        }

        /// <summary>True if the page has any text.</summary>
        public bool PageHasText(int pageIndex)
        {
            if (string.IsNullOrEmpty(_fullText)) return false;
            return GetPageStart(pageIndex) < _fullText.Length;
        }

        /// <summary>
        /// Total page count (lazy walk to the end of the text; capped for safety).
        /// Used for progress-based visuals like dynamic stack thickness.
        /// </summary>
        public int CountPages(int maxPages = 512)
        {
            if (string.IsNullOrEmpty(_fullText)) return 0;
            for (int pg = 0; pg < maxPages; pg++)
                if (GetPageStart(pg) >= _fullText.Length) return pg;
            return maxPages;
        }

        /// <summary>True if the given spread (pair of pages 2s, 2s+1) is past the end of the text.</summary>
        public bool IsBeyondText(int spread)
        {
            if (string.IsNullOrEmpty(_fullText)) return true;
            return GetPageStart(2 * spread) >= _fullText.Length;
        }

        /// <summary>Text of the given logical page ("" past the end). LRU-cached.</summary>
        public string GetPageText(int pageIndex)
        {
            if (string.IsNullOrEmpty(_fullText)) return "";

            if (_pageTextCache.TryGetValue(pageIndex, out string cached))
            {
                // Refresh LRU position.
                _pageTextLru.Remove(pageIndex);
                _pageTextLru.AddLast(pageIndex);
                return cached;
            }

            int start = GetPageStart(pageIndex);
            string text = "";
            if (start < _fullText.Length)
            {
                TextZone zone = GetMeasureZone(pageIndex);
                if (zone != null) text = _surface.GetPageText(_fullText, start, zone, out _);
            }

            _pageTextCache[pageIndex] = text;
            _pageTextLru.AddLast(pageIndex);
            while (_pageTextLru.Count > PageTextCacheCapacity)
            {
                _pageTextCache.Remove(_pageTextLru.First.Value);
                _pageTextLru.RemoveFirst();
            }
            return text;
        }

        private TextZone GetMeasureZone(int pageIndex)
        {
            if (measureZoneSelector != null) return measureZoneSelector(pageIndex);

            // Default: alternate between the first two page slots (left/right template).
            var slots = _surface.GetPageSlots();
            if (slots == null || slots.Count == 0) return null;
            return slots[pageIndex % Mathf.Min(2, slots.Count)];
        }
    }
}
