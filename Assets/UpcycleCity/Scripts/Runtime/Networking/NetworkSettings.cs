using System;
using System.Net;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace UpcycleCity.Networking
{
    /// <summary>Transport path used to establish a co-op session.</summary>
    public enum CoopTransportMode
    {
        /// <summary>Unity Multiplayer Services Sessions + Relay join code (works over mobile/NAT).</summary>
        Relay = 0,
        /// <summary>Direct UDP IP:port via Unity Transport (LAN, localhost, and automated tests).</summary>
        Direct = 1,
    }

    /// <summary>
    /// ScriptableObject configuration for Upcycle City online co-op multiplayer:
    /// tick rate, max players, Relay vs Direct Connect defaults, automatic <see cref="NetworkPrefabsList"/>
    /// reference, core prefab slots, and pure validation helpers.
    /// </summary>
    [CreateAssetMenu(fileName = "NetworkSettings", menuName = "Upcycle City/Networking/Network Settings")]
    public sealed class NetworkSettings : ScriptableObject
    {
        [Header("Session")]
        [SerializeField, Range(2, 4)] int maxPlayers = 4;
        [SerializeField, Range(15, 60)] uint tickRate = 30;
        [SerializeField] CoopTransportMode defaultTransportMode = CoopTransportMode.Relay;
        [Tooltip("Gameplay scene loaded when a solo player confirms their look in the Character Creator. Leave empty to use CharacterCreatorController.GameplayScene.")]
        [SerializeField] string soloSceneName = "EcosystemSandbox";
        [Tooltip("Gameplay scene the host loads for everyone (NGO scene management) once all players are ready.")]
        [SerializeField] string coopSceneName = "EcosystemSandbox";
        [Tooltip("Title screen (Play Solo / Host Room / Join Room). Leaving a co-op session returns here.")]
        [SerializeField] string menuSceneName = "MainMenu";
        [Tooltip("Character Creator scene: solo players go here from the menu; in co-op the host loads it for the whole room.")]
        [SerializeField] string characterSceneName = "CharacterCreator";

        [Header("Economy")]
        [Tooltip("Coins every run starts with: the shared team wallet in co-op and the player's wallet in solo. Players also start with no tools.")]
        [SerializeField, Min(0)] int startingTeamCoins = 0;

        [Header("Direct Connect Fallback")]
        [SerializeField] string defaultDirectAddress = "127.0.0.1";
        [SerializeField, Range(1024, 65535)] ushort defaultDirectPort = 7777;

        [Header("Timeouts & Replication")]
        [SerializeField, Range(3f, 30f)] float relayTimeoutSeconds = 12f;
        [Tooltip("Seconds without packets before Unity Transport disconnects a peer (15s balances surviving brief mobile app-switches against faster dead-host detection).")]
        [SerializeField, Range(5f, 30f)] float disconnectTimeoutSeconds = 15f;
        [SerializeField, Range(0.1f, 1f)] float stateSyncIntervalSeconds = 0.25f;

        [Header("Network Prefabs")]
        [Tooltip("Auto-populated by Upcycle City -> Networking -> Refresh Network Prefabs.")]
        [SerializeField] NetworkPrefabsList networkPrefabsList;
        [SerializeField] GameObject networkPlayerPrefab;
        [SerializeField] GameObject networkTrashPrefab;
        [SerializeField] GameObject networkCityStatePrefab;

        [Header("UI")]
        [SerializeField] PanelSettings panelSettings;
        [SerializeField] StyleSheet coopMenuStyleSheet;

        public int MaxPlayers => Mathf.Clamp(maxPlayers, 2, 4);
        public uint TickRate => tickRate < 15 ? 30u : tickRate;
        public CoopTransportMode DefaultTransportMode => defaultTransportMode;
        /// <summary>Solo gameplay scene, or empty string to defer to <c>CharacterCreatorController.GameplayScene</c>.</summary>
        public string SoloSceneName => string.IsNullOrWhiteSpace(soloSceneName) ? string.Empty : soloSceneName.Trim();
        public string CoopSceneName => string.IsNullOrWhiteSpace(coopSceneName) ? "EcosystemSandbox" : coopSceneName.Trim();
        /// <summary>Title screen scene (default <c>MainMenu</c>); leaving a session returns here.</summary>
        public string MenuSceneName => string.IsNullOrWhiteSpace(menuSceneName) ? "MainMenu" : menuSceneName.Trim();
        /// <summary>Character Creator scene (default <c>CharacterCreator</c>).</summary>
        public string CharacterSceneName => string.IsNullOrWhiteSpace(characterSceneName) ? "CharacterCreator" : characterSceneName.Trim();
        /// <summary>Coins a run starts with (team wallet in co-op, player wallet in solo). Default 0.</summary>
        public int StartingTeamCoins => Mathf.Max(0, startingTeamCoins);
        public string DefaultDirectAddress => string.IsNullOrWhiteSpace(defaultDirectAddress) ? "127.0.0.1" : defaultDirectAddress;
        public ushort DefaultDirectPort => defaultDirectPort == 0 ? (ushort)7777 : defaultDirectPort;
        public float RelayTimeoutSeconds => Mathf.Clamp(relayTimeoutSeconds, 3f, 30f);
        /// <summary>Unity Transport disconnect timeout in seconds (default 15s).</summary>
        public float DisconnectTimeoutSeconds => Mathf.Clamp(disconnectTimeoutSeconds, 5f, 30f);
        public float StateSyncIntervalSeconds => Mathf.Clamp(stateSyncIntervalSeconds, 0.1f, 1f);
        public NetworkPrefabsList NetworkPrefabsList => networkPrefabsList;
        public GameObject NetworkPlayerPrefab => networkPlayerPrefab;
        public GameObject NetworkTrashPrefab => networkTrashPrefab;
        public GameObject NetworkCityStatePrefab => networkCityStatePrefab;
        public PanelSettings PanelSettings => panelSettings;
        public StyleSheet CoopMenuStyleSheet => coopMenuStyleSheet;

        /// <summary>Editor/setup helper to assign the session flow scene names.</summary>
        public void ConfigureScenes(string menuScene, string characterScene, string soloScene, string coopScene)
        {
            menuSceneName = menuScene;
            characterSceneName = characterScene;
            soloSceneName = soloScene;
            coopSceneName = coopScene;
        }

        /// <summary>Sets the coins every run starts with (tests / setup tools).</summary>
        public void SetStartingTeamCoins(int coins) => startingTeamCoins = Mathf.Max(0, coins);

        /// <summary>Editor/setup helper to assign prefab, prefab-list, and UI references.</summary>
        public void ConfigureAssets(
            NetworkPrefabsList prefabsList,
            GameObject playerPrefab,
            GameObject trashPrefab,
            GameObject cityStatePrefab,
            PanelSettings uiPanel,
            StyleSheet menuStyleSheet)
        {
            networkPrefabsList = prefabsList;
            networkPlayerPrefab = playerPrefab;
            networkTrashPrefab = trashPrefab;
            networkCityStatePrefab = cityStatePrefab;
            panelSettings = uiPanel;
            coopMenuStyleSheet = menuStyleSheet;
        }

        /// <summary>Trims whitespace and normalizes a Relay/Session join code to uppercase alphanumeric characters.</summary>
        public static string NormalizeJoinCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            Span<char> buffer = stackalloc char[Math.Min(raw.Length, 16)];
            int written = 0;
            for (int i = 0; i < raw.Length && written < buffer.Length; i++)
            {
                char c = raw[i];
                if (char.IsLetterOrDigit(c))
                    buffer[written++] = char.ToUpperInvariant(c);
            }
            return new string(buffer.Slice(0, written));
        }

        /// <summary>Returns true when <paramref name="raw"/> contains only alphanumeric characters (plus optional spaces/hyphens) and normalizes to 4–12 characters.</summary>
        public static bool IsValidJoinCode(string raw, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (!char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && c != '-')
                    return false;
            }

            normalized = NormalizeJoinCode(raw);
            return normalized.Length >= 4 && normalized.Length <= 12;
        }

        /// <summary>
        /// Parses a direct-connect endpoint such as <c>127.0.0.1:7777</c>, <c>192.168.1.24</c>, or <c>localhost:7778</c>.
        /// Falls back to <paramref name="defaultPort"/> when no port suffix is specified.
        /// </summary>
        public static bool TryParseDirectEndpoint(
            string raw,
            ushort defaultPort,
            out string address,
            out ushort port,
            out string error)
        {
            address = string.Empty;
            port = defaultPort == 0 ? (ushort)7777 : defaultPort;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "Enter an IP address (for example 127.0.0.1:7777).";
                return false;
            }

            string trimmed = raw.Trim();
            int colon = trimmed.LastIndexOf(':');
            string hostPart = trimmed;
            if (colon >= 0)
            {
                hostPart = trimmed.Substring(0, colon).Trim();
                string portPart = trimmed.Substring(colon + 1).Trim();
                if (!ushort.TryParse(portPart, out ushort parsedPort) || parsedPort < 1024)
                {
                    error = "Port must be a number between 1024 and 65535.";
                    return false;
                }
                port = parsedPort;
            }

            if (string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                address = "127.0.0.1";
                return true;
            }

            if (IPAddress.TryParse(hostPart, out IPAddress parsedIp)
                && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                address = parsedIp.ToString();
                return true;
            }

            error = "Enter a valid IPv4 address or 'localhost' (for example 127.0.0.1:7777).";
            return false;
        }
    }
}
