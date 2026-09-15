using System.Collections.Generic;
using MeshTextBaker;
using NUnit.Framework;
using TMPro;

namespace MeshTextBaker.Tests
{
    public class InlineFontResolverTests
    {
        [Test]
        public void LibraryAlias_RewritesToRegisteredTmpAssetName()
        {
            TMP_FontAsset font = TMP_Settings.defaultFontAsset;
            if (font == null) Assert.Ignore("TMP default font asset is not installed.");
            var library = new List<NamedFont> { new NamedFont { name = "body", font = font } };
            string output = InlineFontResolver.Process("a <font=body>b</font> c", library);
            Assert.AreEqual($"a <font=\"{font.name}\">b</font> c", output);
        }

        [Test]
        public void FontInsideNoparse_RemainsLiteral()
        {
            TMP_FontAsset font = TMP_Settings.defaultFontAsset;
            if (font == null) Assert.Ignore("TMP default font asset is not installed.");
            var library = new List<NamedFont> { new NamedFont { name = "body", font = font } };
            const string source = "<noparse><font=body>example</font></noparse>";
            Assert.AreEqual(source, InlineFontResolver.Process(source, library));
        }

        [Test]
        public void UnknownName_IsLeftForNativeTmpResourcesLookup()
            => Assert.AreEqual("<font=NativeFont>x</font>",
                InlineFontResolver.Process("<font=NativeFont>x</font>", null));
    }
}
