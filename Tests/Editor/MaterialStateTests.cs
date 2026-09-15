// Mesh Text Baker — MaterialStateTests.cs
// Regression coverage for book surfaces reused after a generated PBR page.

using System.Reflection;
using MeshTextBaker;
using NUnit.Framework;
using UnityEngine;

namespace MeshTextBaker.Tests
{
    public class MaterialStateTests
    {
        private const string TestShaderName = "Hidden/MeshTextBaker/Tests/MaterialState";

        [Test]
        public void PartialRebake_RestoresOriginalPbrStateBeforeApplyingNextPage()
        {
            Shader shader = Shader.Find(TestShaderName);
            if (shader == null)
                Assert.Ignore($"Test shader '{TestShaderName}' was not imported.");

            var go = new GameObject("MeshTextBaker material-state test");
            var renderer = go.AddComponent<MeshRenderer>();
            var surface = go.AddComponent<MeshTextSurface>();
            var original = new Material(shader) { name = "Original page" };
            var authoredMask = new Texture2D(1, 1) { name = "Authored mask" };
            var generatedMask = new Texture2D(1, 1) { name = "Generated mask" };
            var albedo = new RenderTexture(4, 4, 0);
            albedo.Create();

            try
            {
                original.SetColor("_BaseColor", Color.black);
                original.SetColor("_Color", Color.black);
                original.SetFloat("_Metallic", 0.2f);
                original.SetFloat("_Smoothness", 0.35f);
                original.SetTexture("_MetallicGlossMap", authoredMask);
                original.EnableKeyword("_METALLICGLOSSMAP");
                renderer.sharedMaterial = original;

                var result = new MeshTextBakerCore.BakeResult { success = true };
                result.bakes.Add(new MeshTextBakerCore.MaterialBake
                {
                    renderer = renderer,
                    materialIndex = 0,
                    texture = albedo,
                    emission = null,
                    pbrMask = null
                });

                InvokeApplyBakeResult(surface, result);
                Material instance = surface.GetMaterialInstance(renderer, 0);
                Assert.NotNull(instance);
                Assert.AreEqual(Color.white, instance.GetColor("_BaseColor"));
                Assert.AreEqual(Color.white, instance.GetColor("_Color"));

                // A page without generated PBR must retain author-defined source state.
                Assert.AreSame(authoredMask, instance.GetTexture("_MetallicGlossMap"));
                Assert.AreEqual(0.2f, instance.GetFloat("_Metallic"), 0.0001f);
                Assert.AreEqual(0.35f, instance.GetFloat("_Smoothness"), 0.0001f);
                Assert.IsTrue(instance.IsKeywordEnabled("_METALLICGLOSSMAP"));

                // Simulate the state left by a generated mask page: full-range scalar
                // multipliers plus a temporary map. The next physical page has no PBR map.
                instance.SetFloat("_Metallic", 1f);
                instance.SetFloat("_Smoothness", 1f);
                instance.SetTexture("_MetallicGlossMap", generatedMask);
                instance.EnableKeyword("_METALLICGLOSSMAP");

                InvokeApplyBakeResult(surface, result);
                Material reused = surface.GetMaterialInstance(renderer, 0);

                Assert.AreSame(instance, reused, "The pooled material instance should be reused.");
                Assert.AreSame(authoredMask, reused.GetTexture("_MetallicGlossMap"));
                Assert.AreEqual(0.2f, reused.GetFloat("_Metallic"), 0.0001f);
                Assert.AreEqual(0.35f, reused.GetFloat("_Smoothness"), 0.0001f);
                Assert.IsTrue(reused.IsKeywordEnabled("_METALLICGLOSSMAP"));
            }
            finally
            {
                surface.ClearBake();
                albedo.Release();
                Object.DestroyImmediate(albedo);
                Object.DestroyImmediate(authoredMask);
                Object.DestroyImmediate(generatedMask);
                Object.DestroyImmediate(original);
                Object.DestroyImmediate(go);
            }
        }

        private static void InvokeApplyBakeResult(MeshTextSurface surface,
            MeshTextBakerCore.BakeResult result)
        {
            MethodInfo method = typeof(MeshTextSurface).GetMethod("ApplyBakeResult",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, "MeshTextSurface.ApplyBakeResult was not found.");
            method.Invoke(surface, new object[] { result });
        }
    }
}
