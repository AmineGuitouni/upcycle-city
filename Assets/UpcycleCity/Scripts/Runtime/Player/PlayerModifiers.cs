using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Player
{
    public enum PlayerStat
    {
        MoveSpeedMultiplier = 0,
        JumpMultiplier = 1,
        GrabReachBonus = 2,
        ThrowForceBonus = 3,
    }

    public enum PlayerModifierFlag
    {
        BlockSprint = 0,
        BlockJump = 1,
        SmogImmune = 2,
    }

    /// <summary>
    /// Keyed stat &amp; flag modifier stack so multiple independent gameplay systems
    /// (held bulky trash, equipped tools, smog zones, speed buffs, shop upgrades) can modify
    /// player stats concurrently without overwriting each other.
    /// </summary>
    public sealed class PlayerModifiers
    {
        readonly struct Key : IEquatable<Key>
        {
            public readonly string Source;
            public readonly int Id;

            public Key(string source, int id)
            {
                Source = source;
                Id = id;
            }

            public bool Equals(Key other) => Id == other.Id && string.Equals(Source, other.Source, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Source, Id);
        }

        readonly Dictionary<Key, float> multipliers = new Dictionary<Key, float>(8);
        readonly Dictionary<Key, float> additives = new Dictionary<Key, float>(8);
        readonly HashSet<Key> flags = new HashSet<Key>();
        readonly List<Key> scratchRemove = new List<Key>(8);

        public event Action Changed;

        public void SetMultiplier(string sourceId, PlayerStat stat, float value)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;

            var key = new Key(sourceId, (int)stat);
            if (multipliers.TryGetValue(key, out float existing) && Mathf.Approximately(existing, value))
                return;

            multipliers[key] = Mathf.Max(0f, value);
            Changed?.Invoke();
        }

        public void RemoveMultiplier(string sourceId, PlayerStat stat)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;
            if (multipliers.Remove(new Key(sourceId, (int)stat)))
                Changed?.Invoke();
        }

        public void SetAdditive(string sourceId, PlayerStat stat, float value)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;

            var key = new Key(sourceId, (int)stat);
            if (additives.TryGetValue(key, out float existing) && Mathf.Approximately(existing, value))
                return;

            additives[key] = value;
            Changed?.Invoke();
        }

        public void RemoveAdditive(string sourceId, PlayerStat stat)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;
            if (additives.Remove(new Key(sourceId, (int)stat)))
                Changed?.Invoke();
        }

        public void SetFlag(string sourceId, PlayerModifierFlag flag, bool active = true)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;

            var key = new Key(sourceId, (int)flag);
            bool modified = active ? flags.Add(key) : flags.Remove(key);
            if (modified)
                Changed?.Invoke();
        }

        public void RemoveFlag(string sourceId, PlayerModifierFlag flag) => SetFlag(sourceId, flag, false);

        /// <summary>Removes every multiplier, additive bonus, and flag registered by <paramref name="sourceId"/>.</summary>
        public void RemoveAllFromSource(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId))
                return;

            bool modified = false;

            scratchRemove.Clear();
            foreach (Key k in multipliers.Keys)
            {
                if (string.Equals(k.Source, sourceId, StringComparison.Ordinal))
                    scratchRemove.Add(k);
            }
            for (int i = 0; i < scratchRemove.Count; i++)
                modified |= multipliers.Remove(scratchRemove[i]);

            scratchRemove.Clear();
            foreach (Key k in additives.Keys)
            {
                if (string.Equals(k.Source, sourceId, StringComparison.Ordinal))
                    scratchRemove.Add(k);
            }
            for (int i = 0; i < scratchRemove.Count; i++)
                modified |= additives.Remove(scratchRemove[i]);

            scratchRemove.Clear();
            foreach (Key k in flags)
            {
                if (string.Equals(k.Source, sourceId, StringComparison.Ordinal))
                    scratchRemove.Add(k);
            }
            for (int i = 0; i < scratchRemove.Count; i++)
                modified |= flags.Remove(scratchRemove[i]);

            if (modified)
                Changed?.Invoke();
        }

        public float GetMultiplier(PlayerStat stat, float minClamp = 0.1f, float maxClamp = 3f)
        {
            int id = (int)stat;
            float total = 1f;
            foreach (KeyValuePair<Key, float> kvp in multipliers)
            {
                if (kvp.Key.Id == id)
                    total *= kvp.Value;
            }
            return Mathf.Clamp(total, minClamp, maxClamp);
        }

        public float GetAdditive(PlayerStat stat)
        {
            int id = (int)stat;
            float total = 0f;
            foreach (KeyValuePair<Key, float> kvp in additives)
            {
                if (kvp.Key.Id == id)
                    total += kvp.Value;
            }
            return total;
        }

        public bool HasFlag(PlayerModifierFlag flag)
        {
            int id = (int)flag;
            foreach (Key k in flags)
            {
                if (k.Id == id)
                    return true;
            }
            return false;
        }
    }
}
