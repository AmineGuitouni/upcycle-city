using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// A gull (Art/UpcycleProps/Intro/gull.fbx: Body, Wing_L, Wing_R) circling in the clean sky of the memory:
    /// bursts of flapping between long glides, banking into its circle, and now and then a cry.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Gull")]
    public sealed class Gull : MonoBehaviour
    {
        [SerializeField] Transform model;
        [SerializeField] Vector3 center;
        [SerializeField] Vector2 radii = new Vector2(14f, 8f);
        [SerializeField] float height = 9f;
        [SerializeField] float degreesPerSecond = 14f;
        [SerializeField] float scale = 1.6f;

        [Header("Cries (optional)")]
        [SerializeField] AudioClip[] cries = new AudioClip[0];
        [SerializeField] Vector2 cryInterval = new Vector2(4f, 9f);
        [SerializeField, Range(0f, 1f)] float cryVolume = 0.5f;

        Transform wingL, wingR;
        Quaternion wingLRest, wingRRest;
        float angle, flap, nextCry;
        AudioSource source;

        public float VolumeScale { get; set; } = 1f;

        void Awake()
        {
            if (model == null && transform.childCount > 0)
                model = transform.GetChild(0);
            if (model != null)
            {
                model.localScale = Vector3.one * scale;
                foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Wing_L") { wingL = t; wingLRest = t.localRotation; }
                    if (t.name == "Wing_R") { wingR = t; wingRRest = t.localRotation; }
                }
            }
            angle = Random.value * 360f;
            nextCry = Time.time + Random.Range(1f, cryInterval.y);
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0.6f;
            source.minDistance = 6f;
            source.maxDistance = 60f;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            angle += degreesPerSecond * dt;
            float a = angle * Mathf.Deg2Rad;
            Vector3 pos = center + new Vector3(Mathf.Cos(a) * radii.x, height + Mathf.Sin(a * 2.3f) * 0.8f, Mathf.Sin(a) * radii.y);
            float a2 = a + Mathf.Sign(degreesPerSecond) * 0.05f;
            Vector3 ahead = center + new Vector3(Mathf.Cos(a2) * radii.x, height + Mathf.Sin(a2 * 2.3f) * 0.8f, Mathf.Sin(a2) * radii.y);
            float bank = -Mathf.Sign(degreesPerSecond) * 22f;
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(ahead - pos, Vector3.up) * Quaternion.Euler(0f, 0f, bank));

            // Flap in bursts, then glide with the wings held slightly up
            float burst = Mathf.Sin(Time.time * 0.6f + angle * 0.01f);
            float flapping = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.6f, burst));
            flap += dt * Mathf.Lerp(1f, 9f, flapping);
            float wing = Mathf.Lerp(8f, Mathf.Sin(flap) * 38f, flapping);
            if (wingL != null) wingL.localRotation = wingLRest * Quaternion.Euler(0f, 0f, wing);
            if (wingR != null) wingR.localRotation = wingRRest * Quaternion.Euler(0f, 0f, -wing);

            if (cries != null && cries.Length > 0 && Time.time >= nextCry)
            {
                AudioClip clip = cries[Random.Range(0, cries.Length)];
                if (clip != null)
                    source.PlayOneShot(clip, cryVolume * VolumeScale);
                nextCry = Time.time + Random.Range(cryInterval.x, cryInterval.y);
            }
        }
    }
}
