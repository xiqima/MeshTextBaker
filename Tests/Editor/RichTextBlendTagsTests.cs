using System.Collections.Generic;
using MeshTextBaker;
using NUnit.Framework;

namespace MeshTextBaker.Tests
{
    public class RichTextBlendTagsTests
    {
        [Test]
        public void Preprocess_ConvertsBlendToZeroWidthLink()
            => Assert.AreEqual("a <link=\"mtb-blend@multiply\">b</link> c",
                RichTextBlendTags.Preprocess("a <blend=multiply>b</blend> c"));

        [Test]
        public void Variants_AreDistinct()
        {
            string text = RichTextBlendTags.Preprocess(
                "<blend=screen>a</blend><blend=screen>b</blend><blend=overlay>c</blend>");
            var list = new List<MeshTextBlendMode>();
            RichTextBlendTags.GetVariants(text, list);
            CollectionAssert.AreEqual(new[] { MeshTextBlendMode.Screen, MeshTextBlendMode.Overlay }, list);
        }

        [Test]
        public void BaseAndVariantMarkup_SelectOppositeContent()
        {
            string text = RichTextBlendTags.Preprocess("a<blend=add>b</blend>c");
            StringAssert.Contains("<alpha=#FF>a", RichTextBlendTags.BuildBaseMarkup(text));
            StringAssert.Contains("<alpha=#00>", RichTextBlendTags.BuildBaseMarkup(text));
            StringAssert.Contains("<alpha=#FF>b", RichTextBlendTags.BuildVariantMarkup(text, MeshTextBlendMode.Add));
        }

        [Test]
        public void Noparse_DoesNotConvertExample()
            => Assert.AreEqual("<noparse><blend=screen>x</blend></noparse>",
                RichTextBlendTags.Preprocess("<noparse><blend=screen>x</blend></noparse>"));
    }
}
