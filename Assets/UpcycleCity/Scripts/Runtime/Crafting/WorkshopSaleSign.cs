using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// The "FOR SALE" board at the entrance of a <see cref="WorkshopProperty"/>. [E] asks the authority to buy the lot
    /// with team coins; once owned the board turns into the lot's name plate and stops being interactable.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Workshop Sale Sign")]
    public sealed class WorkshopSaleSign : MonoBehaviour, IInteractable
    {
        static readonly Color ForSaleTitle = new Color(1f, 0.85f, 0.2f);
        static readonly Color OwnedTitle = new Color(0.55f, 0.95f, 0.62f);

        [SerializeField] WorkshopProperty property;
        [SerializeField] TextMesh titleText;
        [SerializeField] TextMesh detailText;
        [SerializeField] Collider interactCollider;

        string cachedPrompt = string.Empty;
        int cachedCoins = int.MinValue;
        int cachedPrice = int.MinValue;

        public WorkshopProperty Property => property;
        public Transform Transform => transform;

        /// <summary>Builder/test wiring.</summary>
        public void Configure(WorkshopProperty owner, TextMesh title, TextMesh detail, Collider collider)
        {
            property = owner;
            titleText = title;
            detailText = detail;
            interactCollider = collider;
        }

        internal void Bind(WorkshopProperty owner)
        {
            property = owner;
            Refresh();
        }

        public string InteractionPrompt
        {
            get
            {
                if (property == null || property.IsOwned)
                    return string.Empty;
                int coins = GameAuthority.Current.TeamCoins;
                int price = property.PropertyPrice;
                if (coins != cachedCoins || price != cachedPrice)
                {
                    cachedCoins = coins;
                    cachedPrice = price;
                    cachedPrompt = coins >= price
                        ? $"[E] Buy {property.DisplayName} · {price} coins"
                        : $"{property.DisplayName} · {price} coins · {WorkshopProperty.ShortfallText(price)}";
                }
                return cachedPrompt;
            }
        }

        public bool CanInteract(PlayerInteractor interactor) => interactor != null && property != null && !property.IsOwned;

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor))
                return;
            Vector3 pos = transform.position;
            property.RequestBuyProperty((success, message) =>
            {
                if (!success)
                    WorkshopProperty.NotifyRefused(message, pos);
            });
        }

        /// <summary>Re-reads the property state into the board text.</summary>
        public void Refresh()
        {
            if (property == null)
                return;
            bool owned = property.IsOwned;
            cachedCoins = int.MinValue;
            if (titleText != null)
            {
                titleText.text = owned ? property.DisplayName.ToUpperInvariant() : "FOR SALE";
                titleText.color = owned ? OwnedTitle : ForSaleTitle;
            }
            if (detailText != null)
            {
                detailText.text = owned
                    ? "Team workshop · open"
                    : $"{property.DisplayName}\n{property.PropertyPrice} coins · [E] Buy";
            }
            if (interactCollider != null)
                interactCollider.enabled = !owned;
        }
    }
}
