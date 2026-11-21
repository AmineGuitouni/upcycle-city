using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Player
{
    /// <summary>
    /// Timed buffs from shop consumables (an espresso, a bottle of water, a kaskrout...). Each boost writes its
    /// stats into <see cref="PlayerModifiers"/> under its own source id and removes them when it runs out;
    /// taking the same boost again refreshes its timer. Added to the player on first use.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Player/Player Boosts")]
    public sealed class PlayerBoosts : MonoBehaviour
    {
        /// <summary>What a boost does while it lasts.</summary>
        [Serializable]
        public struct Effect
        {
            [Tooltip("Movement speed multiplier (1 = none).")]
            [Range(0.5f, 2f)] public float moveSpeed;
            [Tooltip("Jump height multiplier (1 = none).")]
            [Range(0.5f, 2f)] public float jump;
            [Tooltip("Extra grab reach in metres.")]
            [Min(0f)] public float grabReach;
            [Tooltip("Extra throw speed.")]
            [Min(0f)] public float throwForce;
            [Tooltip("Ignore smog slowdown while it lasts.")]
            public bool smogImmune;

            public static Effect Neutral => new Effect { moveSpeed = 1f, jump = 1f };
        }

        public sealed class Active
        {
            public string Id;
            public string Label;
            public Color Color;
            public float Duration;
            public float Remaining;
            public float Normalized => Duration > 0f ? Mathf.Clamp01(Remaining / Duration) : 0f;
        }

        const string SourcePrefix = "boost_";

        readonly List<Active> active = new List<Active>(4);
        PlayerController player;

        public IReadOnlyList<Active> ActiveBoosts => active;
        public event Action Changed;

        /// <summary>The boosts component on <paramref name="player"/>, added if missing.</summary>
        public static PlayerBoosts For(PlayerController player)
        {
            if (player == null)
                return null;
            PlayerBoosts boosts = player.GetComponent<PlayerBoosts>();
            return boosts != null ? boosts : player.gameObject.AddComponent<PlayerBoosts>();
        }

        void Awake() => player = GetComponent<PlayerController>();

        void OnDisable()
        {
            for (int i = 0; i < active.Count; i++)
                player?.Modifiers?.RemoveAllFromSource(SourcePrefix + active[i].Id);
            active.Clear();
        }

        /// <summary>Starts (or refreshes) a boost.</summary>
        public void Apply(string id, string label, Color color, float duration, in Effect effect)
        {
            PlayerModifiers modifiers = player != null ? player.Modifiers : null;
            if (modifiers == null || string.IsNullOrEmpty(id))
                return;

            string source = SourcePrefix + id;
            modifiers.RemoveAllFromSource(source);
            if (!Mathf.Approximately(effect.moveSpeed, 1f) && effect.moveSpeed > 0f)
                modifiers.SetMultiplier(source, PlayerStat.MoveSpeedMultiplier, effect.moveSpeed);
            if (!Mathf.Approximately(effect.jump, 1f) && effect.jump > 0f)
                modifiers.SetMultiplier(source, PlayerStat.JumpMultiplier, effect.jump);
            if (effect.grabReach > 0f)
                modifiers.SetAdditive(source, PlayerStat.GrabReachBonus, effect.grabReach);
            if (effect.throwForce > 0f)
                modifiers.SetAdditive(source, PlayerStat.ThrowForceBonus, effect.throwForce);
            if (effect.smogImmune)
                modifiers.SetFlag(source, PlayerModifierFlag.SmogImmune);

            Active entry = Find(id);
            if (entry == null)
            {
                entry = new Active { Id = id };
                active.Add(entry);
            }
            entry.Label = label;
            entry.Color = color;
            entry.Duration = Mathf.Max(1f, duration);
            entry.Remaining = entry.Duration;
            Changed?.Invoke();
        }

        void Update()
        {
            if (active.Count == 0)
                return;

            float dt = Time.deltaTime;
            bool expired = false;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                active[i].Remaining -= dt;
                if (active[i].Remaining > 0f)
                    continue;
                player?.Modifiers?.RemoveAllFromSource(SourcePrefix + active[i].Id);
                active.RemoveAt(i);
                expired = true;
            }
            if (expired)
                Changed?.Invoke();
        }

        Active Find(string id)
        {
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i].Id == id)
                    return active[i];
            }
            return null;
        }
    }
}
