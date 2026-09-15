// Mesh Text Baker — InlineImageParserTests.cs
// EditMode tests for <image=...> processing and source mapping.

using System.Collections.Generic;
using NUnit.Framework;
using MeshTextBaker;
using UnityEngine;

namespace MeshTextBaker.Tests
{
    public class InlineImageParserTests
    {
        [Test]
        public void HasImageTags_Detects()
        {
            Assert.IsTrue(InlineImageParser.HasImageTags("x <image=seal> y"));
            Assert.IsFalse(InlineImageParser.HasImageTags("no images here"));
        }

        [Test]
        public void MapProcessedToSource_SnapsInsideReplacementToTagStart()
        {
            var segments = new List<ImageTagSegment>
            {
                new ImageTagSegment { srcStart = 2, srcLen = 12, procStart = 2, procLen = 1 }
            };
            // "a <image=x> b" → processed "a \u200B b"; index inside replacement snaps to 2
            Assert.AreEqual(2, InlineImageParser.MapProcessedToSource(2, segments));
            Assert.AreEqual(2, InlineImageParser.MapProcessedToSource(2 + 0, segments));
            // after replacement: proc 3 maps to src 2+12 + (3-(2+1)) = 14? 
            // procOffset after seg = srcLen - procLen = 11
            // procIndex 3 >= 3 → return 3 + 11 = 14
            Assert.AreEqual(14, InlineImageParser.MapProcessedToSource(3, segments));
        }

        [Test]
        public void MapProcessedToSource_EmptySegments_Identity()
        {
            Assert.AreEqual(5, InlineImageParser.MapProcessedToSource(5, null));
            Assert.AreEqual(5, InlineImageParser.MapProcessedToSource(5, new List<ImageTagSegment>()));
        }

        [Test]
        public void Process_UnknownLibraryName_DropsTagAndWarns()
        {
            var warnings = new List<string>();
            var requests = new List<InlineImageRequest>();
            string outText = InlineImageParser.Process(
                "before <image=missing> after", 16f, null, requests, warnings);
            Assert.AreEqual("before  after", outText);
            Assert.AreEqual(0, requests.Count);
            Assert.IsTrue(warnings.Count >= 1);
        }

        [Test]
        public void FilePath_RejectsParentDirectory()
        {
            var warnings = new List<string>();
            var requests = new List<InlineImageRequest>();
            // file: with .. should be rejected without throwing
            string outText = InlineImageParser.Process(
                "x <image=file:../secret.png> y", 16f, null, requests, warnings);
            Assert.AreEqual("x  y", outText);
            Assert.AreEqual(0, requests.Count);
            Assert.IsTrue(warnings.Exists(w => w.Contains("..") || w.Contains("not allowed")));
        }

        [Test]
        public void ImageInsideNoparse_RemainsLiteral()
        {
            var tex = new Texture2D(2, 2);
            try
            {
                var library = new List<NamedImage> { new NamedImage { name = "seal", texture = tex } };
                var requests = new List<InlineImageRequest>();
                string source = "<noparse><image=seal h=120></noparse>";
                string output = InlineImageParser.Process(source, 16f, library, requests, new List<string>());
                Assert.AreEqual(source, output);
                Assert.AreEqual(0, requests.Count);
            }
            finally { Object.DestroyImmediate(tex); }
        }

        [Test]
        public void Image_InheritsSurfaceHeightAndDirectBlend()
        {
            var tex = new Texture2D(2, 2);
            try
            {
                var library = new List<NamedImage> { new NamedImage { name = "seal", texture = tex } };
                var requests = new List<InlineImageRequest>();
                string source = RichTextPbrTags.Preprocess(
                    "<surface h=0.75><opacity=0.5><image=seal blend=screen opacity=0.5></opacity></surface>");
                source = RichTextOpacityTags.Preprocess(source);
                InlineImageParser.Process(source, 16f, library, requests, new List<string>());
                Assert.AreEqual(1, requests.Count);
                Assert.AreEqual(0.75f, requests[0].scopeHeight, 0.001f);
                Assert.IsTrue(requests[0].hasBlendMode);
                Assert.AreEqual(MeshTextBlendMode.Screen, requests[0].blendMode);
                Assert.AreEqual(0.25f, requests[0].opacity, 0.001f);
            }
            finally { Object.DestroyImmediate(tex); }
        }
    }
}
