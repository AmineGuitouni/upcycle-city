using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Ecosystem;
using UpcycleCity.Interaction;
using UpcycleCity.Inventory;
using UpcycleCity.Player;
using UpcycleCity.Shops;
using UpcycleCity.Waste;

namespace UpcycleCity.Core
{
    /// <summary>
    /// Request payload for purchasing an item from a <see cref="ShopUI"/>.
    /// Deliberately excludes any client-supplied price — the authoritative authority resolves the offer
    /// and price directly from the <see cref="ShopCatalog"/>.
    /// </summary>
    public readonly struct ShopPurchaseRequest
    {
        public readonly string ShopKey;
        public readonly string ItemId;
        public readonly int Quantity;
        public readonly Vector3 ShopPosition;
        public readonly PlayerInventory BuyerInventory;
        public readonly ShopCatalog Catalog;

        public ShopPurchaseRequest(
            string shopKey,
            string itemId,
            int quantity,
            Vector3 shopPosition,
            PlayerInventory buyerInventory,
            ShopCatalog catalog = null)
        {
            ShopKey = shopKey ?? string.Empty;
            ItemId = itemId ?? string.Empty;
            Quantity = quantity;
            ShopPosition = shopPosition;
            BuyerInventory = buyerInventory;
            Catalog = catalog;
        }
    }

    /// <summary>Authoritative outcome of a <see cref="ShopPurchaseRequest"/>.</summary>
    public readonly struct ShopPurchaseResult
    {
        public readonly bool Approved;
        public readonly string ItemId;
        public readonly int Quantity;
        public readonly int Price;
        public readonly int RemainingCoins;
        public readonly string RejectionReason;

        public ShopPurchaseResult(
            bool approved,
            string itemId,
            int quantity,
            int price,
            int remainingCoins,
            string rejectionReason = "")
        {
            Approved = approved;
            ItemId = itemId ?? string.Empty;
            Quantity = quantity;
            Price = price;
            RemainingCoins = remainingCoins;
            RejectionReason = rejectionReason ?? string.Empty;
        }
    }

    /// <summary>
    /// Runtime-side authoritative request metadata passed to every <see cref="CustomGameActionHandler"/>.
    /// Gives gameplay handlers the acting player's ID, world position, and target ID so they can perform
    /// range and ownership validation without referencing <c>Unity.Netcode</c>.
    /// </summary>
    public readonly struct CustomGameActionContext
    {
        /// <summary>Acting player's client ID (0 in solo or when invoked by the host).</summary>
        public readonly ulong SenderClientId;

        /// <summary>Target identifier (e.g., a machine's unique <c>SyncKey</c> or object name).</summary>
        public readonly string TargetId;

        /// <summary>Optional integer payload (e.g., recipe index or upgrade tier).</summary>
        public readonly int IntArg;

        /// <summary>Optional float payload.</summary>
        public readonly float FloatArg;

        /// <summary>
        /// Acting player's authoritative world position when available (local player position in solo;
        /// server-side player rig position in co-op; null only in headless tests with no player rig).
        /// </summary>
        public readonly Vector3? SenderPosition;

        public CustomGameActionContext(
            ulong senderClientId,
            string targetId,
            int intArg,
            float floatArg,
            Vector3? senderPosition)
        {
            SenderClientId = senderClientId;
            TargetId = targetId ?? string.Empty;
            IntArg = intArg;
            FloatArg = floatArg;
            SenderPosition = senderPosition;
        }

        /// <summary>Returns true if the sender is within <paramref name="maxRange"/> meters of <paramref name="targetWorldPosition"/>.</summary>
        public bool IsWithinRange(Vector3 targetWorldPosition, float maxRange)
        {
            if (!SenderPosition.HasValue)
                return false;
            return Vector3.Distance(SenderPosition.Value, targetWorldPosition) <= maxRange;
        }
    }

    /// <summary>Authoritative outcome of a custom game action request, returned to the requesting client.</summary>
    public readonly struct CustomGameActionResult
    {
        public readonly bool Success;
        public readonly string Message;

        public CustomGameActionResult(bool success, string message = "")
        {
            Success = success;
            Message = message ?? string.Empty;
        }

        public static CustomGameActionResult Ok(string message = "") => new CustomGameActionResult(true, message);
        public static CustomGameActionResult Fail(string message = "") => new CustomGameActionResult(false, message);
    }

    /// <summary>
    /// Delegate executed on the authoritative side (local in solo, host in co-op) for extensible custom game actions
    /// such as crafting, automated machines, or vehicle unlocks. Return <see cref="CustomGameActionResult.Ok"/> or
    /// <see cref="CustomGameActionResult.Fail"/> with an optional user-facing message that is sent back to the requester.
    /// </summary>
    public delegate CustomGameActionResult CustomGameActionHandler(in CustomGameActionContext context);

    /// <summary>
    /// Single authoritative gameplay-action gateway shared by solo and co-op play.
    /// Gameplay systems route mutations (sorting, buy-back, bin servicing, factory repairs, shop purchases,
    /// host-side networked prefab spawning/despawning, city events, and custom future actions) through
    /// <see cref="GameAuthority.Current"/> so solo and co-op execute the exact same gameplay logic without
    /// scattering networking conditionals across gameplay classes.
    /// </summary>
    public interface IGameAuthority
    {
        /// <summary>True in solo mode and on the co-op host; false on non-host co-op clients.</summary>
        bool IsAuthoritative { get; }

        /// <summary>True when an online co-op session is active.</summary>
        bool IsMultiplayer { get; }

        /// <summary>Shared authoritative clock in seconds (NetworkManager.ServerTime.Time in co-op, Time.unscaledTimeAsDouble in solo).</summary>
        double ServerTime { get; }

        /// <summary>Current active city event title (empty when no event is active).</summary>
        string ActiveEventTitle { get; }

        /// <summary>Remaining seconds on the active city event (computed locally from authoritative end time).</summary>
        float ActiveEventTimeRemaining { get; }

        /// <summary>Severity level of the active city event (0 = none, 1 = normal, 2+ = urgent).</summary>
        byte ActiveEventSeverity { get; }

        /// <summary>Current shared team coin balance (local inventory coins in solo, SharedTeamWallet in co-op).</summary>
        int TeamCoins { get; }

        /// <summary>Host-only (or solo) helper to spend shared team coins (used by authoritative machine upgrades).</summary>
        bool TrySpendTeamCoins(int amount);

        /// <summary>Host-only (or solo) helper to credit shared team coins (e.g. starter kit or refund).</summary>
        void CreditTeamCoins(int amount);

        /// <summary>Host-only hint to push any dirty <see cref="ISyncedStateProvider"/> deltas immediately to co-op clients.</summary>
        void FlushSyncedStates();

        /// <summary>
        /// Sends a targeted player feedback toast/event to <paramref name="targetClientId"/> via the existing
        /// authority feedback channel (local feedback in solo or when targeting the host; targeted ClientRpc in co-op).
        /// </summary>
        void SendTargetedFeedback(
            ulong targetClientId,
            string message,
            Color color,
            PlayerFeedbackEvent feedbackEvent,
            Vector3 worldPos);

        /// <summary>Requests sorting <paramref name="item"/> into <paramref name="crate"/>.</summary>
        void RequestSortTrash(SortingCrate crate, TrashItem item, PlayerInteractor interactor);

        /// <summary>Requests depositing <paramref name="item"/> into a crafting machine hopper (<paramref name="hopper"/>).</summary>
        void RequestDepositTrash(ITrashHopper hopper, TrashItem item, PlayerInteractor interactor);

        /// <summary>Requests selling <paramref name="item"/> at <paramref name="buyBack"/>.</summary>
        void RequestSellTrash(RecyclingBuyBack buyBack, TrashItem item, PlayerInteractor interactor);

        /// <summary>Requests servicing/emptying <paramref name="bin"/>.</summary>
        void RequestServiceBin(OverflowingBinSource bin, PlayerInteractor interactor);

        /// <summary>Requests repairing the scrubber on <paramref name="factory"/>.</summary>
        void RequestRepairFactory(FactorySmokestackSource factory, PlayerInteractor interactor);

        /// <summary>Requests an authoritative coin spend for a shop purchase (price resolved from catalog on host) and invokes <paramref name="onResult"/>.</summary>
        void RequestShopPurchase(ShopPurchaseRequest request, Action<ShopPurchaseResult> onResult);

        /// <summary>
        /// Dispatches a named custom action to a handler registered via
        /// <see cref="GameAuthority.RegisterCustomActionHandler"/> (for future crafting, machines, bicycles, etc.).
        /// When <paramref name="onResult"/> is provided, receives <c>(success, message)</c> synchronously in solo/host
        /// or via a targeted host→client RPC in co-op so UIs can show feedback such as "Not enough materials".
        /// </summary>
        bool RequestCustomAction(
            string actionId,
            string targetId = "",
            int intArg = 0,
            float floatArg = 0f,
            Action<bool, string> onResult = null);

        /// <summary>Spawns a physical trash item through the active authority (local GameObject in solo, NetworkObject on host).</summary>
        TrashItem SpawnTrash(
            WasteCatalog catalog,
            WasteCatalog.Entry entry,
            WasteSourceKind sourceKind,
            Vector3 worldPosition,
            Vector3 initialVelocity,
            Transform parent = null);

        /// <summary>
        /// Host-only (or local solo) prefab spawning: instantiates locally in solo or spawns a <c>NetworkObject</c> on the host.
        /// Calling this on a non-host client logs an error and returns null — clients that need an item spawned
        /// (e.g. crafting) must send a validated <see cref="RequestCustomAction"/> whose host handler spawns it.
        /// In co-op, <paramref name="parent"/> is applied via <c>NetworkObject.TrySetParent</c> only when it has a spawned
        /// <c>NetworkObject</c>; passing a non-networked parent logs a warning and spawns at root.
        /// </summary>
        GameObject SpawnNetworkedPrefab(
            GameObject prefab,
            Vector3 worldPosition,
            Quaternion rotation,
            Transform parent = null);

        /// <summary>
        /// Host-only (or local solo) prefab spawning with a pre-spawn callback invoked AFTER instantiation and
        /// BEFORE <c>NetworkObject.Spawn</c>. Use this to assign a stable placement ID via
        /// <see cref="IMachineIdentityReceiver.ApplyPlacementId"/> so clients receive the final ID on first spawn
        /// without double registration or referencing <c>UpcycleCity.Networking</c>.
        /// </summary>
        GameObject SpawnNetworkedPrefab(
            GameObject prefab,
            Vector3 worldPosition,
            Quaternion rotation,
            Action<GameObject> beforeSpawn,
            Transform parent = null);

        /// <summary>
        /// Host-only (or local solo) object despawning. Calling this on a non-host client logs an error and returns false.
        /// </summary>
        bool DespawnNetworkedObject(GameObject instance);

        /// <summary>Triggers an advance-warned city event and countdown banner (local in solo, replicated on host in co-op).</summary>
        void TriggerCityEvent(string title, float durationSeconds, byte severity = 1);
    }

    /// <summary>
    /// Default offline <see cref="IGameAuthority"/> implementation. Executes every gameplay action directly and
    /// synchronously with zero Netcode dependencies so single-player behaves identically to the offline build.
    /// </summary>
    public sealed class LocalGameAuthority : IGameAuthority
    {
        public static readonly LocalGameAuthority Instance = new LocalGameAuthority();

        string activeEventTitle = string.Empty;
        double activeEventEndTime;
        byte activeEventSeverity;
        PlayerInventory localWalletOverride;

        public void SetLocalWalletOverride(PlayerInventory inventory) => localWalletOverride = inventory;

        PlayerInventory ResolveLocalWallet() => localWalletOverride != null ? localWalletOverride : PlayerContext.ActiveInventory;

        public bool IsAuthoritative => true;
        public bool IsMultiplayer => false;
        public double ServerTime => Time.unscaledTimeAsDouble;
        public string ActiveEventTitle => ActiveEventTimeRemaining > 0f ? activeEventTitle : string.Empty;
        public float ActiveEventTimeRemaining => activeEventEndTime > 0d ? Mathf.Max(0f, (float)(activeEventEndTime - Time.unscaledTimeAsDouble)) : 0f;
        public byte ActiveEventSeverity => ActiveEventTimeRemaining > 0f ? activeEventSeverity : (byte)0;
        public int TeamCoins
        {
            get
            {
                PlayerInventory inv = ResolveLocalWallet();
                return inv != null ? inv.Coins : 0;
            }
        }

        public bool TrySpendTeamCoins(int amount)
        {
            if (amount <= 0)
                return true;
            PlayerInventory inv = ResolveLocalWallet();
            return inv != null && inv.TrySpendCoins(amount);
        }

        public void CreditTeamCoins(int amount)
        {
            if (amount <= 0)
                return;
            ResolveLocalWallet()?.AddCoins(amount);
        }

        public void FlushSyncedStates() { }

        public void SendTargetedFeedback(
            ulong targetClientId,
            string message,
            Color color,
            PlayerFeedbackEvent feedbackEvent,
            Vector3 worldPos)
        {
            if (string.IsNullOrEmpty(message))
                return;
            PlayerContext.ActiveInteractor?.NotifyFeedback(message, color);
            PlayerContext.ActiveFeedback?.Emit(feedbackEvent, worldPos, 1f, message, color);
        }

        public void RequestSortTrash(SortingCrate crate, TrashItem item, PlayerInteractor interactor)
        {
            if (crate == null || item == null)
                return;
            PlayerInteractor resolved = interactor ?? PlayerContext.ActiveInteractor;
            PlayerInventory inventory = resolved != null ? resolved.Inventory : PlayerContext.ActiveInventory;
            crate.ExecuteAuthoritativeSort(item, inventory, resolved);
        }

        public void RequestDepositTrash(ITrashHopper hopper, TrashItem item, PlayerInteractor interactor)
        {
            if (hopper == null || item == null)
                return;
            hopper.ExecuteAuthoritativeDeposit(item, interactor ?? PlayerContext.ActiveInteractor, out _, out _, emitLocalFeedback: true);
        }

        public void RequestSellTrash(RecyclingBuyBack buyBack, TrashItem item, PlayerInteractor interactor)
        {
            if (buyBack == null || item == null)
                return;
            buyBack.ExecuteAuthoritativeBuy(item, interactor ?? PlayerContext.ActiveInteractor);
        }

        public void RequestServiceBin(OverflowingBinSource bin, PlayerInteractor interactor)
        {
            if (bin == null)
                return;
            bin.ExecuteAuthoritativeService(interactor);
        }

        public void RequestRepairFactory(FactorySmokestackSource factory, PlayerInteractor interactor)
        {
            if (factory == null)
                return;
            factory.ExecuteAuthoritativeRepair(interactor);
        }

        public void RequestShopPurchase(ShopPurchaseRequest request, Action<ShopPurchaseResult> onResult)
        {
            PlayerInventory inv = request.BuyerInventory ?? PlayerContext.ActiveInventory;
            if (inv == null)
            {
                onResult?.Invoke(new ShopPurchaseResult(false, request.ItemId, request.Quantity, 0, 0, "No inventory."));
                return;
            }

            ShopCatalog.Offer offer = null;
            bool found = request.Catalog != null && request.Catalog.TryFindOffer(request.ItemId, request.Quantity, out offer);
            if (!found)
                found = ShopCounter.TryResolveOffer(request.ShopKey, request.ItemId, request.Quantity, out _, out offer);

            if (!found || offer == null || offer.price <= 0)
            {
                onResult?.Invoke(new ShopPurchaseResult(false, request.ItemId, request.Quantity, 0, inv.Coins, "Unknown shop offer."));
                return;
            }

            int price = offer.price;
            if (!inv.TrySpendCoins(price))
            {
                onResult?.Invoke(new ShopPurchaseResult(
                    false,
                    request.ItemId,
                    request.Quantity,
                    price,
                    inv.Coins,
                    $"Not enough coins — costs {price}."));
                return;
            }

            if (offer.item != null && offer.item.UseBehavior is IMaterialStockCreditBehavior stockCredit && stockCredit.CreditOnPurchase)
                stockCredit.CreditAuthoritativeStock(offer.quantity, out _);

            onResult?.Invoke(new ShopPurchaseResult(true, request.ItemId, request.Quantity, price, inv.Coins));
        }

        public bool RequestCustomAction(
            string actionId,
            string targetId = "",
            int intArg = 0,
            float floatArg = 0f,
            Action<bool, string> onResult = null)
        {
            CustomGameActionResult result = GameAuthority.InvokeCustomActionLocally(
                0UL, actionId, targetId, intArg, floatArg, senderPosition: null);
            onResult?.Invoke(result.Success, result.Message);
            return result.Success;
        }

        public TrashItem SpawnTrash(
            WasteCatalog catalog,
            WasteCatalog.Entry entry,
            WasteSourceKind sourceKind,
            Vector3 worldPosition,
            Vector3 initialVelocity,
            Transform parent = null)
        {
            return WasteCatalog.SpawnEntryLocal(entry, sourceKind, worldPosition, initialVelocity, parent);
        }

        public GameObject SpawnNetworkedPrefab(
            GameObject prefab,
            Vector3 worldPosition,
            Quaternion rotation,
            Transform parent = null) =>
            SpawnNetworkedPrefab(prefab, worldPosition, rotation, beforeSpawn: null, parent: parent);

        public GameObject SpawnNetworkedPrefab(
            GameObject prefab,
            Vector3 worldPosition,
            Quaternion rotation,
            Action<GameObject> beforeSpawn,
            Transform parent = null)
        {
            if (prefab == null)
                return null;
            GameObject go = UnityEngine.Object.Instantiate(prefab, worldPosition, rotation, parent);
            beforeSpawn?.Invoke(go);
            return go;
        }

        public bool DespawnNetworkedObject(GameObject instance)
        {
            if (instance == null)
                return false;
            UnityEngine.Object.Destroy(instance);
            return true;
        }

        public void TriggerCityEvent(string title, float durationSeconds, byte severity = 1)
        {
            activeEventTitle = title ?? string.Empty;
            activeEventEndTime = durationSeconds > 0f ? Time.unscaledTimeAsDouble + durationSeconds : 0d;
            activeEventSeverity = durationSeconds > 0f ? severity : (byte)0;
        }

        internal void ResetState()
        {
            activeEventTitle = string.Empty;
            activeEventEndTime = 0d;
            activeEventSeverity = 0;
        }
    }

    /// <summary>
    /// Global service locator for the active <see cref="IGameAuthority"/> and extensible custom-action registry.
    /// Defaults to <see cref="LocalGameAuthority.Instance"/> in offline play and resets cleanly when Domain Reload is disabled.
    /// </summary>
    public static class GameAuthority
    {
        readonly struct ActionHandlerKey : IEquatable<ActionHandlerKey>
        {
            public readonly string ActionId;
            public readonly string TargetId;

            public ActionHandlerKey(string actionId, string targetId)
            {
                ActionId = actionId ?? string.Empty;
                TargetId = targetId ?? string.Empty;
            }

            public bool Equals(ActionHandlerKey other) =>
                string.Equals(ActionId, other.ActionId, StringComparison.Ordinal)
                && string.Equals(TargetId, other.TargetId, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is ActionHandlerKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h1 = StringComparer.Ordinal.GetHashCode(ActionId);
                    int h2 = StringComparer.Ordinal.GetHashCode(TargetId);
                    return (h1 * 397) ^ h2;
                }
            }
        }

        static IGameAuthority current = LocalGameAuthority.Instance;
        static readonly Dictionary<ActionHandlerKey, CustomGameActionHandler> CustomHandlers =
            new Dictionary<ActionHandlerKey, CustomGameActionHandler>(32);

        /// <summary>Always-valid active game authority (<see cref="LocalGameAuthority"/> offline, networked authority in co-op).</summary>
        public static IGameAuthority Current => current ?? LocalGameAuthority.Instance;

        /// <summary>Shortcut for <c>GameAuthority.Current.IsAuthoritative</c>.</summary>
        public static bool IsAuthoritative => Current.IsAuthoritative;

        /// <summary>Shortcut for <c>GameAuthority.Current.IsMultiplayer</c>.</summary>
        public static bool IsMultiplayer => Current.IsMultiplayer;

        /// <summary>
        /// Resolves this machine's co-op client ID. Installed by the networking layer while a session runs; gameplay code
        /// reads <see cref="LocalClientId"/> to find its own per-player state (e.g. its Recycling Bag) without Netcode.
        /// </summary>
        public static Func<ulong> LocalClientIdResolver { get; set; }

        /// <summary>This machine's client ID (0 in solo and on the host), matching <see cref="CustomGameActionContext.SenderClientId"/>.</summary>
        public static ulong LocalClientId => LocalClientIdResolver != null ? LocalClientIdResolver() : 0UL;

        /// <summary>Installs an authority implementation (passing null restores <see cref="LocalGameAuthority.Instance"/>).</summary>
        public static void SetAuthority(IGameAuthority authority)
        {
            current = authority ?? LocalGameAuthority.Instance;
        }

        /// <summary>Restores <see cref="LocalGameAuthority.Instance"/> (used on disconnect or test teardown).</summary>
        public static void ResetToLocal()
        {
            LocalGameAuthority.Instance.ResetState();
            current = LocalGameAuthority.Instance;
        }

        /// <summary>
        /// Registers an authoritative handler for a named custom game action scoped to a specific
        /// <paramref name="targetId"/> (pass empty string for a global/wildcard handler).
        /// Multiple instances can safely register the same <paramref name="actionId"/> with different
        /// <paramref name="targetId"/>s; registering two different handlers for the exact same
        /// <c>(actionId, targetId)</c> logs a warning.
        /// </summary>
        public static void RegisterCustomActionHandler(string actionId, string targetId, CustomGameActionHandler handler)
        {
            if (string.IsNullOrWhiteSpace(actionId) || handler == null)
                return;

            var key = new ActionHandlerKey(actionId, targetId ?? string.Empty);
            if (CustomHandlers.TryGetValue(key, out CustomGameActionHandler existing))
            {
                if (existing == handler)
                    return;

                Debug.LogWarning(
                    $"[Upcycle City] Duplicate custom action handler registered for action '{key.ActionId}' " +
                    $"and target '{key.TargetId}'. Use distinct targetIds per instance (e.g. SyncKey or unique name). Replacing previous handler.");
            }

            CustomHandlers[key] = handler;
        }

        /// <summary>Registers a global/wildcard authoritative handler for <paramref name="actionId"/> (empty targetId).</summary>
        public static void RegisterCustomActionHandler(string actionId, CustomGameActionHandler handler)
        {
            RegisterCustomActionHandler(actionId, string.Empty, handler);
        }

        /// <summary>Removes a previously registered custom action handler for <c>(actionId, targetId)</c>.</summary>
        public static void UnregisterCustomActionHandler(string actionId, string targetId, CustomGameActionHandler handler = null)
        {
            if (string.IsNullOrWhiteSpace(actionId))
                return;

            var key = new ActionHandlerKey(actionId, targetId ?? string.Empty);
            if (handler == null)
            {
                CustomHandlers.Remove(key);
                return;
            }
            if (CustomHandlers.TryGetValue(key, out CustomGameActionHandler existing) && existing == handler)
                CustomHandlers.Remove(key);
        }

        /// <summary>Removes a previously registered global/wildcard custom action handler.</summary>
        public static void UnregisterCustomActionHandler(string actionId, CustomGameActionHandler handler = null)
        {
            UnregisterCustomActionHandler(actionId, string.Empty, handler);
        }

        /// <summary>
        /// Invokes a registered custom action handler on the authoritative side.
        /// Routes first to the exact <c>(actionId, targetId)</c> handler, falling back to a wildcard
        /// <c>(actionId, "")</c> handler if present. When <paramref name="senderPosition"/> is null in solo,
        /// automatically resolves the active local player's world position.
        /// </summary>
        public static CustomGameActionResult InvokeCustomActionLocally(
            ulong senderClientId,
            string actionId,
            string targetId,
            int intArg,
            float floatArg,
            Vector3? senderPosition = null)
        {
            if (string.IsNullOrWhiteSpace(actionId))
                return CustomGameActionResult.Fail("Invalid action.");

            string normalizedTarget = targetId ?? string.Empty;
            CustomGameActionHandler handler = null;
            if (!string.IsNullOrEmpty(normalizedTarget))
                CustomHandlers.TryGetValue(new ActionHandlerKey(actionId, normalizedTarget), out handler);

            if (handler == null)
                CustomHandlers.TryGetValue(new ActionHandlerKey(actionId, string.Empty), out handler);

            if (handler == null)
                return CustomGameActionResult.Fail("Action unavailable.");

            Vector3? resolvedPos = senderPosition;
            if (!resolvedPos.HasValue && !IsMultiplayer && PlayerContext.Active != null)
                resolvedPos = PlayerContext.Active.transform.position;

            var context = new CustomGameActionContext(senderClientId, normalizedTarget, intArg, floatArg, resolvedPos);
            return handler(in context);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            LocalGameAuthority.Instance.ResetState();
            current = LocalGameAuthority.Instance;
            CustomHandlers.Clear();
            LocalClientIdResolver = null;
        }
    }
}
