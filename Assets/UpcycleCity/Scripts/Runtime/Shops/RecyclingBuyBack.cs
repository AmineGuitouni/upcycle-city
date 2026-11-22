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
    public sealed class RecyclingBuyBack : MonoBehaviour, IInteractable, ITriggerReceiver, ISyncedStateProvider
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
        string syncKey;

        /// <summary>(buy-back, item, coins paid) for HUDs and statistics.</summary>
        public static event Action<RecyclingBuyBack, TrashItem, int> ItemBought;

        public Rates PriceRates => rates;
        public int ItemsBought => itemsBought;
        public int CoinsPaid => coinsPaid;
        public Transform Transform => transform;
        public string SyncKey => syncKey ??= SyncedStateRegistry.BuildSpatialKey("buyback", gameObject.name, transform.position);

        public string InteractionPrompt
        {
            get
            {
                TrashItem held = Held(PlayerContext.ActiveInteractor);
                if (held != null)
                    return $"[E] Sell {held.DisplayName} · +{PriceOf(held)} Coins";
                int carried = RecyclingBagService.LocalCarriedCount;
                return carried > 0
                    ? $"[E] Sell what you carry ({carried})"
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

        public bool CanInteract(PlayerInteractor interactor)
        {
            if (Held(interactor) != null)
                return true;
            return RecyclingBagService.LocalCarriedCount > 0;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (Held(interactor) == null)
            {
                // Hands free: open the carry screen in sell mode (pick an item, see its price, confirm)
                if (CanInteract(interactor))
                    CarryBagUI.EnsureInstance().Open(this);
                return;
            }
            TrashItem item = interactor.Grabber.ReleaseForDeposit() as TrashItem;
            if (item != null)
                GameAuthority.Current.RequestSellTrash(this, item, interactor);
        }

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
            GameAuthority.Current.RequestSellTrash(this, item, null);
        }

        /// <summary>Executes authoritative buy-back logic (invoked directly in solo or on the host in co-op).</summary>
        public bool ExecuteAuthoritativeBuy(
            TrashItem item,
            PlayerInteractor interactor,
            out int price,
            out string message,
            out Color colour,
            bool creditLocalInventory = true,
            bool emitLocalFeedback = true)
        {
            price = 0;
            message = string.Empty;
            colour = Color.white;

            if (item == null || item.IsDisposed)
                return false;

            price = PriceOf(item);
            if (creditLocalInventory)
            {
                PlayerInventory inventory = interactor != null ? interactor.Inventory : PlayerContext.ActiveInventory;
                inventory?.AddCoins(price);
            }
            itemsBought++;
            coinsPaid += price;

            colour = WasteStreamPalette.ColorFor(item.Stream);
            message = $"RÉCUP bought {item.DisplayName} · +{price} Coins";
            if (emitLocalFeedback)
            {
                interactor?.NotifyFeedback(message, colour);
                PlayerContext.ActiveFeedback?.Emit(
                    PlayerFeedbackEvent.SellTrash,
                    transform.position,
                    1f,
                    message,
                    colour,
                    FeedbackSignal.EncodeMaterial(item.Stream, item.IsBulky));
            }

            ItemBought?.Invoke(this, item, price);
            item.Dispose();
            return true;
        }

        /// <summary>Convenience overload used by <see cref="LocalGameAuthority"/>.</summary>
        public void ExecuteAuthoritativeBuy(TrashItem item, PlayerInteractor interactor)
        {
            ExecuteAuthoritativeBuy(item, interactor, out _, out _, out _, creditLocalInventory: true, emitLocalFeedback: true);
        }

        public SyncedStateRecord CaptureSyncState() => new SyncedStateRecord
        {
            i0 = itemsBought,
            i1 = coinsPaid,
        };

        public void ApplySyncState(in SyncedStateRecord state)
        {
            itemsBought = Mathf.Max(0, state.i0);
            coinsPaid = Mathf.Max(0, state.i1);
        }

        static TrashItem Held(PlayerInteractor interactor) =>
            interactor != null && interactor.Grabber != null && interactor.Grabber.IsHolding
                ? interactor.Grabber.HeldObject as TrashItem
                : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ItemBought = null;
    }
}
