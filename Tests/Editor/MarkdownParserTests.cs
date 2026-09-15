// Mesh Text Baker — MarkdownParserTests.cs
// EditMode tests for the markdown → TMP rich text converter.

using NUnit.Framework;
using MeshTextBaker;

namespace MeshTextBaker.Tests
{
    public class MarkdownParserTests
    {
        [Test]
        public void Bold_ConvertsWithinLine()
            => Assert.AreEqual("<b>hi</b>", MarkdownParser.ConvertToRichText("**hi**"));

        [Test]
        public void Bold_ConvertsAcrossLines()
            => Assert.AreEqual("<b>a\nb</b>", MarkdownParser.ConvertToRichText("**a\nb**"));

        [Test]
        public void Italic_ConvertsWithinLine()
            => Assert.AreEqual("<i>hi</i>", MarkdownParser.ConvertToRichText("*hi*"));

        [Test]
        public void Italic_ConvertsAcrossLines()
            => Assert.AreEqual("<i>a\nb</i>", MarkdownParser.ConvertToRichText("*a\nb*"));

        [Test]
        public void Heading1_ConvertsToSizedBold()
            => Assert.AreEqual("<size=150%><b>Title</b></size>\nbody",
                MarkdownParser.ConvertToRichText("# Title\nbody"));

        [Test]
        public void ListItem_ConvertsToBullet()
            => Assert.AreEqual("\u2022 item", MarkdownParser.ConvertToRichText("- item"));

        [Test]
        public void StarListItem_ConvertsToBullet()
            => Assert.AreEqual("\u2022 item", MarkdownParser.ConvertToRichText("* item"));

        [Test]
        public void PlainLessThan_PassesThroughVerbatim()
            => Assert.AreEqual("2 < 3 and <b>x</b>", MarkdownParser.ConvertToRichText("2 < 3 and <b>x</b>"));

        [Test]
        public void UnknownTag_IsNotSwallowed()
            => Assert.AreEqual("a <foo> b", MarkdownParser.ConvertToRichText("a <foo> b"));

        [Test]
        public void TmpNativeColor_PassesThrough()
            => Assert.AreEqual("<color=#ff0000>red</color>",
                MarkdownParser.ConvertToRichText("<color=#ff0000>red</color>"));

        [Test]
        public void HexShorthand_PassesThrough()
            => Assert.AreEqual("<#ff0000>red</color>",
                MarkdownParser.ConvertToRichText("<#ff0000>red</color>"));

        [Test]
        public void NewPageToken_PassesThrough()
            => Assert.AreEqual("a<newpage>b", MarkdownParser.ConvertToRichText("a<newpage>b"));

        [Test]
        public void CodeSpan_WrapsInNoparse()
            => Assert.AreEqual("see <noparse><color></noparse> tag",
                MarkdownParser.ConvertToRichText("see `<color>` tag"));

        [Test]
        public void CodeSpan_PreservesEmissionExample()
            => Assert.AreEqual("x <noparse><emission=#00ffcc>y</emission></noparse> z",
                MarkdownParser.ConvertToRichText("x `<emission=#00ffcc>y</emission>` z"));

        [Test]
        public void PipeTable_ProducesAtomicPositionedRows()
        {
            string converted = MarkdownParser.ConvertToRichText(
                "| Name | Value |\n|---|---|\n| Alpha | 10 |");
            StringAssert.Contains("<size=", converted);
            StringAssert.Contains("<nobr><b><pos=0%>Name", converted);
            StringAssert.Contains("</b></nobr></size>", converted);
            StringAssert.Contains("<nobr><pos=0%>Alpha", converted);
            Assert.IsFalse(converted.Contains("|---|"));
        }
    }
}
