using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.Player;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// A physical piece of trash in the world implementing <see cref="IGrabbable"/>.
    /// Trash NEVER enters the player inventory; it is grabbed one at a time via physics
    /// and carried/thrown into bins, hoppers, or workbenches.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("Upcycle City/Waste/Trash Item")]
    public sealed class TrashItem : MonoBehaviour, IGrabbable
    {
        [Header("Identity")]
        [SerializeField] string displayName = "Litter";
        [SerializeField] WasteStream stream = WasteStream.Plastic;
        [SerializeField] WasteSourceKind source = WasteSourceKind.Initial;

        [Header("Simulation & Feel")]
        [Tooltip("How much this item contributes to local zone pollution while lying on the ground.")]
        [SerializeField, Min(0.05f)] float pollutionWeight = 1f;

        [Tooltip("Base coin value when sorted cleanly at 100% purity.")]
        [SerializeField, Min(1)] int baseCoinValue = 6;

        [Tooltip("Movement speed multiplier while carrying this item (lower for bulky objects).")]
        [SerializeField, Range(0.4f, 1f)] float carrySpeedMultiplier = 1f;

        [Tooltip("Extra distance in front of the camera when held, so large objects don't block the view.")]
        [SerializeField, Range(0f, 1.2f)] float holdDistanceOffset;

        [Tooltip("If true, player cannot sprint while carrying this item.")]
        [SerializeField] bool isBulky;

        Rigidbody body;
        Collider[] colliders;
        int restLayer = GameLayers.Grabbable;

        public static event Action<TrashItem> Spawned;
        public static event Action<TrashItem> Removed;

        public Transform Transform => transform;
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? WasteStreamPalette.LabelFor(stream) : displayName;
        public string GrabPrompt => $"[LMB / E] Grab {DisplayName} ({WasteStreamPalette.LabelFor(stream)})";
        public WasteStream Stream => stream;
        public WasteSourceKind Source => source;
        public float PollutionWeight => pollutionWeight;
        public int BaseCoinValue => baseCoinValue;
        public float CarrySpeedMultiplier => carrySpeedMultiplier;
        public float HoldDistanceOffset => holdDistanceOffset;
        public bool IsBulky => isBulky;
        public Rigidbody Body => body;
        public bool IsHeld { get; private set; }
        public bool IsDisposed { get; private set; }
        /// <summary>
        /// Collected into a container (the shopping cart's basket): it no longer lies in the street, so it doesn't
        /// pollute its district and isn't taken by sorting crates or the RÉCUP scale it rolls past.
        /// </summary>
        public bool IsContained { get; private set; }
        public float SpawnTime { get; private set; }

        public void Configure(
            string itemName,
            WasteStream wasteStream,
            WasteSourceKind wasteSource = WasteSourceKind.Initial,
            float weight = 1f,
            int coinValue = 6,
            bool bulky = false,
            float carrySpeed = 1f,
            float holdOffset = 0f)
        {
            displayName = itemName;
            stream = wasteStream;
            source = wasteSource;
            pollutionWeight = Mathf.Max(0.05f, weight);
            baseCoinValue = Mathf.Max(1, coinValue);
            isBulky = bulky;
            carrySpeedMultiplier = Mathf.Clamp(carrySpeed, 0.4f, 1f);
            holdDistanceOffset = Mathf.Clamp(holdOffset, 0f, 1.2f);
        }

        void Awake()
        {
            body = GetComponent<Rigidbody>();
            colliders = GetComponentsInChildren<Collider>(true);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            SpawnTime = Time.time;

            if (gameObject.layer == GameLayers.Default)
                GameLayers.SetLayerRecursively(gameObject, GameLayers.Grabbable);
            restLayer = gameObject.layer;

            if (CompareTag(GameTags.Untagged))
                gameObject.tag = GameTags.Trash;
        }

        void OnEnable()
        {
            IsDisposed = false;
            Spawned?.Invoke(this);
        }

        void OnDisable()
        {
            Removed?.Invoke(this);
        }

        public bool CanGrab(PhysicsGrabController grabber)
        {
            return !IsDisposed && !IsHeld && body != null;   // contained items can be taken back out of the cart
        }

        public void OnGrabbed(PhysicsGrabController grabber)
        {
            IsHeld = true;
            IsContained = false;
            restLayer = gameObject.layer == GameLayers.HeldObject ? GameLayers.Grabbable : gameObject.layer;
            GameLayers.SetLayerRecursively(gameObject, GameLayers.HeldObject);

            body.useGravity = false;
            body.linearDamping = 4f;
            body.angularDamping = 6f;
        }

        public void OnReleased(PhysicsGrabController grabber, Vector3 releaseVelocity, bool thrown)
        {
            IsHeld = false;
            GameLayers.SetLayerRecursively(gameObject, restLayer);

            body.useGravity = true;
            body.linearDamping = 0.25f;
            body.angularDamping = 0.5f;
            body.linearVelocity = releaseVelocity;
        }

        /// <summary>Marks the item as stowed in (or taken out of) a container such as the shopping cart.</summary>
        public void SetContained(bool contained) => IsContained = contained;

        public void SetIgnoreCollision(Collider other, bool ignore)
        {
            if (other == null || colliders == null)
                return;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null && !colliders[i].isTrigger)
                    Physics.IgnoreCollision(colliders[i], other, ignore);
            }
        }

        /// <summary>Called when sorted into a bin or consumed by a machine.</summary>
        public void Dispose()
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            IsHeld = false;
            IsContained = false;
            Destroy(gameObject);
        }
    }
}
