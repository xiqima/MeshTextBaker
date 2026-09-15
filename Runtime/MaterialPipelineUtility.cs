// Mesh Text Baker — MaterialPipelineUtility.cs
// Runs the render pipeline's OWN material validation after we change textures/keywords in
// code. This is exactly what the material inspector does after any tweak — which is why the
// "material is broken until you wiggle a slider" bug existed: our keyword changes alone did
// not fully re-sync HDRP's derived keywords and render passes.
//
// HDRP: UnityEditor.Rendering.HighDefinition.HDMaterial.ValidateMaterial(mat)
// URP:  no equivalent needed — keywords map 1:1 to features; Standard likewise.
//
// Resolved via reflection so this assembly has NO hard reference to the HDRP package and
// still compiles on URP/Built-in-only projects (and on 2022.3 ↔ Unity 6.x alike).

using System;
using System.Reflection;
using UnityEngine;

namespace MeshTextBaker
{
    public static class MaterialPipelineUtility
    {
        private static bool s_Resolved;
        private static MethodInfo s_HdrpValidate; // HDMaterial.ValidateMaterial(Material)

        /// <summary>
        /// Re-runs the pipeline's material fix-up for the given material (keywords, passes,
        /// stencil state). Safe no-op when the pipeline offers none (URP/Built-in) or when
        /// called in a player build (HDMaterial validation is editor-only).
        /// </summary>
        public static void ValidateMaterial(Material mat)
        {
            if (mat == null) return;

#if UNITY_EDITOR
            if (!s_Resolved)
            {
                s_Resolved = true;
                // HDMaterial lives in the HDRP *editor* assembly on older versions and in
                // Runtime on newer ones — probe both.
                string[] candidates =
                {
                    "UnityEditor.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Editor",
                    "UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime"
                };
                foreach (var typeName in candidates)
                {
                    var t = Type.GetType(typeName);
                    var m = t?.GetMethod("ValidateMaterial",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(Material) }, null);
                    if (m != null) { s_HdrpValidate = m; break; }
                }
            }

            if (s_HdrpValidate != null)
            {
                try { s_HdrpValidate.Invoke(null, new object[] { mat }); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MeshTextBaker] HDRP material validation failed: {e.Message}");
                    s_HdrpValidate = null; // don't retry every bake
                }
            }
#endif
        }
    }
}
