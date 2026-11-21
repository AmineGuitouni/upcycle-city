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
    public sealed class SortingCrate : MonoBehaviour, IInteractable, ITriggerReceiver
    {
        [SerializeField] WasteStream acceptedStream = WasteStream.Plastic;
        [SerializeField] bool payDirectlyOnSort = true;

        int correctCount;
        int wrongCount;

        public WasteStream AcceptedStream => acceptedStream;
        public int CorrectCount => correctCount;
        public int WrongCount => wrongCount;
        public int TotalCount => correctCount + wrongCount;
        public float Purity => TotalCount == 0 ? 1f : (float)correctCount / TotalCount;

        public static event Action<SortingCrate, TrashItem, bool, int> ItemSorted;

        public Transform Transform => transform;
        public string InteractionPrompt => $"[E] Deposit into {WasteStreamPalette.LabelFor(acceptedStream)} ({Mathf.RoundToInt(Purity * 100f)}% Purity)";

        public void Configure(WasteStream stream, bool payOnSort = true)
        {
            acceptedStream = stream;
            payDirectlyOnSort = payOnSort;
        }

        public bool CanInteract(PlayerInteractor interactor)
        {
            return interactor != null
                && interactor.Grabber != null
                && interactor.Grabber.IsHolding
                && interactor.Grabber.HeldObject is TrashItem;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor))
                return;

            TrashItem item = interactor.Grabber.ReleaseForDeposit() as TrashItem;
            if (item != null)
                AcceptItem(item, interactor.Inventory, interactor);
        }

        // The catch zone is a child trigger: relay its events here
        void Awake() => TriggerRelay.AttachToChildTriggers(this);

        public void OnRelayedTriggerEnter(Collider other) => OnTriggerEnter(other);

        void OnTriggerEnter(Collider other)
        {
            TrashItem item = other.GetComponentInParent<TrashItem>();
            if (item == null || item.IsDisposed || item.IsHeld || item.IsContained)
                return;

            AcceptItem(item, PlayerContext.ActiveInventory, PlayerContext.ActiveInteractor);
        }

        void AcceptItem(TrashItem item, PlayerInventory inventory, PlayerInteractor interactor)
        {
            if (item == null || item.IsDisposed)
                return;

            bool match = item.Stream == acceptedStream;
            if (match)
                correctCount++;
            else
                wrongCount++;

            int payout = 0;
            if (payDirectlyOnSort && match)
            {
                float multiplier = EcosystemBalance.Active != null ? EcosystemBalance.Active.sortingPayout : 1f;
                payout = Mathf.Max(1, Mathf.RoundToInt(item.BaseCoinValue * Purity * multiplier));
                if (inventory != null)
                    inventory.AddCoins(payout);
            }

            Color feedbackColor = match ? WasteStreamPalette.ColorFor(acceptedStream) : new Color(0.85f, 0.25f, 0.22f);
            string message = match
                ? $"+1 {WasteStreamPalette.LabelFor(acceptedStream)} · +{payout} Coins ({Mathf.RoundToInt(Purity * 100f)}% Purity)"
                : $"WRONG BIN! {item.DisplayName} is {WasteStreamPalette.LabelFor(item.Stream)} · Purity {Mathf.RoundToInt(Purity * 100f)}%";

            interactor?.NotifyFeedback(message, feedbackColor);
            PlayerContext.ActiveFeedback?.Emit(
                match ? PlayerFeedbackEvent.SortCorrect : PlayerFeedbackEvent.SortWrong,
                transform.position,
                1f,
                message,
                feedbackColor);

            ItemSorted?.Invoke(this, item, match, payout);
            item.Dispose();
        }
    }
}
