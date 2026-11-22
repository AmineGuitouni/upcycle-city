using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UpcycleCity.Core;
using UpcycleCity.Crafting;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Turns the Factory Yard <c>Upcycle_Workshop_Bay</c> into a buyable lot: a FOR SALE sign at the entrance, light
    /// cordon tape while it is for sale, an empty stand (plinth + name/icon/price sign) on every machine spot, and a
    /// <see cref="WorkshopMachineLock"/> on each scene-placed machine. Idempotent: the generated <c>Workshop_Purchase</c>
    /// group is rebuilt on each run, the tuning in <c>Settings/Crafting/WorkshopConfig.asset</c> is only created when missing.
    /// </summary>
    public static class WorkshopPurchaseSetup
    {
        public const string ConfigPath = "Assets/UpcycleCity/Settings/Crafting/WorkshopConfig.asset";
        const string BayName = "Upcycle_Workshop_Bay";
        const string GroupName = "Workshop_Purchase";
        const string MaterialDir = "Assets/UpcycleCity/Materials/Workshop";

        /// <summary>Stand slot → scene machine instance name, default price, short name, icon key.</summary>
        static readonly (string slot, string instance, int price, string shortName, string icon)[] Slots =
        {
            ("pedal_generator", "Factory_PedalGenerator", 40, "Pedal Generator", "ui_energy"),
            ("pulper", "Factory_ManualPulper", 60, "Pulper", "stream_paper"),
            ("plastic_melter", "Factory_PlasticMelter", 80, "Plastic Melter", "stream_plastic"),
            ("crucible_furnace", "Factory_CrucibleFurnace", 100, "Crucible Furnace", "stream_cans"),
            ("tyre_stripper", "Factory_TyreStripper", 90, "Tyre Stripper", "mat_old_wheel"),
            ("workbench", "Factory_UpcycleWorkbench", 120, "Upcycle Workbench", "mat_built_bicycle"),
        };

        // Entrance sign: west side of the bay, on the cross-street approach, facing the avenue (local +Z = world -X)
        static readonly Vector3 SignPosition = new Vector3(36.1f, 0f, -18.2f);
        const float SignYaw = -90f;

        // Light cordon around the open sides of the bay (visual only, no colliders: the Factory Yard stays walkable)
        const float CordonWestX = 36.8f, CordonEastX = 45.2f, CordonNorthZ = -7.9f, CordonSouthZ = -25.3f;

        [MenuItem("Upcycle City/Crafting/Setup Workshop Purchase In Open Scene", priority = 62)]
        public static void SetupInOpenSceneMenu()
        {
            Scene scene = SceneManager.GetActiveScene();
            Transform bay = FindBay(scene);
            if (bay == null)
            {
                EditorUtility.DisplayDialog("Workshop Purchase",
                    $"No '{BayName}' found in the open scene '{scene.name}'. Open EcosystemSandbox first.", "OK");
                return;
            }

            bool wasDirty = scene.isDirty;
            WorkshopProperty property = BuildInBay(bay);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!wasDirty && !Application.isPlaying)
            {
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[Upcycle City] Workshop purchase set up in '{scene.name}' and saved ({property.Stands.Count} stands).", property);
            }
            else
            {
                Debug.LogWarning($"[Upcycle City] Workshop purchase set up in '{scene.name}' ({property.Stands.Count} stands). " +
                                 "The scene already had unsaved changes, so it was NOT saved automatically — save it yourself.", property);
            }
        }

        static Transform FindBay(Scene scene)
        {
            if (!scene.IsValid())
                return null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform found = FindDeep(root.transform, BayName);
                if (found != null)
                    return found;
            }
            return null;
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name)
                return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform found = FindDeep(t.GetChild(i), name);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>Creates the WorkshopConfig asset when missing (keeps existing tuning).</summary>
        public static WorkshopConfig EnsureConfig(Transform bay = null)
        {
            WorkshopConfig config = AssetDatabase.LoadAssetAtPath<WorkshopConfig>(ConfigPath);
            bool created = false;
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<WorkshopConfig>();
                config.ConfigureProperty("upcycle_garage", "Upcycle Garage", 120, 6f);
                AssetDatabase.CreateAsset(config, ConfigPath);
                created = true;
            }

            foreach (var slot in Slots)
            {
                MachineDefinition def = null;
                Transform machine = bay != null ? bay.Find(slot.instance) : null;
                if (machine != null)
                {
                    if (machine.TryGetComponent(out CraftingMachine cm))
                        def = cm.Definition;
                    else if (machine.TryGetComponent(out ManualEnergyGenerator gen))
                        def = gen.Definition;
                }
                // Only fills missing stands / references: designer prices survive re-runs
                config.SetMachine(slot.slot, def, slot.price, slot.shortName, slot.icon, overwritePrice: created || config.IndexOfSlot(slot.slot) < 0);
            }

            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
            return config;
        }

        /// <summary>Builds (or rebuilds) the sale sign, cordon and stands under <paramref name="bay"/>.</summary>
        public static WorkshopProperty BuildInBay(Transform bay)
        {
            WorkshopConfig config = EnsureConfig(bay);

            Transform old = bay.Find(GroupName);
            if (old != null)
                Object.DestroyImmediate(old.gameObject);

            var group = new GameObject(GroupName);
            group.transform.SetParent(bay, false);
            group.transform.position = SignPosition;
            WorkshopProperty property = group.AddComponent<WorkshopProperty>();

            Materials mats = EnsureMaterials();
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            WorkshopSaleSign sign = BuildSaleSign(group.transform, property, mats, font);
            GameObject cordon = BuildCordon(group.transform, mats);

            var stands = new List<WorkshopMachineStand>();
            for (int i = 0; i < config.MachineCount; i++)
            {
                WorkshopConfig.MachineOffer offer = config.GetMachine(i);
                string instance = null;
                foreach (var slot in Slots)
                {
                    if (slot.slot == offer.slotId)
                        instance = slot.instance;
                }
                Transform machine = instance != null ? bay.Find(instance) : null;
                if (machine == null)
                {
                    Debug.LogWarning($"[Upcycle City] Workshop stand '{offer.slotId}' has no machine '{instance}' in {BayName}; skipped.");
                    continue;
                }

                WorkshopMachineLock machineLock = machine.GetComponent<WorkshopMachineLock>();
                if (machineLock == null)
                {
                    machineLock = machine.gameObject.AddComponent<WorkshopMachineLock>();
                    if (PrefabUtility.IsPartOfPrefabInstance(machine.gameObject))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(machineLock);
                }

                stands.Add(BuildStand(group.transform, property, i, offer, machine, machineLock, mats, font));
            }

            property.Configure(config, sign, stands, lockedOnly: new[] { cordon }, ownedOnly: null);
            EditorUtility.SetDirty(property);
            return property;
        }

        // ------------------------------------------------------------ pieces

        static WorkshopSaleSign BuildSaleSign(Transform parent, WorkshopProperty property, Materials mats, Font font)
        {
            var root = new GameObject("Workshop_ForSaleSign");
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(SignPosition, Quaternion.Euler(0f, SignYaw, 0f));
            root.layer = GameLayers.Interactable;

            Box("Post_L", root.transform, new Vector3(-1.35f, 1.15f, 0f), new Vector3(0.12f, 2.3f, 0.12f), mats.post);
            Box("Post_R", root.transform, new Vector3(1.35f, 1.15f, 0f), new Vector3(0.12f, 2.3f, 0.12f), mats.post);
            Box("Board", root.transform, new Vector3(0f, 1.75f, 0f), new Vector3(2.9f, 1.25f, 0.08f), mats.saleRed);
            Box("Board_Trim", root.transform, new Vector3(0f, 1.75f, -0.01f), new Vector3(3.0f, 1.35f, 0.06f), mats.hazard);

            // TextMesh is readable when looking along its +Z; the viewer stands on the board's +Z side
            TextMesh title = Text("Title", root.transform, new Vector3(0f, 2.12f, 0.06f), 96, 0.042f, font, Color.yellow);
            TextMesh detail = Text("Detail", root.transform, new Vector3(0f, 1.55f, 0.06f), 64, 0.032f, font, Color.white);

            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 1.3f, 0f);
            collider.size = new Vector3(3.0f, 2.6f, 0.35f);

            WorkshopSaleSign sign = root.AddComponent<WorkshopSaleSign>();
            sign.Configure(property, title, detail, collider);
            return sign;
        }

        static GameObject BuildCordon(Transform parent, Materials mats)
        {
            var root = new GameObject("Workshop_Cordon");
            root.transform.SetParent(parent, false);
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;

            CordonLine(root.transform, new Vector3(CordonWestX, 0f, CordonNorthZ), new Vector3(CordonWestX, 0f, CordonSouthZ), mats);
            CordonLine(root.transform, new Vector3(CordonWestX, 0f, CordonNorthZ), new Vector3(CordonEastX, 0f, CordonNorthZ), mats);
            CordonLine(root.transform, new Vector3(CordonWestX, 0f, CordonSouthZ), new Vector3(CordonEastX, 0f, CordonSouthZ), mats);
            return root;
        }

        static void CordonLine(Transform parent, Vector3 from, Vector3 to, Materials mats)
        {
            float length = Vector3.Distance(from, to);
            int segments = Mathf.Max(1, Mathf.CeilToInt(length / 2.8f));
            Quaternion rot = Quaternion.LookRotation((to - from).normalized, Vector3.up);
            for (int i = 0; i <= segments; i++)
            {
                Vector3 p = Vector3.Lerp(from, to, i / (float)segments);
                GameObject post = Cylinder("Cordon_Post", parent, p + Vector3.up * 0.5f, new Vector3(0.08f, 0.5f, 0.08f), mats.post);
                post.transform.rotation = Quaternion.identity;
                Cylinder("Cordon_Cap", parent, p + Vector3.up * 1.0f, new Vector3(0.12f, 0.03f, 0.12f), mats.hazard);
                if (i == segments)
                    continue;
                Vector3 next = Vector3.Lerp(from, to, (i + 1) / (float)segments);
                float seg = Vector3.Distance(p, next);
                GameObject tape = Box("Cordon_Tape", parent, Vector3.Lerp(p, next, 0.5f) + Vector3.up * 0.92f, new Vector3(0.015f, 0.09f, seg), mats.hazard);
                tape.transform.rotation = rot;
                GameObject stripe = Box("Cordon_Tape_Stripe", parent, Vector3.Lerp(p, next, 0.5f) + Vector3.up * 0.92f, new Vector3(0.02f, 0.025f, seg), mats.post);
                stripe.transform.rotation = rot;
            }
        }

        static WorkshopMachineStand BuildStand(
            Transform parent,
            WorkshopProperty property,
            int index,
            WorkshopConfig.MachineOffer offer,
            Transform machine,
            WorkshopMachineLock machineLock,
            Materials mats,
            Font font)
        {
            var root = new GameObject($"Stand_{offer.slotId}");
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(machine.position, machine.rotation);
            root.layer = GameLayers.Interactable;

            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);

            // Plinth with a hazard-yellow rim; the machine stands here once bought
            Cylinder("Pad_Rim", visual.transform, new Vector3(0f, 0.025f, 0f), new Vector3(2.0f, 0.025f, 2.0f), mats.hazard);
            Cylinder("Pad", visual.transform, new Vector3(0f, 0.06f, 0f), new Vector3(1.8f, 0.04f, 1.8f), mats.pad);

            // Small sign on a post at the front edge (local +Z faces the walkway, like the machine)
            const float signZ = 1.05f;
            Box("Sign_Post", visual.transform, new Vector3(0f, 0.55f, signZ), new Vector3(0.07f, 1.1f, 0.07f), mats.post);
            Box("Sign_Board", visual.transform, new Vector3(0f, 1.27f, signZ), new Vector3(1.05f, 0.66f, 0.05f), mats.board);
            Box("Sign_Trim", visual.transform, new Vector3(0f, 1.27f, signZ - 0.01f), new Vector3(1.12f, 0.72f, 0.04f), mats.hazard);

            var iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(visual.transform, false);
            iconGo.transform.localPosition = new Vector3(0f, 1.44f, signZ + 0.035f);
            iconGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            SpriteRenderer icon = iconGo.AddComponent<SpriteRenderer>();

            TextMesh nameText = Text("Name", visual.transform, new Vector3(0f, 1.19f, signZ + 0.035f), 64, 0.0155f, font, Color.white);
            TextMesh priceText = Text("Price", visual.transform, new Vector3(0f, 1.04f, signZ + 0.035f), 64, 0.0135f, font, Color.yellow);

            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, 0.75f, 0.35f);
            collider.size = new Vector3(1.9f, 1.5f, 1.6f);

            WorkshopMachineStand stand = root.AddComponent<WorkshopMachineStand>();
            stand.Configure(property, index, machineLock, visual, collider, nameText, priceText, icon);
            return stand;
        }

        // ------------------------------------------------------------ primitives & materials

        static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.layer = parent.gameObject.layer;
            return go;
        }

        static GameObject Cylinder(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.layer = parent.gameObject.layer;
            return go;
        }

        static TextMesh Text(string name, Transform parent, Vector3 localPos, int fontSize, float charSize, Font font, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            go.layer = parent.gameObject.layer;
            TextMesh tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.fontSize = fontSize;
            tm.characterSize = charSize;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontStyle = FontStyle.Bold;
            tm.color = color;
            tm.text = name;
            if (font != null)
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            return tm;
        }

        sealed class Materials
        {
            public Material pad, hazard, post, board, saleRed;
        }

        static Materials EnsureMaterials() => new Materials
        {
            pad = Mat("Workshop_StandPad", new Color(0.46f, 0.47f, 0.48f), 0.15f),
            hazard = Mat("Workshop_Hazard", new Color(0.95f, 0.76f, 0.11f), 0.3f), // Safety Yellow #F2C21B
            post = Mat("Workshop_Post", new Color(0.12f, 0.13f, 0.14f), 0.35f),
            board = Mat("Workshop_StandBoard", new Color(0.11f, 0.33f, 0.32f), 0.2f), // dark teal
            saleRed = Mat("Workshop_SaleRed", new Color(0.70f, 0.20f, 0.16f), 0.25f),
        };

        static Material Mat(string matName, Color color, float smoothness)
        {
            if (!AssetDatabase.IsValidFolder(MaterialDir))
                AssetDatabase.CreateFolder("Assets/UpcycleCity/Materials", "Workshop");
            string path = $"{MaterialDir}/{matName}.mat";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_Color", color);
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
