using Unity.Netcode;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Null-safe static query surface for gameplay systems to check whether an online co-op session is active.
    /// When no <see cref="NetworkManager"/> exists or networking has not been started (offline solo play),
    /// <see cref="IsOnline"/> is false and <see cref="IsAuthoritative"/> is true so all existing single-player
    /// systems execute identically without requiring any Netcode objects in the scene.
    /// </summary>
    public static class NetSession
    {
        /// <summary>True when a Netcode session is currently listening (host, server, or connected client).</summary>
        public static bool IsOnline
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening;
            }
        }

        /// <summary>True when running as the host (server + local client) in an active online session.</summary>
        public static bool IsHost
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening && nm.IsHost;
            }
        }

        /// <summary>True when running as the authoritative server/host in an active online session.</summary>
        public static bool IsServer
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening && nm.IsServer;
            }
        }

        /// <summary>True when connected as a non-host client in an active online session.</summary>
        public static bool IsClientOnly
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening && nm.IsClient && !nm.IsServer;
            }
        }

        /// <summary>
        /// True when this instance owns authoritative gameplay simulation: always true in offline solo mode,
        /// and true on the host/server in online co-op. Clients send requests to the host instead.
        /// </summary>
        public static bool IsAuthoritative => !IsOnline || IsServer;

        /// <summary>Local client ID when online, or 0 when offline.</summary>
        public static ulong LocalClientId
        {
            get
            {
                NetworkManager nm = NetworkManager.Singleton;
                return nm != null && nm.IsListening ? nm.LocalClientId : 0UL;
            }
        }
    }
}
