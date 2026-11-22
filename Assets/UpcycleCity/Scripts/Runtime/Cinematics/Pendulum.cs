using UnityEngine;

namespace UpcycleCity.Cinematics
{
    /// <summary>
    /// Swings a transform gently around its rest rotation: a hanging lamp drifting on its cable (so the
    /// shadows move), a bag swinging from a hand. Two detuned sines on two axes so it never looks mechanical.
    /// </summary>
    [AddComponentMenu("Upcycle City/Cinematics/Pendulum")]
    [DefaultExecutionOrder(150)] // after CharacterRigPoser has posed the hand it may hang from
    public sealed class Pendulum : MonoBehaviour
    {
        [SerializeField] Vector2 amplitudeDegrees = new Vector2(4f, 2.5f);
        [SerializeField, Min(0.01f)] float frequency = 0.35f;
        [Tooltip("Extra swing from the parent's movement (e.g. a walking hand).")]
        [SerializeField, Min(0f)] float inertia = 0f;
        [Tooltip("Hang straight down under gravity whatever the parent's rotation (a bag held in a hand), " +
                 "turning only with the parent's heading.")]
        [SerializeField] bool hangUpright;

        Quaternion rest;
        float phase;
        Vector3 lastPosition;
        Vector2 kick;

        void Awake()
        {
            rest = transform.localRotation;
            phase = Random.value * 10f;
            lastPosition = transform.position;
        }

        /// <summary>Re-reads the rest rotation, e.g. after re-parenting.</summary>
        public void ResetRest()
        {
            rest = transform.localRotation;
            lastPosition = transform.position;
            kick = Vector2.zero;
        }

        void LateUpdate()
        {
            float t = Time.time * frequency * Mathf.PI * 2f + phase;
            float x = Mathf.Sin(t) * amplitudeDegrees.x + Mathf.Sin(t * 0.53f) * amplitudeDegrees.x * 0.4f;
            float z = Mathf.Sin(t * 0.81f + 1.3f) * amplitudeDegrees.y;

            if (inertia > 0f && Time.deltaTime > 0f)
            {
                Vector3 velocity = (transform.position - lastPosition) / Time.deltaTime;
                Vector3 local = transform.parent != null ? transform.parent.InverseTransformDirection(velocity) : velocity;
                kick = Vector2.Lerp(kick, new Vector2(-local.z, local.x) * inertia, 1f - Mathf.Exp(-4f * Time.deltaTime));
            }
            lastPosition = transform.position;
            if (hangUpright)
            {
                float heading = transform.parent != null ? transform.parent.eulerAngles.y : 0f;
                Transform root = transform.root;
                if (root != null)
                    heading = root.eulerAngles.y;
                transform.rotation = Quaternion.Euler(0f, heading, 0f) * Quaternion.Euler(x + kick.x, 0f, z + kick.y);
                return;
            }
            transform.localRotation = rest * Quaternion.Euler(x + kick.x, 0f, z + kick.y);
        }
    }
}
