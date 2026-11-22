using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// A physical color-coded sorting bin/crate. Accepts TrashItems dropped or thrown into its
    /// catch trigger (or deposited via E while holding an item), tracks sorting Purity %, and pays coins.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Waste/Sorting Crate")]
    public sealed class SortingCrate : MonoBehaviour, IInteractable, ITriggerReceiver, ISyncedStateProvider
    {
        [SerializeField] WasteStream acceptedStream = WasteStream.Plastic;
        [SerializeField] bool payDirectlyOnSort = true;

        int correctCount;
        int wrongCount;
        string syncKey;

        public WasteStream AcceptedStream => acceptedStream;
        public int CorrectCount => correctCount;
        public int WrongCount => wrongCount;
        public int TotalCount => correctCount + wrongCount;
        public float Purity => TotalCount == 0 ? 1f : (float)correctCount / TotalCount;

        public string SyncKey => syncKey ??= SyncedStateRegistry.BuildSpatialKey("crate", gameObject.name, transform.position);

        public static event Action<SortingCrate, TrashItem, bool, int> ItemSorted;

        public Transform Transform => transform;
        public string InteractionPrompt
        {
            get
            {
                PlayerInteractor local = PlayerContext.ActiveInteractor;
                bool holding = local != null && local.Grabber != null && local.Grabber.IsHolding;
                int inBag = holding ? 0 : MatchingInBag();
                return inBag > 0
                    ? $"[E] Empty {inBag} {WasteStreamPalette.LabelFor(acceptedStream)} from your bag"
                    : $"[E] Deposit into {WasteStreamPalette.LabelFor(acceptedStream)} ({Mathf.RoundToInt(Purity * 100f)}% Purity)";
            }
        }

        /// <summary>Finds in the local player's <see cref="RecyclingBag"/> that belong in this bin.</summary>
        int MatchingInBag()
        {
            if (acceptedStream == WasteStream.BulkyUpcyclable)
                return 0;
            RecyclingBag bag = RecyclingBagService.LocalBag;
            BinLootTable table = BinLootTable.Active;
            return bag != null && table != null ? bag.CountMatching(table, false, acceptedStream) : 0;
        }

        /// <summary>Host: counts finds emptied from a Recycling Bag as correctly sorted (they were picked by hand).</summary>
        public void RecordBagDeposit(int count) => correctCount += Mathf.Max(0, count);

        public void Configure(WasteStream stream, bool payOnSort = true)
        {
            acceptedStream = stream;
            payDirectlyOnSort = payOnSort;
        }

        public bool CanInteract(PlayerInteractor interactor)
        {
            if (interactor == null || interactor.Grabber == null)
                return false;
            if (interactor.Grabber.IsHolding)
                return interactor.Grabber.HeldObject is TrashItem;
            return MatchingInBag() > 0;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor))
                return;

            if (!interactor.Grabber.IsHolding)
            {
                RecyclingBagService.RequestDeposit(this);
                return;
            }

            TrashItem item = interactor.Grabber.ReleaseForDeposit() as TrashItem;
            if (item != null)
                GameAuthority.Current.RequestSortTrash(this, item, interactor);
        }

        // The catch zone is a child trigger: relay its events here
        void Awake() => TriggerRelay.AttachToChildTriggers(this);

        void OnEnable() => SyncedStateRegistry.Register(this);

        void OnDisable() => SyncedStateRegistry.Unregister(this);

        public void OnRelayedTriggerEnter(Collider other) => OnTriggerEnter(other);

        void OnTriggerEnter(Collider other)
        {
            if (!GameAuthority.IsAuthoritative)
                return;

            TrashItem item = other.GetComponentInParent<TrashItem>();
            if (item == null || item.IsDisposed || item.IsHeld || item.IsContained)
                return;

            GameAuthority.Current.RequestSortTrash(this, item, null);
        }

        /// <summary>Executes authoritative sorting logic (invoked directly in solo or on the host in co-op).</summary>
        public bool ExecuteAuthoritativeSort(
            TrashItem item,
            PlayerInventory inventory,
            PlayerInteractor interactor,
            out bool match,
            out int payout,
            out string message,
            out Color feedbackColor,
            bool creditLocalInventory = true,
            bool emitLocalFeedback = true)
        {
            match = false;
            payout = 0;
            message = string.Empty;
            feedbackColor = Color.white;

            if (item == null || item.IsDisposed)
                return false;

            match = item.Stream == acceptedStream;
            if (match)
                correctCount++;
            else
                wrongCount++;

            if (payDirectlyOnSort && match)
            {
                float multiplier = EcosystemBalance.Active != null ? EcosystemBalance.Active.sortingPayout : 1f;
                payout = Mathf.Max(1, Mathf.RoundToInt(item.BaseCoinValue * Purity * multiplier));
                if (creditLocalInventory && inventory != null)
                    inventory.AddCoins(payout);
            }

            feedbackColor = match ? WasteStreamPalette.ColorFor(acceptedStream) : new Color(0.85f, 0.25f, 0.22f);
            message = match
                ? $"+1 {WasteStreamPalette.LabelFor(acceptedStream)} · +{payout} Coins ({Mathf.RoundToInt(Purity * 100f)}% Purity)"
                : $"WRONG BIN! {item.DisplayName} is {WasteStreamPalette.LabelFor(item.Stream)} · Purity {Mathf.RoundToInt(Purity * 100f)}%";

            if (emitLocalFeedback)
            {
                interactor?.NotifyFeedback(message, feedbackColor);
                PlayerContext.ActiveFeedback?.Emit(
                    match ? PlayerFeedbackEvent.SortCorrect : PlayerFeedbackEvent.SortWrong,
                    transform.position,
                    1f,
                    message,
                    feedbackColor,
                    FeedbackSignal.EncodeMaterial(item.Stream, item.IsBulky));
            }

            ItemSorted?.Invoke(this, item, match, payout);
            item.Dispose();
            return true;
        }

        /// <summary>Convenience overload used by <see cref="LocalGameAuthority"/>.</summary>
        public void ExecuteAuthoritativeSort(TrashItem item, PlayerInventory inventory, PlayerInteractor interactor)
        {
            ExecuteAuthoritativeSort(item, inventory, interactor, out _, out _, out _, out _, creditLocalInventory: true, emitLocalFeedback: true);
        }

        public SyncedStateRecord CaptureSyncState() => new SyncedStateRecord
        {
            i0 = correctCount,
            i1 = wrongCount,
        };

        public void ApplySyncState(in SyncedStateRecord state)
        {
            correctCount = Mathf.Max(0, state.i0);
            wrongCount = Mathf.Max(0, state.i1);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ItemSorted = null;
    }
}
