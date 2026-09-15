// Mesh Text Baker — RichTextTagRepairTests.cs
// EditMode tests for rich-text tag repair on slice boundaries.

using NUnit.Framework;
using MeshTextBaker;

namespace MeshTextBaker.Tests
{
    public class RichTextTagRepairTests
    {
        [Test]
        public void Split_InsideBoldScope_ClosesAndReopens()
        {
            RichTextTagRepair.Split("<b>hello world</b>", 8, out string head, out string tail);
            Assert.AreEqual("<b>hello</b>", head);
            Assert.AreEqual("<b> world</b>", tail);
        }

        [Test]
        public void Split_NestedScopes_PreservesOrder()
        {
            string text = "<color=#f00><b>bold red</b> just red</color> plain";
            RichTextTagRepair.Split(text, 20, out string head, out string tail);
            // 20 chars: "<color=#f00><b>bold " — both scopes open.
            Assert.AreEqual("<color=#f00><b>bold </b></color>", head);
            Assert.IsTrue(tail.StartsWith("<color=#f00><b>"));
        }

        [Test]
        public void Split_NoOpenScopes_ReturnsPlainSlices()
        {
            RichTextTagRepair.Split("<b>x</b> abc", 9, out string head, out string tail);
            Assert.AreEqual("<b>x</b> ", head);
            Assert.AreEqual("abc", tail);
        }

        [Test]
        public void VoidTags_DoNotOpenScopes()
        {
            Assert.AreEqual("", RichTextTagRepair.GetClosingSuffix("a<br>b"));
            Assert.AreEqual("", RichTextTagRepair.GetClosingSuffix("a<sprite=3>b"));
        }

        [Test]
        public void HexColorShorthand_ClosesWithColor()
        {
            Assert.AreEqual("</color>", RichTextTagRepair.GetClosingSuffix("<#ff0000>red"));
            Assert.AreEqual("", RichTextTagRepair.GetClosingSuffix("<#ff0000>red</color>"));
        }

        [Test]
        public void GetReopenPrefix_ReflectsOpenScopesAtPosition()
        {
            string text = "<b><i>abc</i>def";
            Assert.AreEqual("<b><i>", RichTextTagRepair.GetReopenPrefix(text, 9));   // inside both
            Assert.AreEqual("<b>", RichTextTagRepair.GetReopenPrefix(text, text.Length)); // i closed
        }

        [Test]
        public void PlainLessThan_IsNotATag()
        {
            Assert.AreEqual("", RichTextTagRepair.GetClosingSuffix("2 < 3"));
        }

        [Test]
        public void Noparse_DoesNotOpenScopes()
        {
            // Documentation example inside noparse must not leave an open <color> scope.
            string s = "see <noparse><color></noparse> later";
            Assert.AreEqual("", RichTextTagRepair.GetClosingSuffix(s));
            Assert.AreEqual("", RichTextTagRepair.GetReopenPrefix(s, s.Length));
        }

        [Test]
        public void BareColorTag_DoesNotOpenScope()
        {
            Assert.AreEqual("", RichTextTagRepair.GetClosingSuffix("bare <color> word"));
        }
    }
}
