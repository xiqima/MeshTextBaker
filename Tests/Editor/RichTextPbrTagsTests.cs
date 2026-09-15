// Mesh Text Baker — RichTextPbrTagsTests.cs
// EditMode tests for per-word PBR tags (<emission>, <surface>).

using NUnit.Framework;
using MeshTextBaker;

namespace MeshTextBaker.Tests
{
    public class RichTextPbrTagsTests
    {
        [Test]
        public void Preprocess_Emission_BecomesZeroWidthLink()
            => Assert.AreEqual("a <link=\"mtb-glow\">word</link> b",
                RichTextPbrTags.Preprocess("a <emission>word</emission> b"));

        [Test]
        public void Preprocess_EmissionWithColor_KeepsHex()
            => Assert.AreEqual("<link=\"mtb-glow#00ffcc\">x</link>",
                RichTextPbrTags.Preprocess("<emission=#00ffcc>x</emission>"));

        [Test]
        public void Preprocess_EmissionWithIntensity_KeepsI()
            => Assert.AreEqual("<link=\"mtb-glow#00ffcc@2\">x</link>",
                RichTextPbrTags.Preprocess("<emission=#00ffcc i=2>x</emission>"));

        [Test]
        public void Preprocess_Surface_BecomesLink()
            => Assert.AreEqual("<link=\"mtb-shine\">gold</link>",
                RichTextPbrTags.Preprocess("<surface>gold</surface>"));

        [Test]
        public void Preprocess_SurfaceWithParams_KeepsMS()
            => Assert.AreEqual("<link=\"mtb-shine@1,0.9\">gold</link>",
                RichTextPbrTags.Preprocess("<surface m=1 s=0.9>gold</surface>"));

        [Test]
        public void Preprocess_PlainText_Unchanged()
        {
            const string s = "plain <b>text</b> 2 < 3";
            Assert.AreEqual(s, RichTextPbrTags.Preprocess(s));
        }

        [Test]
        public void GlowMarkup_HidesOutside_ShowsInside()
        {
            string pre = RichTextPbrTags.Preprocess("ab <emission>cd</emission> ef");
            Assert.AreEqual(
                "<alpha=#00>ab <link=\"mtb-glow\"><alpha=#FF>cd</link><alpha=#00> ef",
                RichTextPbrTags.BuildGlowMarkup(pre));
        }

        [Test]
        public void GlowMarkup_CustomColor_WrapsScope()
        {
            string pre = RichTextPbrTags.Preprocess("x <emission=#ff0000>y</emission> z");
            Assert.AreEqual(
                "<alpha=#00>x <link=\"mtb-glow#ff0000\"><alpha=#FF><color=#ff0000>y</color></link><alpha=#00> z",
                RichTextPbrTags.BuildGlowMarkup(pre));
        }

        [Test]
        public void ShineMarkup_NoColorTags()
        {
            string pre = RichTextPbrTags.Preprocess("m <surface>n</surface> o");
            Assert.AreEqual(
                "<alpha=#00>m <link=\"mtb-shine\"><alpha=#FF>n</link><alpha=#00> o",
                RichTextPbrTags.BuildShineMarkup(pre));
        }

        [Test]
        public void HasGlow_HasShine_DetectScopes()
        {
            Assert.IsTrue(RichTextPbrTags.HasGlow(RichTextPbrTags.Preprocess("<emission>a</emission>")));
            Assert.IsFalse(RichTextPbrTags.HasShine(RichTextPbrTags.Preprocess("<surface>a</surface>")),
                "Bare surface is intentionally a visual PBR no-op.");
            Assert.IsTrue(RichTextPbrTags.HasShine(RichTextPbrTags.Preprocess("<surface m=1>a</surface>")));
            Assert.IsFalse(RichTextPbrTags.HasGlow("plain"));
            Assert.IsFalse(RichTextPbrTags.HasShine("plain"));
        }

        [Test]
        public void TagRepair_ReopensEmissionLink_AcrossPageBoundary()
        {
            string pre = RichTextPbrTags.Preprocess("<emission>hello world</emission>");
            // Cut after "hello" content, not a magic absolute index.
            int cut = pre.IndexOf(" world");
            Assert.Greater(cut, 0);
            RichTextTagRepair.Split(pre, cut, out string head, out string tail);
            Assert.AreEqual("<link=\"mtb-glow\">hello</link>", head);
            Assert.AreEqual("<link=\"mtb-glow\"> world</link>", tail);
        }

        [Test]
        public void GetMaxGlowIntensity_ReadsI()
        {
            string pre = RichTextPbrTags.Preprocess("<emission i=3>a</emission> <emission i=1.5>b</emission>");
            Assert.AreEqual(3f, RichTextPbrTags.GetMaxGlowIntensity(pre), 0.001f);
        }

        [Test]
        public void SurfaceHeight_IsEncodedAndDetectedWithoutMetalMask()
        {
            string pre = RichTextPbrTags.Preprocess("<surface h=-0.5>x</surface>");
            Assert.AreEqual("<link=\"mtb-shine@,,-0.5\">x</link>", pre);
            Assert.IsTrue(RichTextPbrTags.HasHeight(pre));
            Assert.IsFalse(RichTextPbrTags.HasShine(pre));
            RichTextPbrTags.ParseSurfaceParams(pre, out float m, out float s, out float h);
            Assert.AreEqual(-1f, m);
            Assert.AreEqual(-1f, s);
            Assert.AreEqual(-0.5f, h, 0.001f);
        }

        [Test]
        public void BareSurface_HasNoHeightOrShine()
        {
            string pre = RichTextPbrTags.Preprocess("<surface>x</surface>");
            Assert.IsFalse(RichTextPbrTags.HasHeight(pre));
            Assert.IsFalse(RichTextPbrTags.HasShine(pre));
        }
    }
}
