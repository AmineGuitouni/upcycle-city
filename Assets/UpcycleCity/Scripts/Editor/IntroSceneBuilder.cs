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
        const string FocusProfilePath = SettingsFolder + "/IntroFocus.asset";
        const string TransitionProfilePath = SettingsFolder + "/IntroTransition.asset";
        const string CausticsPath = SettingsFolder + "/IntroCaustics.png";
        const string FootprintTexPath = SettingsFolder + "/IntroFootprint.png";
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
            VolumeProfile focus = CreateOrFillFocusProfile();
            VolumeProfile transition = CreateOrFillTransitionProfile();
            Mesh waterMesh = CreateOrLoadWaterMesh();
            Texture2D caustics = CreateOrLoadTexture(CausticsPath, 256, CausticsPixel, TextureWrapMode.Repeat);
            Texture2D footprint = CreateOrLoadTexture(FootprintTexPath, 64, FootprintPixel, TextureWrapMode.Clamp);
            var mats = new Materials(footprint);

            var ctx = new Context { mats = mats, waterMesh = waterMesh, anchors = new GameObject("Anchors").transform };
            BuildCore(ctx, grim, dream, focus, transition, caustics);
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
            public Transform tangledBag, heldBag, coin;
            public Light lamp, windowLight;
            public Volume transitionVolume;
            public GameObject memoryOnly, presentOnly;
            public CausticsCookie caustics;
            public FootprintTrail footprints;
            public ParticleSystem sandPuff, coinGlint;
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
            const string sfx = "Assets/UpcycleCity/Audio/SFX/Intro/";
            if (config.windLoop == null) config.windLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(sfx + "ambience_wind_loop.wav");
            if (config.underwaterLoop == null) config.underwaterLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(sfx + "ambience_underwater_loop.wav");
            if (config.heartbeat == null) config.heartbeat = AssetDatabase.LoadAssetAtPath<AudioClip>(sfx + "heartbeat.wav");
            if (config.sting == null) config.sting = AssetDatabase.LoadAssetAtPath<AudioClip>(sfx + "sting.wav");
            if (config.gullCries == null || config.gullCries.Length == 0)
                config.gullCries = LoadClips("Assets/UpcycleCity/Audio/SFX/Intro", "gull_");
            if (config.footsteps == null || config.footsteps.Length == 0)
                config.footsteps = LoadClips("Assets/UpcycleCity/Audio/SFX/Footsteps", "walk_");
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

        static AudioClip[] LoadClips(string folder, string prefix)
        {
            var clips = new List<AudioClip>();
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path).StartsWith(prefix))
                    clips.Add(AssetDatabase.LoadAssetAtPath<AudioClip>(path));
            }
            clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return clips.ToArray();
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
                color.postExposure.Override(-0.45f);
                color.contrast.Override(20f);
                color.saturation.Override(42f);
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
                tone.mode.Override(TonemappingMode.ACES);
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssetIfDirty(p);
            return p;
        }

        /// <summary>Bokeh depth of field whose focus distance CinematicCamera drives shot by shot.</summary>
        static VolumeProfile CreateOrFillFocusProfile()
        {
            VolumeProfile p = LoadOrCreateProfile(FocusProfilePath);
            if (AddIfMissing<DepthOfField>(p) is { } dof)
            {
                dof.mode.Override(DepthOfFieldMode.Bokeh);
                dof.focusDistance.Override(5f);
                dof.focalLength.Override(55f);
                dof.aperture.Override(3.2f);
                dof.bladeCount.Override(6);
            }
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssetIfDirty(p);
            return p;
        }

        /// <summary>The ripple as the memory begins: lens warp, colour fringes and a bloom swell (weight pulsed).</summary>
        static VolumeProfile CreateOrFillTransitionProfile()
        {
            VolumeProfile p = LoadOrCreateProfile(TransitionProfilePath);
            if (AddIfMissing<LensDistortion>(p) is { } lens)
            {
                lens.intensity.Override(-0.55f);
                lens.scale.Override(0.92f);
            }
            if (AddIfMissing<ChromaticAberration>(p) is { } chroma)
                chroma.intensity.Override(1f);
            if (AddIfMissing<Bloom>(p) is { } bloom)
            {
                bloom.intensity.Override(4f);
                bloom.threshold.Override(0.55f);
                bloom.scatter.Override(0.85f);
            }
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssetIfDirty(p);
            return p;
        }

        /// <summary>Writes a generated greyscale texture once (then keeps it, so it can be painted over).</summary>
        static Texture2D CreateOrLoadTexture(string path, int size, System.Func<float, float, float> pixel, TextureWrapMode wrap)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null)
                return existing;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float v = Mathf.Clamp01(pixel(x / (float)size, y / (float)size));
                pixels[y * size + x] = new Color(v, v, v, v);
            }
            tex.SetPixels(pixels);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = wrap;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = wrap == TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Tileable caustics: bright wavy lines where cells of a Voronoi pattern meet.</summary>
        static float CausticsPixel(float u, float v)
        {
            float best = 9f, second = 9f;
            for (int cx = -1; cx <= 4; cx++)
            for (int cy = -1; cy <= 4; cy++)
            {
                // A 4x4 grid of jittered points, wrapped so the texture tiles
                int hx = (cx + 4) % 4, hy = (cy + 4) % 4;
                float jx = Mathf.Repeat(Mathf.Sin(hx * 12.9898f + hy * 78.233f) * 43758.5453f, 1f);
                float jy = Mathf.Repeat(Mathf.Sin(hx * 39.3468f + hy * 11.135f) * 24634.6345f, 1f);
                float px = (cx + 0.15f + jx * 0.7f) / 4f, py = (cy + 0.15f + jy * 0.7f) / 4f;
                float d = Vector2.Distance(new Vector2(u, v), new Vector2(px, py));
                if (d < best) { second = best; best = d; }
                else if (d < second) second = d;
            }
            float edge = second - best;
            return 0.25f + 0.75f * Mathf.Pow(Mathf.Clamp01(1f - edge * 9f), 3f);
        }

        /// <summary>A soft oval, a shoe print pressed into sand.</summary>
        static float FootprintPixel(float u, float v)
        {
            float x = (u - 0.5f) * 2f, y = (v - 0.5f) * 2f;
            float sole = Mathf.Clamp01(1f - (x * x / 0.55f + y * y / 0.95f));
            float waist = 1f - 0.45f * Mathf.Exp(-y * y * 18f) * Mathf.Clamp01(x * x * 6f);
            return Mathf.SmoothStep(0f, 1f, sole * 2.2f) * waist;
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
            public readonly Material plastic, footprint, particles, smog, bulb;

            public Materials(Texture2D footprintTex)
            {
                plastic = Get("Intro_PlasticBag", "UpcycleCity/Plastic Bag", null);
                footprint = Get("Intro_Footprint", "Universal Render Pipeline/Particles/Unlit", m =>
                {
                    Transparent(m, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
                    m.SetColor("_BaseColor", new Color(0.18f, 0.15f, 0.12f, 0.5f));
                    m.SetTexture("_BaseMap", footprintTex);
                });
                particles = Get("Intro_ParticlesAlpha", "Universal Render Pipeline/Particles/Unlit", m =>
                {
                    Transparent(m, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha);
                    m.SetColor("_BaseColor", Color.white);
                    var tex = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
                    if (tex != null)
                        m.SetTexture("_BaseMap", tex);
                });
                smog = AssetDatabase.LoadAssetAtPath<Material>("Assets/UpcycleCity/Materials/SmogDeck.mat");
                bulb = Get("Intro_StreetBulb", "Universal Render Pipeline/Unlit", m => m.SetColor("_BaseColor", new Color(1f, 0.8f, 0.5f)));
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
                    Transparent(m, BlendMode.One, BlendMode.One);
                    m.SetFloat("_Blend", 2f);
                    m.SetColor("_BaseColor", new Color(0.75f, 0.95f, 1f, 0.6f));
                    var tex = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
                    if (tex != null)
                        m.SetTexture("_BaseMap", tex);
                });
            }

            static void Transparent(Material m, BlendMode src, BlendMode dst)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_SrcBlend", (float)src);
                m.SetFloat("_DstBlend", (float)dst);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
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

        static void BuildCore(Context ctx, VolumeProfile grim, VolumeProfile dream, VolumeProfile focus, VolumeProfile transition,
            Texture2D caustics)
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
                // The memory heals the beach over this long: litter dissolves, the sky clears
                so.FindProperty("transitionTime").floatValue = 2.6f;
                so.FindProperty("profile").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<PollutionProfile>("Assets/UpcycleCity/Settings/PollutionProfile.asset");
                so.FindProperty("sun").objectReferenceValue = ctx.sun;
                // The sun rises over the sea, behind the shots: everything on the beach is backlit
                so.FindProperty("sunAzimuth").floatValue = 170f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            ctx.grimVolume = AddVolume(env, "Volume_Grim", grim, 1f, 1f);
            ctx.dreamVolume = AddVolume(env, "Volume_Dream", dream, 0f, 2f);
            ctx.transitionVolume = AddVolume(env, "Volume_Transition", transition, 0f, 4f);
            Volume focusVolume = AddVolume(env, "Volume_Focus", focus, 1f, 5f);

            ctx.caustics = sunGo.AddComponent<CausticsCookie>();
            using (var so = new SerializedObject(ctx.caustics))
            {
                so.FindProperty("sun").objectReferenceValue = ctx.sun;
                so.FindProperty("caustics").objectReferenceValue = caustics;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            ctx.caustics.enabled = false;

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
            var cinematic = camGo.AddComponent<CinematicCamera>();
            using (var so = new SerializedObject(cinematic))
            {
                so.FindProperty("focusVolume").objectReferenceValue = focusVolume;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
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
                AddSway(Place(Art + $"QuaterniusNature/PalmTree_{1 + i % 4}.fbx", root, pos, Range(random, 0f, 360f), Range(random, 1.6f, 2.2f)), 1.4f, 0.22f);
            }
            AddSway(Place(Art + "QuaterniusNature/PalmTree_2.fbx", root, new Vector3(8.5f, 0f, -2.5f), 210f, 2f), 1.4f, 0.22f);
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
                {
                    SettleOnGround(item, 0f);
                    Visibility(item.gameObject, PollutionVisibilityMode.WhenPolluted, Range(random, 0.3f, 0.85f));
                }
            }
            for (int i = 0; i < 4; i++)
            {
                var pos = new Vector3(Range(random, -26f, 20f), 0f, Range(random, -9f, -3f));
                Transform pile = Place(Props + "Bourguiba/Litter/bag_pile.fbx", ctx.litter.transform, pos, Range(random, 0f, 360f), 1f);
                if (pile != null)
                    Visibility(pile.gameObject, PollutionVisibilityMode.WhenPolluted, Range(random, 0.4f, 0.7f));
            }
            for (int i = 0; i < 16; i++)
            {
                var pos = new Vector3(Range(random, -24f, 22f), 0f, Range(random, 7f, 22f));
                Transform item = Place(Props + FloatingLitter[random.Next(FloatingLitter.Length)] + ".fbx", ctx.litter.transform,
                    pos, Range(random, 0f, 360f), 1f, tiltRandom: random);
                if (item != null)
                {
                    SettleOnGround(item, 0.16f);
                    Visibility(item.gameObject, PollutionVisibilityMode.WhenPolluted, Range(random, 0.3f, 0.85f));
                }
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
            Visibility(ctx.deadTurtle, PollutionVisibilityMode.WhenPolluted, 0.55f);
            // The plastic caught round its neck, wrapped around the head bone so it follows the limp pose
            Transform turtleHead = model != null ? model.Find("Head") : null;
            ctx.tangledBag = Place(Props + "Intro/bag_tangled.fbx", turtleHead != null ? turtleHead : ctx.deadTurtle.transform, Vector3.zero, 0f, 1.25f);
            if (ctx.tangledBag != null)
            {
                ctx.tangledBag.name = "TangledBag";
                ctx.tangledBag.localPosition = new Vector3(0f, 0f, 0.05f);
                ctx.tangledBag.localRotation = Quaternion.Euler(0f, 0f, 25f);
                Plastic(ctx.tangledBag, ctx.mats.plastic);
            }
            // The same bag as he carries it, hidden until he frees it; it swings from his hand
            ctx.heldBag = Place(Props + "Intro/bag_held.fbx", root, new Vector3(0f, -5f, 0f), 0f, 1f);
            if (ctx.heldBag != null)
            {
                ctx.heldBag.name = "HeldBag";
                Plastic(ctx.heldBag, ctx.mats.plastic);
                var swing = ctx.heldBag.gameObject.AddComponent<Pendulum>();
                using (var so = new SerializedObject(swing))
                {
                    so.FindProperty("amplitudeDegrees").vector2Value = new Vector2(6f, 4f);
                    so.FindProperty("frequency").floatValue = 0.9f;
                    so.FindProperty("inertia").floatValue = 9f;
                    so.FindProperty("hangUpright").boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                ctx.heldBag.gameObject.SetActive(false);
            }
            ctx.sandPuff = Particles(root, "SandPuff", new Vector3(1.6f, 0.1f, 3.4f), ctx.mats.particles, ps =>
            {
                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.3f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 1.1f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
                main.startColor = new Color(0.68f, 0.6f, 0.47f, 0.7f);
                main.gravityModifier = 0.5f;
                var emission = ps.emission;
                emission.rateOverTime = 0f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 0.15f;
                Fade(ps);
            });

            // The memory: the same turtle alive, playing in the shallows (hidden in the present)
            Transform[] loop = Loop(ctx.anchors, "Path_ShoreTurtle", new Vector3(1.4f, 0f, 7.7f), new Vector2(3.4f, 1.3f), 8,
                i => i % 2 == 0 ? 0.02f : -0.16f);
            var shoreGo = new GameObject("MemoryTurtle_Shore");
            shoreGo.transform.SetParent(root);
            shoreGo.transform.position = loop[0].position;
            Transform shoreModel = Place(Props + "Intro/sea_turtle.fbx", shoreGo.transform, Vector3.zero, 0f, 1.7f);
            ResetLocal(shoreModel);
            ctx.shoreTurtle = shoreGo.AddComponent<SeaTurtle>();
            SetTurtle(ctx.shoreTurtle, shoreModel, SeaTurtle.TurtleMode.Swim, loop, 1.3f, 0f);
            using (var so = new SerializedObject(ctx.shoreTurtle))
            {
                so.FindProperty("bankDegrees").floatValue = 9f; // stays flat in the shallows, shell to the sky
                so.FindProperty("strokeDegrees").floatValue = 22f; // short strokes: flippers stay under the surface
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Visibility(shoreGo, PollutionVisibilityMode.WhenClean, 0.35f);

            BuildBeachPresent(ctx, root);
            BuildBeachMemory(ctx, root);

            // Shots and marks
            Mark(ctx.anchors, "Mark_ShoreStart", new Vector3(-16f, 0f, 1.8f), 90f);
            PathPoints(ctx.anchors, "Path_Shore", new Vector3(-9f, 0f, 2.3f), new Vector3(-3f, 0f, 1.9f), new Vector3(0.5f, 0f, 2.2f));
            Mark(ctx.anchors, "Mark_Kneel", new Vector3(1.02f, 0f, 2.76f), 45f);
            Shot(ctx.anchors, "B1_From", new Vector3(-5.5f, 1.9f, -6.5f), new Vector3(-12f, 1.1f, 2f), 38f);
            Shot(ctx.anchors, "B1_To", new Vector3(-2.8f, 1.75f, -6f), new Vector3(-4.5f, 1.0f, 2.4f), 36f);
            // The turtle low in the foreground, the man walking up behind it
            Shot(ctx.anchors, "B2_From", new Vector3(5.3f, 0.95f, 5.6f), new Vector3(-0.8f, 0.75f, 2.1f), 34f);
            Shot(ctx.anchors, "B2_To", new Vector3(4.7f, 0.85f, 5.3f), new Vector3(0.5f, 0.75f, 2.5f), 31f);
            // Three-quarter front of the kneeling man, the turtle at the bottom of frame
            // Three-quarter front, low: his face, his hand on the shell, the turtle in the foreground
            // From his left, a little ahead: the kneeling silhouette, his hand on the shell, his face
            Shot(ctx.anchors, "B3_From", new Vector3(-1.0f, 1.0f, 5.95f), new Vector3(1.35f, 0.62f, 3.15f), 40f);
            Shot(ctx.anchors, "B3_To", new Vector3(-0.75f, 0.98f, 5.65f), new Vector3(1.35f, 0.66f, 3.15f), 36f);
            Shot(ctx.anchors, "B4_From", new Vector3(0.9f, 1.4f, 2.7f), new Vector3(1.6f, 0.1f, 8.2f), 40f);
            Shot(ctx.anchors, "B4_To", new Vector3(1.0f, 1.3f, 3.4f), new Vector3(1.6f, 0.05f, 8.2f), 31f);
            // Side-on: the man and the turtle in profile as he frees the bag
            // From his right, the hand that frees the bag: him and the turtle side by side
            Shot(ctx.anchors, "B6_From", new Vector3(3.5f, 1.35f, 0.25f), new Vector3(0.95f, 0.8f, 2.75f), 40f);
            Shot(ctx.anchors, "B6_To", new Vector3(3.3f, 1.35f, 0.45f), new Vector3(0.95f, 0.95f, 2.7f), 40f);
        }

        /// <summary>Only in the grey present: flies over the turtle, blowing grit, a bag tumbling in the wind.</summary>
        static void BuildBeachPresent(Context ctx, Transform root)
        {
            ctx.presentOnly = new GameObject("PresentOnly");
            ctx.presentOnly.transform.SetParent(root);

            Particles(ctx.presentOnly.transform, "Flies", new Vector3(1.6f, 0.45f, 3.4f), ctx.mats.particles, ps =>
            {
                var main = ps.main;
                main.startLifetime = 4f;
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.02f);
                main.startColor = new Color(0.05f, 0.05f, 0.05f, 1f);
                main.maxParticles = 12;
                main.prewarm = true;
                var emission = ps.emission;
                emission.rateOverTime = 3f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.35f;
                var velocity = ps.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.Local;
                velocity.orbitalX = new ParticleSystem.MinMaxCurve(-2f, 2f);
                velocity.orbitalY = new ParticleSystem.MinMaxCurve(2f, 4f);
                velocity.orbitalZ = new ParticleSystem.MinMaxCurve(-2f, 2f);
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = 1.2f;
                noise.frequency = 2f;
            });

            Particles(ctx.presentOnly.transform, "BlowingGrit", new Vector3(-8f, 0.3f, 0f), ctx.mats.particles, ps =>
            {
                var main = ps.main;
                main.startLifetime = 5f;
                main.startSpeed = 0f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
                main.startColor = new Color(0.55f, 0.5f, 0.42f, 0.45f);
                main.maxParticles = 300;
                main.prewarm = true;
                var emission = ps.emission;
                emission.rateOverTime = 45f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(30f, 0.5f, 14f);
                var velocity = ps.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.World;
                velocity.x = new ParticleSystem.MinMaxCurve(1.5f, 3f);
                velocity.y = new ParticleSystem.MinMaxCurve(-0.1f, 0.3f);
                velocity.z = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
                Fade(ps);
            });

            Transform tumbler = Place(Props + "Intro/bag_held.fbx", ctx.presentOnly.transform, new Vector3(-14f, 0.1f, -1.5f), 0f, 0.85f);
            if (tumbler != null)
            {
                tumbler.name = "TumblingBag";
                Plastic(tumbler, ctx.mats.plastic);
                tumbler.gameObject.AddComponent<TumblingLitter>();
            }
        }

        /// <summary>Only in the memory: gulls wheeling over the clean bay, splashes when the turtle surfaces.</summary>
        static void BuildBeachMemory(Context ctx, Transform root)
        {
            ctx.memoryOnly = new GameObject("MemoryOnly");
            ctx.memoryOnly.transform.SetParent(root);
            var gullModel = AssetDatabase.LoadAssetAtPath<GameObject>(Props + "Intro/gull.fbx");
            AudioClip[] cries = LoadClips("Assets/UpcycleCity/Audio/SFX/Intro", "gull_");
            var gulls = new[]
            {
                (new Vector3(3f, 0f, 16f), new Vector2(12f, 7f), 8.5f, 13f),
                (new Vector3(-4f, 0f, 22f), new Vector2(16f, 9f), 11f, -10f),
                (new Vector3(8f, 0f, 26f), new Vector2(9f, 6f), 13f, 16f),
            };
            for (int i = 0; i < gulls.Length && gullModel != null; i++)
            {
                var go = new GameObject($"Gull_{i}");
                go.transform.SetParent(ctx.memoryOnly.transform);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(gullModel, go.transform);
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
                    r.shadowCastingMode = ShadowCastingMode.Off; // its shadow would sweep the sand far below
                var gull = go.AddComponent<Gull>();
                using var so = new SerializedObject(gull);
                so.FindProperty("model").objectReferenceValue = model.transform;
                so.FindProperty("center").vector3Value = gulls[i].Item1;
                so.FindProperty("radii").vector2Value = gulls[i].Item2;
                so.FindProperty("height").floatValue = gulls[i].Item3;
                so.FindProperty("degreesPerSecond").floatValue = gulls[i].Item4;
                SerializedProperty c = so.FindProperty("cries");
                c.arraySize = i == 0 ? cries.Length : 0; // one bird calls; three would be a racket
                for (int k = 0; k < c.arraySize; k++)
                    c.GetArrayElementAtIndex(k).objectReferenceValue = cries[k];
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            var splashGo = new GameObject("TurtleSplash");
            splashGo.transform.SetParent(ctx.memoryOnly.transform);
            ParticleSystem droplets = Particles(splashGo.transform, "Droplets", Vector3.zero, ctx.mats.particles, ps =>
            {
                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 2.4f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
                main.startColor = new Color(0.9f, 0.98f, 1f, 0.9f);
                main.gravityModifier = 1.2f;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = ps.emission;
                emission.rateOverTime = 0f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 30f;
                shape.radius = 0.25f;
                shape.rotation = new Vector3(-90f, 0f, 0f);
            });
            ParticleSystem ripples = Particles(splashGo.transform, "Ripples", Vector3.zero, ctx.mats.particles, ps =>
            {
                var main = ps.main;
                main.startLifetime = 1.6f;
                main.startSpeed = 0f;
                main.startSize = 0.3f;
                main.startColor = new Color(1f, 1f, 1f, 0.5f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = ps.emission;
                emission.rateOverTime = 0f;
                var shape = ps.shape;
                shape.enabled = false;
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 7f));
                Fade(ps);
                ps.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.HorizontalBillboard;
            });
            var splash = splashGo.AddComponent<SplashOnSurface>();
            using (var so = new SerializedObject(splash))
            {
                so.FindProperty("tracked").objectReferenceValue = ctx.shoreTurtle != null ? ctx.shoreTurtle.transform : null;
                so.FindProperty("surfaceHeight").floatValue = 0.22f;
                so.FindProperty("probeHeight").floatValue = 0.3f;
                so.FindProperty("droplets").objectReferenceValue = droplets;
                so.FindProperty("ripples").objectReferenceValue = ripples;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            ctx.memoryOnly.SetActive(false);
        }

        static void Visibility(GameObject go, PollutionVisibilityMode mode, float threshold)
        {
            var visibility = go.GetComponent<PollutionVisibility>();
            if (visibility == null)
                visibility = go.AddComponent<PollutionVisibility>();
            using var so = new SerializedObject(visibility);
            so.FindProperty("mode").enumValueIndex = (int)mode;
            so.FindProperty("threshold").floatValue = threshold;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Plastic(Transform t, Material plastic)
        {
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = plastic;
                r.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        static void AddSway(Transform t, float degrees, float frequency)
        {
            if (t == null)
                return;
            var sway = t.gameObject.AddComponent<Sway>();
            using var so = new SerializedObject(sway);
            so.FindProperty("degrees").floatValue = degrees;
            so.FindProperty("frequency").floatValue = frequency;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static ParticleSystem Particles(Transform parent, string name, Vector3 position, Material material, System.Action<ParticleSystem> setup)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.position = position;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            setup(ps);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            return ps;
        }

        static void Fade(ParticleSystem ps)
        {
            var color = ps.colorOverLifetime;
            color.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
            color.color = g;
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
                Transform placed = Place(Art + model + ".fbx", root, pos, Range(random, 0f, 360f), scale);
                if (model.Contains("Plant"))
                    AddSway(placed, 7f, 0.18f);
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
            // A trail of bubbles rising from the turtle as it swims
            ParticleSystem bubbles = Particles(turtleGo.transform, "Bubbles", turtleGo.transform.position + Vector3.up * 0.2f, ctx.mats.motes, ps =>
            {
                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
                main.startColor = new Color(0.85f, 1f, 1f, 0.8f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.gravityModifier = -0.08f;
                var emission = ps.emission;
                emission.rateOverTime = 7f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.12f;
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = 0.25f;
                noise.frequency = 1.2f;
                Fade(ps);
            });
            bubbles.transform.localPosition = new Vector3(0f, 0.25f, 0.3f);

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
            var streetLampGo = new GameObject("StreetLampLight");
            streetLampGo.transform.SetParent(root);
            streetLampGo.transform.position = o + new Vector3(4.1f, 3.9f, -4f);
            var streetLamp = streetLampGo.AddComponent<Light>();
            streetLamp.type = LightType.Point;
            streetLamp.range = 11f;
            streetLamp.intensity = 6f;
            streetLamp.color = new Color(1f, 0.72f, 0.42f);
            streetLamp.shadows = LightShadows.None;
            var bulbGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulbGo.name = "Bulb";
            Object.DestroyImmediate(bulbGo.GetComponent<Collider>());
            bulbGo.transform.SetParent(streetLampGo.transform, false);
            bulbGo.transform.localScale = Vector3.one * 0.14f;
            bulbGo.GetComponent<MeshRenderer>().sharedMaterial = ctx.mats.bulb;
            var flicker = streetLampGo.AddComponent<LightFlicker>();
            using (var so = new SerializedObject(flicker))
            {
                so.FindProperty("bulb").objectReferenceValue = bulbGo.GetComponent<MeshRenderer>();
                so.ApplyModifiedPropertiesWithoutUndo();
            }

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
            Place(Props + "Garage/crate_plastic_blank.fbx", root, o + new Vector3(0.15f, 0f, -0.36f), 0f, 1f);
            Place(Props + "Garage/crate_paper_blank.fbx", root, o + new Vector3(1.3f, 0f, 1.1f), 0f, 1f);
            Place(Props + "Garage/crt_tv.fbx", root, o + new Vector3(1.3f, 0.3f, 1.15f), 200f, 1f);
            Place(Props + "Garage/armchair_old.fbx", root, o + new Vector3(-1.3f, 0f, -0.95f), 45f, 1f);
            Place(Props + "Garage/shelf_unit.fbx", root, o + new Vector3(1.55f, 0f, 0.15f), 270f, 1f);
            Place(Props + "Garage/item_jar.fbx", root, o + new Vector3(1.55f, 1.02f, 0.3f), 0f, 1.2f);
            Place(Props + "Garage/item_can_red.fbx", root, o + new Vector3(1.55f, 1.02f, -0.05f), 0f, 1f);
            Place(Props + "Garage/item_newspapers.fbx", root, o + new Vector3(-0.2f, 0f, -1.05f), 30f, 1f);

            // The coin tin on the crate, with a few coins beside it
            Place(Props + "Garage/item_tin.fbx", root, o + new Vector3(0.14f, 0.3f, -0.24f), 0f, 1.6f);
            for (int i = 0; i < 3; i++)
                Coin(root, $"Coin_Spare_{i}", o + new Vector3(0.24f + i * 0.032f, 0.302f + i * 0.004f, -0.24f + i * 0.012f), ctx.mats.gold);
            ctx.coin = Coin(root, "Coin_Promise", o + new Vector3(0.1f, 0.58f, -0.2f), ctx.mats.gold);
            ctx.coinGlint = Particles(root, "CoinGlint", o + new Vector3(0.14f, 0.5f, -0.24f), ctx.mats.motes, ps =>
            {
                var main = ps.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.6f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.015f, 0.04f);
                main.startColor = new Color(1f, 0.82f, 0.35f, 1f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var emission = ps.emission;
                emission.rateOverTime = 0f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.03f;
                Fade(ps);
            });

            // The only light: a bare work lamp hanging from the roof on its cable, drifting slowly so the
            // shadows move; plus cold night through the window (it turns to dawn as he makes his promise)
            var lampRig = new GameObject("LampRig").transform;
            lampRig.SetParent(root);
            lampRig.position = o + new Vector3(0.1f, h, -0.1f);
            var lampSwing = lampRig.gameObject.AddComponent<Pendulum>();
            using (var so = new SerializedObject(lampSwing))
            {
                so.FindProperty("amplitudeDegrees").vector2Value = new Vector2(3.5f, 2.5f);
                so.FindProperty("frequency").floatValue = 0.28f;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Transform lampModel = Place(Props + "Garage/work_lamp.fbx", lampRig, o + new Vector3(0.1f, h, -0.1f), 0f, 1f);
            var lampGo = new GameObject("LampLight");
            lampGo.transform.SetParent(lampRig);
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
            ctx.windowLight = window;

            // A soft warm fill from the camera side, so his face reads under the brim of his hat
            var fillGo = new GameObject("FaceFill");
            fillGo.transform.SetParent(root);
            fillGo.transform.position = o + new Vector3(0.55f, 1.15f, -0.75f);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.range = 2.6f;
            fill.intensity = 0.9f;
            fill.color = new Color(1f, 0.86f, 0.72f);
            fill.shadows = LightShadows.None;

            Particles(root, "LampDust", o + new Vector3(0.1f, 1.3f, -0.1f), ctx.mats.motes, ps =>
            {
                var main = ps.main;
                main.startLifetime = 8f;
                main.startSpeed = 0.02f;
                main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.015f);
                main.startColor = new Color(1f, 0.9f, 0.7f, 0.5f);
                main.maxParticles = 120;
                main.prewarm = true;
                var emission = ps.emission;
                emission.rateOverTime = 14f;
                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(1.6f, 1.2f, 1.6f);
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = 0.04f;
                noise.frequency = 0.3f;
                Fade(ps);
            });

            Mark(ctx.anchors, "Mark_Interior", o + new Vector3(-0.35f, 0f, 0.3f), 180f);
            // The bag is laid down on its side on the crate (its pivot is the handles)
            Transform bagRest = new GameObject("Mark_BagRest").transform;
            bagRest.SetParent(ctx.anchors);
            bagRest.SetPositionAndRotation(o + new Vector3(-0.12f, 0.36f, -0.46f), Quaternion.Euler(0f, 90f, 0f) * Quaternion.Euler(-84f, 0f, 0f));
            Mark(ctx.anchors, "Mark_CoinStart", o + new Vector3(0.12f, 0.56f, -0.21f), 0f);
            Mark(ctx.anchors, "Mark_CoinDrop", o + new Vector3(0.14f, 0.46f, -0.24f), 0f);
            // Face height, so his face shows under the hat
            Shot(ctx.anchors, "B8_From", o + new Vector3(0.95f, 1.0f, -1.4f), o + new Vector3(-0.35f, 0.85f, 0.3f), 50f);
            Shot(ctx.anchors, "B8_To", o + new Vector3(0.8f, 0.98f, -1.28f), o + new Vector3(-0.32f, 0.85f, 0.28f), 45f);
            // The promise: close on his face as he looks up at the dawn in the window
            // From the window side: he turns towards the dawn, and it lights his face
            Shot(ctx.anchors, "B9_From", o + new Vector3(-1.3f, 1.02f, -0.6f), o + new Vector3(-0.4f, 1.2f, 0.3f), 38f);
            Shot(ctx.anchors, "B9_To", o + new Vector3(-1.15f, 1.04f, -0.43f), o + new Vector3(-0.4f, 1.22f, 0.3f), 34f);
            // The crate high in frame so the promise line falls on the dark floor below it
            Shot(ctx.anchors, "B8b_From", o + new Vector3(1.1f, 0.95f, -1.45f), o + new Vector3(0.05f, 0.5f, -0.1f), 42f);
            Shot(ctx.anchors, "B8b_To", o + new Vector3(0.98f, 0.92f, -1.32f), o + new Vector3(0.06f, 0.52f, -0.1f), 37f);
        }

        static Transform Coin(Transform parent, string name, Vector3 position, Material gold)
        {
            var coin = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            coin.name = name;
            Object.DestroyImmediate(coin.GetComponent<Collider>());
            coin.transform.SetParent(parent);
            coin.transform.position = position;
            coin.transform.localScale = new Vector3(0.03f, 0.002f, 0.03f);
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
            // The saved look, or the default worn-coat look on a first run: the same man the creator then shows.
            // The skinned body lets CharacterRigPoser walk, kneel and sit him.
            avatar.Configure(followSavedAppearance: true, ShadowCastingMode.On, riggedBody: true);
            EditorUtility.SetDirty(avatar);

            var poser = actorGo.AddComponent<CharacterRigPoser>();
            using (var so = new SerializedObject(poser))
            {
                so.FindProperty("avatar").objectReferenceValue = avatar;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            ctx.actor = actorGo.AddComponent<CinematicActor>();
            using (var so = new SerializedObject(ctx.actor))
            {
                so.FindProperty("poser").objectReferenceValue = poser;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            Transform start = ctx.anchors.Find("Mark_ShoreStart");
            if (start != null)
                actorGo.transform.SetPositionAndRotation(start.position, start.rotation);

            // Footprints in the sand (beach only) and soft footsteps everywhere
            var printsGo = new GameObject("Footprints");
            ctx.footprints = printsGo.AddComponent<FootprintTrail>();
            using (var so = new SerializedObject(ctx.footprints))
            {
                so.FindProperty("poser").objectReferenceValue = poser;
                so.FindProperty("material").objectReferenceValue = ctx.mats.footprint;
                so.FindProperty("xRange").vector2Value = new Vector2(-60f, 60f);
                AudioClip[] steps = LoadClips("Assets/UpcycleCity/Audio/SFX/Footsteps", "walk_");
                SerializedProperty list = so.FindProperty("steps");
                list.arraySize = steps.Length;
                for (int i = 0; i < steps.Length; i++)
                    list.GetArrayElementAtIndex(i).objectReferenceValue = steps[i];
                so.ApplyModifiedPropertiesWithoutUndo();
            }
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
                so.FindProperty("transitionVolume").objectReferenceValue = ctx.transitionVolume;
                so.FindProperty("memoryOnly").objectReferenceValue = ctx.memoryOnly;
                so.FindProperty("presentOnly").objectReferenceValue = ctx.presentOnly;
                so.FindProperty("caustics").objectReferenceValue = ctx.caustics;
                so.FindProperty("footprints").objectReferenceValue = ctx.footprints;
                SerializedProperty water = so.FindProperty("waterRenderers");
                water.arraySize = ctx.water.Count;
                for (int i = 0; i < ctx.water.Count; i++)
                    water.GetArrayElementAtIndex(i).objectReferenceValue = ctx.water[i];
                so.FindProperty("deadTurtle").objectReferenceValue = ctx.deadTurtle;
                so.FindProperty("shoreTurtle").objectReferenceValue = ctx.shoreTurtle;
                so.FindProperty("underwaterTurtle").objectReferenceValue = ctx.underwaterTurtle;
                so.FindProperty("tangledBag").objectReferenceValue = ctx.tangledBag;
                so.FindProperty("heldBag").objectReferenceValue = ctx.heldBag;
                so.FindProperty("sandPuff").objectReferenceValue = ctx.sandPuff;
                so.FindProperty("coinGlint").objectReferenceValue = ctx.coinGlint;
                so.FindProperty("windowLight").objectReferenceValue = ctx.windowLight;
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
