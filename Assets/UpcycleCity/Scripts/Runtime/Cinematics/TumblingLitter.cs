using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Something light blown along by the wind: a plastic bag skipping and tumbling across the sand, lifting
    /// on gusts. It wraps back to its start when it leaves the area, so it keeps drifting through the shot.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Tumbling Litter")]
    public sealed class TumblingLitter : MonoBehaviour
    {
        [SerializeField] Vector3 wind = new Vector3(1.6f, 0f, 0.35f);
        [SerializeField, Min(0f)] float gustLift = 0.6f;
        [SerializeField, Min(1f)] float travel = 30f;
        [SerializeField] float groundHeight = 0.05f;

        Vector3 start;
        float seed;

        void Awake()
        {
            start = transform.position;
            seed = Random.value * 50f;
        }

        void Update()
        {
            float t = Time.time + seed;
            float gust = Mathf.PerlinNoise(t * 0.35f, 0.5f);
            Vector3 p = transform.position + wind * (0.4f + gust) * Time.deltaTime;
            // Skips and hops: lifted on gusts, falling back to the sand
            float hop = Mathf.Abs(Mathf.Sin(t * 2.1f)) * gustLift * gust * gust;
            p.y = groundHeight + hop;
            if ((p - start).magnitude > travel)
                p = start;
            transform.position = p;
            transform.rotation = Quaternion.Euler(Mathf.Sin(t * 1.3f) * 70f, t * 90f, Mathf.Cos(t * 1.7f) * 50f);
        }
    }
}
