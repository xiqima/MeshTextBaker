using MeshTextBaker;
using NUnit.Framework;

namespace MeshTextBaker.Tests
{
    public class TextLayoutDefaultsTests
    {
        [Test]
        public void ZeroZoneSize_UsesDefaultFontSizeMode()
        {
            var settings = new BakeSettings
            {
                defaultFontSize = 10f,
                defaultFontSizeMode = FontSizeMode.PercentOfZoneHeight
            };
            var zone = new TextZone
            {
                fontSize = 0f,
                fontSizeMode = FontSizeMode.Absolute
            };
            float pixels = TextLayoutEngine.ResolveZonePixelFontSize(zone, settings, 200, 1024);
            Assert.AreEqual(20f, pixels, 0.001f);
        }
    }
}
