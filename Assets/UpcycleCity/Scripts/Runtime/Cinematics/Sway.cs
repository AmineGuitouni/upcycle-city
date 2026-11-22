using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>Slow sway around the pivot, for palms in the breeze and seaweed in the current.</summary>
    [AddComponentMenu("Upcycle City/Cinematics/Sway")]
    public sealed class Sway : MonoBehaviour
    {
        [SerializeField] float degrees = 2f;
        [SerializeField, Min(0.01f)] float frequency = 0.25f;
        [Tooltip("World direction the sway leans towards (the wind or the current).")]
        [SerializeField] Vector3 direction = new Vector3(1f, 0f, 0.3f);

        Quaternion rest;
        float phase;

        void Awake()
        {
            rest = transform.rotation;
            phase = Random.value * 20f;
        }

        void Update()
        {
            float t = Time.time * frequency * Mathf.PI * 2f + phase;
            float lean = (Mathf.Sin(t) * 0.7f + Mathf.Sin(t * 2.3f) * 0.3f) * degrees;
            Vector3 axis = Vector3.Cross(Vector3.up, direction.sqrMagnitude > 0f ? direction.normalized : Vector3.right);
            transform.rotation = Quaternion.AngleAxis(lean, axis) * rest;
        }
    }
}
