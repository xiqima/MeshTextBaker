// Mesh Text Baker — BakeToAssetUtility.cs
// "Bake to Asset": converts the surface's live RenderTexture bakes into persistent PNG
// textures/materials and assigns them to renderers. Export is based on the surface's recorded
// source materials, never on an earlier persistent export.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MeshTextBaker.Editor
{
    public static class BakeToAssetUtility
    {
        private const string DefaultFolder = "Assets/MeshTextBakerOutput";
        private const string LastFolderPrefKey = "MeshTextBaker.BakeToAsset.LastFolder";

        private sealed class RendererSnapshot
        {
            public Renderer renderer;
            public Material[] materials;
        }

        private sealed class ExportPlan
        {
            public MeshTextBakerCore.MaterialBake bake;
            public Renderer renderer;
            public int slot;
            public Material liveMaterial;
            public Material sourceMaterial;
            public Material candidateMaterial;
            public Material persistentMaterial;
            public string baseName;
            public string materialPath;
            public string albedoPath;
            public string emissionPath;
            public string maskPath;
            public string heightPath;
            public string emissionProperty;
            public string maskProperty;
            public string heightProperty;
            public Texture2D albedoTexture;
            public Texture2D emissionTexture;
            public Texture2D maskTexture;
            public Texture2D heightTexture;
        }

        private sealed class FileBackup
        {
            public string path;
            public bool existed;
            public byte[] contents;
            public byte[] metaContents;
        }

        private sealed class MaterialBackup
        {
            public string path;
            public Material asset;
            public Material snapshot;
            public string originalName;
            public bool created;
        }

        /// <summary>
        /// Asks the user for an output folder (remembers the last choice), then bakes.
        /// Returns the number of albedo textures written; 0 if cancelled or failed.
        /// </summary>
        public static int BakeToAssetsInteractive(MeshTextSurface surface, string locale)
        {
            if (surface == null) return 0;

            string startFolder = EditorPrefs.GetString(LastFolderPrefKey, DefaultFolder);
            if (!AssetDatabase.IsValidFolder(startFolder)) startFolder = "Assets";

            string absolute = EditorUtility.SaveFolderPanel(
                "Bake to Asset — output folder", startFolder, "");
            if (string.IsNullOrEmpty(absolute)) return 0;

            string assetsFull;
            string selectedFull;
            try
            {
                assetsFull = NormalizeFullPath(Application.dataPath);
                selectedFull = NormalizeFullPath(absolute);
            }
            catch (System.Exception)
            {
                EditorUtility.DisplayDialog("Bake to Asset", "The selected folder path is invalid.", "OK");
                return 0;
            }

            if (!IsSameOrChildPath(selectedFull, assetsFull))
            {
                EditorUtility.DisplayDialog("Bake to Asset",
                    "The output folder must be inside this project's Assets folder.", "OK");
                return 0;
            }

            string relative = "Assets" + selectedFull.Substring(assetsFull.Length).Replace('\\', '/');
            EditorPrefs.SetString(LastFolderPrefKey, relative);
            return BakeToAssets(surface, locale, relative);
        }

        /// <summary>
        /// Performs a fresh bake and persists every returned target. Renderer assignments are
        /// deferred until all PNG/material assets are ready. On failure, renderer/material/file
        /// state is rolled back best-effort and all live GPU state is released safely.
        /// </summary>
        public static int BakeToAssets(MeshTextSurface surface, string locale, string outputFolder = null)
        {
            if (surface == null) return 0;
            if (!TryNormalizeOutputFolder(outputFolder, out string folder, out string folderError))
            {
                Debug.LogError("[MeshTextBaker] Bake to Asset aborted: " + folderError, surface);
                return 0;
            }

            string texProp = string.IsNullOrEmpty(surface.bakeSettings.texturePropertyName)
                ? MeshTextBakerCore.DetectTexturePropertyName()
                : surface.bakeSettings.texturePropertyName;
            string emitProp = string.IsNullOrEmpty(surface.bakeSettings.emissionPropertyName)
                ? MeshTextBakerCore.DetectEmissionPropertyName()
                : surface.bakeSettings.emissionPropertyName;

            List<RendererSnapshot> rendererSnapshots;
            try
            {
                if (TryFindUntrackedLegacyExport(surface, texProp, out string legacyError))
                {
                    // An untracked export has no recoverable clean baseline. Fail closed instead
                    // of baking over already-baked text once more.
                    Debug.LogError("[MeshTextBaker] Bake to Asset aborted: " + legacyError, surface);
                    return 0;
                }
                rendererSnapshots = CaptureRendererState(surface);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[MeshTextBaker] Bake to Asset preflight failed: " + ex, surface);
                return 0;
            }

            var fileBackups = new List<FileBackup>();
            var materialBackups = new List<MaterialBackup>();
            var plans = new List<ExportPlan>();

            try
            {
                MeshTextBakerCore.BakeResult result = surface.BakeForLocaleWithResult(locale);
                if (result == null || !result.success ||
                    !string.Equals(surface.currentBakedLocale, locale, System.StringComparison.Ordinal))
                {
                    ReleaseAndRestore(surface, rendererSnapshots);
                    Debug.LogError("[MeshTextBaker] Bake to Asset aborted because the fresh bake failed.", surface);
                    return 0;
                }

                EnsureOutputFolder(folder);
                BuildExportPlans(surface, result, locale, folder, texProp, emitProp, plans);
                if (plans.Count == 0)
                {
                    surface.ReleaseLiveBakeKeepingMaterials();
                    Debug.LogWarning("[MeshTextBaker] Bake succeeded but produced no exportable targets.", surface);
                    return 0;
                }

                // Read every RT and import every texture before changing any renderer.
                for (int i = 0; i < plans.Count; i++)
                {
                    ExportPlan plan = plans[i];
                    plan.albedoTexture = SaveWithBackup(plan.bake.texture, plan.albedoPath,
                        false, fileBackups);
                    if (plan.bake.emission != null && !string.IsNullOrEmpty(plan.emissionPath))
                        plan.emissionTexture = SaveWithBackup(plan.bake.emission, plan.emissionPath,
                            false, fileBackups);
                    if (plan.bake.pbrMask != null && !string.IsNullOrEmpty(plan.maskPath))
                        plan.maskTexture = SaveWithBackup(plan.bake.pbrMask, plan.maskPath,
                            true, fileBackups);
                    if (plan.bake.height != null && !string.IsNullOrEmpty(plan.heightPath))
                        plan.heightTexture = SaveWithBackup(plan.bake.height, plan.heightPath,
                            true, fileBackups);
                    plan.candidateMaterial = BuildPersistentMaterial(plan, texProp);
                }

                // Existing materials are updated in place to preserve their GUIDs and every
                // renderer/reference that already points at them.
                for (int i = 0; i < plans.Count; i++)
                    CommitMaterialAsset(plans[i], materialBackups);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                AssignPersistentMaterials(plans);

                // Renderers now point at persistent assets, so generated materials/RTs can be
                // destroyed without restoring over the new assignment.
                surface.ReleaseLiveBakeKeepingMaterials();
                for (int i = 0; i < plans.Count; i++)
                {
                    ExportPlan plan = plans[i];
                    surface.RecordPersistentBakeSource(plan.renderer, plan.slot,
                        plan.sourceMaterial, plan.persistentMaterial);
                }
                try { AssetDatabase.SaveAssets(); }
                catch (System.Exception saveEx)
                {
                    // The export itself is complete and valid in memory. Do not roll it back
                    // merely because Unity could not flush dirty prefab/asset state right now.
                    Debug.LogWarning("[MeshTextBaker] Export completed, but SaveAssets failed: " +
                        saveEx.Message, surface);
                }
                Debug.Log($"[MeshTextBaker] Baked {plans.Count} texture(s) to '{folder}'.", surface);
                return plans.Count;
            }
            catch (System.Exception ex)
            {
                ReleaseAndRestore(surface, rendererSnapshots);
                RollBackMaterials(materialBackups);
                RollBackFiles(fileBackups);
                try
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                }
                catch (System.Exception rollbackEx)
                {
                    Debug.LogError("[MeshTextBaker] Export rollback also failed: " + rollbackEx.Message, surface);
                }
                Debug.LogError("[MeshTextBaker] Bake to Asset failed and was rolled back: " + ex, surface);
                return 0;
            }
            finally
            {
                for (int i = 0; i < plans.Count; i++)
                    DestroyTransientMaterial(plans[i].candidateMaterial);
                for (int i = 0; i < materialBackups.Count; i++)
                    DestroyTransientMaterial(materialBackups[i].snapshot);
            }
        }

        private static void DestroyTransientMaterial(Material material)
        {
            if (material == null || AssetDatabase.Contains(material)) return;
            try { Object.DestroyImmediate(material); }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[MeshTextBaker] Could not destroy a temporary material: " +
                    ex.Message);
            }
        }

        private static void BuildExportPlans(MeshTextSurface surface,
            MeshTextBakerCore.BakeResult result, string locale, string folder,
            string texProp, string emitProp, List<ExportPlan> plans)
        {
            var paths = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.bakes.Count; i++)
            {
                MeshTextBakerCore.MaterialBake bake = result.bakes[i];
                Renderer renderer = bake.renderer != null ? bake.renderer : surface.targetRenderer;
                if (renderer == null)
                    throw new System.InvalidOperationException("A bake target has no renderer.");

                Material[] materials = renderer.sharedMaterials;
                if (bake.materialIndex < 0 || bake.materialIndex >= materials.Length ||
                    materials[bake.materialIndex] == null)
                    throw new System.InvalidOperationException(
                        $"Material slot {bake.materialIndex} is invalid on '{renderer.name}'.");
                if (bake.texture == null)
                    throw new System.InvalidOperationException(
                        $"Bake target '{renderer.name}' slot {bake.materialIndex} has no albedo texture.");

                Material live = materials[bake.materialIndex];
                Material source = surface.GetBakeSourceMaterial(renderer, bake.materialIndex) ?? live;
                if (!live.HasProperty(texProp) || !source.HasProperty(texProp))
                    throw new System.InvalidOperationException(
                        $"Material '{source.name}' has no albedo property '{texProp}'.");
                string unique = StableTargetId(surface, renderer, bake.materialIndex);
                string readable = Sanitize(
                    $"{surface.name}_{renderer.name}_slot{bake.materialIndex}_{locale}");
                if (readable.Length > 120) readable = readable.Substring(0, 120);
                string baseName = readable + "_" + unique;
                var plan = new ExportPlan
                {
                    bake = bake,
                    renderer = renderer,
                    slot = bake.materialIndex,
                    liveMaterial = live,
                    sourceMaterial = source,
                    baseName = baseName,
                    materialPath = AssetPath(folder, baseName + ".mat"),
                    albedoPath = AssetPath(folder, baseName + ".png")
                };

                if (bake.emission != null && live.HasProperty(emitProp))
                {
                    plan.emissionProperty = emitProp;
                    plan.emissionPath = AssetPath(folder, baseName + "_emission.png");
                }
                else if (bake.emission != null)
                    Debug.LogWarning($"[MeshTextBaker] '{live.name}' has no '{emitProp}'; emission was not exported.", surface);

                string maskProp = MeshTextBakerCore.DetectMaskPropertyName(live);
                if (bake.pbrMask != null && !string.IsNullOrEmpty(maskProp) && live.HasProperty(maskProp))
                {
                    plan.maskProperty = maskProp;
                    plan.maskPath = AssetPath(folder, baseName + "_mask.png");
                }
                else if (bake.pbrMask != null)
                    Debug.LogWarning($"[MeshTextBaker] '{live.name}' has no compatible mask property; mask was not exported.", surface);

                string heightProp = string.IsNullOrEmpty(surface.bakeSettings.heightPropertyName)
                    ? MeshTextBakerCore.DetectHeightPropertyName(live)
                    : surface.bakeSettings.heightPropertyName;
                if (bake.height != null && !string.IsNullOrEmpty(heightProp) && live.HasProperty(heightProp))
                {
                    plan.heightProperty = heightProp;
                    plan.heightPath = AssetPath(folder, baseName + "_height.png");
                }
                else if (bake.height != null)
                    Debug.LogWarning($"[MeshTextBaker] '{live.name}' has no compatible height property; height was not exported.", surface);

                AddUniquePath(paths, plan.materialPath);
                AddUniquePath(paths, plan.albedoPath);
                if (!string.IsNullOrEmpty(plan.emissionPath)) AddUniquePath(paths, plan.emissionPath);
                if (!string.IsNullOrEmpty(plan.maskPath)) AddUniquePath(paths, plan.maskPath);
                if (!string.IsNullOrEmpty(plan.heightPath)) AddUniquePath(paths, plan.heightPath);
                plans.Add(plan);
            }
        }

        private static Material BuildPersistentMaterial(ExportPlan plan, string texProp)
        {
            var persistent = new Material(plan.sourceMaterial) { name = plan.baseName };

            // The live material contains pipeline-specific emission state prepared by
            // MeshTextSurface. Copy it when needed, then replace every generated RT reference.
            if (plan.emissionTexture != null)
                persistent.CopyPropertiesFromMaterial(plan.liveMaterial);
            if (persistent.HasProperty(texProp)) persistent.SetTexture(texProp, plan.albedoTexture);

            if (plan.emissionTexture != null && persistent.HasProperty(plan.emissionProperty))
            {
                persistent.SetTexture(plan.emissionProperty, plan.emissionTexture);
                persistent.EnableKeyword("_EMISSION");
                persistent.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            if (plan.maskTexture != null && persistent.HasProperty(plan.maskProperty))
            {
                CopyMaskState(plan.liveMaterial, persistent);
                persistent.SetTexture(plan.maskProperty, plan.maskTexture);
                if (plan.maskProperty == "_MaskMap") persistent.EnableKeyword("_MASKMAP");
                else
                {
                    persistent.EnableKeyword("_METALLICGLOSSMAP");
                    persistent.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
            }
            if (plan.heightTexture != null && persistent.HasProperty(plan.heightProperty))
            {
                CopyHeightState(plan.liveMaterial, persistent);
                persistent.SetTexture(plan.heightProperty, plan.heightTexture);
                if (plan.heightProperty == "_ParallaxMap") persistent.EnableKeyword("_PARALLAXMAP");
                else persistent.EnableKeyword("_HEIGHTMAP");
            }

            MeshTextBakerCore.SetMaterialAlbedoColorWhite(persistent);
            MaterialPipelineUtility.ValidateMaterial(persistent);
            return persistent;
        }

        private static void CommitMaterialAsset(ExportPlan plan, List<MaterialBackup> backups)
        {
            Object mainAsset = AssetDatabase.LoadMainAssetAtPath(plan.materialPath);
            if (mainAsset != null && !(mainAsset is Material))
                throw new System.InvalidOperationException(
                    $"Export path '{plan.materialPath}' is occupied by a non-material asset.");
            if (mainAsset == null && File.Exists(plan.materialPath))
                throw new System.InvalidOperationException(
                    $"Export path '{plan.materialPath}' is occupied by an unimported file.");

            Material existing = mainAsset as Material;
            if (existing != null)
            {
                var backup = new MaterialBackup
                {
                    path = plan.materialPath,
                    asset = existing,
                    snapshot = new Material(existing),
                    originalName = existing.name,
                    created = false
                };
                backups.Add(backup);
                CopyCompleteMaterialState(plan.candidateMaterial, existing, plan.baseName);
                EditorUtility.SetDirty(existing);
                plan.persistentMaterial = existing;
                Object.DestroyImmediate(plan.candidateMaterial);
                plan.candidateMaterial = null;
                return;
            }

            Material created = plan.candidateMaterial;
            backups.Add(new MaterialBackup
            {
                path = plan.materialPath,
                asset = created,
                created = true
            });
            AssetDatabase.CreateAsset(created, plan.materialPath);
            Material imported = AssetDatabase.LoadAssetAtPath<Material>(plan.materialPath);
            if (imported == null)
                throw new System.InvalidOperationException(
                    $"Unity failed to create material asset '{plan.materialPath}'.");
            plan.persistentMaterial = imported;
            plan.candidateMaterial = null; // AssetDatabase owns it now.
        }

        private static void AssignPersistentMaterials(List<ExportPlan> plans)
        {
            var byRenderer = new Dictionary<Renderer, Material[]>();
            for (int i = 0; i < plans.Count; i++)
            {
                ExportPlan plan = plans[i];
                if (!byRenderer.TryGetValue(plan.renderer, out Material[] materials))
                {
                    materials = plan.renderer.sharedMaterials;
                    byRenderer.Add(plan.renderer, materials);
                }
                if (plan.slot < 0 || plan.slot >= materials.Length)
                    throw new System.InvalidOperationException(
                        $"Material slots changed during export on '{plan.renderer.name}'.");
                materials[plan.slot] = plan.persistentMaterial;
            }

            foreach (var pair in byRenderer)
            {
                Undo.RecordObject(pair.Key, "Bake Text To Asset");
                pair.Key.sharedMaterials = pair.Value;
                EditorUtility.SetDirty(pair.Key);
                if (PrefabUtility.IsPartOfPrefabInstance(pair.Key))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(pair.Key);
            }
        }

        private static List<RendererSnapshot> CaptureRendererState(MeshTextSurface surface)
        {
            var renderers = CollectAffectedRenderers(surface);
            var snapshots = new List<RendererSnapshot>(renderers.Count);
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                Material[] materials = renderer.sharedMaterials;
                // A fresh bake destroys previous generated instances. Store their valid source
                // instead so failure recovery never reassigns a destroyed material.
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (materials[slot] == surface.GetMaterialInstance(renderer, slot))
                        materials[slot] = surface.GetBakeSourceMaterial(renderer, slot) ?? materials[slot];
                }
                snapshots.Add(new RendererSnapshot { renderer = renderer, materials = materials });
            }
            return snapshots;
        }

        private static void ReleaseAndRestore(MeshTextSurface surface,
            List<RendererSnapshot> snapshots)
        {
            try { surface.ReleaseLiveBakeKeepingMaterials(); }
            catch (System.Exception ex)
            {
                Debug.LogError("[MeshTextBaker] Failed to release live bake state: " + ex.Message, surface);
            }

            for (int i = 0; i < snapshots.Count; i++)
            {
                RendererSnapshot snapshot = snapshots[i];
                if (snapshot.renderer == null || snapshot.materials == null) continue;
                try
                {
                    snapshot.renderer.sharedMaterials = snapshot.materials;
                    EditorUtility.SetDirty(snapshot.renderer);
                    if (PrefabUtility.IsPartOfPrefabInstance(snapshot.renderer))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(snapshot.renderer);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[MeshTextBaker] Could not restore renderer " +
                        $"'{snapshot.renderer.name}': {ex.Message}");
                }
            }
        }

        private static void RollBackMaterials(List<MaterialBackup> backups)
        {
            for (int i = backups.Count - 1; i >= 0; i--)
            {
                MaterialBackup backup = backups[i];
                try
                {
                    if (backup.created)
                    {
                        AssetDatabase.DeleteAsset(backup.path);
                        // DeleteAsset can return false for a partially-created/unimported file.
                        if (File.Exists(backup.path)) File.Delete(backup.path);
                        if (File.Exists(backup.path + ".meta")) File.Delete(backup.path + ".meta");
                    }
                    else if (backup.asset != null && backup.snapshot != null)
                    {
                        CopyCompleteMaterialState(backup.snapshot, backup.asset, backup.originalName);
                        EditorUtility.SetDirty(backup.asset);
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[MeshTextBaker] Could not roll back material '{backup.path}': {ex.Message}");
                }
            }
        }

        private static void RollBackFiles(List<FileBackup> backups)
        {
            for (int i = backups.Count - 1; i >= 0; i--)
            {
                FileBackup backup = backups[i];
                try
                {
                    if (backup.existed)
                    {
                        File.WriteAllBytes(backup.path, backup.contents);
                        File.WriteAllBytes(backup.path + ".meta", backup.metaContents);
                        AssetDatabase.ImportAsset(backup.path, ImportAssetOptions.ForceUpdate);
                    }
                    else
                    {
                        if (AssetDatabase.LoadMainAssetAtPath(backup.path) != null)
                            AssetDatabase.DeleteAsset(backup.path);
                        if (File.Exists(backup.path)) File.Delete(backup.path);
                        if (File.Exists(backup.path + ".meta")) File.Delete(backup.path + ".meta");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[MeshTextBaker] Could not roll back texture '{backup.path}': {ex.Message}");
                }
            }
        }

        private static Texture2D SaveWithBackup(RenderTexture rt, string path, bool linear,
            List<FileBackup> backups)
        {
            var backup = new FileBackup { path = path, existed = File.Exists(path) };
            if (backup.existed)
            {
                if (!File.Exists(path + ".meta") || AssetImporter.GetAtPath(path) == null)
                    throw new System.InvalidOperationException(
                        $"Export path '{path}' is occupied by an unimported file.");
                backup.contents = File.ReadAllBytes(path);
                backup.metaContents = File.ReadAllBytes(path + ".meta");
            }
            backups.Add(backup);
            return SaveRenderTextureAsPng(rt, path, linear);
        }

        private static Texture2D SaveRenderTextureAsPng(RenderTexture rt, string assetPath,
            bool linear = false)
        {
            if (rt == null) throw new System.ArgumentNullException(nameof(rt));
            assetPath = assetPath.Replace('\\', '/');
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                RenderTexture.active = rt;
                readable = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, linear);
                readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readable.Apply();
                byte[] bytes = readable.EncodeToPNG();
                if (bytes == null || bytes.Length == 0)
                    throw new System.InvalidOperationException("Unity returned an empty PNG.");
                File.WriteAllBytes(assetPath, bytes);
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null) Object.DestroyImmediate(readable);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = !linear;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                if (linear) importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.SaveAndReimport();
            }

            Texture2D imported = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (imported == null)
                throw new System.InvalidOperationException($"Unity failed to import '{assetPath}'.");
            return imported;
        }

        private static void CopyMaskState(Material source, Material destination)
        {
            string[] floats =
            {
                "_Metallic", "_Smoothness", "_GlossMapScale", "_SmoothnessTextureChannel",
                "_SmoothnessRemapMin", "_SmoothnessRemapMax", "_MetallicRemapMin",
                "_MetallicRemapMax", "_AORemapMin", "_AORemapMax"
            };
            for (int i = 0; i < floats.Length; i++)
                if (source.HasProperty(floats[i]) && destination.HasProperty(floats[i]))
                    destination.SetFloat(floats[i], source.GetFloat(floats[i]));
        }

        private static void CopyHeightState(Material source, Material destination)
        {
            string[] floats =
            {
                "_Parallax", "_HeightScale", "_HeightMapParametrization", "_HeightCenter",
                "_HeightAmplitude", "_HeightTessCenter", "_HeightTessAmplitude",
                "_HeightPoMAmplitude", "_DisplacementMode"
            };
            for (int i = 0; i < floats.Length; i++)
                if (source.HasProperty(floats[i]) && destination.HasProperty(floats[i]))
                    destination.SetFloat(floats[i], source.GetFloat(floats[i]));
            if (source.IsKeywordEnabled("_PIXEL_DISPLACEMENT"))
                destination.EnableKeyword("_PIXEL_DISPLACEMENT");
        }

        private static void CopyCompleteMaterialState(Material source, Material destination,
            string destinationName)
        {
            destination.shader = source.shader;
            destination.CopyPropertiesFromMaterial(source);
            destination.name = destinationName;
            destination.renderQueue = source.renderQueue;
            destination.enableInstancing = source.enableInstancing;
            destination.doubleSidedGI = source.doubleSidedGI;
            destination.globalIlluminationFlags = source.globalIlluminationFlags;
        }

        private static bool TryNormalizeOutputFolder(string requested, out string folder,
            out string error)
        {
            folder = null;
            error = null;
            string raw = string.IsNullOrWhiteSpace(requested)
                ? DefaultFolder
                : requested.Trim().Replace('\\', '/').TrimEnd('/');
            if (raw.Length == 0) raw = "Assets";
            if (!raw.Equals("Assets", System.StringComparison.OrdinalIgnoreCase) &&
                !raw.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
            {
                error = "Output folder must be 'Assets' or a child of 'Assets/'.";
                return false;
            }
            if (Path.IsPathRooted(raw))
            {
                error = "Output folder must be a project-relative Assets path.";
                return false;
            }

            try
            {
                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                if (parent == null) throw new System.InvalidOperationException("Project root is unavailable.");
                string assetsFull = NormalizeFullPath(Application.dataPath);
                string candidateFull = NormalizeFullPath(Path.Combine(parent.FullName, raw));
                if (!IsSameOrChildPath(candidateFull, assetsFull))
                {
                    error = "Output folder resolves outside this project's Assets folder.";
                    return false;
                }
                folder = "Assets" + candidateFull.Substring(assetsFull.Length).Replace('\\', '/');
                return true;
            }
            catch (System.Exception ex)
            {
                error = "Invalid output folder: " + ex.Message;
                return false;
            }
        }

        private static void EnsureOutputFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid))
                        throw new IOException($"Could not create output folder '{next}'.");
                }
                current = next;
            }
            if (!AssetDatabase.IsValidFolder(folder))
                throw new IOException($"Output folder '{folder}' is not a valid asset folder.");
        }

        private static bool TryFindUntrackedLegacyExport(MeshTextSurface surface, string textureProperty,
            out string error)
        {
            error = null;
            var slotsByRenderer = new Dictionary<Renderer, HashSet<int>>();
            foreach (TextZone zone in surface.zones)
            {
                if (zone == null) continue;
                Renderer renderer = surface.GetZoneRenderer(zone);
                if (renderer == null) continue;
                if (!slotsByRenderer.TryGetValue(renderer, out HashSet<int> slots))
                {
                    slots = new HashSet<int>();
                    slotsByRenderer.Add(renderer, slots);
                }
                slots.Add(zone.materialIndex);
            }

            foreach (var pair in slotsByRenderer)
            {
                Renderer renderer = pair.Key;
                Material[] materials = renderer.sharedMaterials;
                foreach (int slot in pair.Value)
                {
                    if (slot < 0 || slot >= materials.Length || materials[slot] == null) continue;
                    if (surface.GetBakeSourceMaterial(renderer, slot) != null) continue;

                    Material material = materials[slot];
                    string materialPath = AssetDatabase.GetAssetPath(material);
                    if (string.IsNullOrEmpty(materialPath) ||
                        !materialPath.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    string baseName = Path.GetFileNameWithoutExtension(materialPath);
                    string legacyPrefix = Sanitize(
                        $"{surface.name}_{renderer.name}_slot{slot}_");
                    if (!baseName.StartsWith(legacyPrefix, System.StringComparison.OrdinalIgnoreCase))
                        continue;
                    string adjacentPng = AssetPath(
                        Path.GetDirectoryName(materialPath) ?? "Assets", baseName + ".png");
                    Texture2D adjacentTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(adjacentPng);
                    if (adjacentTexture == null ||
                        !MaterialUsesTexture(material, adjacentTexture, textureProperty)) continue;

                    error = $"'{renderer.name}' slot {slot} uses a baked export whose source " +
                        "material is not recorded (for example, an export made by an older " +
                        "package version or manually reassigned later). Reassign the true source " +
                        "material to that slot, then export again.";
                    return true;
                }
            }
            return false;
        }

        private static bool MaterialUsesTexture(Material material, Texture texture,
            string preferredProperty)
        {
            if (!string.IsNullOrEmpty(preferredProperty) && material.HasProperty(preferredProperty) &&
                material.GetTexture(preferredProperty) == texture) return true;
            string[] commonProperties = { "_BaseColorMap", "_BaseMap", "_MainTex" };
            for (int i = 0; i < commonProperties.Length; i++)
                if (material.HasProperty(commonProperties[i]) &&
                    material.GetTexture(commonProperties[i]) == texture) return true;
            return false;
        }

        private static List<Renderer> CollectAffectedRenderers(MeshTextSurface surface)
        {
            var result = new List<Renderer>();
            if (surface.targetRenderer != null) result.Add(surface.targetRenderer);
            foreach (TextZone zone in surface.zones)
            {
                if (zone == null) continue;
                Renderer renderer = surface.GetZoneRenderer(zone);
                if (renderer != null && !result.Contains(renderer)) result.Add(renderer);
            }
            return result;
        }

        private static string StableTargetId(MeshTextSurface surface, Renderer renderer, int slot)
        {
            string identity = StableObjectIdentity(surface) + "|" +
                StableObjectIdentity(renderer) + "|" + slot;
            return Fnv1a64(identity).ToString("x16") + Fnv1a64("MeshTextBaker|" + identity).ToString("x16");
        }

        private static string StableObjectIdentity(Component component)
        {
            const string nullGlobalId =
                "GlobalObjectId_V1-0-00000000000000000000000000000000-0-0";
            string identity = GlobalObjectId.GetGlobalObjectIdSlow(component).ToString();
            bool unsavedScene = component.gameObject.scene.IsValid() &&
                string.IsNullOrEmpty(component.gameObject.scene.path);

            if (identity == nullGlobalId)
            {
                // Fail-safe for objects for which Unity cannot produce a GlobalObjectId. A
                // saved scene/asset gets a deterministic hierarchy fallback; an unsaved scene
                // additionally needs a session identity because it has no persistent owner yet.
                string owner = component.gameObject.scene.IsValid()
                    ? component.gameObject.scene.path
                    : AssetDatabase.GetAssetPath(component);
                identity += "|fallback:" + owner + "|" + ComponentHierarchyIdentity(component);
            }
            if (unsavedScene)
                identity += "|session:" + component.gameObject.scene.handle + ":" +
                    component.GetHashCode();
            return identity;
        }

        private static string ComponentHierarchyIdentity(Component component)
        {
            var parts = new List<string>();
            Transform current = component.transform;
            while (current != null)
            {
                parts.Add(current.GetSiblingIndex() + ":" + current.name);
                current = current.parent;
            }
            parts.Reverse();

            int componentIndex = 0;
            Component[] components = component.gameObject.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] == component) { componentIndex = i; break; }
            }
            return string.Join("/", parts) + "|" + component.GetType().FullName +
                "|component:" + componentIndex;
        }

        private static ulong Fnv1a64(string value)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }

        private static void AddUniquePath(HashSet<string> paths, string path)
        {
            if (!paths.Add(path))
                throw new System.InvalidOperationException(
                    $"Two bake targets resolved to the same export path '{path}'.");
        }

        private static string AssetPath(string folder, string fileName)
            => Path.Combine(folder, fileName).Replace('\\', '/');

        private static string NormalizeFullPath(string path)
            => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool IsSameOrChildPath(string candidate, string parent)
            => candidate.Equals(parent, System.StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(parent + Path.DirectorySeparatorChar,
                   System.StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(parent + Path.AltDirectorySeparatorChar,
                   System.StringComparison.OrdinalIgnoreCase);

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }
    }
}
