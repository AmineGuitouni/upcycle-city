using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Core
{
    /// <summary>
    /// Compact, allocation-free value payload replicated by the generic state registry.
    /// Systems pack their live numeric/flag state into these slots so new gameplay systems
    /// (districts, bins, factories, crates, future machines, events) replicate automatically
    /// without defining one-off network variables.
    /// </summary>
    public struct SyncedStateRecord : IEquatable<SyncedStateRecord>
    {
        public float v0;
        public float v1;
        public float v2;
        public float v3;
        public int i0;
        public int i1;
        public byte flags;

        public bool ApproximatelyEquals(in SyncedStateRecord other, float epsilon = 0.001f)
        {
            return Mathf.Abs(v0 - other.v0) <= epsilon
                && Mathf.Abs(v1 - other.v1) <= epsilon
                && Mathf.Abs(v2 - other.v2) <= epsilon
                && Mathf.Abs(v3 - other.v3) <= epsilon
                && i0 == other.i0
                && i1 == other.i1
                && flags == other.flags;
        }

        public bool Equals(SyncedStateRecord other) => ApproximatelyEquals(in other, 0.0001f);

        public override bool Equals(object obj) => obj is SyncedStateRecord other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(v0, v1, v2, v3, i0, i1, flags);
    }

    /// <summary>
    /// Implement on any scene object or manager that wants host-authoritative state replication in co-op.
    /// Register in <c>OnEnable</c> and unregister in <c>OnDisable</c> via <see cref="SyncedStateRegistry"/>.
    /// </summary>
    public interface ISyncedStateProvider
    {
        /// <summary>Deterministic unique key across host and clients (e.g. <c>"bin:Bin_Avenue_01"</c>).</summary>
        string SyncKey { get; }

        /// <summary>Captures current authoritative state on the host.</summary>
        SyncedStateRecord CaptureSyncState();

        /// <summary>Applies replicated host state on non-host clients.</summary>
        void ApplySyncState(in SyncedStateRecord state);
    }

    /// <summary>
    /// Instantiable state-provider registry. Production code uses <see cref="SyncedStateRegistry.Default"/>;
    /// multi-instance tests can construct separate registries per host/client to verify real client-side
    /// <see cref="ISyncedStateProvider.ApplySyncState"/> execution without static cross-talk.
    /// </summary>
    public sealed class SyncedStateRegistryInstance
    {
        readonly List<ISyncedStateProvider> providers = new List<ISyncedStateProvider>(64);
        readonly Dictionary<uint, ISyncedStateProvider> byHash = new Dictionary<uint, ISyncedStateProvider>(64);

        /// <summary>Raised when a provider registers or unregisters so an active replicator can refresh immediately.</summary>
        public event Action Changed;

        /// <summary>Raised when a provider is unregistered and its key hash is no longer owned by any active provider.</summary>
        public event Action<uint> ProviderUnregistered;

        /// <summary>Read-only list of currently active state providers in this registry.</summary>
        public IReadOnlyList<ISyncedStateProvider> AllProviders => providers;

        /// <summary>Registers a provider by deterministic FNV-1a hash of its <see cref="ISyncedStateProvider.SyncKey"/>.</summary>
        public void Register(ISyncedStateProvider provider)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.SyncKey))
                return;

            uint hash = SyncedStateRegistry.ComputeKeyHash(provider.SyncKey);
            if (byHash.TryGetValue(hash, out ISyncedStateProvider existing))
            {
                if (ReferenceEquals(existing, provider))
                    return;

                if (!string.Equals(existing.SyncKey, provider.SyncKey, StringComparison.Ordinal))
                {
                    Debug.LogError(
                        $"[Upcycle City] SyncedStateRegistry key hash collision (0x{hash:X8}) between existing key " +
                        $"'{existing.SyncKey}' and new key '{provider.SyncKey}'. Keeping existing provider; rename one key to resolve.");
                    return;
                }

                providers.Remove(existing);
            }

            byHash[hash] = provider;
            providers.Add(provider);
            Changed?.Invoke();
        }

        /// <summary>Unregisters a provider when disabled, destroyed, or before its <see cref="ISyncedStateProvider.SyncKey"/> changes.</summary>
        public void Unregister(ISyncedStateProvider provider, string previousSyncKey = null)
        {
            if (provider == null)
                return;

            bool removedHash = false;
            uint removedKeyHash = 0u;
            string key = string.IsNullOrWhiteSpace(previousSyncKey) ? provider.SyncKey : previousSyncKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                uint hash = SyncedStateRegistry.ComputeKeyHash(key);
                if (byHash.TryGetValue(hash, out ISyncedStateProvider existing) && ReferenceEquals(existing, provider))
                {
                    byHash.Remove(hash);
                    removedHash = true;
                    removedKeyHash = hash;
                }
            }

            if (providers.Remove(provider))
            {
                if (removedHash)
                    ProviderUnregistered?.Invoke(removedKeyHash);
                Changed?.Invoke();
            }
        }

        /// <summary>Looks up a registered provider by its deterministic key hash.</summary>
        public bool TryGet(uint keyHash, out ISyncedStateProvider provider)
        {
            return byHash.TryGetValue(keyHash, out provider) && provider != null;
        }

        /// <summary>Applies a replicated record directly to the matching provider, returning true if found.</summary>
        public bool TryApply(uint keyHash, in SyncedStateRecord state)
        {
            if (!TryGet(keyHash, out ISyncedStateProvider provider))
                return false;
            provider.ApplySyncState(in state);
            return true;
        }

        /// <summary>Clears all registered providers.</summary>
        public void Clear()
        {
            providers.Clear();
            byHash.Clear();
        }

        internal void ResetAll()
        {
            providers.Clear();
            byHash.Clear();
            Changed = null;
            ProviderUnregistered = null;
        }
    }

    /// <summary>
    /// Data-driven registry connecting gameplay <see cref="ISyncedStateProvider"/> components to the
    /// networking layer without coupling gameplay classes to Netcode.
    /// </summary>
    public static class SyncedStateRegistry
    {
        static readonly SyncedStateRegistryInstance DefaultInstance = new SyncedStateRegistryInstance();

        /// <summary>Default scene-wide registry instance used by gameplay components and <c>NetworkStateReplicator</c>.</summary>
        public static SyncedStateRegistryInstance Default => DefaultInstance;

        /// <summary>Raised when a provider registers so an active replicator can refresh immediately.</summary>
        public static event Action Changed
        {
            add => DefaultInstance.Changed += value;
            remove => DefaultInstance.Changed -= value;
        }

        /// <summary>Read-only list of currently active state providers in the scene.</summary>
        public static IReadOnlyList<ISyncedStateProvider> AllProviders => DefaultInstance.AllProviders;

        /// <summary>Registers a provider by deterministic FNV-1a hash of its <see cref="ISyncedStateProvider.SyncKey"/>.</summary>
        public static void Register(ISyncedStateProvider provider) => DefaultInstance.Register(provider);

        /// <summary>Unregisters a provider when disabled, destroyed, or before its key changes.</summary>
        public static void Unregister(ISyncedStateProvider provider, string previousSyncKey = null) =>
            DefaultInstance.Unregister(provider, previousSyncKey);

        /// <summary>Looks up a registered provider by its deterministic key hash.</summary>
        public static bool TryGet(uint keyHash, out ISyncedStateProvider provider) => DefaultInstance.TryGet(keyHash, out provider);

        /// <summary>Applies a replicated record directly to the matching provider, returning true if found.</summary>
        public static bool TryApply(uint keyHash, in SyncedStateRecord state) => DefaultInstance.TryApply(keyHash, in state);

        /// <summary>
        /// Deterministic 32-bit FNV-1a hash of <paramref name="key"/> (identical across platforms and runs,
        /// unlike <see cref="string.GetHashCode"/> which is randomized per process).
        /// </summary>
        public static uint ComputeKeyHash(string key)
        {
            if (string.IsNullOrEmpty(key))
                return 0u;

            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < key.Length; i++)
                {
                    hash ^= key[i];
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        /// <summary>Builds a deterministic scene-stable key combining a category prefix, explicit ID, and world position.</summary>
        public static string BuildSpatialKey(string category, string explicitId, Vector3 worldPosition)
        {
            int qx = Mathf.RoundToInt(worldPosition.x * 10f);
            int qy = Mathf.RoundToInt(worldPosition.y * 10f);
            int qz = Mathf.RoundToInt(worldPosition.z * 10f);
            string namePart = string.IsNullOrWhiteSpace(explicitId) ? "obj" : explicitId;
            return $"{category}:{namePart}:{qx},{qy},{qz}";
        }

        /// <summary>Clears all registered providers (used in unit tests and domain-reload-disabled resets).</summary>
        public static void Clear() => DefaultInstance.Clear();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => DefaultInstance.ResetAll();
    }
}
