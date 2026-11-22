using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Crafting;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Player;
using UpcycleCity.Props;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// Owns every player's <see cref="RecyclingBag"/> and runs the street-bin search actions through the shared
    /// game authority, so solo and co-op use one path (AGENTS.md §7):
    /// <list type="bullet">
    /// <item><c>bin.search.open</c> / <c>bin.search.close</c>: start or pause rummaging through a bin.</item>
    /// <item><c>bin.search.take</c>: move a find that has turned up into the requester's bag.</item>
    /// <item><c>bag.deposit</c>: empty the matching finds into a sorting bin (recyclables) or the compost bin (organic),
    /// crediting the Team Material Stock and team coins.</item>
    /// </list>
    /// The host decides everything; each bag replicates to its owner as one compact synced record.
    /// Created automatically when a scene loads.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Waste/Recycling Bag Service")]
    public sealed partial class RecyclingBagService : MonoBehaviour
    {
        public const string ActionOpen = "bin.search.open";
        public const string ActionClose = "bin.search.close";
        public const string ActionTake = "bin.search.take";
        public const string ActionDeposit = "bag.deposit";
        public const string ActionStore = "carry.store";
        public const string ActionThrow = "carry.throw";
        public const string ActionSell = "carry.sell";
        public const string ActionBuyBag = "carry.buy_bag";

        /// <summary>Slot value for <see cref="ActionSell"/> that sells everything RÉCUP buys.</summary>
        public const int SellAll = -1;

        const float ReachRange = 4.5f;

        readonly Dictionary<ulong, RecyclingBag> hostBags = new Dictionary<ulong, RecyclingBag>();
        RecyclingBag localBag;
        ulong localBagOwner = ulong.MaxValue;
        bool localBagIsHostBag;

        public static RecyclingBagService Instance { get; private set; }

        /// <summary>The local player's bag (the host's own bag on the host, a replicated mirror on clients).</summary>
        public static RecyclingBag LocalBag => Instance != null ? Instance.EnsureLocalBag() : null;

        /// <summary>Raised when the local player's bag changes.</summary>
        public static event Action LocalBagChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            LocalBagChanged = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap() => EnsureInstance();

        public static RecyclingBagService EnsureInstance()
        {
            if (Instance != null)
                return Instance;
            var go = new GameObject("RecyclingBagService");
            DontDestroyOnLoad(go);
            return go.AddComponent<RecyclingBagService>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        void OnEnable()
        {
            GameAuthority.RegisterCustomActionHandler(ActionOpen, HandleOpen);
            GameAuthority.RegisterCustomActionHandler(ActionClose, HandleClose);
            GameAuthority.RegisterCustomActionHandler(ActionTake, HandleTake);
            GameAuthority.RegisterCustomActionHandler(ActionDeposit, HandleDeposit);
            GameAuthority.RegisterCustomActionHandler(ActionStore, HandleStore);
            GameAuthority.RegisterCustomActionHandler(ActionThrow, HandleThrow);
            GameAuthority.RegisterCustomActionHandler(ActionSell, HandleSell);
            GameAuthority.RegisterCustomActionHandler(ActionBuyBag, HandleBuyBag);
            RegisterCartHandlers();
        }

        void OnDisable()
        {
            GameAuthority.UnregisterCustomActionHandler(ActionOpen, HandleOpen);
            GameAuthority.UnregisterCustomActionHandler(ActionClose, HandleClose);
            GameAuthority.UnregisterCustomActionHandler(ActionTake, HandleTake);
            GameAuthority.UnregisterCustomActionHandler(ActionDeposit, HandleDeposit);
            GameAuthority.UnregisterCustomActionHandler(ActionStore, HandleStore);
            GameAuthority.UnregisterCustomActionHandler(ActionThrow, HandleThrow);
            GameAuthority.UnregisterCustomActionHandler(ActionSell, HandleSell);
            GameAuthority.UnregisterCustomActionHandler(ActionBuyBag, HandleBuyBag);
            UnregisterCartHandlers();
            foreach (RecyclingBag bag in hostBags.Values)
                SyncedStateRegistry.Unregister(bag);
            if (localBag != null && !localBagIsHostBag)
                SyncedStateRegistry.Unregister(localBag);
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            EnsureLocalBag();
            TickAutoStore();
            TickCartOwnership();
            if (PlayerContext.Active != null && CarryBagUI.Instance == null)
                CarryBagUI.EnsureInstance();
        }

        // ------------------------------------------------------------ bags

        /// <summary>Host: the bag of <paramref name="clientId"/>, created and registered for replication on first use.</summary>
        public RecyclingBag GetHostBag(ulong clientId)
        {
            if (!hostBags.TryGetValue(clientId, out RecyclingBag bag))
            {
                bag = new RecyclingBag(clientId);
                hostBags.Add(clientId, bag);
                SyncedStateRegistry.Register(bag);
            }
            return bag;
        }

        // Follows solo → host / client transitions: the local bag is the host's own bag, or a mirror of ours on a client
        RecyclingBag EnsureLocalBag()
        {
            bool authoritative = GameAuthority.IsAuthoritative;
            ulong id = GameAuthority.LocalClientId;
            if (localBag != null && localBagOwner == id && localBagIsHostBag == authoritative)
                return localBag;

            if (localBag != null)
            {
                localBag.Changed -= RaiseLocalChanged;
                if (!localBagIsHostBag)
                    SyncedStateRegistry.Unregister(localBag);
            }

            if (authoritative)
            {
                // A client that became host (or went solo) starts over: drop mirrors of bags it no longer owns
                localBag = GetHostBag(id);
            }
            else
            {
                foreach (RecyclingBag bag in hostBags.Values)
                    SyncedStateRegistry.Unregister(bag);
                hostBags.Clear();
                localBag = new RecyclingBag(id);
                SyncedStateRegistry.Register(localBag);
            }
            localBagOwner = id;
            localBagIsHostBag = authoritative;
            localBag.Changed += RaiseLocalChanged;
            RaiseLocalChanged();
            return localBag;
        }

        static void RaiseLocalChanged() => LocalBagChanged?.Invoke();

        // ------------------------------------------------------------ requests (any machine)

        public static void RequestOpen(OverflowingBinSource bin) =>
            GameAuthority.Current.RequestCustomAction(ActionOpen, bin.SyncKey);

        public static void RequestClose(OverflowingBinSource bin) =>
            GameAuthority.Current.RequestCustomAction(ActionClose, bin.SyncKey);

        /// <summary>Asks to take the find in <paramref name="slot"/> (expected to be <paramref name="entry"/>, so a stale click can't grab something else).</summary>
        public static void RequestTake(OverflowingBinSource bin, int slot, int entry, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionTake, bin.SyncKey, slot, entry, onResult);

        public static void RequestDeposit(ISyncedStateProvider target, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionDeposit, target.SyncKey, 0, 0f, onResult);

        /// <summary>Stores the street trash the local player is holding into their carry.</summary>
        public static void RequestStoreHeld(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionStore, string.Empty, 0, 0f, onResult);

        /// <summary>Throws the carried item in <paramref name="slot"/> (expected to be <paramref name="id"/>) back into the world.</summary>
        public static void RequestThrow(int slot, int id, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionThrow, string.Empty, slot, id, onResult);

        /// <summary>Sells the carried item in <paramref name="slot"/> at RÉCUP, or everything it buys with <see cref="SellAll"/>.</summary>
        public static void RequestSell(ISyncedStateProvider buyBack, int slot, int id, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionSell, buyBack.SyncKey, slot, id, onResult);

        /// <summary>Buys a Recycling Bag at the shop counter that sells it.</summary>
        public static void RequestBuyBag(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionBuyBag, string.Empty, 0, 0f, onResult);

        public static string FullMessage(RecyclingBag bag) => bag != null && bag.HasBag
            ? "Your bag is full. Sell at RÉCUP or empty it at the sorting bins."
            : "Your arms are full! Sell at RÉCUP, or buy a bag at the Quincaillerie.";

        // ------------------------------------------------------------ host handlers

        static bool TryResolve<T>(string key, out T target) where T : class
        {
            target = null;
            if (string.IsNullOrEmpty(key))
                return false;
            if (SyncedStateRegistry.TryGet(SyncedStateRegistry.ComputeKeyHash(key), out ISyncedStateProvider provider))
                target = provider as T;
            return target != null;
        }

        CustomGameActionResult HandleOpen(in CustomGameActionContext context)
        {
            if (!TryResolve(context.TargetId, out OverflowingBinSource bin))
                return CustomGameActionResult.Fail("That bin is gone.");
            if (!context.IsWithinRange(bin.transform.position, ReachRange) && context.SenderPosition.HasValue)
                return CustomGameActionResult.Fail("Too far from the bin.");
            bin.AuthoritativeSetSearcher(context.SenderClientId, true);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok();
        }

        CustomGameActionResult HandleClose(in CustomGameActionContext context)
        {
            if (TryResolve(context.TargetId, out OverflowingBinSource bin))
            {
                bin.AuthoritativeSetSearcher(context.SenderClientId, false);
                GameAuthority.Current.FlushSyncedStates();
            }
            return CustomGameActionResult.Ok();
        }

        CustomGameActionResult HandleTake(in CustomGameActionContext context)
        {
            BinLootTable table = BinLootTable.Active;
            if (table == null || !TryResolve(context.TargetId, out OverflowingBinSource bin))
                return CustomGameActionResult.Fail("That bin is gone.");
            if (!context.IsWithinRange(bin.transform.position, ReachRange) && context.SenderPosition.HasValue)
                return CustomGameActionResult.Fail("Too far from the bin.");

            RecyclingBag bag = GetHostBag(context.SenderClientId);
            if (bag.IsFull)
                return CustomGameActionResult.Fail(FullMessage(bag));

            int entry = bin.AuthoritativeTake(context.IntArg, Mathf.RoundToInt(context.FloatArg), context.SenderClientId);
            if (entry < 0)
                return CustomGameActionResult.Fail("Someone got there first.");
            bag.TryAdd(CarryItems.IdForLoot(entry));
            GameAuthority.Current.FlushSyncedStates();

            table.TryGet(entry, out BinLootTable.Entry e);
            return CustomGameActionResult.Ok(e != null ? $"+ {e.displayName}" : string.Empty);
        }

        CustomGameActionResult HandleDeposit(in CustomGameActionContext context)
        {
            bool organic;
            WasteStream stream = WasteStream.Plastic;
            Vector3 position;
            SortingCrate crate = null;
            CompostBin compost = null;
            if (TryResolve(context.TargetId, out crate))
            {
                organic = false;
                stream = crate.AcceptedStream;
                position = crate.transform.position;
            }
            else if (TryResolve(context.TargetId, out compost))
            {
                organic = true;
                position = compost.transform.position;
            }
            else
            {
                return CustomGameActionResult.Fail("Nothing to empty the bag into.");
            }
            if (!context.IsWithinRange(position, ReachRange) && context.SenderPosition.HasValue)
                return CustomGameActionResult.Fail("Too far away.");

            RecyclingBag bag = GetHostBag(context.SenderClientId);
            TeamMaterialStock stock = TeamMaterialStock.EnsureInstance();
            EcosystemBalance balance = EcosystemBalance.Active;
            float coinScale = balance != null ? balance.sortingPayout : 1f;
            float yieldScale = balance != null ? balance.sortingMaterialYieldScale : 1f;

            int delivered = 0, coins = 0;
            for (int i = bag.Count - 1; i >= 0; i--)
            {
                CarryItems.Info e = CarryItems.Resolve(bag[i]);
                if (!CarryItems.Accepts(e, organic, stream))
                    continue;
                if (stock != null && !string.IsNullOrEmpty(e.MaterialId) && e.MaterialAmount > 0f)
                    stock.TryAddMaterialAuthoritative(e.MaterialId, e.MaterialAmount * Mathf.Max(0f, yieldScale));
                coins += Mathf.RoundToInt(e.BaseValue * coinScale);
                delivered++;
                bag.RemoveAt(i);
            }

            // The cart the player pushes (or stands next to) is emptied too
            CartStorage cart = CartFor(context);
            if (cart != null)
            {
                for (int i = cart.Count - 1; i >= 0; i--)
                {
                    CarryItems.Info e = CarryItems.Resolve(cart[i]);
                    if (!CarryItems.Accepts(e, organic, stream))
                        continue;
                    if (stock != null && !string.IsNullOrEmpty(e.MaterialId) && e.MaterialAmount > 0f)
                        stock.TryAddMaterialAuthoritative(e.MaterialId, e.MaterialAmount * Mathf.Max(0f, yieldScale));
                    coins += Mathf.RoundToInt(e.BaseValue * coinScale);
                    delivered++;
                    cart.RemoveAt(i);
                }
            }

            if (delivered == 0)
            {
                string where = cart != null ? "your bag or cart" : "your bag";
                return CustomGameActionResult.Fail(organic
                    ? $"No organic waste in {where}."
                    : $"No {WasteStreamPalette.LabelFor(stream)} in {where}.");
            }

            if (coins > 0)
                GameAuthority.Current.CreditTeamCoins(coins);
            crate?.RecordBagDeposit(delivered);
            compost?.RecordDeposit(delivered);
            GameAuthority.Current.FlushSyncedStates();

            string label = organic ? "ORGANIC" : WasteStreamPalette.LabelFor(stream);
            Color color = organic ? BinLootTable.OrganicColor : WasteStreamPalette.ColorFor(stream);
            string message = coins > 0 ? $"+{delivered} {label} · +{coins} Coins" : $"+{delivered} {label}";
            GameAuthority.Current.SendTargetedFeedback(context.SenderClientId, message, color, PlayerFeedbackEvent.SortCorrect, position);
            return CustomGameActionResult.Ok(message);
        }
    
        // ------------------------------------------------------------ carrying street trash, throwing, selling, buying

        CustomGameActionResult HandleStore(in CustomGameActionContext context)
        {
            TrashItem item = FindHeldBy(context.SenderClientId);
            if (item == null)
                return CustomGameActionResult.Fail("Nothing in your hands.");
            int id = CarryItems.IdForTrash(item);
            if (id < 0)
                return CustomGameActionResult.Fail(item.IsBulky ? "Too bulky to carry: drag it to RÉCUP." : "Can't carry that.");

            RecyclingBag bag = GetHostBag(context.SenderClientId);
            CartStorage overflow = null;
            if (bag.IsFull)
            {
                // A moving bag: what doesn't fit the hands/bag goes into the cart the player pushes or stands next to
                overflow = CartFor(context);
                if (overflow == null)
                    return CustomGameActionResult.Fail(FullMessage(bag));
                if (overflow.IsFull)
                    return CustomGameActionResult.Fail("Your bag and your cart are full. Sell at RÉCUP or empty them at the sorting bins.");
            }

            // The host's own grabber lets go before the item disappears (remote holders are cleared by the despawn)
            PhysicsGrabController grabber = PlayerContext.ActiveGrabber;
            if (context.SenderClientId == GameAuthority.LocalClientId && grabber != null && ReferenceEquals(grabber.HeldObject, item))
                grabber.ReleaseForDeposit();

            string name = item.DisplayName;
            if (overflow != null)
            {
                overflow.TryAdd(id);
                item.Dispose();
                GameAuthority.Current.FlushSyncedStates();
                return CustomGameActionResult.Ok($"+ {name} → cart  ({overflow.Count}/{overflow.Capacity})");
            }
            bag.TryAdd(id);
            item.Dispose();
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok($"+ {name}  ({bag.Count}/{bag.Capacity})");
        }

        static TrashItem FindHeldBy(ulong clientId)
        {
            if (clientId == GameAuthority.LocalClientId && PlayerContext.ActiveGrabber != null
                && PlayerContext.ActiveGrabber.HeldObject is TrashItem local && !local.IsDisposed)
                return local;
            foreach (TrashItem t in FindObjectsByType<TrashItem>(FindObjectsInactive.Exclude))
            {
                if (t == null || t.IsDisposed)
                    continue;
                if (t.TryGetComponent(out INetworkedPhysicsGrabbable net) && net.IsHeldByAnyone && net.HolderClientId == clientId)
                    return t;
            }
            return null;
        }

        CustomGameActionResult HandleThrow(in CustomGameActionContext context)
        {
            RecyclingBag bag = GetHostBag(context.SenderClientId);
            int slot = context.IntArg, expected = Mathf.RoundToInt(context.FloatArg);
            ShoppingCart fromCart = null;
            if (slot >= CartSlotBase)
            {
                slot -= CartSlotBase;
                fromCart = CartObjectFor(context);
                if (fromCart == null || slot >= fromCart.Storage.Count || fromCart.Storage[slot] != expected)
                    return CustomGameActionResult.Fail("That item isn't there any more.");
            }
            else if (slot < 0 || slot >= bag.Count || bag[slot] != expected)
                return CustomGameActionResult.Fail("That item isn't there any more.");

            CarryItems.Info info = CarryItems.Resolve(expected);
            WasteCatalog catalog = CarryItems.Catalog;
            if (catalog == null || info.CatalogIndex < 0 || info.CatalogIndex >= catalog.Entries.Count)
                return CustomGameActionResult.Fail("Can't throw that out here.");

            Vector3 origin = fromCart != null ? fromCart.transform.position : context.SenderPosition ?? Vector3.zero;
            var rng = new System.Random(unchecked((int)(context.SenderClientId * 7919UL) ^ bag.Count ^ (int)(Time.time * 1000f)));
            float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
            var dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            Vector3 position = origin + Vector3.up * 1.2f + dir * 0.6f;
            Vector3 velocity = dir * 1.6f + Vector3.up * 1.4f;

            TrashItem spawned = GameAuthority.Current.SpawnTrash(catalog, catalog.Entries[info.CatalogIndex], WasteSourceKind.Initial, position, velocity);
            if (spawned == null)
                return CustomGameActionResult.Fail("Couldn't throw it out.");
            if (fromCart != null)
                fromCart.Storage.RemoveAt(slot);
            else
                bag.RemoveAt(slot);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok($"Threw out {info.Name}");
        }

        CustomGameActionResult HandleSell(in CustomGameActionContext context)
        {
            if (!TryResolve(context.TargetId, out Shops.RecyclingBuyBack buyBack))
                return CustomGameActionResult.Fail("RÉCUP isn't here.");
            if (!context.IsWithinRange(buyBack.transform.position, ReachRange) && context.SenderPosition.HasValue)
                return CustomGameActionResult.Fail("Too far from RÉCUP.");

            RecyclingBag bag = GetHostBag(context.SenderClientId);
            CartStorage cart = CartFor(context);
            int slot = context.IntArg, expected = Mathf.RoundToInt(context.FloatArg);
            int sold = 0, coins = 0;
            if (slot == SellAll)
            {
                for (int i = bag.Count - 1; i >= 0; i--)
                {
                    CarryItems.Info info = CarryItems.Resolve(bag[i]);
                    if (!info.Valid || info.Organic)
                        continue;
                    coins += buyBack.PriceFor(info.Stream, info.BaseValue);
                    sold++;
                    bag.RemoveAt(i);
                }
                for (int i = cart != null ? cart.Count - 1 : -1; i >= 0; i--)
                {
                    CarryItems.Info info = CarryItems.Resolve(cart[i]);
                    if (!info.Valid || info.Organic)
                        continue;
                    coins += buyBack.PriceFor(info.Stream, info.BaseValue);
                    sold++;
                    cart.RemoveAt(i);
                }
                if (sold == 0)
                    return CustomGameActionResult.Fail("Nothing RÉCUP buys. Organic waste goes to the compost bin.");
            }
            else
            {
                bool fromCart = slot >= CartSlotBase;
                if (fromCart)
                    slot -= CartSlotBase;
                int count = fromCart ? (cart != null ? cart.Count : 0) : bag.Count;
                if (slot < 0 || slot >= count || (fromCart ? cart[slot] : bag[slot]) != expected)
                    return CustomGameActionResult.Fail("That item isn't there any more.");
                CarryItems.Info info = CarryItems.Resolve(expected);
                if (!info.Valid || info.Organic)
                    return CustomGameActionResult.Fail("RÉCUP doesn't buy organic waste: use the compost bin.");
                coins = buyBack.PriceFor(info.Stream, info.BaseValue);
                sold = 1;
                if (fromCart)
                    cart.RemoveAt(slot);
                else
                    bag.RemoveAt(slot);
            }

            GameAuthority.Current.CreditTeamCoins(coins);
            GameAuthority.Current.FlushSyncedStates();
            string message = sold == 1 ? $"Sold · +{coins} Coins" : $"Sold {sold} items · +{coins} Coins";
            GameAuthority.Current.SendTargetedFeedback(context.SenderClientId, message, new Color(0.95f, 0.76f, 0.11f),
                PlayerFeedbackEvent.SellTrash, buyBack.transform.position);
            return CustomGameActionResult.Ok(message);
        }

        CustomGameActionResult HandleBuyBag(in CustomGameActionContext context)
        {
            RecyclingBag bag = GetHostBag(context.SenderClientId);
            if (bag.HasBag)
                return CustomGameActionResult.Fail("You already have a bag.");

            // The price comes from the host's own shop catalogs, never from the client
            Shops.ShopCounter seller = null;
            int price = 0;
            foreach (Shops.ShopCounter counter in FindObjectsByType<Shops.ShopCounter>(FindObjectsInactive.Exclude))
            {
                if (counter.Catalog == null || !counter.Catalog.TryFindOffer(CarryItems.BagUpgradeItemId, 1, out Shops.ShopCatalog.Offer offer))
                    continue;
                if (context.SenderPosition.HasValue && !context.IsWithinRange(counter.transform.position, ReachRange + 2f))
                    continue;
                seller = counter;
                price = offer.price;
                break;
            }
            if (seller == null)
                return CustomGameActionResult.Fail("Bags are sold at the Quincaillerie.");
            if (!GameAuthority.Current.TrySpendTeamCoins(price))
                return CustomGameActionResult.Fail($"Not enough coins (need {price}).");

            bag.GrantBag();
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok($"Recycling Bag! You can carry {bag.Capacity} items now.");
        }

        // ------------------------------------------------------------ local: picked-up trash goes straight into the carry

        PhysicsGrabController watchedGrabber;
        TrashItem pendingStore;
        float pendingStoreTime;
        bool storeInFlight;

        void TickAutoStore()
        {
            PhysicsGrabController grabber = PlayerContext.ActiveGrabber;
            if (grabber != watchedGrabber)
            {
                if (watchedGrabber != null)
                    watchedGrabber.Grabbed -= OnLocalGrabbed;
                watchedGrabber = grabber;
                if (watchedGrabber != null)
                    watchedGrabber.Grabbed += OnLocalGrabbed;
            }

            if (pendingStore == null || storeInFlight)
                return;
            if (pendingStore.IsDisposed || grabber == null || !ReferenceEquals(grabber.HeldObject, pendingStore)
                || Time.unscaledTime - pendingStoreTime > 3f)
            {
                pendingStore = null;
                return;
            }

            // In co-op, wait until the host has us down as the holder, so it can find the item
            if (!GameAuthority.IsAuthoritative && pendingStore.TryGetComponent(out INetworkedPhysicsGrabbable net)
                && net.HolderClientId != GameAuthority.LocalClientId)
                return;

            storeInFlight = true;
            TrashItem target = pendingStore;
            RequestStoreHeld((ok, message) =>
            {
                storeInFlight = false;
                if (ReferenceEquals(pendingStore, target))
                    pendingStore = null;
                if (!ok && !string.IsNullOrEmpty(message))
                    PlayerContext.ActiveInteractor?.NotifyFeedback(message, new Color(0.95f, 0.55f, 0.2f));
            });
        }

        void OnLocalGrabbed(IGrabbable grabbed)
        {
            if (grabbed is not TrashItem item || item.IsBulky)
                return;
            RecyclingBag bag = EnsureLocalBag();
            if (bag != null && bag.IsFull)
            {
                ShoppingCart cart = ShoppingCart.FindForLocal();
                if (cart == null || cart.IsFull)
                {
                    PlayerContext.ActiveInteractor?.NotifyFeedback(cart != null
                        ? "Your bag and your cart are full." : FullMessage(bag), new Color(0.95f, 0.55f, 0.2f));
                    return;
                }
            }
            pendingStore = item;
            pendingStoreTime = Time.unscaledTime;
        }
}
}
