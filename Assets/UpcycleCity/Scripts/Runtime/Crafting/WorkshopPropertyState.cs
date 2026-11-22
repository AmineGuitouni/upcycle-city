using UpcycleCity.Core;

namespace UpcycleCity.Crafting
{
    /// <summary>
    /// Replicated state of a buyable workshop lot: whether the team owns it and which machine stands were bought
    /// (one bit per <see cref="WorkshopConfig.Machines"/> index). Packs into a single <see cref="SyncedStateRecord"/>
    /// (<c>flags</c> bit 0 = owned, <c>i0</c> = machine mask), so a purchase costs one tiny delta.
    /// </summary>
    public readonly struct WorkshopPropertyState : System.IEquatable<WorkshopPropertyState>
    {
        public const int MaxMachineSlots = 31;
        const byte OwnedFlag = 1;

        public readonly bool Owned;
        public readonly int MachineMask;

        public WorkshopPropertyState(bool owned, int machineMask)
        {
            Owned = owned;
            MachineMask = machineMask & AllMachinesMask;
        }

        /// <summary>Mask with every machine slot set.</summary>
        public static int AllMachinesMask => int.MaxValue; // bits 0..30

        public static int MaskFor(int count) =>
            count <= 0 ? 0 : count >= MaxMachineSlots ? AllMachinesMask : (1 << count) - 1;

        public bool HasMachine(int index) =>
            index >= 0 && index < MaxMachineSlots && (MachineMask & (1 << index)) != 0;

        public WorkshopPropertyState WithOwned(bool owned) => new WorkshopPropertyState(owned, MachineMask);

        public WorkshopPropertyState WithMachine(int index) =>
            index < 0 || index >= MaxMachineSlots ? this : new WorkshopPropertyState(Owned, MachineMask | (1 << index));

        public SyncedStateRecord Encode() => new SyncedStateRecord
        {
            i0 = MachineMask,
            flags = Owned ? OwnedFlag : (byte)0,
        };

        public static WorkshopPropertyState Decode(in SyncedStateRecord record) =>
            new WorkshopPropertyState((record.flags & OwnedFlag) != 0, record.i0);

        public bool Equals(WorkshopPropertyState other) => Owned == other.Owned && MachineMask == other.MachineMask;
        public override bool Equals(object obj) => obj is WorkshopPropertyState other && Equals(other);
        public override int GetHashCode() => (MachineMask * 2) ^ (Owned ? 1 : 0);
        public override string ToString() => $"Owned={Owned} Machines=0x{MachineMask:X}";
    }
}
