using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UpcycleCity.Character;
using UpcycleCity.Pollution;

namespace UpcycleCity.UI
{
    /// <summary>
    /// Live 3D backdrop for the Main Menu and Character Creator: a small Bab Bhar street corner (facades, municipal bin,
    /// lamp, bikes, planter, litter) built from the game's own art, lit like the Avenue, with the player's character
    /// standing in it and a slow cinematic camera drift. Lives in <c>Resources/MenuBackdrop.prefab</c> (built by
    /// <b>Upcycle City → UI → Build Menu Backdrop</b>) and installs itself when either scene loads, so the menu scenes
    /// and their builders stay untouched. Phone-light: static meshes, one shadowed sun, no post-processing of its own.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/UI/Menu Backdrop")]
    public sealed class MenuBackdrop : MonoBehaviour
    {
        public const string ResourcePath = "MenuBackdrop";
        static readonly string[] SceneNames = { "MainMenu", "CharacterCreator" };

        [Header("Atmosphere")]
        [SerializeField] Material skybox;
        [SerializeField, Range(0f, 1f)] float pollution = 0.22f;
        [SerializeField] Color ambientSky = new Color(0.62f, 0.64f, 0.66f);
        [SerializeField] Color ambientEquator = new Color(0.55f, 0.50f, 0.44f);
        [SerializeField] Color ambientGround = new Color(0.25f, 0.22f, 0.20f);
        [SerializeField] Color fogColor = new Color(0.74f, 0.70f, 0.63f);
        [SerializeField] float fogDensity = 0.0075f;

        [Header("Main Menu")]
        [Tooltip("The player's character standing in the set (Main Menu only; the creator brings its own).")]
        [SerializeField] GameObject menuCharacter;
        [SerializeField] Vector3 menuCameraPosition = new Vector3(-0.2f, 1.75f, 8.2f);
        [SerializeField] Vector3 menuCameraTarget = new Vector3(-1.7f, 1.45f, 0f);
        [SerializeField] float menuFieldOfView = 42f;
        [SerializeField] float driftYawDegrees = 4f;
        [SerializeField] float driftHeight = 0.12f;
        [SerializeField] float driftPeriod = 26f;

        [Header("Keep the character clear of the menu UI")]
        [Tooltip("USS class of the title column; the character stays left of it.")]
        [SerializeField] string titleColumnClass = "mm-brand";
        [Tooltip("USS class of the left card (Settings / Join / Room); the character stays right of it while shown.")]
        [SerializeField] string leftCardClass = "mm-panel";
        [SerializeField, Min(0.1f)] float framingSharpness = 4f;

        [Header("Character Creator")]
        [Tooltip("Studio objects of the creator scene hidden when the street set is shown.")]
        [SerializeField] string[] creatorObjectsToHide = { "Floor", "KeySpot", "Rim", "Fill" };


        Camera menuCamera;
        bool drifting;
        float driftTime;
        float yawCorrection;
        bool yawCorrectionSnapped;
        VisualElement titleColumn, leftCard;
        float nextUiLookup;

        /// <summary>Screen fraction (0..1) the Main Menu character is aimed at (tests / tooling).</summary>
        public float CharacterTargetFraction { get; private set; } = 0.35f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid() || System.Array.IndexOf(SceneNames, scene.name) < 0)
                return;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.GetComponent<MenuBackdrop>() != null)
                    return;
            }

            GameObject prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null)
                return;
            GameObject instance = Instantiate(prefab);
            instance.name = "MenuBackdrop";
            SceneManager.MoveGameObjectToScene(instance, scene);
            instance.GetComponent<MenuBackdrop>()?.Install(scene.name == "MainMenu");
        }

        /// <summary>Applies the atmosphere and camera for the given menu scene.</summary>
        public void Install(bool isMainMenu)
        {
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ambientSky;
            RenderSettings.ambientEquatorColor = ambientEquator;
            RenderSettings.ambientGroundColor = ambientGround;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = fogDensity;

            // Gameplay leaves its pollution grid/zones bound globally: show a calm, lightly hazy city instead
            PollutionShaderGlobals.SetLevel(pollution);
            PollutionShaderGlobals.ClearGrid();
            PollutionShaderGlobals.SetZones(System.Array.Empty<PollutionZone>());

            menuCamera = Camera.main;
            if (menuCamera != null)
            {
                menuCamera.clearFlags = skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                menuCamera.backgroundColor = fogColor;
                menuCamera.farClipPlane = Mathf.Max(menuCamera.farClipPlane, 120f);
            }

            if (isMainMenu)
            {
                if (menuCharacter != null)
                    menuCharacter.SetActive(true);
                if (menuCamera != null)
                {
                    menuCamera.fieldOfView = menuFieldOfView;
                    drifting = true;
                    ApplyDrift(0f);
                }
            }
            else
            {
                if (menuCharacter != null)
                    menuCharacter.SetActive(false);
                HideCreatorStudio();
                // The creator frames its character itself, in the gap between its card and title column
            }
        }

        void HideCreatorStudio()
        {
            var hide = new HashSet<string>(creatorObjectsToHide);
            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if (root != gameObject && hide.Contains(root.name))
                    root.SetActive(false);
            }
        }

        void LateUpdate()
        {
            if (!drifting || menuCamera == null)
                return;
            driftTime += Time.unscaledDeltaTime;
            ApplyDrift(driftTime);
            KeepCharacterClearOfUi();
        }

        /// <summary>Yaws the camera so the character is centred in the free strip between the UI columns.</summary>
        void KeepCharacterClearOfUi()
        {
            if (menuCharacter == null || !menuCharacter.activeInHierarchy)
                return;
            if ((titleColumn == null || titleColumn.panel == null) && Time.unscaledTime >= nextUiLookup)
            {
                nextUiLookup = Time.unscaledTime + 0.5f;
                foreach (UIDocument doc in FindObjectsByType<UIDocument>())
                {
                    VisualElement r = doc.rootVisualElement;
                    if (r == null)
                        continue;
                    titleColumn ??= r.Q(className: titleColumnClass);
                    leftCard ??= r.Q(className: leftCardClass);
                }
            }

            float left = 0.04f, right = 0.62f;
            if (titleColumn != null && titleColumn.panel != null)
            {
                float width = titleColumn.panel.visualTree.layout.width;
                if (width > 1f && !float.IsNaN(width))
                {
                    float margin = width * 0.015f;
                    if (titleColumn.resolvedStyle.display != DisplayStyle.None && titleColumn.worldBound.width > 0f)
                        right = (titleColumn.worldBound.xMin - margin) / width;
                    if (leftCard != null && leftCard.resolvedStyle.display != DisplayStyle.None && leftCard.worldBound.width > 0f)
                        left = (leftCard.worldBound.xMax + margin) / width;
                }
            }
            if (right <= left + 0.05f)
                right = left + 0.05f;
            float fraction = (left + right) * 0.5f;
            CharacterTargetFraction = fraction;

            Transform cam = menuCamera.transform;
            Vector3 local = cam.InverseTransformPoint(menuCharacter.transform.position + Vector3.up * 1f);
            if (local.z < 0.1f)
                return;
            float tanHalfH = Mathf.Tan(menuCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) * menuCamera.aspect;
            float current = Mathf.Atan(local.x / local.z) * Mathf.Rad2Deg;
            float desired = Mathf.Atan((fraction - 0.5f) * 2f * tanHalfH) * Mathf.Rad2Deg;
            float targetYaw = current - desired;
            yawCorrection = yawCorrectionSnapped
                ? Mathf.Lerp(yawCorrection, targetYaw, 1f - Mathf.Exp(-framingSharpness * Time.unscaledDeltaTime))
                : targetYaw;
            yawCorrectionSnapped = true;
            cam.rotation = Quaternion.AngleAxis(yawCorrection, Vector3.up) * cam.rotation;
        }

        void ApplyDrift(float t)
        {
            float phase = driftPeriod > 0.1f ? t * (2f * Mathf.PI / driftPeriod) : 0f;
            Vector3 offset = menuCameraPosition - menuCameraTarget;
            Quaternion yaw = Quaternion.Euler(0f, Mathf.Sin(phase) * driftYawDegrees, 0f);
            Vector3 pos = menuCameraTarget + yaw * offset + Vector3.up * (Mathf.Sin(phase * 0.5f) * driftHeight);
            Transform cam = menuCamera.transform;
            cam.position = transform.TransformPoint(pos);
            cam.rotation = Quaternion.LookRotation(transform.TransformPoint(menuCameraTarget) - cam.position, Vector3.up);
        }

        /// <summary>Camera framing used on the Main Menu (character on the left third, title on the right).</summary>
        public void SetMenuCamera(Vector3 position, Vector3 target, float fieldOfView)
        {
            menuCameraPosition = position;
            menuCameraTarget = target;
            menuFieldOfView = fieldOfView;
        }

        /// <summary>Editor builder: assigns the serialized references.</summary>
        public void Configure(Material sky, GameObject character)
        {
            skybox = sky;
            menuCharacter = character;
        }
    }
}
