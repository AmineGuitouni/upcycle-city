using UnityEngine;
using UpcycleCity.Inventory;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Implemented by world targets that react to an equipped tool or usable item
    /// (e.g. jammed machines for Wrench, street grime for Broom, benches for Paint Tin).
    /// </summary>
    public interface IToolTarget
    {
        Transform Transform { get; }
        string GetToolPrompt(IInventoryItem equippedItem);
        bool CanUseTool(PlayerInteractor interactor, IInventoryItem equippedItem);
        bool UseTool(PlayerInteractor interactor, IInventoryItem equippedItem);
    }
}
