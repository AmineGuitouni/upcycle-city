using System;
using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;

namespace UpcycleCity.Shops
{
    /// <summary>
    /// The interactable part of a shop: sits on the shopkeeper (or the counter) and opens the
    /// <see cref="ShopUI"/> window for its <see cref="ShopCatalog"/> when the player presses E.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Shops/Shop Counter")]
    public sealed class ShopCounter : MonoBehaviour, IInteractable
    {
        static readonly List<ShopCounter> ActiveCountersList = new List<ShopCounter>(8);

        [SerializeField] ShopCatalog catalog;
        [SerializeField] Shopkeeper keeper;

        string shopKey;

        /// <summary>All currently active shop counters in the scene (used for authoritative server price lookup).</summary>
        public static IReadOnlyList<ShopCounter> ActiveCounters => ActiveCountersList;

        /// <summary>Deterministic key identifying this counter across host and clients.</summary>
        public string ShopKey => shopKey ??= SyncedStateRegistry.BuildSpatialKey(
            "shop",
            catalog != null ? catalog.ShopName : gameObject.name,
            transform.position);

        public ShopCatalog Catalog => catalog;
        public Shopkeeper Keeper => keeper;
        public Transform Transform => transform;

        public string InteractionPrompt => catalog == null
            ? "[E] Shop"
            : $"[E] Talk to {catalog.KeeperName} · {catalog.ShopName}";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ActiveCountersList.Clear();

        void OnEnable()
        {
            if (!ActiveCountersList.Contains(this))
                ActiveCountersList.Add(this);
        }

        void OnDisable()
        {
            ActiveCountersList.Remove(this);
        }

        public void Configure(ShopCatalog shopCatalog, Shopkeeper shopkeeper)
        {
            catalog = shopCatalog;
            keeper = shopkeeper;
            shopKey = null;
        }

        /// <summary>
        /// Authoritatively resolves a <see cref="ShopCatalog.Offer"/> on the host (or solo) from active
        /// <see cref="ShopCounter"/> instances without trusting any client-supplied price.
        /// </summary>
        public static bool TryResolveOffer(
            string shopKey,
            string itemId,
            int quantity,
            out ShopCounter counter,
            out ShopCatalog.Offer offer)
        {
            counter = null;
            offer = null;
            if (string.IsNullOrWhiteSpace(itemId) || quantity <= 0)
                return false;

            if (!string.IsNullOrWhiteSpace(shopKey))
            {
                for (int i = 0; i < ActiveCountersList.Count; i++)
                {
                    ShopCounter candidate = ActiveCountersList[i];
                    if (candidate == null || candidate.Catalog == null)
                        continue;
                    if (!string.Equals(candidate.ShopKey, shopKey, StringComparison.Ordinal))
                        continue;
                    if (candidate.Catalog.TryFindOffer(itemId, quantity, out offer))
                    {
                        counter = candidate;
                        return true;
                    }
                    return false;
                }
                return false;
            }

            // Fallback when no explicit shopKey is supplied: search all active counters in scene
            for (int i = 0; i < ActiveCountersList.Count; i++)
            {
                ShopCounter candidate = ActiveCountersList[i];
                if (candidate == null || candidate.Catalog == null)
                    continue;
                if (candidate.Catalog.TryFindOffer(itemId, quantity, out offer))
                {
                    counter = candidate;
                    return true;
                }
            }

            return false;
        }

        public bool CanInteract(PlayerInteractor interactor) =>
            catalog != null && ShopUI.Instance != null && !ShopUI.Instance.IsOpen;

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor))
                return;
            keeper?.Greet(force: true, speak: false);
            ShopUI.Instance.Open(this, interactor);
        }

        public void OnFocusEnter(PlayerInteractor interactor) => keeper?.SetFocused(true);

        public void OnFocusExit(PlayerInteractor interactor) => keeper?.SetFocused(false);
    }
}
