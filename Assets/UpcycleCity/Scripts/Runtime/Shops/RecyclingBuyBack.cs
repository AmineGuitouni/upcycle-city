using System;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Shops
{
    /// <summary>
    /// The RÉCUP weighing platform: the city's buy-back point. Trash dropped or thrown onto the scale, or
    /// placed on it with E, is bought for coins. Recyclers pay more for sorted materials than the street
    /// bins do, by stream (cans and bulky items are worth the most), so cleaning up pays for the shops.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Shops/Recycling Buy-Back")]
    public sealed class RecyclingBuyBack : MonoBehaviour, IInteractable, ITriggerReceiver
    {
        [Serializable]
        public struct Rates
        {
            [Min(0f)] public float paper;
            [Min(0f)] public float plastic;
            [Min(0f)] public float glass;
            [Min(0f)] public float cans;
            [Min(0f)] public float bulky;

            public static Rates Default => new Rates { paper = 1.3f, plastic = 1.5f, glass = 1.6f, cans = 1.8f, bulky = 2.0f };

            public float For(WasteStream stream)
            {
                switch (stream)
                {
                    case WasteStream.Paper: return paper;
                    case WasteStream.Plastic: return plastic;
                    case WasteStream.Glass: return glass;
                    case WasteStream.Cans: return cans;
                    default: return bulky;
                }
            }
        }

        [SerializeField] string label = "RÉCUP scale";
        [Tooltip("Coins paid = item base value × the rate of its stream.")]
        [SerializeField] Rates rates = Rates.Default;

        int itemsBought;
        int coinsPaid;

        /// <summary>(buy-back, item, coins paid) for HUDs and statistics.</summary>
        public static event Action<RecyclingBuyBack, TrashItem, int> ItemBought;

        public Rates PriceRates => rates;
        public int ItemsBought => itemsBought;
        public int CoinsPaid => coinsPaid;
        public Transform Transform => transform;

        public string InteractionPrompt
        {
            get
            {
                TrashItem held = Held(PlayerContext.ActiveInteractor);
                return held != null
                    ? $"[E] Sell {held.DisplayName} · +{PriceOf(held)} Coins"
                    : $"{label} · bring trash here to sell it";
            }
        }

        public void Configure(string displayLabel, Rates priceRates)
        {
            label = displayLabel;
            rates = priceRates;
        }

        /// <summary>The city's payout multiplier from its EcosystemBalance (1 when none is active).</summary>
        static float Payout => EcosystemBalance.Active != null ? EcosystemBalance.Active.recyclingPayout : 1f;

        /// <summary>Coins per unit of base value for a stream, including the city's payout multiplier.</summary>
        public float RateFor(WasteStream stream) => rates.For(stream) * Payout;

        public int PriceOf(TrashItem item) => item == null ? 0 : Mathf.Max(1, Mathf.RoundToInt(item.BaseCoinValue * RateFor(item.Stream)));

        /// <summary>Price of a typical item of each stream at base value <paramref name="baseValue"/>.</summary>
        public int PriceFor(WasteStream stream, int baseValue) => Mathf.Max(1, Mathf.RoundToInt(baseValue * RateFor(stream)));

        public bool CanInteract(PlayerInteractor interactor) => Held(interactor) != null;

        public void Interact(PlayerInteractor interactor)
        {
            if (Held(interactor) == null)
                return;
            TrashItem item = interactor.Grabber.ReleaseForDeposit() as TrashItem;
            Buy(item, interactor);
        }

        void Awake() => TriggerRelay.AttachToChildTriggers(this);

        public void OnRelayedTriggerEnter(Collider other) => OnTriggerEnter(other);

        void OnTriggerEnter(Collider other)
        {
            TrashItem item = other.GetComponentInParent<TrashItem>();
            if (item == null || item.IsDisposed || item.IsHeld || item.IsContained)
                return;
            Buy(item, PlayerContext.ActiveInteractor);
        }

        void Buy(TrashItem item, PlayerInteractor interactor)
        {
            if (item == null || item.IsDisposed)
                return;

            int price = PriceOf(item);
            PlayerInventory inventory = interactor != null ? interactor.Inventory : PlayerContext.ActiveInventory;
            inventory?.AddCoins(price);
            itemsBought++;
            coinsPaid += price;

            Color colour = WasteStreamPalette.ColorFor(item.Stream);
            string message = $"RÉCUP bought {item.DisplayName} · +{price} Coins";
            interactor?.NotifyFeedback(message, colour);
            PlayerContext.ActiveFeedback?.Emit(PlayerFeedbackEvent.PickupCoins, transform.position, 1f, message, colour);

            ItemBought?.Invoke(this, item, price);
            item.Dispose();
        }

        static TrashItem Held(PlayerInteractor interactor) =>
            interactor != null && interactor.Grabber != null && interactor.Grabber.IsHolding
                ? interactor.Grabber.HeldObject as TrashItem
                : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ItemBought = null;
    }
}
