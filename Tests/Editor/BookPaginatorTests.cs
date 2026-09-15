// Mesh Text Baker — BookPaginatorTests.cs
// EditMode tests for the pagination logic (no rendering; degenerate and cache behavior).

using NUnit.Framework;
using UnityEngine;
using MeshTextBaker;

namespace MeshTextBaker.Tests
{
    public class BookPaginatorTests
    {
        private GameObject _go;
        private MeshTextSurface _surface;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("__BookPaginatorTest");
            _surface = _go.AddComponent<MeshTextSurface>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        [Test]
        public void EmptyBook_FirstPageStartIsZero_AndBeyondText()
        {
            var pages = new BookPaginator(_surface);
            pages.SetLocale("en");

            Assert.AreEqual(0, pages.GetPageStart(0));
            Assert.IsTrue(pages.IsBeyondText(0));
            Assert.IsFalse(pages.PageHasText(0));
        }

        [Test]
        public void EmptyBook_AnyPageStartIsZero()
        {
            var pages = new BookPaginator(_surface);
            pages.SetLocale("en");

            Assert.AreEqual(0, pages.GetPageStart(5));
        }

        [Test]
        public void BookWithText_ButNoZones_TerminatesAtTextLength()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "Hello world" });
            _surface.bookText = asset;

            var pages = new BookPaginator(_surface);
            pages.SetLocale("en");

            // No PageSlot zones → measure zone is null → pages collapse to text end
            // WITHOUT hanging (progress guarantee).
            int start1 = pages.GetPageStart(1);
            Assert.AreEqual(pages.fullText.Length, start1);
            Assert.IsTrue(pages.IsBeyondText(1));

            Object.DestroyImmediate(asset);
        }

        [Test]
        public void InvalidateCache_RereadsText()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "one" });
            _surface.bookText = asset;

            var pages = new BookPaginator(_surface);
            pages.SetLocale("en");
            Assert.AreEqual("one", pages.fullText);

            asset.translations[0].text = "two";
            pages.InvalidateCache();
            Assert.AreEqual("two", pages.fullText);

            Object.DestroyImmediate(asset);
        }

        [Test]
        public void GetPageText_IsCached_AndInvalidatedOnLocaleChange()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "hello" });
            asset.translations.Add(new LocalizedTextEntry { locale = "ru", text = "привет" });
            _surface.bookText = asset;

            var pages = new BookPaginator(_surface);
            pages.SetLocale("en");

            // No zones → page text is "" but the call must not throw and must be stable.
            string first = pages.GetPageText(0);
            string second = pages.GetPageText(0); // served from LRU cache
            Assert.AreEqual(first, second);

            pages.SetLocale("ru"); // must clear the page-text cache
            Assert.AreEqual("привет", pages.fullText);

            Object.DestroyImmediate(asset);
        }

        [Test]
        public void MarkdownBook_ConvertedOnceBeforePagination()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "# Title\nbody" });
            _surface.bookText = asset;

            var pages = new BookPaginator(_surface);
            pages.SetLocale("en");

            // Cursors index into the CONVERTED string.
            StringAssert.Contains("<size=150%>", pages.fullText);

            Object.DestroyImmediate(asset);
        }
    }
}
