using System;

namespace UpcycleCity.Audio
{
    /// <summary>
    /// Audio mixing bus categories with independent volume and mute controls.
    /// </summary>
    public enum SoundCategory
    {
        Master = 0,
        SFX = 1,
        Music = 2,
        Ambience = 3,
        UI = 4,
    }

    /// <summary>
    /// Strongly-typed identifiers for core gameplay, material, economy, and UI sound cues.
    /// <para>
    /// New sounds can either be added to this enum OR played directly by <see cref="SoundCue"/> reference
    /// or string key via <see cref="SoundLibrary"/> without modifying any existing scripts.
    /// </para>
    /// </summary>
    public enum SoundId
    {
        None = 0,

        // Locomotion
        FootstepWalk = 1,
        FootstepRun = 2,
        FootstepCrouch = 3,
        Jump = 4,
        Land = 5,

        // Grabbing & Throwing
        GrabGeneric = 10,
        GrabPaper = 11,
        GrabPlastic = 12,
        GrabGlass = 13,
        GrabMetal = 14,
        GrabBulky = 15,
        LitterPickerSnap = 16,
        Throw = 17,
        Drop = 18,

        // Material Physics Impacts (3D spatialized)
        ImpactPaper = 20,
        ImpactPlastic = 21,
        ImpactGlass = 22,
        ImpactMetal = 23,
        ImpactBulky = 24,

        // Sorting, Recycling & Economy
        SortCorrect = 30,
        SortWrong = 31,
        SellTrash = 32,
        SpendCoins = 33,
        EarnCoins = 34,
        BinService = 35,

        // Tools, Pickups & World Interaction
        PickupItem = 40,
        ToolBroomSweep = 41,
        ToolWrenchRepair = 42,
        ToolUseGeneric = 43,
        Interact = 44,
        CartGrab = 45,

        // UI & Alerts
        HotbarSwitch = 50,
        UiClick = 51,
        InventoryFull = 52,
        AttentionAlert = 53,
    }

    /// <summary>
    /// Lightweight generational handle referencing an active pooled voice in <see cref="SoundManager"/>.
    /// Safe to store and pass back to stop or fade out a sound even after the voice has recycled.
    /// </summary>
    public readonly struct SoundHandle : IEquatable<SoundHandle>
    {
        internal readonly int SlotIndex;
        internal readonly int Generation;

        public static SoundHandle Invalid => new SoundHandle(-1, 0);
        public bool IsValid => SlotIndex >= 0 && Generation > 0;

        internal SoundHandle(int slotIndex, int generation)
        {
            SlotIndex = slotIndex;
            Generation = generation;
        }

        public bool Equals(SoundHandle other) => SlotIndex == other.SlotIndex && Generation == other.Generation;
        public override bool Equals(object obj) => obj is SoundHandle other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SlotIndex, Generation);
        public static bool operator ==(SoundHandle a, SoundHandle b) => a.Equals(b);
        public static bool operator !=(SoundHandle a, SoundHandle b) => !a.Equals(b);
    }
}
