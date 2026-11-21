using UnityEngine;
using UpcycleCity.Interaction;
using UpcycleCity.Player;

namespace UpcycleCity.Inventory
{
    /// <summary>
    /// A collectible player item or coin pouch in the world that goes into PlayerInventory.
    /// Bobbing/rotating makes usable gear visually distinct from physical TrashItems.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Inventory/World Pickup Item")]
    public sealed class WorldPickupItem : MonoBehaviour, IPickupItem
    {
        [Header("Payload (Item or Coins)")]
        [SerializeField] ItemDefinition item;
        [SerializeField, Min(1)] int quantity = 1;
        [SerializeField, Min(0)] int coinAmount;

        [Header("Presentation")]
        [SerializeField] bool animateBob = true;
        [SerializeField] float bobAmplitude = 0.08f;
        [SerializeField] float bobSpeed = 2.4f;
        [SerializeField] float spinSpeed = 55f;

        Vector3 basePosition;
        bool collected;

        public Transform Transform => transform;

        public string PickupPrompt
        {
            get
            {
                if (coinAmount > 0 && item == null)
                    return $"[E] Pick up +{coinAmount} Coins";
                if (item != null)
                    return quantity > 1 ? $"[E] Pick up {item.DisplayName} x{quantity}" : $"[E] Pick up {item.DisplayName}";
                return "[E] Pick up";
            }
        }

        public bool CanCollect(PlayerInteractor interactor)
        {
            return !collected && interactor != null && interactor.Inventory != null;
        }

        public void ConfigureItem(ItemDefinition definition, int count = 1)
        {
            item = definition;
            quantity = Mathf.Max(1, count);
            coinAmount = 0;
        }

        public void ConfigureCoins(int coins)
        {
            item = null;
            quantity = 1;
            coinAmount = Mathf.Max(1, coins);
        }

        void Start()
        {
            basePosition = transform.position;
        }

        void Update()
        {
            if (!animateBob || collected)
                return;

            float offset = Mathf.Sin(Time.time * bobSpeed) * bobAmplitude;
            transform.position = basePosition + Vector3.up * offset;
            transform.Rotate(0f, spinSpeed * Time.deltaTime, 0f, Space.World);
        }

        public bool TryCollect(PlayerInteractor interactor)
        {
            if (!CanCollect(interactor))
                return false;

            bool success = false;
            if (coinAmount > 0)
            {
                interactor.Inventory.AddCoins(coinAmount);
                Color coinCol = new Color(0.95f, 0.76f, 0.11f);
                interactor.NotifyFeedback($"+{coinAmount} Coins", coinCol);
                interactor.Feedback?.Emit(PlayerFeedbackEvent.PickupCoins, transform.position, 0.8f, $"+{coinAmount} Coins", coinCol);
                success = true;
            }

            if (item != null)
            {
                if (!interactor.Inventory.TryAddItem(item, quantity))
                {
                    Color warnCol = new Color(0.85f, 0.3f, 0.25f);
                    interactor.NotifyFeedback("Hotbar full!", warnCol);
                    interactor.Feedback?.Emit(PlayerFeedbackEvent.InventoryFull, transform.position, 0.7f, "Hotbar full!", warnCol);
                    return success;
                }
                interactor.NotifyFeedback($"Picked up {item.DisplayName}", item.AccentColor);
                interactor.Feedback?.Emit(PlayerFeedbackEvent.PickupItem, transform.position, 0.9f, item.DisplayName, item.AccentColor);
                success = true;
            }

            if (success)
            {
                collected = true;
                Destroy(gameObject);
            }
            return success;
        }
    }
}
