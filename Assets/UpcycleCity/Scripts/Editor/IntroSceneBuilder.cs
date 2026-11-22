using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UpcycleCity.Character;
using UpcycleCity.Cinematics;
using UpcycleCity.Pollution;

namespace UpcycleCity.Editor
{
    /// <summary>
    /// Builds the intro cinematic scene ("The Turtle"), the first scene of the game: it plays before the
    /// character creator. Safe to re-run: it asks before replacing the scene and only creates the intro's
    /// config, grades and materials when they are missing, so Inspector tweaks are kept.
    ///
    /// One scene, three sets far apart:
    ///   Beach (origin)        grim present and, with the litter hidden, the clean memory
    ///   Underwater (+500 x)   the dream: sea floor, fish, light shafts, the swimming turtle
    ///   Home (-500 x)         dusk street, the shack, and its interior (further along x)
    /// Every shot (CameraAnchor), mark and walk path is a named child of "Anchors", so shots can be
    /// reframed by moving them in the Scene view.
    /// </summary>
    static class IntroSceneBuilder
    {
        const string ScenePath = "Assets/UpcycleCity/Scenes/Intro.unity";
        const string CreatorScenePath = "Assets/UpcycleCity/Scenes/CharacterCreator.unity";
        const string SettingsFolder = "Assets/UpcycleCity/Settings/Intro";
        const string MaterialsFolder = "Assets/UpcycleCity/Materials/Intro";
        const string ConfigPath = SettingsFolder + "/IntroConfig.asset";
        const string GrimProfilePath = SettingsFolder + "/IntroGrim.asset";
        const string DreamProfilePath = SettingsFolder + "/IntroDream.asset";
        const string WaterMeshPath = SettingsFolder + "/IntroWaterGrid.asset";
        const string Art = "Assets/UpcycleCity/Art/";
        const string Props = Art + "UpcycleProps/";

        static readonly Vector3 UnderwaterOrigin = new Vector3(500f, 0f, 0f);
        static readonly Vector3 HomeOrigin = new Vector3(-500f, 0f, 0f);
        static readonly Vector3 InteriorOrigin = new Vector3(-540f, 0f, 60f);

        // Beach litter, scattered with a fixed seed so rebuilds look the same
        static readonly string[] LitterModels =
        {
            "Litter/bag_black", "Litter/bag_blue", "Litter/bag_white", "Litter/bottle_2l", "Litter/bottle_blue",
            "Litter/bottle_clear", "Litter/bottle_crushed", "Litter/bottle_glass_brown", "Litter/bottle_glass_green",
            "Litter/can", "Litter/can_crushed", "Litter/cup_paper", "Litter/cup_paper_red", "Litter/pizza_box",
            "Litter/takeaway_box", "Litter/tray_foam", "Litter/wrapper_red", "Litter/wrapper_yellow",
            "Litter/cigarette_butts", "Litter/newspaper", "Litter/cardboard_box", "Litter/tyre",
            "Bourguiba/Litter/bottle_water", "Bourguiba/Litter/bottle_water_crushed", "Bourguiba/Litter/bag_black_tied",
            "Bourguiba/Litter/bag_blue_tied", "Bourguiba/Litter/tin_tuna", "Bourguiba/Litter/tin_harissa",
            "Bourguiba/Litter/cup_espresso", "Bourguiba/Litter/kaskrout_paper", "Bourguiba/Litter/glibettes_shells",
        };
        static readonly string[] FloatingLitter =
        {
            "Litter/bag_white", "Litter/bag_blue", "Litter/bottle_2l", "Litter/bottle_clear", "Litter/tray_foam",
            "Bourguiba/Litter/bottle_water", "Litter/cup_paper",
        };

        [MenuItem("Upcycle City/Build Intro Scene", priority = 19)]
        static void BuildFromMenu()
        {
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Rebuild Intro?",
                    $"{ScenePath} already exists and will be replaced. The intro config, grades and materials are kept.",
                    "Rebuild", "Cancel"))
                return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            Build();
        }

        /// <summary>Builds without asking (for automation). Replaces the scene file.</summary>
        public static void Build()
        {
            // New scene first: opening it unloads assets held only by local variables
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory(SettingsFolder);
            Directory.CreateDirectory(MaterialsFolder);

            IntroConfig config = CreateOrFillConfig();
            VolumeProfile grim = CreateOrFillGrimProfile();
            VolumeProfile dream = CreateOrFillDreamProfile();
            Mesh waterMesh = CreateOrLoadWaterMesh();
            var mats = new Materials();

            var ctx = new Context { mats = mats, waterMesh = waterMesh, anchors = new GameObject("Anchors").transform };
            BuildCore(ctx, grim, dream);
            BuildBeach(ctx);
            BuildUnderwater(ctx);
            BuildHome(ctx);
            BuildInterior(ctx);
            BuildActor(ctx);
            BuildDirector(ctx, config);

            EditorSceneManager.SaveScene(scene, ScenePath);
            UpdateBuildScenes();
            AssetDatabase.SaveAssets();
            Debug.Log($"[Upcycle City] Built {ScenePath}. Press Play to watch the intro; it then loads the character creator.");
        }

        sealed class Context
        {
            public Materials mats;
            public Mesh waterMesh;
            public Transform anchors;
            public Camera camera;
            public Light sun;
            public PollutionController pollution;
            public Volume grimVolume, dreamVolume;
            public readonly List<Renderer> water = new List<Renderer>();
            public GameObject litter, deadTurtle;
            public SeaTurtle shoreTurtle, underwaterTurtle;
            public Transform bag, coin;
            public Light lamp;
            public CinematicActor actor;
        }

        // ---------------------------------------------------------------- assets

        static IntroConfig CreateOrFillConfig()
        {
            var config = AssetDatabase.LoadAssetAtPath<IntroConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<IntroConfig>();
                AssetDatabase.CreateAsset(config, ConfigPath);
            }
            if (config.grimMusic == null)
                config.grimMusic = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/UpcycleCity/Audio/Music/Intro/intro_sadness.ogg");
            if (config.dreamMusic == null)
                config.dreamMusic = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/UpcycleCity/Audio/Music/Intro/intro_catmint.ogg");
            if (config.coinClip == null)
                config.coinClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/UpcycleCity/Audio/SFX/Economy/seller_getting_money.wav");
            if (config.waveClips == null || config.waveClips.Length == 0)
            {
                var waves = new List<AudioClip>();
                foreach (string guid in AssetDatabase.FindAssets("t:AudioClip wave_", new[] { "Assets/UpcycleCity/Audio/SFX/Intro" }))
                    waves.Add(AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(guid)));
                config.waveClips = waves.ToArray();
            }
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
            return config;
        }

        static VolumeProfile LoadOrCreateProfile(string path)
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile != null)
                return profile;
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
            return profile;
        }

        /// <summary>Adds a component only if the profile doesn't have one yet; returns null when it already exists.</summary>
        static T AddIfMissing<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.Has<T>())
                return null;
            T component = profile.Add<T>(true);
            component.name = typeof(T).Name;
            AssetDatabase.AddObjectToAsset(component, profile);
            return component;
        }

        static VolumeProfile CreateOrFillGrimProfile()
        {
            VolumeProfile p = LoadOrCreateProfile(GrimProfilePath);
            if (AddIfMissing<ColorAdjustments>(p) is { } color)
            {
                color.contrast.Override(14f);
                color.saturation.Override(-58f);
                color.colorFilter.Override(new Color(0.86f, 0.92f, 1f));
                color.postExposure.Override(-0.1f);
            }
            if (AddIfMissing<Vignette>(p) is { } vignette)
            {
                vignette.intensity.Override(0.4f);
                vignette.smoothness.Override(0.45f);
            }
            if (AddIfMissing<FilmGrain>(p) is { } grain)
            {
                grain.type.Override(FilmGrainLookup.Medium3);
                grain.intensity.Override(0.38f);
                grain.response.Override(0.75f);
            }
            if (AddIfMissing<Bloom>(p) is { } bloom)
            {
                bloom.intensity.Override(0.25f);
                bloom.threshold.Override(1.1f);
            }
            if (AddIfMissing<Tonemapping>(p) is { } tone)
                tone.mode.Override(TonemappingMode.Neutral);
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssetIfDirty(p);
            return p;
        }

        static VolumeProfile CreateOrFillDreamProfile()
        {
            VolumeProfile p = LoadOrCreateProfile(DreamProfilePath);
            if (AddIfMissing<Bloom>(p) is { } bloom)
            {
                bloom.intensity.Override(0.9f);
                bloom.threshold.Override(1.05f);
                bloom.scatter.Override(0.7f);
                bloom.tint.Override(new Color(1f, 0.93f, 0.82f));
            }
            if (AddIfMissing<ColorAdjustments>(p) is { } color)
            {
                color.postExposure.Override(-0.25f);
                color.contrast.Override(6f);
                color.saturation.Override(32f);
                color.colorFilter.Override(new Color(1f, 0.98f, 0.94f));
            }
            if (AddIfMissing<SplitToning>(p) is { } split)
            {
                split.shadows.Override(new Color(0.25f, 0.6f, 0.65f));
                split.highlights.Override(new Color(1f, 0.78f, 0.55f));
                split.balance.Override(12f);
            }
            if (AddIfMissing<Vignette>(p) is { } vignette)
            {
                vignette.color.Override(new Color(0.05f, 0.12f, 0.14f));
                vignette.intensity.Override(0.28f);
                vignette.smoothness.Override(0.9f);
            }
            if (AddIfMissing<DepthOfField>(p) is { } dof)
            {
                dof.mode.Override(DepthOfFieldMode.Gaussian);
                dof.gaussianStart.Override(28f);
                dof.gaussianEnd.Override(90f);
                dof.gaussianMaxRadius.Override(1f);
            }
            if (AddIfMissing<ChromaticAberration>(p) is { } chroma)
                chroma.intensity.Override(0.18f);
            if (AddIfMissing<Tonemapping>(p) is { } tone)
                tone.mode.Override(TonemappingMode.Neutral);
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssetIfDirty(p);
            return p;
        }

        /// <summary>A 200 x 120 m grid; UV is in metres (u along the shore, v out to sea), as Stylized Water expects.</summary>
        static Mesh CreateOrLoadWaterMesh()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(WaterMeshPath);
            if (mesh != null)
                return mesh;
            const int nx = 80, nz = 48;
            const float width = 200f, depth = 120f;
            var vertices = new Vector3[(nx + 1) * (nz + 1)];
            var uvs = new Vector2[vertices.Length];
            for (int z = 0; z <= nz; z++)
            {
                for (int x = 0; x <= nx; x++)
                {
                    // Denser rows near the shore, where the foam is
                    float fz = z / (float)nz;
                    float pz = fz * fz * depth;
                    float px = (x / (float)nx - 0.5f) * width;
                    int i = z * (nx + 1) + x;
                    vertices[i] = new Vector3(px, 0f, pz);
                    uvs[i] = new Vector2(px, pz);
                }
            }
            var triangles = new int[nx * nz * 6];
            int t = 0;
            for (int z = 0; z < nz; z++)
            {
                for (int x = 0; x < nx; x++)
                {
                    int i = z * (nx + 1) + x;
                    triangles[t++] = i;
                    triangles[t++] = i + nx + 1;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + 1;
                    triangles[t++] = i + nx + 1;
                    triangles[t++] = i + nx + 2;
                }
            }
            mesh = new Mesh { name = "IntroWaterGrid", vertices = vertices, uv = uvs, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(new Vector3(0f, 0f, depth * 0.5f), new Vector3(width, 2f, depth));
            AssetDatabase.CreateAsset(mesh, WaterMeshPath);
            return mesh;
        }

        /// <summary>The intro's own materials, created once and then left alone.</summary>
        sealed class Materials
        {
            public readonly Material water, sand, seaFloor, shaft, motes, plywood, concrete, roof, gold, fabric;

            public Materials()
            {
                water = Get("Intro_Water", "UpcycleCity/Stylized Water", null);
                sand = Lit("Intro_Sand", "#D6C29A", 0.1f, 1f, 0.75f);
                seaFloor = Lit("Intro_SeaFloor", "#B9AE8A", 0.15f, 0f, 0f);
                shaft = Get("Intro_LightShaft", "UpcycleCity/Light Shaft", null);
                plywood = Lit("Intro_Plywood", "#8A6E4E", 0.15f, 0.6f, 0.9f);
                concrete = Lit("Intro_Concrete", "#77736C", 0.1f, 0.6f, 1f);
                roof = Lit("Intro_CorrugatedRoof", "#6A6F70", 0.35f, 0.5f, 1f);
                gold = Lit("Intro_CoinGold", "#E2B53C", 0.65f, 0f, 0f, metallic: 0.85f);
                fabric = Lit("Intro_Curtain", "#5E6F7A", 0.05f, 0.5f, 0.6f);
                motes = Get("Intro_Motes", "Universal Render Pipeline/Particles/Unlit", m =>
                {
                    // Additive, soft white specks
                    m.SetFloat("_Surface", 1f);
                    m.SetFloat("_Blend", 2f);
                    m.SetFloat("_SrcBlend", (float)BlendMode.One);
                    m.SetFloat("_DstBlend", (float)BlendMode.One);
                    m.SetFloat("_ZWrite", 0f);
                    m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    m.renderQueue = (int)RenderQueue.Transparent;
                    m.SetColor("_BaseColor", new Color(0.75f, 0.95f, 1f, 0.6f));
                    var tex = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
                    if (tex != null)
                        m.SetTexture("_BaseMap", tex);
                });
            }

            static Material Get(string name, string shaderName, System.Action<Material> setup)
            {
                string path = $"{MaterialsFolder}/{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat != null)
                    return mat;
                Shader shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"[Upcycle City] Shader '{shaderName}' not found for {name}.");
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                }
                mat = new Material(shader) { name = name };
                setup?.Invoke(mat);
                AssetDatabase.CreateAsset(mat, path);
                return mat;
            }

            static Material Lit(string name, string hex, float smoothness, float sensitivity, float grime, float metallic = 0f)
            {
                return Get(name, "UpcycleCity/Pollution Lit", m =>
                {
                    ColorUtility.TryParseHtmlString(hex, out Color c);
                    m.SetColor("_BaseColor", c);
                    m.SetFloat("_Smoothness", smoothness);
                    m.SetFloat("_Metallic", metallic);
                    m.SetFloat("_PollutionSensitivity", sensitivity);
                    m.SetFloat("_GrimeAmount", grime);
                    m.SetFloat("_ColorVariation", 0.08f);
                });
            }
        }

        // ---------------------------------------------------------------- core: camera, sun, grades, UI

        static void BuildCore(Context ctx, VolumeProfile grim, VolumeProfile dream)
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>("Assets/UpcycleCity/Materials/Sky_Pollution.mat");
            if (sky != null)
                RenderSettings.skybox = sky;

            var env = new GameObject("Environment").transform;

            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(env);
            sunGo.transform.rotation = Quaternion.Euler(20f, 170f, 0f);
            ctx.sun = sunGo.AddComponent<Light>();
            ctx.sun.type = LightType.Directional;
            ctx.sun.shadows = LightShadows.Soft;
            ctx.sun.intensity = 1.2f;

            var pollutionGo = new GameObject("PollutionController");
            pollutionGo.transform.SetParent(env);
            ctx.pollution = pollutionGo.AddComponent<PollutionController>();
            using (var so = new SerializedObject(ctx.pollution))
            {
                so.FindProperty("pollution").floatValue = 1f;
                so.FindProperty("transitionTime").floatValue = 2f;
                so.FindProperty("profile").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<PollutionProfile>("Assets/UpcycleCity/Settings/PollutionProfile.asset");
                so.FindProperty("sun").objectReferenceValue = ctx.sun;
                // The sun rises over the sea, behind the shots: everything on the beach is backlit
                so.FindProperty("sunAzimuth").floatValue = 170f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            ctx.grimVolume = AddVolume(env, "Volume_Grim", grim, 1f, 1f);
            ctx.dreamVolume = AddVolume(env, "Volume_Dream", dream, 0f, 2f);

            var camGo = new GameObject("IntroCamera") { tag = "MainCamera" };
            camGo.transform.position = new Vector3(-6f, 1.9f, -6.5f);
            ctx.camera = camGo.AddComponent<Camera>();
            ctx.camera.fieldOfView = 38f;
            ctx.camera.nearClipPlane = 0.05f;
            ctx.camera.farClipPlane = 400f;
            camGo.AddComponent<AudioListener>();
            UniversalAdditionalCameraData data = ctx.camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            camGo.AddComponent<CinematicCamera>();
        }

        static Volume AddVolume(Transform parent, string name, VolumeProfile profile, float weight, float priority)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;
            volume.weight = weight;
            volume.priority = priority;
            return volume;
        }

        // ---------------------------------------------------------------- beach

        static void BuildBeach(Context ctx)
        {
            var root = new GameObject("Set_Beach").transform;
            var random = new System.Random(1312);

            Ground(root, "Sand", Vector3.zero, new Vector2(200f, 220f), ctx.mats.sand);
            Water(ctx, root, "Sea", new Vector3(0f, 0.22f, 6f));

            // Palms and dry grass behind the beach, rocks along the waterline
            for (int i = 0; i < 9; i++)
            {
                var pos = new Vector3(Range(random, -34f, 26f), 0f, Range(random, -16f, -7f));
                Place(Art + $"QuaterniusNature/PalmTree_{1 + i % 4}.fbx", root, pos, Range(random, 0f, 360f), Range(random, 1.6f, 2.2f));
            }
            Place(Art + "QuaterniusNature/PalmTree_2.fbx", root, new Vector3(8.5f, 0f, -2.5f), 210f, 2f);
            for (int i = 0; i < 18; i++)
            {
                var pos = new Vector3(Range(random, -30f, 24f), 0f, Range(random, -14f, -4f));
                Place(Props + $"Garage/grass_dry_{i % 4}.fbx", root, pos, Range(random, 0f, 360f), Range(random, 1.5f, 2.6f));
            }
            for (int i = 0; i < 12; i++)
            {
                string rock = i % 3 == 0 ? $"Rock_Moss_{1 + i % 7}" : $"Rock_{1 + i % 7}";
                var pos = new Vector3(Range(random, -28f, 26f), -0.1f, Range(random, 4.5f, 9f));
                if (Mathf.Abs(pos.x - 2f) < 4f)
                    pos.x += 8f; // keep the turtle's spot clear
                Place(Art + $"QuaterniusNature/{rock}.fbx", root, pos, Range(random, 0f, 360f), Range(random, 1.5f, 3.2f));
            }

            // A hazy city skyline behind the beach
            string[] buildings = { "A", "B", "C", "D", "E", "F", "G", "H" };
            for (int i = 0; i < 12; i++)
            {
                var pos = new Vector3(-60f + i * 10.5f + Range(random, -2f, 2f), 0f, -58f + Range(random, -6f, 6f));
                Place(Art + $"KayKitCityBits/building_{buildings[i % buildings.Length]}.fbx", root, pos, 180f + 90f * random.Next(0, 4), 1f);
            }

            // Litter: only in the present
            ctx.litter = new GameObject("Litter");
            ctx.litter.transform.SetParent(root);
            var turtleSpot = new Vector2(1.6f, 3.4f);
            for (int i = 0; i < 90; i++)
            {
                var pos = new Vector3(Range(random, -32f, 24f), 0f, Range(random, -12f, 5.8f));
                if (Vector2.Distance(new Vector2(pos.x, pos.z), turtleSpot) < 1.4f)
                    continue;
                Transform item = Place(Props + LitterModels[random.Next(LitterModels.Length)] + ".fbx", ctx.litter.transform,
                    pos, Range(random, 0f, 360f), Range(random, 0.9f, 1.2f), tiltRandom: random);
                if (item != null)
                    SettleOnGround(item, 0f);
            }
            for (int i = 0; i < 4; i++)
            {
                var pos = new Vector3(Range(random, -26f, 20f), 0f, Range(random, -9f, -3f));
                Place(Props + "Bourguiba/Litter/bag_pile.fbx", ctx.litter.transform, pos, Range(random, 0f, 360f), 1f);
            }
            for (int i = 0; i < 16; i++)
            {
                var pos = new Vector3(Range(random, -24f, 22f), 0f, Range(random, 7f, 22f));
                Transform item = Place(Props + FloatingLitter[random.Next(FloatingLitter.Length)] + ".fbx", ctx.litter.transform,
                    pos, Range(random, 0f, 360f), 1f, tiltRandom: random);
                if (item != null)
                    SettleOnGround(item, 0.06f);
            }

            // The dead turtle on its side at the waterline, a plastic bag caught around its head
            ctx.deadTurtle = new GameObject("DeadTurtle");
            ctx.deadTurtle.transform.SetParent(root);
            ctx.deadTurtle.transform.SetPositionAndRotation(new Vector3(1.6f, 0.02f, 3.4f), Quaternion.Euler(0f, 145f, 0f));
            Transform model = Place(Props + "Intro/sea_turtle.fbx", ctx.deadTurtle.transform, Vector3.zero, 0f, 1.15f);
            if (model != null)
            {
                // Belly down in the wet sand, slightly tipped, head limp (SeaTurtle's dead pose)
                model.localPosition = new Vector3(0f, -0.02f, 0f);
                model.localRotation = Quaternion.Euler(3f, 0f, -7f);
            }
            var dead = ctx.deadTurtle.AddComponent<SeaTurtle>();
            SetTurtle(dead, model, SeaTurtle.TurtleMode.Dead, new Transform[0], 1f, 0f);
            ctx.bag = Place(Props + "Litter/bag_white.fbx", ctx.deadTurtle.transform, Vector3.zero, 0f, 0.6f);
            if (ctx.bag != null)
            {
                ctx.bag.name = "TangledBag";
                ctx.bag.localPosition = new Vector3(0.06f, 0.12f, 0.6f);
                ctx.bag.localRotation = Quaternion.Euler(15f, 30f, 35f);
            }

            // The memory: the same turtle alive, playing in the shallows (hidden in the present)
            Transform[] loop = Loop(ctx.anchors, "Path_ShoreTurtle", new Vector3(1.6f, 0f, 8.2f), new Vector2(2.8f, 1.5f), 8,
                i => i % 2 == 0 ? 0.02f : -0.16f);
            var shoreGo = new GameObject("MemoryTurtle_Shore");
            shoreGo.transform.SetParent(root);
            shoreGo.transform.position = loop[0].position;
            Transform shoreModel = Place(Props + "Intro/sea_turtle.fbx", shoreGo.transform, Vector3.zero, 0f, 1.4f);
            ResetLocal(shoreModel);
            ctx.shoreTurtle = shoreGo.AddComponent<SeaTurtle>();
            SetTurtle(ctx.shoreTurtle, shoreModel, SeaTurtle.TurtleMode.Swim, loop, 1.3f, 0f);
            shoreGo.SetActive(false);

            // Shots and marks
            Mark(ctx.anchors, "Mark_ShoreStart", new Vector3(-16f, 0f, 1.8f), 90f);
            PathPoints(ctx.anchors, "Path_Shore", new Vector3(-9f, 0f, 2.3f), new Vector3(-3f, 0f, 1.9f), new Vector3(0.4f, 0f, 2.3f));
            Shot(ctx.anchors, "B1_From", new Vector3(-5.5f, 1.9f, -6.5f), new Vector3(-12f, 1.1f, 2f), 38f);
            Shot(ctx.anchors, "B1_To", new Vector3(-2.8f, 1.75f, -6f), new Vector3(-4.5f, 1.0f, 2.4f), 36f);
            // The turtle low in the foreground, the man walking up behind it
            Shot(ctx.anchors, "B2_From", new Vector3(5.4f, 0.95f, 6.6f), new Vector3(-0.8f, 0.75f, 2.2f), 34f);
            Shot(ctx.anchors, "B2_To", new Vector3(4.8f, 0.85f, 6.1f), new Vector3(0.5f, 0.7f, 2.6f), 31f);
            // Three-quarter front of the kneeling man, the turtle at the bottom of frame
            Shot(ctx.anchors, "B3_From", new Vector3(2.5f, 1.15f, 5.6f), new Vector3(0.7f, 0.7f, 2.6f), 36f);
            Shot(ctx.anchors, "B3_To", new Vector3(2.2f, 1.1f, 5.1f), new Vector3(0.65f, 0.8f, 2.5f), 31f);
            Shot(ctx.anchors, "B4_From", new Vector3(0.4f, 1.55f, 2.3f), new Vector3(2.2f, 0.1f, 9.5f), 46f);
            Shot(ctx.anchors, "B4_To", new Vector3(0.6f, 1.45f, 3.1f), new Vector3(2.2f, 0.05f, 9.5f), 40f);
            // Side-on: the man and the turtle in profile as he frees the bag
            Shot(ctx.anchors, "B6_From", new Vector3(-1.4f, 1.15f, 5.3f), new Vector3(1.0f, 0.55f, 2.85f), 34f);
            Shot(ctx.anchors, "B6_To", new Vector3(-1.0f, 1.1f, 4.9f), new Vector3(1.0f, 0.6f, 2.85f), 30f);
        }

        // ---------------------------------------------------------------- underwater dream

        static void BuildUnderwater(Context ctx)
        {
            var root = new GameObject("Set_Underwater").transform;
            root.position = UnderwaterOrigin;
            var random = new System.Random(77);
            Vector3 o = UnderwaterOrigin;

            Ground(root, "SeaFloor", o + new Vector3(0f, 0f, 10f), new Vector2(220f, 220f), ctx.mats.seaFloor);
            Water(ctx, root, "SurfaceFromBelow", o + new Vector3(0f, 9f, -40f));

            for (int i = 0; i < 46; i++)
            {
                var pos = o + new Vector3(Range(random, -16f, 16f), 0f, Range(random, 2f, 30f));
                if (Mathf.Abs(pos.x - o.x) < 5f && pos.z - o.z < 13f)
                    pos.x += Mathf.Sign(pos.x - o.x + 0.01f) * 5f; // keep the turtle's loop clear
                string model = (i % 5) switch
                {
                    0 => $"QuaterniusNature/Rock_Moss_{1 + i % 7}",
                    1 => "QuaterniusNature/Flowers",
                    2 => $"QuaterniusNature/Plant_{1 + i % 5}",
                    3 => $"QuaterniusNature/Rock_{1 + i % 7}",
                    _ => $"QuaterniusNature/Plant_{1 + (i + 2) % 5}",
                };
                float scale = model.Contains("Rock") ? Range(random, 1.5f, 4f) : Range(random, 1.4f, 2.6f);
                Place(Art + model + ".fbx", root, pos, Range(random, 0f, 360f), scale);
            }

            // God rays from the surface
            for (int i = 0; i < 9; i++)
            {
                var shaft = GameObject.CreatePrimitive(PrimitiveType.Quad);
                shaft.name = $"LightShaft_{i}";
                Object.DestroyImmediate(shaft.GetComponent<Collider>());
                shaft.transform.SetParent(root);
                shaft.transform.position = o + new Vector3(Range(random, -9f, 9f), 4.5f, Range(random, 6f, 20f));
                shaft.transform.rotation = Quaternion.Euler(Range(random, -12f, 12f), Range(random, -25f, 25f), Range(random, -14f, 14f));
                shaft.transform.localScale = new Vector3(Range(random, 0.9f, 2.2f), 10f, 1f);
                var r = shaft.GetComponent<MeshRenderer>();
                r.sharedMaterial = ctx.mats.shaft;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            // Drifting motes
            var motesGo = new GameObject("Motes");
            motesGo.transform.SetParent(root);
            motesGo.transform.position = o + new Vector3(0f, 4f, 9f);
            var ps = motesGo.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = 9f;
            main.startSpeed = 0.06f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.07f);
            main.maxParticles = 400;
            main.prewarm = true;
            main.loop = true;
            var emission = ps.emission;
            emission.rateOverTime = 40f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(18f, 8f, 16f);
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.08f;
            noise.frequency = 0.25f;
            var psr = motesGo.GetComponent<ParticleSystemRenderer>();
            psr.sharedMaterial = ctx.mats.motes;
            psr.shadowCastingMode = ShadowCastingMode.Off;

            // Fish
            School(root, "School_Small", o + new Vector3(-3f, 2.4f, 11f), new Vector3(3f, 0.8f, 2f), 14, 26f, 3,
                (Art + "QuaterniusFish/Fish1.fbx", 0.05f), (Art + "QuaterniusFish/Fish2.fbx", 0.05f));
            School(root, "School_Bright", o + new Vector3(3.5f, 4.2f, 12f), new Vector3(2.5f, 1f, 2f), 12, -30f, 11,
                (Art + "QuaterniusFish/Fish3.fbx", 0.05f), (Art + "QuaterniusFish/Fish2.fbx", 0.045f));
            School(root, "Dolphins", o + new Vector3(0f, 5f, 18f), new Vector3(11f, 1.2f, 6f), 2, 11f, 5,
                (Art + "QuaterniusFish/Dolphin.fbx", 0.22f));
            School(root, "Manta", o + new Vector3(0f, 6.5f, 15f), new Vector3(8f, 0.6f, 5f), 1, -8f, 2,
                (Art + "QuaterniusFish/Mantaray.fbx", 0.16f));

            // The turtle loops around in front of the camera, with a playful barrel roll now and then
            Transform[] loop = Loop(ctx.anchors, "Path_UnderwaterTurtle", o + new Vector3(0f, 3.2f, 8.5f), new Vector2(4f, 3f), 8,
                i => (i % 2 == 0 ? 0.9f : -0.7f));
            var turtleGo = new GameObject("MemoryTurtle_Underwater");
            turtleGo.transform.SetParent(root);
            turtleGo.transform.position = loop[0].position;
            Transform turtleModel = Place(Props + "Intro/sea_turtle.fbx", turtleGo.transform, Vector3.zero, 0f, 1.3f);
            ResetLocal(turtleModel);
            ctx.underwaterTurtle = turtleGo.AddComponent<SeaTurtle>();
            SetTurtle(ctx.underwaterTurtle, turtleModel,SeaTurtle.TurtleMode.Swim, loop, 1.7f, 11f);

            Shot(ctx.anchors, "B5_From", o + new Vector3(0f, 2.5f, 3.6f), o + new Vector3(0f, 3.4f, 8.5f), 52f);
            Shot(ctx.anchors, "B5_To", o + new Vector3(0.2f, 2.8f, 4.4f), o + new Vector3(0f, 3.4f, 8.5f), 46f);
        }

        // ---------------------------------------------------------------- home: dusk street and the shack

        static void BuildHome(Context ctx)
        {
            var root = new GameObject("Set_Home").transform;
            root.position = HomeOrigin;
            var random = new System.Random(5);
            Vector3 o = HomeOrigin;

            var ground = AssetDatabase.LoadAssetAtPath<Material>("Assets/UpcycleCity/Materials/Ground_Outskirts.mat");
            Ground(root, "Ground", o + new Vector3(0f, -0.01f, 10f), new Vector2(90f, 90f), ground != null ? ground : ctx.mats.concrete);

            for (int i = 0; i < 7; i++)
                Place(Art + "KayKitCityBits/road_straight.fbx", root, o + new Vector3(0f, -0.4f, -22f + i * 8.8f), 0f, 1f);

            // Tired buildings on the far side of the road, scrub and junk on ours
            string[] buildings = { "C", "E", "B", "H", "D", "F" };
            for (int i = 0; i < 6; i++)
                Place(Art + $"KayKitCityBits/building_{buildings[i]}.fbx", root, o + new Vector3(-11f, 0f, -18f + i * 9.5f), 90f, 1f);
            Place(Art + "KayKitCityBits/streetlight.fbx", root, o + new Vector3(4.8f, 0f, -4f), 270f, 1f);
            Place(Art + "KayKitCityBits/dumpster.fbx", root, o + new Vector3(5.5f, 0f, 3f), 200f, 1f);
            Place(Props + "Bourguiba/Litter/bag_pile.fbx", root, o + new Vector3(6.2f, 0f, 5f), 30f, 1f);
            Place(Props + "Bourguiba/Litter/skip_full.fbx", root, o + new Vector3(6.5f, 0f, -10f), 90f, 1f);
            for (int i = 0; i < 14; i++)
                Place(Props + $"Garage/grass_dry_{i % 4}.fbx", root, o + new Vector3(Range(random, 5f, 16f), 0f, Range(random, -16f, 24f)), Range(random, 0f, 360f), 2f);

            // The shack: an old lock-up with a rusty container lean-to, pallets and tyres
            Vector3 shack = o + new Vector3(11f, 0f, 14f);
            Place(Props + "Garage/garage_old.fbx", root, shack, 270f, 1f);
            Place(Props + "Garage/container_rusty.fbx", root, shack + new Vector3(1f, 0f, 5.4f), 0f, 1f);
            Place(Props + "City/pallet.fbx", root, shack + new Vector3(-2.8f, 0.5f, -3.4f), 0f, 1f, tilt: new Vector3(75f, 0f, 0f));
            Place(Props + "Garage/tyre_planter_red.fbx", root, shack + new Vector3(-3f, 0f, 2.6f), 0f, 1f);
            Place(Props + "Garage/tyre_planter_teal.fbx", root, shack + new Vector3(-3.4f, 0f, 3.3f), 0f, 1f);
            Place(Props + "Garage/wheelbarrow.fbx", root, shack + new Vector3(-3.3f, 0f, -1.5f), 120f, 1f);

            Mark(ctx.anchors, "Mark_HomeStart", o + new Vector3(2.6f, 0f, -14f), 0f);
            PathPoints(ctx.anchors, "Path_Home", o + new Vector3(3f, 0f, 2f), o + new Vector3(5.6f, 0f, 9f), o + new Vector3(7.6f, 0f, 13.4f));
            Shot(ctx.anchors, "B7_From", o + new Vector3(-3.5f, 1.3f, -17f), o + new Vector3(3f, 1.4f, 0f), 40f);
            Shot(ctx.anchors, "B7_To", o + new Vector3(-2f, 1.6f, -9f), o + new Vector3(8f, 1.3f, 13f), 34f);
        }

        static void BuildInterior(Context ctx)
        {
            var root = new GameObject("Set_ShackInterior").transform;
            root.position = InteriorOrigin;
            Vector3 o = InteriorOrigin;
            const float w = 3.6f, d = 3f, h = 2.3f;

            Box(root, "Floor", o + new Vector3(0f, -0.05f, 0f), new Vector3(w + 0.3f, 0.1f, d + 0.3f), ctx.mats.concrete);
            Box(root, "Roof", o + new Vector3(0f, h + 0.05f, 0f), new Vector3(w + 0.4f, 0.1f, d + 0.4f), ctx.mats.roof);
            Box(root, "Wall_Back", o + new Vector3(0f, h * 0.5f, d * 0.5f + 0.05f), new Vector3(w + 0.2f, h, 0.1f), ctx.mats.plywood);
            Box(root, "Wall_Front", o + new Vector3(0f, h * 0.5f, -d * 0.5f - 0.05f), new Vector3(w + 0.2f, h, 0.1f), ctx.mats.plywood);
            Box(root, "Wall_Right", o + new Vector3(w * 0.5f + 0.05f, h * 0.5f, 0f), new Vector3(0.1f, h, d), ctx.mats.plywood);
            // Left wall with a small window
            float wx = -w * 0.5f - 0.05f;
            Box(root, "Wall_Left_Low", o + new Vector3(wx, 0.55f, 0f), new Vector3(0.1f, 1.1f, d), ctx.mats.plywood);
            Box(root, "Wall_Left_High", o + new Vector3(wx, 1.95f, 0f), new Vector3(0.1f, 0.7f, d), ctx.mats.plywood);
            Box(root, "Wall_Left_A", o + new Vector3(wx, 1.35f, -0.95f), new Vector3(0.1f, 0.5f, 1.1f), ctx.mats.plywood);
            Box(root, "Wall_Left_B", o + new Vector3(wx, 1.35f, 0.95f), new Vector3(0.1f, 0.5f, 1.1f), ctx.mats.plywood);
            Box(root, "Curtain", o + new Vector3(wx + 0.08f, 1.45f, -0.3f), new Vector3(0.02f, 0.6f, 0.35f), ctx.mats.fabric);

            // Furniture: a mattress on a pallet, a crate for a table, an old TV, an armchair, a shelf
            Place(Props + "City/pallet.fbx", root, o + new Vector3(-0.8f, 0f, 0.7f), 90f, 1f);
            Place(Props + "City/mattress.fbx", root, o + new Vector3(-0.8f, 0.13f, 0.7f), 90f, 1f);
            Place(Props + "Garage/crate_plastic_blank.fbx", root, o + new Vector3(0.2f, 0f, -0.55f), 0f, 1f);
            Place(Props + "Garage/crate_paper_blank.fbx", root, o + new Vector3(1.3f, 0f, 1.1f), 0f, 1f);
            Place(Props + "Garage/crt_tv.fbx", root, o + new Vector3(1.3f, 0.3f, 1.15f), 200f, 1f);
            Place(Props + "Garage/armchair_old.fbx", root, o + new Vector3(1.25f, 0f, -0.75f), 300f, 1f);
            Place(Props + "Garage/shelf_unit.fbx", root, o + new Vector3(1.55f, 0f, 0.15f), 270f, 1f);
            Place(Props + "Garage/item_jar.fbx", root, o + new Vector3(1.55f, 1.02f, 0.3f), 0f, 1.2f);
            Place(Props + "Garage/item_can_red.fbx", root, o + new Vector3(1.55f, 1.02f, -0.05f), 0f, 1f);
            Place(Props + "Garage/item_newspapers.fbx", root, o + new Vector3(-0.2f, 0f, -1.05f), 30f, 1f);

            // The coin tin on the crate, with a few coins beside it
            Place(Props + "Garage/item_tin.fbx", root, o + new Vector3(0.32f, 0.3f, -0.55f), 0f, 1.6f);
            for (int i = 0; i < 3; i++)
                Coin(root, $"Coin_Spare_{i}", o + new Vector3(0.47f + i * 0.03f, 0.302f + i * 0.004f, -0.45f + i * 0.02f), ctx.mats.gold);
            ctx.coin = Coin(root, "Coin_Promise", o + new Vector3(0.1f, 0.62f, -0.35f), ctx.mats.gold);

            // The only light: a bare work lamp hanging from the roof, plus cold dusk through the window
            Transform lampModel = Place(Props + "Garage/work_lamp.fbx", root, o + new Vector3(0.1f, h, -0.1f), 0f, 1f);
            var lampGo = new GameObject("LampLight");
            lampGo.transform.SetParent(root);
            lampGo.transform.position = o + new Vector3(0.1f, h - 0.85f, -0.1f);
            ctx.lamp = lampGo.AddComponent<Light>();
            ctx.lamp.type = LightType.Point;
            ctx.lamp.range = 6f;
            ctx.lamp.intensity = 3.5f;
            ctx.lamp.color = new Color(1f, 0.82f, 0.6f);
            ctx.lamp.shadows = LightShadows.Soft;
            if (lampModel != null)
                lampModel.name = "WorkLamp";

            var windowGo = new GameObject("WindowLight");
            windowGo.transform.SetParent(root);
            windowGo.transform.position = o + new Vector3(wx - 1.5f, 1.9f, 0f);
            windowGo.transform.rotation = Quaternion.LookRotation(o + new Vector3(0f, 0.3f, 0.2f) - windowGo.transform.position);
            var window = windowGo.AddComponent<Light>();
            window.type = LightType.Spot;
            window.spotAngle = 40f;
            window.range = 7f;
            window.intensity = 4f;
            window.color = new Color(0.55f, 0.62f, 0.95f);
            window.shadows = LightShadows.Soft;

            Mark(ctx.anchors, "Mark_Interior", o + new Vector3(-0.35f, 0f, 0.42f), 180f);
            Mark(ctx.anchors, "Mark_BagRest", o + new Vector3(0.05f, 0.34f, -0.6f), 30f);
            Mark(ctx.anchors, "Mark_CoinStart", o + new Vector3(0.1f, 0.62f, -0.35f), 0f);
            Mark(ctx.anchors, "Mark_CoinDrop", o + new Vector3(0.32f, 0.46f, -0.55f), 0f);
            Shot(ctx.anchors, "B8_From", o + new Vector3(0.95f, 1.3f, -1.35f), o + new Vector3(-0.35f, 0.85f, 0.42f), 52f);
            Shot(ctx.anchors, "B8_To", o + new Vector3(0.8f, 1.25f, -1.25f), o + new Vector3(-0.3f, 0.8f, 0.35f), 47f);
            Shot(ctx.anchors, "B8b_From", o + new Vector3(1.0f, 0.95f, -1.4f), o + new Vector3(0.15f, 0.38f, -0.5f), 36f);
            Shot(ctx.anchors, "B8b_To", o + new Vector3(0.85f, 0.88f, -1.25f), o + new Vector3(0.18f, 0.38f, -0.52f), 31f);
        }

        static Transform Coin(Transform parent, string name, Vector3 position, Material gold)
        {
            var coin = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            coin.name = name;
            Object.DestroyImmediate(coin.GetComponent<Collider>());
            coin.transform.SetParent(parent);
            coin.transform.position = position;
            coin.transform.localScale = new Vector3(0.026f, 0.0016f, 0.026f);
            coin.GetComponent<MeshRenderer>().sharedMaterial = gold;
            return coin.transform;
        }

        // ---------------------------------------------------------------- actor, overlay, director

        static void BuildActor(Context ctx)
        {
            var actorGo = new GameObject("Actor_PoorMan");
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(actorGo.transform, false);
            var avatar = bodyGo.AddComponent<CharacterAvatar>();
            avatar.SetLibrary(CharacterCreatorBuilder.CreateOrFillLibrary());
            // The saved look, or the default worn-coat look on a first run: the same man the creator then shows
            avatar.Configure(followSavedAppearance: true, ShadowCastingMode.On);
            EditorUtility.SetDirty(avatar);

            ctx.actor = actorGo.AddComponent<CinematicActor>();
            using (var so = new SerializedObject(ctx.actor))
            {
                so.FindProperty("body").objectReferenceValue = bodyGo.transform;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Transform start = ctx.anchors.Find("Mark_ShoreStart");
            if (start != null)
                actorGo.transform.SetPositionAndRotation(start.position, start.rotation);
        }

        static void BuildDirector(Context ctx, IntroConfig config)
        {
            var uiGo = new GameObject("IntroOverlay");
            var document = uiGo.AddComponent<UIDocument>();
            using (var docSo = new SerializedObject(document))
            {
                docSo.FindProperty("m_PanelSettings").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UpcycleCity/UI/UpcycleCityPanelSettings.asset");
                docSo.ApplyModifiedPropertiesWithoutUndo();
            }
            var overlay = uiGo.AddComponent<CinematicOverlay>();
            using (var so = new SerializedObject(overlay))
            {
                so.FindProperty("styleSheet").objectReferenceValue = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/UpcycleCity/UI/Intro.uss");
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var directorGo = new GameObject("IntroDirector");
            var director = directorGo.AddComponent<IntroDirector>();
            using (var so = new SerializedObject(director))
            {
                so.FindProperty("config").objectReferenceValue = config;
                so.FindProperty("cinematicCamera").objectReferenceValue = ctx.camera.GetComponent<CinematicCamera>();
                so.FindProperty("overlay").objectReferenceValue = overlay;
                so.FindProperty("actor").objectReferenceValue = ctx.actor;
                so.FindProperty("anchors").objectReferenceValue = ctx.anchors;
                so.FindProperty("pollution").objectReferenceValue = ctx.pollution;
                so.FindProperty("sun").objectReferenceValue = ctx.sun;
                so.FindProperty("grimVolume").objectReferenceValue = ctx.grimVolume;
                so.FindProperty("dreamVolume").objectReferenceValue = ctx.dreamVolume;
                SerializedProperty water = so.FindProperty("waterRenderers");
                water.arraySize = ctx.water.Count;
                for (int i = 0; i < ctx.water.Count; i++)
                    water.GetArrayElementAtIndex(i).objectReferenceValue = ctx.water[i];
                so.FindProperty("litter").objectReferenceValue = ctx.litter;
                so.FindProperty("deadTurtle").objectReferenceValue = ctx.deadTurtle;
                so.FindProperty("shoreTurtle").objectReferenceValue = ctx.shoreTurtle;
                so.FindProperty("underwaterTurtle").objectReferenceValue = ctx.underwaterTurtle;
                so.FindProperty("bag").objectReferenceValue = ctx.bag;
                so.FindProperty("coin").objectReferenceValue = ctx.coin;
                so.FindProperty("lamp").objectReferenceValue = ctx.lamp;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ---------------------------------------------------------------- helpers

        static float Range(System.Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);

        static Transform Place(string assetPath, Transform parent, Vector3 position, float yaw, float scale,
            System.Random tiltRandom = null, Vector3? tilt = null)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
            {
                Debug.LogWarning($"[Upcycle City] Intro builder: missing {assetPath}");
                return null;
            }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            if (tilt.HasValue)
                rotation *= Quaternion.Euler(tilt.Value);
            else if (tiltRandom != null && tiltRandom.NextDouble() < 0.5)
                rotation *= Quaternion.Euler(90f * (tiltRandom.NextDouble() < 0.5 ? 1f : -1f), 0f, Range(tiltRandom, -20f, 20f));
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = Vector3.one * scale;
            return go.transform;
        }

        static void ResetLocal(Transform t)
        {
            if (t == null)
                return;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
        }

        /// <summary>Moves an object so its lowest point rests at the given height (sinking it a touch).</summary>
        static void SettleOnGround(Transform item, float height)
        {
            var renderers = item.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return;
            Bounds b = renderers[0].bounds;
            foreach (Renderer r in renderers)
                b.Encapsulate(r.bounds);
            item.position += Vector3.up * (height - b.min.y - 0.01f);
        }

        static void Ground(Transform parent, string name, Vector3 center, Vector2 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.transform.localScale = new Vector3(size.x / 10f, 1f, size.y / 10f);
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        static void Box(Transform parent, string name, Vector3 center, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent);
            go.transform.position = center;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        static void Water(Context ctx, Transform parent, string name, Vector3 shoreEdgeCenter)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = shoreEdgeCenter;
            go.AddComponent<MeshFilter>().sharedMesh = ctx.waterMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = ctx.mats.water;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ctx.water.Add(renderer);
        }

        static void School(Transform parent, string name, Vector3 center, Vector3 radii, int count, float speed, int seed,
            params (string path, float scale)[] kinds)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = center;
            var school = go.AddComponent<FishSchool>();
            using var so = new SerializedObject(school);
            SerializedProperty species = so.FindProperty("species");
            species.arraySize = kinds.Length;
            for (int i = 0; i < kinds.Length; i++)
            {
                SerializedProperty s = species.GetArrayElementAtIndex(i);
                s.FindPropertyRelative("prefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(kinds[i].path);
                s.FindPropertyRelative("swim").objectReferenceValue = FirstClip(kinds[i].path);
                s.FindPropertyRelative("scale").floatValue = kinds[i].scale;
            }
            so.FindProperty("count").intValue = count;
            so.FindProperty("radii").vector3Value = radii;
            so.FindProperty("angularSpeed").floatValue = speed;
            so.FindProperty("seed").intValue = seed;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static AnimationClip FirstClip(string modelPath)
        {
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(modelPath))
            {
                if (o is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                    return clip;
            }
            return null;
        }

        static void SetTurtle(SeaTurtle turtle, Transform model, SeaTurtle.TurtleMode mode, Transform[] waypoints, float speed, float rollEvery)
        {
            using var so = new SerializedObject(turtle);
            so.FindProperty("mode").enumValueIndex = (int)mode;
            so.FindProperty("model").objectReferenceValue = model;
            SerializedProperty points = so.FindProperty("waypoints");
            points.arraySize = waypoints.Length;
            for (int i = 0; i < waypoints.Length; i++)
                points.GetArrayElementAtIndex(i).objectReferenceValue = waypoints[i];
            so.FindProperty("speed").floatValue = speed;
            so.FindProperty("rollEvery").floatValue = rollEvery;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>A closed loop of waypoints on an ellipse, with a height offset per point.</summary>
        static Transform[] Loop(Transform anchors, string name, Vector3 center, Vector2 radii, int count, System.Func<int, float> height)
        {
            var root = new GameObject(name).transform;
            root.SetParent(anchors);
            root.position = center;
            var points = new Transform[count];
            for (int i = 0; i < count; i++)
            {
                float a = i / (float)count * Mathf.PI * 2f;
                // Wobble the radius a little so the loop isn't a perfect ellipse
                float wobble = 1f + 0.18f * Mathf.Sin(a * 3f + 0.7f);
                var p = new GameObject($"P{i}").transform;
                p.SetParent(root);
                p.position = center + new Vector3(Mathf.Cos(a) * radii.x * wobble, height(i), Mathf.Sin(a) * radii.y * wobble);
                points[i] = p;
            }
            return points;
        }

        static void PathPoints(Transform anchors, string name, params Vector3[] points)
        {
            var root = new GameObject(name).transform;
            root.SetParent(anchors);
            for (int i = 0; i < points.Length; i++)
            {
                var p = new GameObject($"P{i}").transform;
                p.SetParent(root);
                p.position = points[i];
            }
        }

        static void Mark(Transform anchors, string name, Vector3 position, float yaw)
        {
            var t = new GameObject(name).transform;
            t.SetParent(anchors);
            t.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        }

        static void Shot(Transform anchors, string name, Vector3 position, Vector3 lookAt, float fov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(anchors);
            go.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position, Vector3.up));
            go.AddComponent<CameraAnchor>().fieldOfView = fov;
        }

        /// <summary>Intro first, then the character creator, then everything else already listed.</summary>
        internal static void UpdateBuildScenes()
        {
            var scenes = new List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(ScenePath, true) };
            bool hasCreator = false;
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
            {
                if (s.path == ScenePath || !File.Exists(s.path))
                    continue;
                if (s.path == CreatorScenePath)
                {
                    hasCreator = true;
                    scenes.Insert(1, s);
                    continue;
                }
                scenes.Add(s);
            }
            if (!hasCreator && File.Exists(CreatorScenePath))
                scenes.Insert(1, new EditorBuildSettingsScene(CreatorScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
