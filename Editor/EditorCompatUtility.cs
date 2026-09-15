// Mesh Text Baker — EditorCompatUtility.cs
// Cross-version editor API shims. Unity 6.x replaces the instance-id APIs with entity-id
// ones and marks the old ones as compile ERRORS (not just warnings), while 2022.3 LTS has
// only the old ones. Both are resolved via reflection so the same source compiles everywhere.

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace MeshTextBaker.Editor
{
    public static class EditorCompatUtility
    {
        private static bool s_Resolved;
        private static MethodInfo s_Method;      // EntityIdToObject or InstanceIDToObject
        private static MethodInfo s_ArgConvert;  // optional int → EntityId implicit operator

        /// <summary>
        /// EditorUtility.EntityIdToObject / InstanceIDToObject — whichever this Unity has.
        /// The int ids passed by editor callbacks (OnOpenAsset etc.) are valid for both.
        /// </summary>
        public static UnityEngine.Object ObjectFromId(int id)
        {
            if (!s_Resolved)
            {
                s_Resolved = true;
                var t = typeof(EditorUtility);

                // Prefer the modern API.
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "EntityIdToObject") continue;
                    var pars = m.GetParameters();
                    if (pars.Length != 1) continue;

                    var pt = pars[0].ParameterType;
                    if (pt == typeof(int)) { s_Method = m; break; }

                    // EntityId struct: use its implicit int conversion when present.
                    var conv = pt.GetMethod("op_Implicit",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
                    if (conv != null && conv.ReturnType == pt)
                    {
                        s_Method = m;
                        s_ArgConvert = conv;
                        break;
                    }
                }

                // Legacy fallback (2022.3 LTS). Reflection sidesteps the obsolete-error.
                if (s_Method == null)
                    s_Method = t.GetMethod("InstanceIDToObject",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int) }, null);
            }

            if (s_Method == null) return null;
            try
            {
                object arg = s_ArgConvert != null ? s_ArgConvert.Invoke(null, new object[] { id }) : id;
                return s_Method.Invoke(null, new[] { arg }) as UnityEngine.Object;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MeshTextBaker] Object-from-id resolution failed: {e.Message}");
                return null;
            }
        }
    }
}
