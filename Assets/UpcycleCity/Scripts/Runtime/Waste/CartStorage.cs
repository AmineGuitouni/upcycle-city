using System;
using UnityEngine;
using UpcycleCity.Core;

namespace UpcycleCity.Waste
{
    /// <summary>
    /// What a Shopping Cart holds: up to <see cref="CarryItems.CartCapacity"/> carry ids (same id space as the
    /// <see cref="RecyclingBag"/>) plus who is pushing it. The host owns it; it replicates as three compact synced
    /// records (<c>cart:&lt;id&gt;:0..2</c>, 10 / 10 / 4 ids packed by <see cref="CarryCodec"/>). Record 0 also carries the
    /// pusher's client id (v0, stored +1 so 0 means nobody).
    /// <para>Partial updates are fine: each record is applied on its own and every host request re-checks the expected
    /// id, so a stale view can never move or sell the wrong item.</para>
    /// </summary>
    public sealed class CartStorage
    {
        public const ulong NoPusher = ulong.MaxValue;
        public const int Chunks = 3;

        readonly int[] slots = new int[CarryItems.CartCapacity];
        readonly int[] chunkCounts = new int[Chunks];
        readonly int[][] chunkValues = { new int[CarryCodec.MaxSlots], new int[CarryCodec.MaxSlots], new int[CarryCodec.MaxSlots] };
        readonly Record[] records = new Record[Chunks];
        SyncedStateRegistryInstance registeredIn;

        public CartStorage(string baseKey)
        {
            BaseKey = baseKey ?? string.Empty;
            for (int c = 0; c < Chunks; c++)
                records[c] = new Record(this, c, $"{BaseKey}:{c}");
        }

        /// <summary><c>cart:&lt;placement id&gt;</c>; record keys append <c>:0..2</c>.</summary>
        public string BaseKey { get; }

        /// <summary>Key of the first record (also used to find a cart from a request's target id).</summary>
        public string SyncKey => records[0].SyncKey;

        public int Count { get; private set; }
        public int Capacity => CarryItems.CartCapacity;
        public bool IsFull => Count >= Capacity;
        public int this[int index] => slots[index];

        /// <summary>Client id of the player pushing the cart, or <see cref="NoPusher"/>.</summary>
        public ulong PusherClientId { get; private set; } = NoPusher;

        /// <summary>True while this storage is registered for replication (host) or receiving it (client mirror).</summary>
        public bool IsRegistered => registeredIn != null;

        /// <summary>Raised when the contents or the pusher change (host edits or replicated updates).</summary>
        public event Action Changed;

        public static string KeyFor(string placementId) => "cart:" + placementId;

        // ------------------------------------------------------------ host edits

        public bool TryAdd(int id)
        {
            if (id < 0 || id > CarryItems.MaxId || IsFull)
                return false;
            slots[Count++] = id;
            Changed?.Invoke();
            return true;
        }

        public void RemoveAt(int index)
        {
            if (index < 0 || index >= Count)
                return;
            for (int i = index; i < Count - 1; i++)
                slots[i] = slots[i + 1];
            Count--;
            Changed?.Invoke();
        }

        public void SetPusher(ulong clientId)
        {
            if (PusherClientId == clientId)
                return;
            PusherClientId = clientId;
            Changed?.Invoke();
        }

        /// <summary>How many stored items the given bin takes.</summary>
        public int CountMatching(bool organic, WasteStream stream)
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
            {
                if (CarryItems.Accepts(CarryItems.Resolve(slots[i]), organic, stream))
                    n++;
            }
            return n;
        }

        // ------------------------------------------------------------ replication

        /// <summary>Registers the three records (host: to replicate; client: to receive).</summary>
        public void Register(SyncedStateRegistryInstance registry)
        {
            if (registry == null || ReferenceEquals(registeredIn, registry))
                return;
            Unregister();
            for (int c = 0; c < Chunks; c++)
                registry.Register(records[c]);
            registeredIn = registry;
        }

        public void Unregister()
        {
            if (registeredIn == null)
                return;
            for (int c = 0; c < Chunks; c++)
                registeredIn.Unregister(records[c]);
            registeredIn = null;
        }

        /// <summary>True when another object already owns these keys in <paramref name="registry"/> (in-process host + client tests).</summary>
        public bool IsKeyTakenByOther(SyncedStateRegistryInstance registry) =>
            registry != null
            && registry.TryGet(SyncedStateRegistry.ComputeKeyHash(SyncKey), out ISyncedStateProvider existing)
            && !ReferenceEquals(existing, records[0]);

        SyncedStateRecord Capture(int chunk)
        {
            int start = chunk * CarryCodec.MaxSlots;
            int n = Mathf.Clamp(Count - start, 0, CarryCodec.MaxSlots);
            CarryCodec.Pack(slots, start, n, out int i0, out int i1);
            var record = new SyncedStateRecord { i0 = i0, i1 = i1 };
            if (chunk == 0)
            {
                record.v0 = PusherClientId == NoPusher ? 0f : PusherClientId + 1f;
                record.v1 = Count;
            }
            return record;
        }

        void Apply(int chunk, in SyncedStateRecord state)
        {
            int before = Count;
            ulong beforePusher = PusherClientId;
            bool changed = false;

            int[] values = chunkValues[chunk];
            int previousCount = chunkCounts[chunk];
            CarryCodec.Pack(values, previousCount, out int b0, out int b1);
            chunkCounts[chunk] = CarryCodec.Unpack(state.i0, state.i1, values);
            changed |= previousCount != chunkCounts[chunk] || b0 != state.i0 || b1 != state.i1;

            if (chunk == 0)
            {
                int raw = Mathf.RoundToInt(state.v0);
                PusherClientId = raw <= 0 ? NoPusher : (ulong)(raw - 1);
            }

            // The contents are the chunks in order, up to the first one that isn't full
            int count = 0;
            for (int c = 0; c < Chunks; c++)
            {
                int n = chunkCounts[c];
                Array.Copy(chunkValues[c], 0, slots, count, Mathf.Min(n, slots.Length - count));
                count += n;
                if (n < CarryCodec.MaxSlots)
                    break;
            }
            Count = Mathf.Min(count, slots.Length);

            if (changed || before != Count || beforePusher != PusherClientId)
                Changed?.Invoke();
        }

        sealed class Record : ISyncedStateProvider
        {
            readonly CartStorage owner;
            readonly int chunk;

            public Record(CartStorage owner, int chunk, string key)
            {
                this.owner = owner;
                this.chunk = chunk;
                SyncKey = key;
            }

            public string SyncKey { get; }
            public CartStorage Owner => owner;
            public SyncedStateRecord CaptureSyncState() => owner.Capture(chunk);
            public void ApplySyncState(in SyncedStateRecord state) => owner.Apply(chunk, in state);
        }
    }
}
