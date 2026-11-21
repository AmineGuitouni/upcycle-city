namespace UpcycleCity.Inventory
{
    /// <summary>
    /// Categories allowed inside the player inventory. Trash is deliberately excluded.
    /// </summary>
    public enum ItemCategory
    {
        Tool = 0,
        Usable = 1,
        Placeable = 2,
        UpgradePart = 3,
    }
}
