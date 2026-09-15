using MeshTextBaker;
using NUnit.Framework;
using UnityEngine;

namespace MeshTextBaker.Tests
{
    public class MeshTextAssetMarkupTests
    {
        [Test]
        public void NewAsset_DefaultsToRichTextAndMarkdownAuthoring()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            try
            {
                Assert.IsTrue(asset.richTextEnabled);
                Assert.IsTrue(asset.markdownEnabled);
            }
            finally { Object.DestroyImmediate(asset); }
        }

        [Test]
        public void MarkupDetection_ScansAllLocales()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            try
            {
                asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "plain" });
                asset.translations.Add(new LocalizedTextEntry { locale = "ru", text = "**жирный** <color=red>цвет</color>" });
                Assert.IsTrue(asset.ContainsMarkdownMarkup());
                Assert.IsTrue(asset.ContainsRichTextMarkup());
            }
            finally { Object.DestroyImmediate(asset); }
        }
    }
}
