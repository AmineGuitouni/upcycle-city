using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Shops;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Creates (or updates) the shop data for the Bourguiba avenue: the food and drink consumables, one
    /// <see cref="ShopCatalog"/> per shop in <c>Settings/Shops</c>, and the shopkeepers' idle animator.
    /// Prices and greetings live in the catalog assets, so they can be tuned in the Inspector afterwards;
    /// running this again resets them to these defaults.
    /// </summary>
    public static class ShopContentBuilder
    {
        public const string ShopsDir = "Assets/UpcycleCity/Settings/Shops";
        const string PlayerItemsDir = "Assets/UpcycleCity/Settings/Player";
        const string LitterArt = "Assets/UpcycleCity/Art/UpcycleProps/Bourguiba/Litter";
        const string IdleClipPath = "Assets/UpcycleCity/Art/Animations/Mixamo/Breathing Idle.fbx";
        const string KeeperControllerPath = "Assets/UpcycleCity/Animation/ShopkeeperAnimator.controller";

        public sealed class Content
        {
            public ShopCatalog cafe, tools, grocery, recycling;
            public RuntimeAnimatorController keeperController;
        }

        [MenuItem("Upcycle City/Shops/Rebuild Shop Catalogs", priority = 40)]
        static void RebuildMenu()
        {
            EnsureAll();
            AssetDatabase.SaveAssets();
            Debug.Log("[Upcycle City] Shop catalogs and consumables rebuilt in " + ShopsDir);
        }

        public static Content EnsureAll()
        {
            EnsureFolder(ShopsDir);

            ItemDefinition espresso = Consumable("Item_Espresso", "food_espresso", "Café Express",
                "A short, strong Tunisian coffee from the corner café. Clean faster.",
                new Color(0.42f, 0.24f, 0.12f), "espresso", 45f,
                new PlayerBoosts.Effect { moveSpeed = 1.2f, jump = 1f }, "Café express! You feel quick", "cup_espresso");
            ItemDefinition tea = Consumable("Item_MintTea", "food_mint_tea", "Thé à la Menthe",
                "Sweet mint tea with pine nuts. Steady hands: reach further, throw harder.",
                new Color(0.20f, 0.55f, 0.30f), "mint_tea", 60f,
                new PlayerBoosts.Effect { moveSpeed = 1f, jump = 1f, grabReach = 1.0f, throwForce = 2.5f }, "Thé à la menthe! Steady hands", "cup_espresso_tan");
            ItemDefinition water = Consumable("Item_Water", "food_water", "Bouteille d'Eau",
                "Cold mineral water. Breathe easy in the smog for a while.",
                new Color(0.22f, 0.55f, 0.85f), "water", 60f,
                new PlayerBoosts.Effect { moveSpeed = 1f, jump = 1f, smogImmune = true }, "Fresh water! Smog can't slow you", "bottle_water");
            ItemDefinition kaskrout = Consumable("Item_Kaskrout", "food_kaskrout", "Kaskrout",
                "A baguette of tuna, harissa and olives. Energy to run and jump.",
                new Color(0.86f, 0.52f, 0.18f), "kaskrout", 90f,
                new PlayerBoosts.Effect { moveSpeed = 1.1f, jump = 1.3f }, "Kaskrout! Full of energy", "kaskrout_paper");

            ItemDefinition broom = Load("Item_Broom");
            ItemDefinition wrench = Load("Item_Wrench");
            ItemDefinition mask = Load("Item_RespiratorMask");
            ItemDefinition picker = Load("Item_LitterPicker");

            var content = new Content
            {
                cafe = Catalog("Shop_Cafe", "Café Bourguiba", "Am Hamadi",
                    "Ahla bik! A coffee to clean faster, or a mint tea for steady hands?",
                    new Color(0.70f, 0.12f, 0.09f), false, Offer(espresso, 6), Offer(tea, 8)),
                tools = Catalog("Shop_Tools", "Quincaillerie", "Si Mounir",
                    "Marhba! Good tools make a clean city. What are you fixing today?",
                    new Color(0.95f, 0.76f, 0.11f), false, Offer(broom, 30), Offer(wrench, 45), Offer(mask, 60), Offer(picker, 20)),
                grocery = Catalog("Shop_Grocery", "Épicerie", "Khalti Fatma",
                    "Ahla ya wildi! Water, kaskrout, everything fresh this morning.",
                    new Color(0.10f, 0.45f, 0.25f), false, Offer(water, 7), Offer(kaskrout, 10), Offer(espresso, 7)),
                recycling = Catalog("Shop_Recup", "RÉCUP · Buy-Back", "Am Salah",
                    "Bring me your trash, sorted or not. I pay better than the street bins: put it on the scale!",
                    new Color(0.18f, 0.55f, 0.34f), true),
                keeperController = KeeperController(),
            };

            AssetDatabase.SaveAssets();
            return content;
        }

        static ShopCatalog.Offer Offer(ItemDefinition item, int price, int quantity = 1) =>
            item == null ? null : new ShopCatalog.Offer { item = item, price = price, quantity = quantity };

        static ShopCatalog Catalog(string file, string shop, string keeper, string hello, Color accent, bool sellPrices,
            params ShopCatalog.Offer[] offers)
        {
            string path = $"{ShopsDir}/{file}.asset";
            ShopCatalog catalog = AssetDatabase.LoadAssetAtPath<ShopCatalog>(path);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<ShopCatalog>();
                AssetDatabase.CreateAsset(catalog, path);
            }
            var list = new List<ShopCatalog.Offer>();
            foreach (ShopCatalog.Offer offer in offers)
            {
                if (offer != null)
                    list.Add(offer);
            }
            catalog.Configure(shop, keeper, hello, accent, sellPrices, list);
            EditorUtility.SetDirty(catalog);
            return catalog;
        }

        static ItemDefinition Consumable(string file, string id, string label, string desc, Color colour, string boostId,
            float seconds, PlayerBoosts.Effect effect, string message, string heldModel)
        {
            string path = $"{ShopsDir}/{file}.asset";
            ItemDefinition item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (item == null)
            {
                item = ScriptableObject.CreateInstance<ItemDefinition>();
                AssetDatabase.CreateAsset(item, path);
            }
            item.Configure(id, label, desc, ItemCategory.Usable, colour, stackLimit: 5, consume: true);
            item.SetUseBehavior(new TimedBoostItemBehavior(boostId, seconds, effect, message));
            using (var so = new SerializedObject(item))
            {
                so.FindProperty("useCooldown").floatValue = 0.6f;
                so.FindProperty("heldVisualPrefab").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameObject>($"{LitterArt}/{heldModel}.fbx");
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            EditorUtility.SetDirty(item);
            return item;
        }

        static ItemDefinition Load(string file)
        {
            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>($"{PlayerItemsDir}/{file}.asset");
            if (item == null)
                Debug.LogWarning($"[Upcycle City] {file} not found in {PlayerItemsDir}; build the Player Gym first. The shop will skip it.");
            return item;
        }

        /// <summary>One looping idle state, with IK on so shopkeepers can look at the player.</summary>
        static RuntimeAnimatorController KeeperController()
        {
            AnimationClip idle = null;
            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(IdleClipPath))
            {
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                {
                    idle = clip;
                    break;
                }
            }

            EnsureFolder("Assets/UpcycleCity/Animation");
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(KeeperControllerPath)
                             ?? AnimatorController.CreateAnimatorControllerAtPath(KeeperControllerPath);
            AnimatorControllerLayer[] layers = controller.layers;
            AnimatorStateMachine machine = layers[0].stateMachine;
            foreach (ChildAnimatorState child in machine.states)
                machine.RemoveState(child.state);
            AnimatorState state = machine.AddState("Idle");
            state.motion = idle;
            machine.defaultState = state;
            layers[0].iKPass = true;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            if (idle == null)
                Debug.LogWarning($"[Upcycle City] No idle clip in {IdleClipPath}; shopkeepers will stand in T-pose.");
            return controller;
        }

        static void EnsureFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return;
            string[] parts = assetPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
