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
        [Tooltip("Use the skinned body (with a skeleton) so code can pose it, e.g. the intro cinematic. " +
                 "The face, hair and beard then follow the Head bone.")]
        [SerializeField] bool useRiggedBody;

        readonly List<CharacterColorRoles.Slot> slots = new List<CharacterColorRoles.Slot>(16);
        MaterialPropertyBlock block;

        GameObject bodyInstance, hairInstance, faceInstance, facialHairInstance;
        int builtSex = -1, builtHair = -1, builtFace = -1, builtFacialHair = -1;
        CharacterAppearance appearance;
        bool hasAppearance;

        Transform headBone;
        Matrix4x4 headRestInverse = Matrix4x4.identity; // avatar space -> head bone space, at rest

        public CharacterPartsLibrary Library => library;
        public CharacterAppearance Appearance => appearance;
        /// <summary>The spawned body (with its bones when <see cref="UseRiggedBody"/> is on).</summary>
        public Transform BodyInstance => bodyInstance != null ? bodyInstance.transform : null;
        public bool UseRiggedBody => useRiggedBody;
        /// <summary>Raised after the body mesh is replaced, so anything holding its bones can rebind.</summary>
        public event System.Action BodyRebuilt;

        public void SetLibrary(CharacterPartsLibrary partsLibrary) => library = partsLibrary;

        /// <summary>Editor setup: follow the saved look, and how the parts cast shadows.</summary>
        public void Configure(bool followSavedAppearance, ShadowCastingMode shadowMode)
        {
            loadSavedOnAwake = followSavedAppearance;
            shadows = shadowMode;
        }

        /// <summary>Editor setup: as above, choosing the skinned body.</summary>
        public void Configure(bool followSavedAppearance, ShadowCastingMode shadowMode, bool riggedBody)
        {
            Configure(followSavedAppearance, shadowMode);
            useRiggedBody = riggedBody;
        }

        /// <summary>Runtime helper: stops or starts following local <see cref="CharacterAppearanceStore"/> edits.</summary>
        public void SetFollowSavedAppearance(bool follow)
        {
            loadSavedOnAwake = follow;
            CharacterAppearanceStore.Changed -= Apply;
            if (follow && isActiveAndEnabled)
                CharacterAppearanceStore.Changed += Apply;
        }

        /// <summary>Runtime helper: updates shadow casting mode on this avatar and all currently spawned body/hair parts.</summary>
        public void SetShadowCastingMode(ShadowCastingMode shadowMode)
        {
            shadows = shadowMode;
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].shadowCastingMode = shadows;
            }
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

            bool bodyChanged = false;
            if (builtSex != (int)next.sex || bodyInstance == null)
            {
                Replace(ref bodyInstance, library.BodyFor(next.sex, useRiggedBody), "Body");
                builtSex = (int)next.sex;
                partsChanged = true;
                bodyChanged = true;
                FindHeadBone();
                // Head parts already spawned move onto the new body's head
                AttachToHead(faceInstance);
                AttachToHead(facialHairInstance);
                AttachToHead(hairInstance);
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
            if (bodyChanged)
                BodyRebuilt?.Invoke();
        }

        void FindHeadBone()
        {
            headBone = null;
            headRestInverse = Matrix4x4.identity;
            if (bodyInstance == null)
                return;
            foreach (Transform t in bodyInstance.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Head" && t != bodyInstance.transform)
                {
                    headBone = t;
                    break;
                }
            }
            if (headBone != null)
            {
                // The body was just spawned, so its bones are at rest: remember the head's rest pose in avatar space
                Matrix4x4 headInAvatar = transform.worldToLocalMatrix * headBone.localToWorldMatrix;
                headRestInverse = headInAvatar.inverse;
            }
        }

        /// <summary>
        /// Head parts are modelled in avatar space around the shared head. On a skinned body they are parented to
        /// the Head bone at the offset that puts them where they belong when the head is at rest.
        /// </summary>
        void AttachToHead(GameObject part)
        {
            if (part == null)
                return;
            if (headBone == null)
            {
                part.transform.SetParent(transform, false);
                part.transform.localPosition = Vector3.zero;
                part.transform.localRotation = Quaternion.identity;
                return;
            }
            part.transform.SetParent(headBone, false);
            part.transform.localPosition = headRestInverse.MultiplyPoint3x4(Vector3.zero);
            part.transform.localRotation = headRestInverse.rotation;
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
            if (label != "Body")
                AttachToHead(instance);
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
