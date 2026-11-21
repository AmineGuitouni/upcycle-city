using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UpcycleCity.Character
{
    /// <summary>
    /// Assembles a character from a <see cref="CharacterPartsLibrary"/> and a <see cref="CharacterAppearance"/>:
    /// spawns the body, hair and face parts as children and recolours them by material role
    /// (<c>skin_</c>, <c>hair_</c>, <c>stubble_</c>, <c>iris_</c>, <c>top_</c>) with property blocks, so no materials are created.
    /// Parts are only respawned when their choice changes; colour changes are free.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Character/Character Avatar")]
    public sealed class CharacterAvatar : MonoBehaviour
    {
        [SerializeField] CharacterPartsLibrary library;
        [Tooltip("Build from the saved appearance on Awake (for gameplay scenes).")]
        [SerializeField] bool loadSavedOnAwake;
        [SerializeField] ShadowCastingMode shadows = ShadowCastingMode.On;
        [Tooltip("How far stubble leans from skin colour towards hair colour.")]
        [SerializeField, Range(0f, 1f)] float stubbleStrength = 0.55f;

        readonly List<CharacterColorRoles.Slot> slots = new List<CharacterColorRoles.Slot>(16);
        MaterialPropertyBlock block;

        GameObject bodyInstance, hairInstance, faceInstance, facialHairInstance;
        int builtSex = -1, builtHair = -1, builtFace = -1, builtFacialHair = -1;
        CharacterAppearance appearance;
        bool hasAppearance;

        public CharacterPartsLibrary Library => library;
        public CharacterAppearance Appearance => appearance;

        public void SetLibrary(CharacterPartsLibrary partsLibrary) => library = partsLibrary;

        /// <summary>Editor setup: follow the saved look, and how the parts cast shadows.</summary>
        public void Configure(bool followSavedAppearance, ShadowCastingMode shadowMode)
        {
            loadSavedOnAwake = followSavedAppearance;
            shadows = shadowMode;
        }

        void Awake()
        {
            if (loadSavedOnAwake)
                Apply(CharacterAppearanceStore.Current);
        }

        void OnEnable()
        {
            if (loadSavedOnAwake)
                CharacterAppearanceStore.Changed += Apply;
        }

        void OnDisable() => CharacterAppearanceStore.Changed -= Apply;

        /// <summary>Shows the given look. Safe to call every time a single option changes.</summary>
        public void Apply(CharacterAppearance next)
        {
            if (library == null)
                return;

            next = library.Sanitize(next);
            bool partsChanged = false;

            if (builtSex != (int)next.sex || bodyInstance == null)
            {
                Replace(ref bodyInstance, library.BodyFor(next.sex), "Body");
                builtSex = (int)next.sex;
                partsChanged = true;
            }
            if (builtFace != next.eyeStyle)
            {
                Replace(ref faceInstance, PrefabAt(library.faceStyles, next.eyeStyle), "Face");
                builtFace = next.eyeStyle;
                partsChanged = true;
            }
            if (builtFacialHair != next.facialHair)
            {
                Replace(ref facialHairInstance, PrefabAt(library.facialHairStyles, next.facialHair), "FacialHair");
                builtFacialHair = next.facialHair;
                partsChanged = true;
            }
            if (builtHair != next.hairStyle)
            {
                Replace(ref hairInstance, PrefabAt(library.hairStyles, next.hairStyle), "Hair");
                builtHair = next.hairStyle;
                partsChanged = true;
            }

            if (partsChanged)
                CollectSlots();

            appearance = next;
            hasAppearance = true;
            Recolour();
        }

        static GameObject PrefabAt(CharacterPartsLibrary.Part[] parts, int index) =>
            index >= 0 && index < parts.Length ? parts[index].prefab : null;

        void Replace(ref GameObject instance, GameObject prefab, string label)
        {
            if (instance != null)
                DestroyPart(instance);
            instance = null;
            if (prefab == null)
                return;

            instance = Instantiate(prefab, transform, false);
            instance.name = $"{label}_{prefab.name}";
            int layer = gameObject.layer;
            foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = layer;
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
                r.shadowCastingMode = shadows;
        }

        static void DestroyPart(GameObject go)
        {
            if (Application.isPlaying)
                Destroy(go);
            else
                DestroyImmediate(go);
        }

        void CollectSlots()
        {
            slots.Clear();
            CharacterColorRoles.Collect(bodyInstance, slots);
            CharacterColorRoles.Collect(faceInstance, slots);
            CharacterColorRoles.Collect(facialHairInstance, slots);
            CharacterColorRoles.Collect(hairInstance, slots);
        }

        void Recolour()
        {
            if (!hasAppearance)
                return;
            block ??= new MaterialPropertyBlock();
            CharacterColorRoles.Apply(slots, library, appearance, block, stubbleStrength);
        }
    }
}
