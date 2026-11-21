using System;

namespace UpcycleCity.Inventory
{
    [Serializable]
    public struct InventorySlot
    {
        public IInventoryItem Item;
        public int Count;

        public bool IsEmpty => Item == null || Count <= 0;

        public void Clear()
        {
            Item = null;
            Count = 0;
        }
    }
}
