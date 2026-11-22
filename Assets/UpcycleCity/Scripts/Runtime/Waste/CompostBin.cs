using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// Brown compost bin at the drop-off: [E] empties the organic waste from the player's <see cref="RecyclingBag"/>
    /// into the Team Material Stock as <c>organic_waste</c>, the feedstock for fertiliser later in the game.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Waste/Compost Bin")]
    public sealed class CompostBin : MonoBehaviour, IInteractable, ISyncedStateProvider
    {
        [Tooltip("Colour the bin's model is tinted (compost brown).")]
        [SerializeField] Color tint = new Color(0.47f, 0.34f, 0.18f);

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        int delivered;
        string syncKey;

        public int Delivered => delivered;
        public Transform Transform => transform;
        public string SyncKey => syncKey ??= SyncedStateRegistry.BuildSpatialKey("compost", gameObject.name, transform.position);

        public string InteractionPrompt
        {
            get
            {
                int organic = OrganicInBag();
                return organic > 0
                    ? $"[E] Empty {organic} organic waste into the COMPOST"
                    : "COMPOST · bring organic waste from street bins";
            }
        }

        static int OrganicInBag()
        {
            return RecyclingBagService.LocalCountMatching(true, WasteStream.Plastic);
        }

        void Awake()
        {
            var block = new MaterialPropertyBlock();
            foreach (Renderer r in GetComponentsInChildren<Renderer>())
            {
                r.GetPropertyBlock(block);
                block.SetColor(BaseColorId, tint);
                block.SetColor(ColorId, tint);
                r.SetPropertyBlock(block);
            }
        }

        void OnEnable() => SyncedStateRegistry.Register(this);

        void OnDisable() => SyncedStateRegistry.Unregister(this);

        public bool CanInteract(PlayerInteractor interactor) => OrganicInBag() > 0;

        public void Interact(PlayerInteractor interactor)
        {
            if (CanInteract(interactor))
                RecyclingBagService.RequestDeposit(this);
        }

        /// <summary>Host: counts a delivery.</summary>
        public void RecordDeposit(int count) => delivered += Mathf.Max(0, count);

        public SyncedStateRecord CaptureSyncState() => new SyncedStateRecord { i0 = delivered };

        public void ApplySyncState(in SyncedStateRecord state) => delivered = Mathf.Max(0, state.i0);
    }
}
