using System;
using System.IO;

namespace UpcycleCity.Editor
{
    /// <summary>How the models of one art folder are imported and how their materials react to pollution.</summary>
    /// <summary>The rig a model is imported with.</summary>
    enum RigType
    {
        None,
        Generic,
        Humanoid, // retargetable: Mixamo and other humanoid animations play on it
    }

    readonly struct ModelImportRule
    {
        public readonly string Folder;
        public readonly float Scale;
        public readonly RigType Rig;
        public readonly float Sensitivity;
        public readonly float Grime;
        public readonly float Foliage;

        public ModelImportRule(string folder, float scale, RigType rig, float sensitivity, float grime, float foliage)
        {
            Folder = folder;
            Scale = scale;
            Rig = rig;
            Sensitivity = sensitivity;
            Grime = grime;
            Foliage = foliage;
        }
    }

    /// <summary>
    /// One line per art pack under <see cref="ArtRoot"/>. To add a pack, drop it in its own folder
    /// and add a rule here; every model in it gets Pollution Lit materials on import.
    /// </summary>
    static class ModelImportRules
    {
        public const string ArtRoot = "Assets/UpcycleCity/Art/";

        // The city is built in metres. KayKit tiles are 2 units wide and are scaled so a road lane is
        // 4.4 m, the scale the Upcycle City concept art was made at.
        static readonly ModelImportRule[] Rules =
        {
            //                  folder under Art/              scale  rig               sensitivity  grime  foliage
            new ModelImportRule("KayKitCityBits/",             4.4f,  RigType.None,     1f,          1f,    0f),
            new ModelImportRule("QuaterniusNature/",           1f,    RigType.None,     1f,          0.4f,  1f),
            new ModelImportRule("QuaterniusFarmAnimals/",      1f,    RigType.Generic,  0.6f,        0.3f,  0f),
            new ModelImportRule("Rocks/",                      1f,    RigType.None,     0.8f,        0.7f,  1f),
            new ModelImportRule("RealisticCarPack/",           1f,    RigType.None,     1f,          0.8f,  0f),
            new ModelImportRule("Animations/",                 1f,    RigType.Humanoid, 1f,          0f,    0f),
            new ModelImportRule("VocationsCharacters/Rigged/", 1f,    RigType.Humanoid, 0.8f,        0.3f,  0f),
            new ModelImportRule("VocationsCharacters/",        1f,    RigType.None,     0.8f,        0.3f,  0f),
            new ModelImportRule("UpcycleProps/Litter/",        1f,    RigType.None,     0.5f,        0.4f,  0f),
            new ModelImportRule("UpcycleProps/",               1f,    RigType.None,     1f,          0.8f,  0f),
        };

        static readonly ModelImportRule Default = new ModelImportRule("", 1f, RigType.None, 1f, 1f, 0f);

        // Materials or models with these words in their name are living plants
        static readonly string[] FoliageHints = { "leaf", "leaves", "grass", "bush", "hedge", "foliage", "plant", "lawn", "moss", "weed", "ivy", "flower" };
        static readonly string[] GlossyHints = { "glass", "window", "screen", "water", "puddle", "solar" };
        static readonly string[] MetalHints = { "metal", "chrome", "steel" };

        public static bool Applies(string assetPath) => assetPath.StartsWith(ArtRoot, StringComparison.Ordinal);

        /// <summary>Animation-only files (e.g. Mixamo "Without Skin" downloads) whose clips loop in place.</summary>
        public static bool IsAnimationLibrary(string assetPath) => assetPath.StartsWith(ArtRoot + "Animations/", StringComparison.Ordinal);

        public static ModelImportRule For(string assetPath)
        {
            string relative = assetPath.Substring(ArtRoot.Length);
            foreach (ModelImportRule rule in Rules)
            {
                if (relative.StartsWith(rule.Folder, StringComparison.Ordinal))
                    return rule;
            }
            return Default;
        }

        public static float FoliageFor(ModelImportRule rule, string assetPath, string materialName)
        {
            return Contains(materialName, FoliageHints) || Contains(Path.GetFileNameWithoutExtension(assetPath), FoliageHints)
                ? 1f
                : rule.Foliage;
        }

        public static bool IsGlossy(string materialName) => Contains(materialName, GlossyHints);

        public static bool IsMetal(string materialName) => Contains(materialName, MetalHints);

        public static bool IsFacade(string materialName) => materialName.StartsWith("facade", StringComparison.OrdinalIgnoreCase);

        static bool Contains(string name, string[] hints)
        {
            foreach (string hint in hints)
            {
                if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
    }
}
