using UnityEngine;
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
        [SerializeField] ShopCatalog catalog;
        [SerializeField] Shopkeeper keeper;

        public ShopCatalog Catalog => catalog;
        public Shopkeeper Keeper => keeper;
        public Transform Transform => transform;

        public string InteractionPrompt => catalog == null
            ? "[E] Shop"
            : $"[E] Talk to {catalog.KeeperName} · {catalog.ShopName}";

        public void Configure(ShopCatalog shopCatalog, Shopkeeper shopkeeper)
        {
            catalog = shopCatalog;
            keeper = shopkeeper;
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
