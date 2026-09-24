using System.Text.RegularExpressions;
using MeshTextBaker;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MeshTextBaker.Tests
{
    public class ZoneBakeQueueTests
    {
        [Test]
        public void NewZone_BakesByDefault_AndCanBeRemovedFromQueue()
        {
            var go = new GameObject("bake queue");
            var surface = go.AddComponent<MeshTextSurface>();
            try
            {
                Assert.IsTrue(new TextZone().bakeEnabled);

                TextZone first = surface.AddZone();
                TextZone second = surface.AddZone();
                Assert.IsTrue(first.bakeEnabled);
                Assert.AreEqual(2, surface.GetBakeQueue().Count);
                Assert.IsTrue(surface.IsZoneBakeEnabled(first.id));

                Assert.IsTrue(surface.SetZoneBakeEnabled(first.id, false));
                Assert.IsFalse(first.bakeEnabled);
                Assert.IsFalse(surface.IsZoneBakeEnabled(first.id));
                Assert.AreEqual(1, surface.GetBakeQueue().Count);
                Assert.AreSame(second, surface.GetBakeQueue()[0]);

                Assert.IsFalse(surface.SetZoneBakeEnabled("missing", true));
                Assert.IsFalse(surface.SetZoneBakeEnabled(-1, false));
                Assert.AreEqual(1, surface.SetAllZonesBakeEnabled(false));
                Assert.IsEmpty(surface.GetBakeQueue());
                Assert.AreEqual(0, surface.SetAllZonesBakeEnabled(false));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void AllZonesDisabled_DoesNotRequireARenderer()
        {
            var go = new GameObject("disabled zones");
            var surface = go.AddComponent<MeshTextSurface>();
            try
            {
                TextZone zone = surface.AddZone();
                zone.bakeEnabled = false;

                MeshTextBakerCore.BakeResult result = surface.BakeForLocaleWithResult("en");
                Assert.IsTrue(result.success);
                Assert.IsEmpty(result.bakes);
                Assert.AreEqual("en", surface.currentBakedLocale);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DuplicateZone_CopiesData_AndDoesNotShareOverflowLink()
        {
            var go = new GameObject("duplicate zone");
            var surface = go.AddComponent<MeshTextSurface>();
            try
            {
                TextZone zone = surface.AddZone();
                zone.displayName = "Title";
                zone.rotation = 25f;
                zone.uvRect = new Rect(0.1f, 0.2f, 0.3f, 0.4f);
                zone.localizationKey = "key";
                zone.overflowBehavior = TextZoneOverflow.ContinueToSubZones;
                zone.continueToZoneId = "child";
                zone.parentZoneId = "page-slot";
                zone.bakeEnabled = false;
                zone.textColor = Color.red;

                TextZone copy = surface.DuplicateZone(zone);
                Assert.IsNotNull(copy);
                Assert.AreNotEqual(zone.id, copy.id);
                Assert.AreEqual("Title Copy", copy.displayName);
                Assert.AreEqual(25f, copy.rotation, 0.001f);
                Assert.AreEqual(0.3f, copy.uvRect.width, 0.0001f);
                Assert.AreEqual(zone.uvRect.x + 0.03f, copy.uvRect.x, 0.0001f);
                Assert.AreEqual(zone.uvRect.y + 0.03f, copy.uvRect.y, 0.0001f);
                Assert.AreEqual("key", copy.localizationKey);
                Assert.AreEqual(Color.red, copy.textColor);
                Assert.AreEqual("", copy.continueToZoneId);
                Assert.AreEqual("", copy.parentZoneId);
                Assert.AreEqual("page-slot", zone.parentZoneId);
                Assert.AreEqual(TextZoneOverflow.ShrinkToFit, copy.overflowBehavior);
                Assert.IsFalse(copy.bakeEnabled);
                Assert.AreEqual(2, surface.zones.Count);

                TextZone again = surface.DuplicateZone(zone);
                Assert.AreEqual("Title Copy 2", again.displayName);
                Assert.IsNull(surface.DuplicateZone(null));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void EnabledZone_WithoutRenderer_StillFails()
        {
            var go = new GameObject("enabled needs renderer");
            var surface = go.AddComponent<MeshTextSurface>();
            try
            {
                surface.AddZone();
                var skipped = surface.AddZone();
                skipped.bakeEnabled = false;

                LogAssert.Expect(LogType.Error, new Regex("No MeshRenderer or SkinnedMeshRenderer"));
                MeshTextBakerCore.BakeResult result = surface.BakeForLocaleWithResult("en");
                Assert.IsFalse(result.success);
                Assert.IsNull(surface.currentBakedLocale);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
