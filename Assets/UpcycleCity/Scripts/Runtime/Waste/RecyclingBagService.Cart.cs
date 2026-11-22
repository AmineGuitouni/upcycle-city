using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Player;
using UpcycleCity.Props;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// Shopping Cart actions (the third carry tier, a "moving bag"), host-authoritative through the shared game authority:
    /// <list type="bullet">
    /// <item><c>cart.push</c> / <c>cart.release</c>: one pusher at a time; the host hands the pusher the simulation.</item>
    /// <item><c>cart.put</c> / <c>cart.take</c>: move an item bag → cart / cart → bag (slot + expected id).</item>
    /// <item><c>carry.buy_cart</c>: buy a cart at the Quincaillerie (host catalog price, team wallet); the host spawns it
    /// next to the shop. One cart per player: the buyer can buy another only if theirs is gone.</item>
    /// </list>
    /// <c>carry.store</c>, <c>carry.throw</c>, <c>carry.sell</c> and <c>bag.deposit</c> also use the cart the sender pushes,
    /// else the nearest cart within <see cref="ShoppingCart.Reach"/> of them (<see cref="CartFor"/>); throw / sell address a
    /// cart slot as <see cref="CartSlotBase"/> + slot.
    /// </summary>
    public sealed partial class RecyclingBagService
    {
        public const string ActionCartPush = "cart.push";
        public const string ActionCartRelease = "cart.release";
        public const string ActionCartPut = "cart.put";
        public const string ActionCartTake = "cart.take";
        public const string ActionBuyCart = "carry.buy_cart";

        /// <summary>Slot values from here address the cart in <c>carry.throw</c> / <c>carry.sell</c> (slot = value − base).</summary>
        public const int CartSlotBase = 100;

        /// <summary>Resources path of the networked cart prefab spawned when one is bought.</summary>
        public const string CartPrefabResource = "ShoppingCart";

        // Host: which cart each player bought (placement id), so each player owns at most one
        readonly Dictionary<ulong, string> boughtCarts = new Dictionary<ulong, string>();
        int cartSerial;

        void RegisterCartHandlers()
        {
            GameAuthority.RegisterCustomActionHandler(ActionCartPush, HandleCartPush);
            GameAuthority.RegisterCustomActionHandler(ActionCartRelease, HandleCartRelease);
            GameAuthority.RegisterCustomActionHandler(ActionCartPut, HandleCartPut);
            GameAuthority.RegisterCustomActionHandler(ActionCartTake, HandleCartTake);
            GameAuthority.RegisterCustomActionHandler(ActionBuyCart, HandleBuyCart);
        }

        void UnregisterCartHandlers()
        {
            GameAuthority.UnregisterCustomActionHandler(ActionCartPush, HandleCartPush);
            GameAuthority.UnregisterCustomActionHandler(ActionCartRelease, HandleCartRelease);
            GameAuthority.UnregisterCustomActionHandler(ActionCartPut, HandleCartPut);
            GameAuthority.UnregisterCustomActionHandler(ActionCartTake, HandleCartTake);
            GameAuthority.UnregisterCustomActionHandler(ActionBuyCart, HandleBuyCart);
        }

        // ------------------------------------------------------------ requests (any machine)

        public static void RequestPushCart(ShoppingCart cart, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionCartPush, cart.SyncKey, 0, 0f, onResult);

        /// <summary>Lets go of the cart; it rolls on at <paramref name="forwardSpeed"/> (m/s along its facing).</summary>
        public static void RequestReleaseCart(ShoppingCart cart, float forwardSpeed, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionCartRelease, cart.SyncKey, 0, forwardSpeed, onResult);

        /// <summary>Moves the bag item in <paramref name="bagSlot"/> (expected <paramref name="id"/>) into the cart.</summary>
        public static void RequestPutInCart(ShoppingCart cart, int bagSlot, int id, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionCartPut, cart.SyncKey, bagSlot, id, onResult);

        /// <summary>Moves the cart item in <paramref name="cartSlot"/> (expected <paramref name="id"/>) into the bag.</summary>
        public static void RequestTakeFromCart(ShoppingCart cart, int cartSlot, int id, Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionCartTake, cart.SyncKey, cartSlot, id, onResult);

        public static void RequestBuyCart(Action<bool, string> onResult = null) =>
            GameAuthority.Current.RequestCustomAction(ActionBuyCart, string.Empty, 0, 0f, onResult);

        /// <summary>Local: what the bag and the usable cart (pushed or within reach) hold that the given bin takes.</summary>
        public static int LocalCountMatching(bool organic, WasteStream stream)
        {
            RecyclingBag bag = LocalBag;
            ShoppingCart cart = ShoppingCart.FindForLocal();
            return (bag != null ? bag.CountMatching(organic, stream) : 0)
                + (cart != null && cart.Storage != null ? cart.Storage.CountMatching(organic, stream) : 0);
        }

        /// <summary>Local: items in the bag plus the usable cart.</summary>
        public static int LocalCarriedCount
        {
            get
            {
                RecyclingBag bag = LocalBag;
                ShoppingCart cart = ShoppingCart.FindForLocal();
                return (bag != null ? bag.Count : 0) + (cart != null ? cart.Count : 0);
            }
        }

        // ------------------------------------------------------------ host helpers

        /// <summary>Host: the cart the sender pushes, else the nearest one within <see cref="ShoppingCart.Reach"/> of them.</summary>
        static ShoppingCart CartObjectFor(in CustomGameActionContext context)
        {
            ShoppingCart cart = ShoppingCart.FindFor(context.SenderClientId, context.SenderPosition);
            return cart != null && cart.Storage != null ? cart : null;
        }

        static CartStorage CartFor(in CustomGameActionContext context) => CartObjectFor(context)?.Storage;

        /// <summary>Host: the cart a cart.* request targets, if the sender may use it (pushing it or within reach).</summary>
        static bool TryResolveCart(in CustomGameActionContext context, out ShoppingCart cart, out CustomGameActionResult failure)
        {
            failure = default;
            cart = ShoppingCart.FindBySyncKey(context.TargetId);
            if (cart == null || cart.Storage == null)
            {
                failure = CustomGameActionResult.Fail("That cart is gone.");
                return false;
            }
            if (cart.PusherClientId != context.SenderClientId && context.SenderPosition.HasValue
                && !context.IsWithinRange(cart.transform.position, ShoppingCart.Reach + 1f))
            {
                failure = CustomGameActionResult.Fail("Too far from the cart.");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------ host handlers

        CustomGameActionResult HandleCartPush(in CustomGameActionContext context)
        {
            if (!TryResolveCart(context, out ShoppingCart cart, out CustomGameActionResult failure))
                return failure;
            ulong sender = context.SenderClientId;
            if (cart.PusherClientId == sender)
                return CustomGameActionResult.Ok();
            if (cart.IsPushedByAnyone)
                return CustomGameActionResult.Fail("Someone else is pushing this cart.");
            for (int i = 0; i < ShoppingCart.All.Count; i++)
            {
                ShoppingCart other = ShoppingCart.All[i];
                if (other != null && other != cart && other.PusherClientId == sender)
                    return CustomGameActionResult.Fail("You're already pushing a cart.");
            }

            cart.AuthoritativeSetPusher(sender);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok();
        }

        CustomGameActionResult HandleCartRelease(in CustomGameActionContext context)
        {
            ShoppingCart cart = ShoppingCart.FindBySyncKey(context.TargetId);
            if (cart == null || cart.Storage == null)
                return CustomGameActionResult.Fail("That cart is gone.");
            if (cart.PusherClientId != context.SenderClientId)
                return CustomGameActionResult.Ok();   // already free (or someone else's now): nothing to undo
            cart.AuthoritativeRelease(context.FloatArg);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok();
        }

        CustomGameActionResult HandleCartPut(in CustomGameActionContext context)
        {
            if (!TryResolveCart(context, out ShoppingCart cart, out CustomGameActionResult failure))
                return failure;
            RecyclingBag bag = GetHostBag(context.SenderClientId);
            int slot = context.IntArg, expected = Mathf.RoundToInt(context.FloatArg);
            if (slot < 0 || slot >= bag.Count || bag[slot] != expected)
                return CustomGameActionResult.Fail("That item isn't there any more.");
            if (cart.Storage.IsFull)
                return CustomGameActionResult.Fail($"The cart is full ({cart.Storage.Count}/{cart.Storage.Capacity}).");

            bag.RemoveAt(slot);
            cart.Storage.TryAdd(expected);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok($"{CarryItems.Resolve(expected).Name} → cart  ({cart.Storage.Count}/{cart.Storage.Capacity})");
        }

        CustomGameActionResult HandleCartTake(in CustomGameActionContext context)
        {
            if (!TryResolveCart(context, out ShoppingCart cart, out CustomGameActionResult failure))
                return failure;
            CartStorage storage = cart.Storage;
            int slot = context.IntArg, expected = Mathf.RoundToInt(context.FloatArg);
            if (slot < 0 || slot >= storage.Count || storage[slot] != expected)
                return CustomGameActionResult.Fail("That item isn't there any more.");
            RecyclingBag bag = GetHostBag(context.SenderClientId);
            if (bag.IsFull)
                return CustomGameActionResult.Fail(FullMessage(bag));

            storage.RemoveAt(slot);
            bag.TryAdd(expected);
            GameAuthority.Current.FlushSyncedStates();
            return CustomGameActionResult.Ok($"{CarryItems.Resolve(expected).Name} → {(bag.HasBag ? "bag" : "arms")}  ({bag.Count}/{bag.Capacity})");
        }

        CustomGameActionResult HandleBuyCart(in CustomGameActionContext context)
        {
            ulong buyer = context.SenderClientId;
            RecyclingBag bag = GetHostBag(buyer);
            if (boughtCarts.TryGetValue(buyer, out string ownedId) && FindCartByPlacementId(ownedId) != null)
                return CustomGameActionResult.Fail("You already have a cart.");

            // The price comes from the host's own shop catalogs, never from the client
            Shops.ShopCounter seller = null;
            int price = 0;
            foreach (Shops.ShopCounter counter in FindObjectsByType<Shops.ShopCounter>(FindObjectsInactive.Exclude))
            {
                if (counter.Catalog == null || !counter.Catalog.TryFindOffer(CarryItems.CartUpgradeItemId, 1, out Shops.ShopCatalog.Offer offer))
                    continue;
                if (context.SenderPosition.HasValue && !context.IsWithinRange(counter.transform.position, ReachRange + 2f))
                    continue;
                seller = counter;
                price = offer.price;
                break;
            }
            if (seller == null)
                return CustomGameActionResult.Fail("Shopping carts are sold at the Quincaillerie.");

            GameObject prefab = Resources.Load<GameObject>(CartPrefabResource);
            if (prefab == null)
                return CustomGameActionResult.Fail("No cart in stock (missing Resources/ShoppingCart prefab).");
            if (!GameAuthority.Current.TrySpendTeamCoins(price))
                return CustomGameActionResult.Fail($"Not enough coins (need {price}).");

            ComputeCartSpawn(seller.transform.position, context.SenderPosition, out Vector3 position, out Quaternion rotation);
            string shopName = seller.Catalog != null && !string.IsNullOrEmpty(seller.Catalog.ShopName) ? seller.Catalog.ShopName : "the shop";
            string placementId = $"cart_{buyer}_{++cartSerial}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";
            GameObject spawned = GameAuthority.Current.SpawnNetworkedPrefab(prefab, position, rotation,
                go => go.GetComponent<ShoppingCart>()?.ApplyPlacementId(placementId));
            if (spawned == null)
            {
                GameAuthority.Current.CreditTeamCoins(price);
                return CustomGameActionResult.Fail("Couldn't wheel the cart out.");
            }

            boughtCarts[buyer] = placementId;
            bag.SetHasCart(true);
            GameAuthority.Current.FlushSyncedStates();
            string message = $"Your cart is waiting outside the {shopName}: push it with [E] ({CarryItems.CartCapacity} items).";
            GameAuthority.Current.SendTargetedFeedback(buyer, $"Your cart is waiting outside the {shopName}!",
                new Color(0.18f, 0.55f, 0.34f), PlayerFeedbackEvent.ShopPurchase, position);
            return CustomGameActionResult.Ok(message);
        }

        float nextOwnershipCheck;
        readonly List<ulong> lostCarts = new List<ulong>(4);

        // Host: a bought cart that is gone (scene change, despawn) can be bought again
        void TickCartOwnership()
        {
            if (boughtCarts.Count == 0 || Time.unscaledTime < nextOwnershipCheck || !GameAuthority.IsAuthoritative)
                return;
            nextOwnershipCheck = Time.unscaledTime + 1f;
            lostCarts.Clear();
            foreach (KeyValuePair<ulong, string> pair in boughtCarts)
            {
                if (FindCartByPlacementId(pair.Value) == null)
                    lostCarts.Add(pair.Key);
            }
            for (int i = 0; i < lostCarts.Count; i++)
            {
                boughtCarts.Remove(lostCarts[i]);
                if (hostBags.TryGetValue(lostCarts[i], out RecyclingBag bag))
                    bag.SetHasCart(false);
            }
            if (lostCarts.Count > 0)
                GameAuthority.Current.FlushSyncedStates();
        }

        /// <summary>The shop building next to a counter: the parent of the nearest <c>ShopPoint</c> marker within 8 m.</summary>
        static Transform FindShopBuilding(Vector3 counter)
        {
            Transform best = null;
            float bestSqr = 64f;
            foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            {
                if (t.parent == null || !t.name.StartsWith("ShopPoint", StringComparison.Ordinal))
                    continue;
                float d = (t.position - counter).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = t.parent;
                }
            }
            return best;
        }

        static ShoppingCart FindCartByPlacementId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            for (int i = 0; i < ShoppingCart.All.Count; i++)
            {
                if (ShoppingCart.All[i] != null && ShoppingCart.All[i].PlacementId == id)
                    return ShoppingCart.All[i];
            }
            return null;
        }

        /// <summary>
        /// On the pavement in front of the shop's facade (the building holding the shop's <c>ShopPoint</c> marker, whose
        /// +Z faces the street), on the ground, clear of props, basket along the street. Falls back to between the counter
        /// and the buyer for shops without a building.
        /// </summary>
        static void ComputeCartSpawn(Vector3 counter, Vector3? buyer, out Vector3 position, out Quaternion rotation)
        {
            Transform building = FindShopBuilding(counter);
            if (building != null)
            {
                Vector3 outward = Vector3.ProjectOnPlane(building.forward, Vector3.up).normalized;
                Vector3 along = Vector3.Cross(Vector3.up, outward);
                // Level with the counter along the facade, a few metres out from the facade
                float offsetAlong = Vector3.Dot(counter - building.position, along);
                Vector3 front = building.position + along * offsetAlong;
                Vector3 chosen = front + outward * 2.6f;
                for (int attempt = 0; attempt < 7; attempt++)
                {
                    float step = (attempt + 1) / 2 * 1.4f * (attempt % 2 == 0 ? 1f : -1f);
                    Vector3 candidate = front + outward * 2.6f + along * (attempt == 0 ? 0f : step);
                    if (!Physics.CheckBox(candidate + Vector3.up * 0.75f, new Vector3(0.35f, 0.6f, 0.55f), Quaternion.LookRotation(along),
                            ~(GameLayers.MaskOf(GameLayers.Player) | GameLayers.MaskOf(GameLayers.IgnoreRaycast) | GameLayers.MaskOf(GameLayers.TriggerZone)),
                            QueryTriggerInteraction.Ignore))
                    {
                        chosen = candidate;
                        break;
                    }
                }
                float y = building.position.y;
                if (Physics.Raycast(chosen + Vector3.up * 2f, Vector3.down, out RaycastHit ground, 6f,
                        GameLayers.MaskOf(GameLayers.Default) | GameLayers.MaskOf(GameLayers.Environment), QueryTriggerInteraction.Ignore))
                    y = ground.point.y;
                position = new Vector3(chosen.x, y + 0.05f, chosen.z);
                rotation = Quaternion.LookRotation(along, Vector3.up);
                return;
            }

            Vector3 toBuyer = buyer.HasValue ? buyer.Value - counter : Vector3.forward;
            toBuyer.y = 0f;
            Vector3 dir = toBuyer.sqrMagnitude > 0.01f ? toBuyer.normalized : Vector3.forward;
            float distance = buyer.HasValue ? Mathf.Clamp(toBuyer.magnitude * 0.5f, 1.2f, 2f) : 1.6f;
            Vector3 guess = counter + dir * distance;
            float groundY = buyer.HasValue ? buyer.Value.y : counter.y;

            Vector3 side = Vector3.Cross(Vector3.up, dir);
            if (Physics.Raycast(guess + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 6f,
                    GameLayers.MaskOf(GameLayers.Default) | GameLayers.MaskOf(GameLayers.Environment), QueryTriggerInteraction.Ignore))
                groundY = hit.point.y;
            // Nudge sideways so it doesn't appear inside the buyer
            if (buyer.HasValue && (new Vector3(buyer.Value.x, 0f, buyer.Value.z) - new Vector3(guess.x, 0f, guess.z)).sqrMagnitude < 1f)
                guess += side * 1f;

            position = new Vector3(guess.x, groundY + 0.05f, guess.z);
            rotation = Quaternion.LookRotation(-dir, Vector3.up);   // basket towards the shop, handle towards the buyer
        }
    }
}
