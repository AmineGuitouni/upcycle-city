using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Leaves fading footprints where a <see cref="CharacterRigPoser"/> plants its feet, and optionally plays a
    /// footstep. A small pool of flat quads, so nothing is allocated while walking. Only prints on ground at or
    /// below <see cref="maxGroundHeight"/> (the beach sand), so it can stay on for the whole intro.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Footprint Trail")]
    public sealed class FootprintTrail : MonoBehaviour
    {
        [SerializeField] CharacterRigPoser poser;
        [SerializeField] Material material;
        [SerializeField, Range(4, 64)] int poolSize = 28;
        [SerializeField] Vector2 size = new Vector2(0.13f, 0.3f);
        [SerializeField, Min(0.5f)] float lifetime = 14f;
        [SerializeField, Range(0f, 1f)] float opacity = 0.55f;
        [Tooltip("Only print when the foot lands below this world height.")]
        [SerializeField] float maxGroundHeight = 0.05f;
        [Tooltip("Only print inside this world-space x range (the beach set).")]
        [SerializeField] Vector2 xRange = new Vector2(-100f, 100f);

        [Header("Footstep sound (optional)")]
        [SerializeField] AudioClip[] steps = new AudioClip[0];
        [SerializeField, Range(0f, 1f)] float stepVolume = 0.35f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        Transform[] prints;
        float[] born;
        MeshRenderer[] renderers;
        MaterialPropertyBlock block;
        int next;
        AudioSource source;

        /// <summary>Volume multiplier for steps (e.g. the director's sound bus).</summary>
        public float VolumeScale { get; set; } = 1f;

        void Awake()
        {
            prints = new Transform[poolSize];
            born = new float[poolSize];
            renderers = new MeshRenderer[poolSize];
            block = new MaterialPropertyBlock();
            for (int i = 0; i < poolSize; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "Footprint";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(transform, false);
                go.transform.localScale = new Vector3(size.x, size.y, 1f);
                renderers[i] = go.GetComponent<MeshRenderer>();
                renderers[i].sharedMaterial = material;
                renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                go.SetActive(false);
                prints[i] = go.transform;
                born[i] = -999f;
            }
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            var lowPass = gameObject.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 2400f; // soft, sandy steps
        }

        void OnEnable()
        {
            if (poser != null)
                poser.FootPlanted += OnFootPlanted;
        }

        void OnDisable()
        {
            if (poser != null)
                poser.FootPlanted -= OnFootPlanted;
        }

        void OnFootPlanted(CharacterRigPoser.Side side, Vector3 heel)
        {
            if (steps != null && steps.Length > 0)
            {
                AudioClip clip = steps[Random.Range(0, steps.Length)];
                if (clip != null)
                {
                    source.pitch = Random.Range(0.85f, 1.0f);
                    source.PlayOneShot(clip, stepVolume * VolumeScale * Random.Range(0.8f, 1f));
                }
            }

            if (heel.y > maxGroundHeight || heel.x < xRange.x || heel.x > xRange.y || prints == null)
                return;
            Transform print = prints[next];
            born[next] = Time.time;
            next = (next + 1) % prints.Length;
            float yaw = poser.transform.eulerAngles.y;
            // A touch forward of the heel, lying on the sand
            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            print.SetPositionAndRotation(heel + forward * 0.06f + Vector3.up * 0.004f, Quaternion.Euler(90f, yaw, 0f));
            print.gameObject.SetActive(true);
        }

        void Update()
        {
            if (prints == null)
                return;
            for (int i = 0; i < prints.Length; i++)
            {
                if (!prints[i].gameObject.activeSelf)
                    continue;
                float age = Time.time - born[i];
                if (age > lifetime)
                {
                    prints[i].gameObject.SetActive(false);
                    continue;
                }
                float alpha = opacity * Mathf.Clamp01(age / 0.3f) * (1f - Mathf.SmoothStep(0f, 1f, age / lifetime));
                block.SetColor(BaseColorId, new Color(0.18f, 0.15f, 0.12f, alpha));
                renderers[i].SetPropertyBlock(block);
            }
        }
    }
}
