using System.Collections.Generic;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Interaction
{
    /// <summary>
    /// Registry mapping deterministic <see cref="ITrashHopper.HopperKey"/> hashes to active hoppers.
    /// Keeps trash-deposit routing independent of <see cref="ISyncedStateProvider"/> and world position.
    /// </summary>
    public static class TrashHopperRegistry
    {
        static readonly Dictionary<uint, ITrashHopper> ByHash = new Dictionary<uint, ITrashHopper>(32);

        public static void Register(ITrashHopper hopper)
        {
            if (hopper == null || string.IsNullOrWhiteSpace(hopper.HopperKey))
                return;

            uint hash = SyncedStateRegistry.ComputeKeyHash(hopper.HopperKey);
            if (ByHash.TryGetValue(hash, out ITrashHopper existing)
                && !ReferenceEquals(existing, hopper)
                && existing != null
                && !string.Equals(existing.HopperKey, hopper.HopperKey, System.StringComparison.Ordinal))
            {
                Debug.LogError(
                    $"[Upcycle City] TrashHopperRegistry key hash collision (0x{hash:X8}) between " +
                    $"'{existing.HopperKey}' and '{hopper.HopperKey}'. Keeping existing hopper.");
                return;
            }

            ByHash[hash] = hopper;
        }

        public static void Unregister(ITrashHopper hopper, string previousKey = null)
        {
            if (hopper == null)
                return;

            string key = string.IsNullOrWhiteSpace(previousKey) ? hopper.HopperKey : previousKey;
            if (string.IsNullOrWhiteSpace(key))
                return;

            uint hash = SyncedStateRegistry.ComputeKeyHash(key);
            if (ByHash.TryGetValue(hash, out ITrashHopper existing) && ReferenceEquals(existing, hopper))
                ByHash.Remove(hash);
        }

        public static bool TryGet(uint keyHash, out ITrashHopper hopper)
        {
            return ByHash.TryGetValue(keyHash, out hopper) && hopper != null;
        }

        public static void Clear() => ByHash.Clear();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => ByHash.Clear();
    }
}
