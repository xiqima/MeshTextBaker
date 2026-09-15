// Mesh Text Baker — BookmarkUtilityTests.cs

using System.Collections.Generic;
using NUnit.Framework;
using MeshTextBaker;

namespace MeshTextBaker.Tests
{
    public class BookmarkUtilityTests
    {
        [Test]
        public void Process_StripsTag_AndMapsIndex()
        {
            var map = new Dictionary<string, int>();
            string outText = BookmarkUtility.Process("Hello <bookmark=ch1>World", map);
            Assert.AreEqual("Hello World", outText);
            Assert.IsTrue(map.ContainsKey("ch1"));
            Assert.AreEqual("Hello ".Length, map["ch1"]);
        }

        [Test]
        public void Process_QuotedId()
        {
            var map = new Dictionary<string, int>();
            string outText = BookmarkUtility.Process("A<bookmark id=\"mid\">B", map);
            Assert.AreEqual("AB", outText);
            Assert.AreEqual(1, map["mid"]);
        }

        [Test]
        public void Process_Multiple_UniqueOnly()
        {
            var map = new Dictionary<string, int>();
            BookmarkUtility.Process("<bookmark=a>x<bookmark=b>y<bookmark=a>z", map);
            Assert.AreEqual(2, map.Count);
            Assert.AreEqual(0, map["a"]); // first occurrence wins
            Assert.AreEqual(1, map["b"]);
        }

        [Test]
        public void HasBookmarkTags_Detects()
        {
            Assert.IsTrue(BookmarkUtility.HasBookmarkTags("x <bookmark=y> z"));
            Assert.IsFalse(BookmarkUtility.HasBookmarkTags("no marks"));
        }
    }
}
