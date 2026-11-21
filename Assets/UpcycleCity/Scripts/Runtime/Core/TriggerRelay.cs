using UnityEngine;

namespace UpcycleCity.Core
{
    /// <summary>Receives trigger events from child catch zones through <see cref="TriggerRelay"/>.</summary>
    public interface ITriggerReceiver
    {
        void OnRelayedTriggerEnter(Collider other);
    }

    /// <summary>
    /// Forwards <c>OnTriggerEnter</c> from a child trigger collider to the nearest <see cref="ITriggerReceiver"/>
    /// above it. Unity only sends trigger messages to the collider's own object (or its Rigidbody's), so a bin's
    /// catch zone on a child would otherwise never reach the bin. Receivers add relays to their own child
    /// triggers with <see cref="AttachToChildTriggers"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Core/Trigger Relay")]
    public sealed class TriggerRelay : MonoBehaviour
    {
        ITriggerReceiver receiver;

        void Awake() => receiver = transform.parent != null ? transform.parent.GetComponentInParent<ITriggerReceiver>() : null;

        void OnTriggerEnter(Collider other) => receiver?.OnRelayedTriggerEnter(other);

        /// <summary>Adds a relay to every child of <paramref name="owner"/> that has a trigger collider.</summary>
        public static void AttachToChildTriggers(Component owner)
        {
            foreach (Collider collider in owner.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger && collider.gameObject != owner.gameObject && collider.GetComponent<TriggerRelay>() == null)
                    collider.gameObject.AddComponent<TriggerRelay>();
            }
        }
    }
}
