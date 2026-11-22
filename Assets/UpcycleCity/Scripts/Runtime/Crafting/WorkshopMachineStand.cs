using UnityEngine;
using UpcycleCity.Core;
using UpcycleCity.Interaction;
using UpcycleCity.UI;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// An empty machine stand inside a <see cref="WorkshopProperty"/>: a plinth with a small sign (name, icon, price).
    /// [E] (once the lot is owned) asks the authority to buy the machine; when bought the stand hides and the
    /// scene-placed machine on the same spot (its <see cref="WorkshopMachineLock"/>) is unlocked.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Crafting/Workshop Machine Stand")]
    public sealed class WorkshopMachineStand : MonoBehaviour, IInteractable
    {
        static readonly Color PriceReady = new Color(1f, 0.85f, 0.2f);
        static readonly Color PriceLocked = new Color(0.72f, 0.72f, 0.72f);

        [SerializeField] WorkshopProperty property;
        [Tooltip("Index of this stand in the property's WorkshopConfig.Machines (its bit in the owned mask).")]
        [SerializeField, Min(0)] int offerIndex;
        [SerializeField] WorkshopMachineLock machineLock;
        [Tooltip("Plinth + sign, hidden once the machine is bought.")]
        [SerializeField] GameObject visualRoot;
        [SerializeField] Collider interactCollider;
        [SerializeField] TextMesh nameText;
        [SerializeField] TextMesh priceText;
        [SerializeField] SpriteRenderer iconRenderer;
        [SerializeField, Min(0.05f)] float iconWorldSize = 0.42f;

        string cachedPrompt = string.Empty;
        int cachedCoins = int.MinValue;
        bool cachedOwned;

        public int OfferIndex => offerIndex;
        public WorkshopMachineLock MachineLock => machineLock;
        public WorkshopProperty Property => property;
        public Transform Transform => transform;
        public bool IsBought => property != null && property.IsMachineOwned(offerIndex);

        /// <summary>True while the empty plinth + sign is shown (the machine is not bought yet).</summary>
        public bool IsVisualShown() => visualRoot != null && visualRoot.activeSelf;

        /// <summary>Builder/test wiring.</summary>
        public void Configure(
            WorkshopProperty owner,
            int index,
            WorkshopMachineLock machine,
            GameObject visuals,
            Collider collider,
            TextMesh nameLabel,
            TextMesh priceLabel,
            SpriteRenderer icon)
        {
            property = owner;
            offerIndex = Mathf.Max(0, index);
            machineLock = machine;
            visualRoot = visuals;
            interactCollider = collider;
            nameText = nameLabel;
            priceText = priceLabel;
            iconRenderer = icon;
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
                if (property == null || IsBought)
                    return string.Empty;
                int coins = GameAuthority.Current.TeamCoins;
                bool owned = property.IsOwned;
                if (coins != cachedCoins || owned != cachedOwned)
                {
                    cachedCoins = coins;
                    cachedOwned = owned;
                    string machineName = property.GetMachineName(offerIndex);
                    int price = property.GetMachinePrice(offerIndex);
                    if (!owned)
                        cachedPrompt = $"{machineName} · {price} coins · Buy the {property.DisplayName} first";
                    else if (coins >= price)
                        cachedPrompt = $"[E] Buy {machineName} · {price} coins";
                    else
                        cachedPrompt = $"{machineName} · {price} coins · {WorkshopProperty.ShortfallText(price)}";
                }
                return cachedPrompt;
            }
        }

        public bool CanInteract(PlayerInteractor interactor) => interactor != null && property != null && !IsBought;

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor))
                return;
            Vector3 pos = transform.position;
            if (!property.IsOwned)
            {
                WorkshopProperty.NotifyRefused($"Buy the {property.DisplayName} first.", pos);
                return;
            }
            property.RequestBuyMachine(offerIndex, (success, message) =>
            {
                if (!success)
                    WorkshopProperty.NotifyRefused(message, pos);
            });
        }

        /// <summary>Re-reads the property state: hides the stand and unlocks the machine once bought.</summary>
        public void Refresh()
        {
            if (property == null)
                return;
            bool bought = IsBought;
            bool owned = property.IsOwned;
            cachedCoins = int.MinValue;

            if (visualRoot != null && visualRoot != gameObject)
                visualRoot.SetActive(!bought);
            if (interactCollider != null)
                interactCollider.enabled = !bought;
            // Edit mode: never touch the machine's renderers/colliders (they would be saved disabled into the scene)
            if (Application.isPlaying)
                machineLock?.SetLocked(!bought);

            if (nameText != null)
                nameText.text = property.GetMachineName(offerIndex);
            if (priceText != null)
            {
                int price = property.GetMachinePrice(offerIndex);
                priceText.text = owned ? $"{price} coins · [E] Buy" : $"{price} coins · locked";
                priceText.color = owned ? PriceReady : PriceLocked;
            }
            if (iconRenderer != null)
            {
                WorkshopConfig.MachineOffer offer = property.Config != null ? property.Config.GetMachine(offerIndex) : null;
                Sprite icon = offer != null && offer.machine != null ? offer.machine.Icon : null;
                IconLibrary library = IconLibrary.Active;
                if (icon == null && library != null && offer != null)
                    icon = library.Get(offer.iconKey);
                if (icon == null && library != null)
                    icon = library.Get("ui_coin");
                if (icon != null && iconRenderer.sprite != icon)
                {
                    iconRenderer.sprite = icon;
                    float size = Mathf.Max(0.01f, Mathf.Max(icon.bounds.size.x, icon.bounds.size.y));
                    iconRenderer.transform.localScale = Vector3.one * (iconWorldSize / size);
                }
                iconRenderer.color = owned ? Color.white : new Color(0.6f, 0.6f, 0.6f, 1f);
            }
        }

        /// <summary>Activity burst on the freshly installed machine (cosmetic, every peer).</summary>
        public void PlayInstalledBurst()
        {
            if (machineLock != null && machineLock.TryGetComponent(out CraftingMachineVisuals visuals))
                visuals.TriggerActivityBurst(1.2f);
        }
    }
}
