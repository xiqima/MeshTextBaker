using MeshTextBaker;
using NUnit.Framework;

namespace MeshTextBaker.Tests
{
    public class RichTextOpacityTagsTests
    {
        [Test]
        public void Preprocess_StoresOpacityInZeroWidthLink()
            => Assert.AreEqual("a<link=\"mtb-opacity@0.5\">b</link>c",
                RichTextOpacityTags.Preprocess("a<opacity=0.5>b</opacity>c"));

        [Test]
        public void Apply_InjectsAndRestoresAlpha()
        {
            string pre = RichTextOpacityTags.Preprocess("a<opacity=0.5>b</opacity>c");
            string applied = RichTextOpacityTags.Apply(pre);
            Assert.AreEqual("a<link=\"mtb-opacity@0.5\"><alpha=#80>b</link><alpha=#FF>c", applied);
        }

        [Test]
        public void NestedOpacity_Multiplies()
        {
            string pre = RichTextOpacityTags.Preprocess(
                "<opacity=0.5>a<opacity=0.5>b</opacity>c</opacity>");
            string applied = RichTextOpacityTags.Apply(pre);
            StringAssert.Contains("<alpha=#40>b", applied);
            StringAssert.Contains("<alpha=#80>c", applied);
        }

        [Test]
        public void ExistingVisibilityAlpha_IsMultiplied()
        {
            string pre = RichTextOpacityTags.Preprocess("<alpha=#80><opacity=0.5>x</opacity>");
            string applied = RichTextOpacityTags.Apply(pre);
            StringAssert.Contains("<alpha=#40>x", applied);
        }
    }
}
