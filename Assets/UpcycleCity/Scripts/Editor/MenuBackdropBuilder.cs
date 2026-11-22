using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UpcycleCity.Character;
using UpcycleCity.UI;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds <c>Resources/MenuBackdrop.prefab</c>: the live 3D street corner shown behind the Main Menu and the
    /// Character Creator (see <see cref="MenuBackdrop"/>). Uses only the game's own generated art (Bab Bhar facades,
    /// municipal bin, double-globe lamp, bikes, planter, litter). Safe to re-run; the scenes are not modified.
    /// </summary>
    public static class MenuBackdropBuilder
    {
        const string PrefabPath = "Assets/UpcycleCity/Resources/MenuBackdrop.prefab";
        const string Art = "Assets/UpcycleCity/Art/UpcycleProps";
        const string Bb = Art + "/Bourguiba";
        const string SkyPath = "Assets/UpcycleCity/Materials/Sky_Pollution.mat";
        const string PavementPath = "Assets/UpcycleCity/Materials/Bourguiba/Bourguiba_Pavement.mat";
        const string AsphaltPath = "Assets/UpcycleCity/Materials/Bourguiba/Bourguiba_Asphalt.mat";
        const string KerbPath = "Assets/UpcycleCity/Materials/Bourguiba/Bourguiba_Kerb.mat";
        const string LibraryPath = "Assets/UpcycleCity/Settings/Character/CharacterPartsLibrary.asset";

        /// <summary>Front line of the back row of facades (they face +Z, towards the camera).</summary>
        const float BackFacadeZ = -10.5f;
        /// <summary>Front line of the right-hand row (faces -X).</summary>
        const float SideFacadeX = 10.5f;

        [MenuItem("Upcycle City/UI/Build Menu Backdrop", priority = 40)]
        public static void Build()
        {
            var root = new GameObject("MenuBackdrop");
            try
            {
                var backdrop = root.AddComponent<MenuBackdrop>();

                // Sun: warm morning light, soft shadows (the only shadowed light)
                var sunGo = new GameObject("Sun");
                sunGo.transform.SetParent(root.transform, false);
                sunGo.transform.rotation = Quaternion.Euler(34f, -38f, 0f);
                Light sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = new Color(1f, 0.93f, 0.82f);
                sun.intensity = 1.45f;
                sun.shadows = LightShadows.Soft;

                // Ground: pavement where the character stands, a kerb and the avenue's asphalt behind the camera side
                Ground(root.transform, "Pavement", PavementPath, new Vector3(0f, 0f, -3f), new Vector3(80f, 0.2f, 16f), 0.0f);
                Ground(root.transform, "Kerb", KerbPath, new Vector3(0f, 0.03f, 5.15f), new Vector3(80f, 0.26f, 0.3f), 0.0f);
                Ground(root.transform, "Asphalt", AsphaltPath, new Vector3(0f, -0.12f, 18f), new Vector3(80f, 0.2f, 26f), 0.0f);

                // Back row of Bab Bhar facades
                string[] back = { "bb_building_d", "shop_cafe", "bb_building_a", "bb_building_gate", "bb_building_c", "bb_building_b" };
                float x = -26f;
                foreach (string name in back)
                {
                    GameObject b = Place(root.transform, $"{Bb}/Buildings/{name}.fbx", name, new Vector3(x, 0f, 0f), 0f);
                    if (b == null)
                        continue;
                    Bounds bb = Bounds(b);
                    b.transform.position += new Vector3(x - bb.min.x, 0f, BackFacadeZ - bb.max.z);
                    x += bb.size.x;
                }

                // Right-hand row turning the corner (depth on the title side)
                string[] side = { "bb_building_e", "shop_tools", "bb_building_d" };
                float z = BackFacadeZ;
                foreach (string name in side)
                {
                    GameObject b = Place(root.transform, $"{Bb}/Buildings/{name}.fbx", name, Vector3.zero, -90f);
                    if (b == null)
                        continue;
                    Bounds bb = Bounds(b);
                    b.transform.position += new Vector3(SideFacadeX - bb.min.x, 0f, z - bb.min.z);
                    z += bb.size.z;
                }

                // Street furniture around the character (who stands at the origin facing +Z)
                Transform bin = new GameObject("MunicipalBin").transform;
                bin.SetParent(root.transform, false);
                bin.localPosition = new Vector3(-1.9f, 0f, -2.6f);
                bin.localRotation = Quaternion.Euler(0f, 14f, 0f);
                Place(bin, $"{Bb}/Street/municipal_bin_body.fbx", "Body", Vector3.zero, 0f, local: true);
                Place(bin, $"{Bb}/Street/municipal_bin_contents.fbx", "Contents", new Vector3(0f, 0.6f, 0f), 0f, local: true);
                Transform lid = Place(bin, $"{Bb}/Street/municipal_bin_lid.fbx", "Lid", new Vector3(0f, 1.24f, -0.55f), 0f, local: true)?.transform;
                if (lid != null)
                    lid.localRotation = Quaternion.Euler(-28f, 0f, 0f);

                Place(root.transform, $"{Bb}/Street/street_lamp.fbx", "StreetLamp", new Vector3(5.4f, 0f, -3.2f), 0f);
                Place(root.transform, $"{Art}/City/bike_rack.fbx", "BikeRack", new Vector3(3.4f, 0f, -1.4f), 90f);
                Place(root.transform, $"{Art}/City/bicycle_teal.fbx", "BicycleTeal", new Vector3(2.1f, 0f, 0.4f), 72f);
                Place(root.transform, $"{Art}/City/bicycle_yellow.fbx", "BicycleYellow", new Vector3(3.5f, 0f, -1.6f), 95f);
                Place(root.transform, $"{Art}/City/flower_planter.fbx", "Planter", new Vector3(1.2f, 0f, -6.6f), 0f);
                Place(root.transform, $"{Art}/City/park_bench.fbx", "Bench", new Vector3(-4.0f, 0f, -8.2f), 0f);
                Place(root.transform, $"{Art}/City/street_bins.fbx", "SortingBins", new Vector3(5.6f, 0f, -8.4f), 0f);
                Place(root.transform, $"{Bb}/Litter/bag_black_tied.fbx", "BagBlack", new Vector3(-1.0f, 0f, -2.1f), 35f);
                Place(root.transform, $"{Bb}/Litter/bag_blue_tied.fbx", "BagBlue", new Vector3(-2.9f, 0f, -2.2f), -20f);
                Place(root.transform, $"{Bb}/Litter/bottle_water_crushed.fbx", "Bottle", new Vector3(0.9f, 0.02f, 1.6f), 60f);
                Place(root.transform, $"{Bb}/Litter/cup_espresso.fbx", "Cup", new Vector3(-0.8f, 0.02f, 2.2f), 0f);
                Place(root.transform, $"{Bb}/Litter/kaskrout_paper.fbx", "Paper", new Vector3(-1.6f, 0.02f, 0.6f), 120f);

                // The player's character (Main Menu only; follows the saved look)
                var character = new GameObject("MenuCharacter");
                character.transform.SetParent(root.transform, false);
                character.transform.localRotation = Quaternion.Euler(0f, -14f, 0f);
                var avatar = character.AddComponent<CharacterAvatar>();
                avatar.SetLibrary(AssetDatabase.LoadAssetAtPath<CharacterPartsLibrary>(LibraryPath));
                avatar.Configure(true, ShadowCastingMode.On);
                character.SetActive(false);

                backdrop.Configure(AssetDatabase.LoadAssetAtPath<Material>(SkyPath), character);

                // Static: let the renderer batch it on phones
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.GetComponentInParent<CharacterAvatar>(true) == null && t.GetComponent<Light>() == null)
                        GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic);
                }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"[Upcycle City] Built {PrefabPath} (live menu backdrop).");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void Ground(Transform parent, string name, string materialPath, Vector3 center, Vector3 size, float yOffset)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center + new Vector3(0f, -size.y * 0.5f + yOffset, 0f);
            go.transform.localScale = size;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat != null)
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
        }

        static GameObject Place(Transform parent, string path, string name, Vector3 position, float yaw, bool local = false)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null)
            {
                Debug.LogWarning($"[Upcycle City] Menu backdrop: missing {path}");
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);
            return go;
        }

        static Bounds Bounds(GameObject go)
        {
            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(go.transform.position, Vector3.one);
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                b.Encapsulate(renderers[i].bounds);
            return b;
        }
    }
}
