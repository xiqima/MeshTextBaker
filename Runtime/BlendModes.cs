// Mesh Text Baker — BlendModes.cs
// Photoshop-style albedo compositing for zones and inline <blend=...> scopes.

using System;

namespace MeshTextBaker
{
    public enum MeshTextBlendMode
    {
        Normal = 0,
        Multiply = 1,
        Screen = 2,
        Overlay = 3,
        Add = 4,
        Subtract = 5
    }

    public static class MeshTextBlendModeUtility
    {
        public static bool TryParse(string value, out MeshTextBlendMode mode)
        {
            mode = MeshTextBlendMode.Normal;
            if (string.IsNullOrEmpty(value)) return false;
            switch (value.Trim().Trim('"').ToLowerInvariant())
            {
                case "normal": mode = MeshTextBlendMode.Normal; return true;
                case "multiply":
                case "mul": mode = MeshTextBlendMode.Multiply; return true;
                case "screen": mode = MeshTextBlendMode.Screen; return true;
                case "overlay": mode = MeshTextBlendMode.Overlay; return true;
                case "add":
                case "linear-dodge": mode = MeshTextBlendMode.Add; return true;
                case "subtract":
                case "sub": mode = MeshTextBlendMode.Subtract; return true;
                default: return false;
            }
        }

        public static string ToTagName(MeshTextBlendMode mode)
            => mode.ToString().ToLowerInvariant();
    }
}
