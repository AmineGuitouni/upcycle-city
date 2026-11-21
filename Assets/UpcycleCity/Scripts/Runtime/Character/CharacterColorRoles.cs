using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Character
{
    /// <summary>
    /// Recolours generated character meshes by material role, using property blocks so no materials are
    /// created. Roles come from the material name prefix the Blender export gives each paint:
    /// <c>skin_</c>, <c>hair_</c>, <c>stubble_</c> (a blend of skin and hair), <c>iris_</c> and <c>top_</c> (the coat).
    /// Shared by the full-body <see cref="CharacterAvatar"/> and the first-person arms.
    /// </summary>
    public static class CharacterColorRoles
    {
        public enum Role
        {
            Skin,
            Hair,
            Stubble,
            Eyes,
            Outfit,
        }

        public struct Slot
        {
            public Renderer Renderer;
            public int Index;
            public Role Role;
        }

        public const float DefaultStubbleStrength = 0.55f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly List<Renderer> RendererScratch = new List<Renderer>(8);

        /// <summary>Appends a slot for every recolourable material under root.</summary>
        public static void Collect(GameObject root, List<Slot> slots)
        {
            if (root == null)
                return;
            root.GetComponentsInChildren(true, RendererScratch);
            foreach (Renderer r in RendererScratch)
            {
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && TryRole(mats[i].name, out Role role))
                        slots.Add(new Slot { Renderer = r, Index = i, Role = role });
                }
            }
            RendererScratch.Clear();
        }

        public static bool TryRole(string materialName, out Role role)
        {
            if (materialName.StartsWith("skin_")) { role = Role.Skin; return true; }
            if (materialName.StartsWith("hair_")) { role = Role.Hair; return true; }
            if (materialName.StartsWith("stubble_")) { role = Role.Stubble; return true; }
            if (materialName.StartsWith("iris_")) { role = Role.Eyes; return true; }
            if (materialName.StartsWith("top_")) { role = Role.Outfit; return true; }
            role = default;
            return false;
        }

        public static Color ColorFor(Role role, CharacterPartsLibrary library, in CharacterAppearance a,
            float stubbleStrength = DefaultStubbleStrength)
        {
            return role switch
            {
                Role.Skin => library.SkinColor(a.skinTone),
                Role.Hair => library.HairColor(a.hairColor),
                Role.Stubble => Color.Lerp(library.SkinColor(a.skinTone), library.HairColor(a.hairColor), stubbleStrength),
                Role.Eyes => library.EyeColor(a.eyeColor),
                _ => library.OutfitColor(a.outfitColor),
            };
        }

        public static void Apply(List<Slot> slots, CharacterPartsLibrary library, in CharacterAppearance appearance,
            MaterialPropertyBlock block, float stubbleStrength = DefaultStubbleStrength)
        {
            if (library == null)
                return;
            foreach (Slot slot in slots)
            {
                if (slot.Renderer == null)
                    continue;
                slot.Renderer.GetPropertyBlock(block, slot.Index);
                block.SetColor(BaseColorId, ColorFor(slot.Role, library, appearance, stubbleStrength));
                slot.Renderer.SetPropertyBlock(block, slot.Index);
            }
        }
    }
}
