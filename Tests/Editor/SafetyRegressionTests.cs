using System.Collections.Generic;
using System.Reflection;
using MeshTextBaker;
using MeshTextBaker.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace MeshTextBaker.Tests
{
    public class SafetyRegressionTests
    {
        [Test]
        public void BakeForLocale_MissingRenderer_ReturnsFailureAndClearsLocale()
        {
            var go = new GameObject("Missing renderer surface");
            var surface = go.AddComponent<MeshTextSurface>();
            try
            {
                LogAssert.Expect(LogType.Error,
                    "[MeshTextBaker] No MeshRenderer or SkinnedMeshRenderer found on this object, " +
                    "and at least one zone has no Target Renderer set. Either add a renderer here " +
                    "or assign Target Renderer on every zone.");
                MeshTextBakerCore.BakeResult result = surface.BakeForLocaleWithResult("fr");
                Assert.IsFalse(result.success);
                Assert.IsNull(surface.currentBakedLocale);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BakerAndLookup_IgnoreNullZones()
        {
            var go = new GameObject("Null zone surface");
            var surface = go.AddComponent<MeshTextSurface>();
            try
            {
                surface.zones.Add(null);
                MeshTextBakerCore.BakeResult result = MeshTextBakerCore.Bake(surface, "en");
                Assert.IsTrue(result.success);
                Assert.IsEmpty(result.bakes);
                Assert.IsNull(surface.FindZoneById("missing"));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PersistentExport_UsesSourceBaseline_AndRestoresExportOnClear()
        {
            Shader shader = Shader.Find("Hidden/MeshTextBaker/Tests/MaterialState");
            if (shader == null) Assert.Ignore("Material-state test shader was not imported.");

            var go = new GameObject("Persistent baseline surface");
            var renderer = go.AddComponent<MeshRenderer>();
            var surface = go.AddComponent<MeshTextSurface>();
            var source = new Material(shader) { name = "Source" };
            var exported = new Material(shader) { name = "Previous export" };
            var sourceMask = new Texture2D(1, 1) { name = "Source mask" };
            var exportedMask = new Texture2D(1, 1) { name = "Old baked mask" };
            var albedo = new RenderTexture(4, 4, 0);
            albedo.Create();

            try
            {
                source.SetTexture("_MetallicGlossMap", sourceMask);
                exported.SetTexture("_MetallicGlossMap", exportedMask);
                renderer.sharedMaterial = exported;
                surface.RecordPersistentBakeSource(renderer, 0, source, exported);

                var result = new MeshTextBakerCore.BakeResult { success = true };
                result.bakes.Add(new MeshTextBakerCore.MaterialBake
                {
                    renderer = renderer,
                    materialIndex = 0,
                    texture = albedo
                });
                InvokeApplyBakeResult(surface, result);

                Material live = surface.GetMaterialInstance(renderer, 0);
                Assert.NotNull(live);
                Assert.AreSame(sourceMask, live.GetTexture("_MetallicGlossMap"),
                    "The previous export must not be used as the next bake baseline.");
                Assert.AreSame(source, surface.GetBakeSourceMaterial(renderer, 0));
                Assert.AreSame(exported, surface.GetOriginalMaterial(renderer, 0));

                surface.ReleaseRenderTextures();
                Assert.AreSame(exported, renderer.sharedMaterial,
                    "Safe RT release must restore the material assigned before the live bake.");
                Assert.IsNull(surface.GetMaterialInstance(renderer, 0));
            }
            finally
            {
                surface.ClearBake();
                if (albedo != null)
                {
                    albedo.Release();
                    Object.DestroyImmediate(albedo);
                }
                Object.DestroyImmediate(sourceMask);
                Object.DestroyImmediate(exportedMask);
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(exported);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void OutputFolder_RejectsAssetsSiblingAndTraversal()
        {
            MethodInfo method = typeof(BakeToAssetUtility).GetMethod("TryNormalizeOutputFolder",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object[] sibling = { "AssetsBackup/Output", null, null };
            Assert.IsFalse((bool)method.Invoke(null, sibling));
            object[] traversal = { "Assets/../Outside", null, null };
            Assert.IsFalse((bool)method.Invoke(null, traversal));
            object[] valid = { "Assets/MeshTextBakerOutput", null, null };
            Assert.IsTrue((bool)method.Invoke(null, valid));
            StringAssert.StartsWith("Assets", (string)valid[1]);
        }

        [Test]
        public void StableTargetId_DiffersForSameNamedObjects()
        {
            var first = new GameObject("Duplicate");
            var second = new GameObject("Duplicate");
            var firstRenderer = first.AddComponent<MeshRenderer>();
            var secondRenderer = second.AddComponent<MeshRenderer>();
            var firstSurface = first.AddComponent<MeshTextSurface>();
            var secondSurface = second.AddComponent<MeshTextSurface>();
            try
            {
                MethodInfo method = typeof(BakeToAssetUtility).GetMethod("StableTargetId",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.NotNull(method);
                string a = (string)method.Invoke(null, new object[] { firstSurface, firstRenderer, 0 });
                string b = (string)method.Invoke(null, new object[] { secondSurface, secondRenderer, 0 });
                Assert.AreNotEqual(a, b);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void UnrelatedGlobalUndo_DoesNotDiscardDirtyDraft()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "saved" });
            var window = ScriptableObject.CreateInstance<MeshTextAssetEditorWindow>();
            try
            {
                SetField(window, "_asset", asset);
                SetField(window, "_workingLocaleIndex", 0);
                SetField(window, "_workingLocaleCode", "en");
                SetField(window, "_workingText", "unsaved draft");
                SetField(window, "_dirty", true);
                SetField(window, "_observedAssetState", EditorJsonUtility.ToJson(asset));
                GetStringList(window, "_undoStack").Add("older draft");

                InvokePrivate(window, "OnAssetUndoRedo");

                Assert.AreEqual("unsaved draft", GetField<string>(window, "_workingText"));
                Assert.IsTrue(GetField<bool>(window, "_dirty"));
                Assert.AreEqual(0, GetField<int>(window, "_workingLocaleIndex"));
                Assert.AreEqual(1, GetStringList(window, "_undoStack").Count);
            }
            finally
            {
                SetField(window, "_dirty", false);
                InvokePrivate(window, "UpdateUnsavedState");
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void AssetUndoWhileDraftDirty_PreservesDraft()
        {
            var asset = ScriptableObject.CreateInstance<MeshTextAsset>();
            asset.translations.Add(new LocalizedTextEntry { locale = "en", text = "before" });
            var window = ScriptableObject.CreateInstance<MeshTextAssetEditorWindow>();
            try
            {
                SetField(window, "_asset", asset);
                SetField(window, "_workingLocaleIndex", 0);
                SetField(window, "_workingLocaleCode", "en");
                SetField(window, "_workingText", "unsaved draft");
                SetField(window, "_dirty", true);
                SetField(window, "_observedAssetState", EditorJsonUtility.ToJson(asset));
                asset.translations[0].text = "changed by Unity undo";

                InvokePrivate(window, "OnAssetUndoRedo");

                Assert.AreEqual("unsaved draft", GetField<string>(window, "_workingText"));
                Assert.IsTrue(GetField<bool>(window, "_dirty"));
            }
            finally
            {
                SetField(window, "_dirty", false);
                InvokePrivate(window, "UpdateUnsavedState");
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(asset);
            }
        }

        private static void InvokeApplyBakeResult(MeshTextSurface surface,
            MeshTextBakerCore.BakeResult result)
        {
            MethodInfo method = typeof(MeshTextSurface).GetMethod("ApplyBakeResult",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(surface, new object[] { result });
        }

        private static void InvokePrivate(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(target, null);
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return (T)field.GetValue(target);
        }

        private static List<string> GetStringList(object target, string name)
            => GetField<List<string>>(target, name);
    }
}
