using UnityEngine;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Keeps a world-space <see cref="TextMesh"/> that uses a custom depth-tested material (UpcycleCity/World Text)
    /// pointed at its font's dynamic atlas, so labels never render on top of walls like the built-in font material.
    /// </summary>
    public static class WorldTextLabel
    {
        /// <summary>Binds the font atlas to the label's material (no-op when already bound or using the font material).</summary>
        public static void BindFontAtlas(TextMesh label)
        {
            if (label == null || label.font == null || label.font.material == null)
                return;
            Texture atlas = label.font.material.mainTexture;
            if (!label.TryGetComponent(out MeshRenderer meshRenderer) || meshRenderer.sharedMaterial == null)
                return;
            Material mat = meshRenderer.sharedMaterial;
            if (mat == label.font.material || mat.mainTexture == atlas)
                return;
            mat.mainTexture = atlas;
        }
    }
}
