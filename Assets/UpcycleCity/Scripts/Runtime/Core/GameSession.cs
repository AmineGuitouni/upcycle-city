using System;
using System.Collections.Generic;
using UnityEngine;

namespace UpcycleCity.Core
{
    /// <summary>One player in the current session, as the pause menu shows them.</summary>
    public interface ISessionPlayer
    {
        string DisplayName { get; }

        /// <summary>1-based player slot (P1…P4).</summary>
        int Slot { get; }

        /// <summary>Round-trip time to the host in milliseconds (0 for the host itself, -1 when unknown).</summary>
        int PingMs { get; }

        bool IsHost { get; }

        /// <summary>This machine's own player.</summary>
        bool IsLocal { get; }
    }

    /// <summary>
    /// Netcode-free view of the current session for UI (the pause menu's player list, ping and join code).
    /// The networking layer registers each spawned player and installs the hooks; in solo the list is empty.
    /// </summary>
    public static class GameSession
    {
        static readonly List<ISessionPlayer> Players = new List<ISessionPlayer>(4);

        /// <summary>Players in the session on this machine (sorted by slot when read through <see cref="CopyPlayers"/>).</summary>
        public static IReadOnlyList<ISessionPlayer> AllPlayers => Players;

        /// <summary>Returns the room's join code (installed by the session manager), or null in solo.</summary>
        public static Func<string> JoinCodeResolver { get; set; }

        /// <summary>Leaves the co-op session back to the main menu (installed by the session manager).</summary>
        public static Action LeaveSessionHandler { get; set; }

        public static string JoinCode => JoinCodeResolver?.Invoke();

        public static void Register(ISessionPlayer player)
        {
            if (player != null && !Players.Contains(player))
                Players.Add(player);
        }

        public static void Unregister(ISessionPlayer player) => Players.Remove(player);

        static readonly Comparison<ISessionPlayer> BySlot = (a, b) => a.Slot.CompareTo(b.Slot);

        /// <summary>Copies the players, sorted by slot, into <paramref name="into"/> (no allocation once it has room).</summary>
        public static void CopyPlayers(List<ISessionPlayer> into)
        {
            into.Clear();
            for (int i = 0; i < Players.Count; i++)
            {
                if (Players[i] is UnityEngine.Object o && o == null)
                    continue;
                into.Add(Players[i]);
            }
            into.Sort(BySlot);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Players.Clear();
            JoinCodeResolver = null;
            LeaveSessionHandler = null;
        }
    }
}
