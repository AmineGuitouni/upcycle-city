using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Ecosystem;
using UpcycleCity.Player;
using UpcycleCity.Waste;

namespace UpcycleCity.Networking
{
    /// <summary>High-level lifecycle state of a co-op multiplayer session.</summary>
    public enum CoopSessionState
    {
        Offline = 0,
        Connecting = 1,
        Lobby = 2,
        LoadingGame = 3,
        InGame = 4,
    }

    /// <summary>
    /// Persistent (<c>DontDestroyOnLoad</c>) co-op session, transport, lobby roster, scene-transition,
    /// and disconnect coordinator. Supports both Unity Multiplayer Services Relay Sessions (join code,
    /// anonymous auth, max 4 players) and Direct IP:port fallback via <see cref="UnityTransport"/>.
    /// Does nothing during offline solo play so single-player remains completely untouched.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Networking/Co-op Session Manager")]
    public sealed class CoopSessionManager : MonoBehaviour
    {
        /// <summary>Client → host named message: <c>FixedString32Bytes</c> name + <see cref="NetworkCharacterAppearance"/>.</summary>
        public const string MsgLobbyHello = "UpcycleCity.LobbyHello";
        /// <summary>Host → all named message: <c>int</c> count + <see cref="CoopLobbyPlayer"/> entries.</summary>
        public const string MsgLobbyRoster = "UpcycleCity.LobbyRoster";
        /// <summary>Client → host named message: <c>byte</c> ready + confirmed <see cref="NetworkCharacterAppearance"/>.</summary>
        public const string MsgLobbyReady = "UpcycleCity.LobbyReady";
        const string PlayerNamePrefsKey = "UpcycleCity.CoopPlayerName.v1";

        /// <summary>Connection approval reason code returned when a client tries to join a game already in progress.</summary>
        public const string DisconnectCodeGameInProgress = "GAME_IN_PROGRESS";

        /// <summary>Connection approval reason code returned when a session has reached MaxPlayers.</summary>
        public const string DisconnectCodeSessionFull = "SESSION_FULL";

        /// <summary>Disconnect reason code sent to connected clients when the host leaves or closes the app.</summary>
        public const string DisconnectCodeHostLeft = "HOST_LEFT";

        [SerializeField] NetworkSettings settings;

        static CoopSessionManager instance;
        static string pendingDisconnectNotice;

        readonly List<CoopLobbyPlayer> lobbyPlayers = new List<CoopLobbyPlayer>(4);

        NetworkManager networkManager;
        UnityTransport unityTransport;
        ISession activeSession;
        IHostSession activeHostSession;

        CoopSessionState state = CoopSessionState.Offline;
        CoopTransportMode activeTransportMode = CoopTransportMode.Relay;
        string joinCode = string.Empty;
        string activeEndpointLabel = string.Empty;
        string localPlayerName = "Cleaner";
        float connectTimeoutSeconds = 10f;
        float connectAttemptStartedAt;
        int connectAttemptVersion;
        CancellationTokenSource activeConnectCts;
        bool intentionallyLeaving;
        bool leaveInProgress;
        bool pendingHostShutdownFlush;
        bool quitFlushInProgress;
        bool quitFlushCompleted;
        bool appPaused;
        bool wasPausedOrUnfocused;
        bool sawReplicatorInGame;
        bool sceneHooksBound;
        bool messagingRegistered;
        bool autoStartWhenClientJoins;
        bool localReady;

        public static CoopSessionManager Instance => instance;

        public NetworkSettings Settings => settings;
        public CoopSessionState State => state;
        public CoopTransportMode ActiveTransportMode => activeTransportMode;
        public string JoinCode => joinCode;
        public string ActiveEndpointLabel => activeEndpointLabel;

        /// <summary>
        /// Shareable join string for the current session: the Relay join code when in Relay mode,
        /// or the Direct <c>IP:port</c> endpoint when in Direct mode.
        /// </summary>
        public string ShareableSessionCode => activeTransportMode == CoopTransportMode.Relay ? joinCode : activeEndpointLabel;

        public int MaxPlayers => settings != null ? settings.MaxPlayers : 4;

        string MenuScene => settings != null ? settings.MenuSceneName : "MainMenu";
        string CharacterScene => settings != null ? settings.CharacterSceneName : "CharacterCreator";

        /// <summary>
        /// True while connected in the pre-game Room (the <see cref="NetworkSettings.MenuSceneName"/> scene):
        /// join code, player list, host can move everyone on to Character Creation.
        /// </summary>
        public bool IsInRoom => state == CoopSessionState.Lobby && !IsInCharacterCreation;

        /// <summary>
        /// True while the co-op party is in the Character Creator scene: everyone customises their look and
        /// presses READY; the host starts the run once <see cref="AllPlayersReady"/>.
        /// </summary>
        public bool IsInCharacterCreation =>
            state == CoopSessionState.Lobby && SceneManager.GetActiveScene().name == CharacterScene;

        /// <summary>Whether the local player has pressed READY in the co-op Character Creator.</summary>
        public bool LocalPlayerReady => localReady;

        /// <summary>Number of roster entries marked ready.</summary>
        public int ReadyPlayerCount
        {
            get
            {
                int ready = 0;
                for (int i = 0; i < lobbyPlayers.Count; i++)
                {
                    if (lobbyPlayers[i].IsReady)
                        ready++;
                }
                return ready;
            }
        }

        /// <summary>
        /// True when every connected player has a roster entry marked ready. On the host this also checks
        /// the roster covers every Netcode connection, so a player whose hello has not arrived yet blocks Start.
        /// </summary>
        public bool AllPlayersReady
        {
            get
            {
                if (lobbyPlayers.Count == 0)
                    return false;
                if (networkManager != null && networkManager.IsServer && networkManager.IsListening
                    && lobbyPlayers.Count < networkManager.ConnectedClientsIds.Count)
                    return false;
                return ReadyPlayerCount == lobbyPlayers.Count;
            }
        }
        public IReadOnlyList<CoopLobbyPlayer> LobbyPlayers => lobbyPlayers;
        public int ConnectedPlayerCount => lobbyPlayers.Count > 0
            ? lobbyPlayers.Count
            : (networkManager != null && networkManager.IsListening ? networkManager.ConnectedClientsIds.Count : 0);

        /// <summary>Maximum seconds a client waits for the Netcode handshake before timing out cleanly.</summary>
        public float ConnectTimeoutSeconds
        {
            get => connectTimeoutSeconds;
            set => connectTimeoutSeconds = Mathf.Max(0.5f, value);
        }

        /// <summary>
        /// Optional async hook invoked during <see cref="HostCoopAsync"/> and <see cref="JoinCoopAsync"/>
        /// before starting Netcode/transport. Used by automated tests to exercise mid-flight cancellation races.
        /// </summary>
        public Func<CancellationToken, Task> PreConnectAsyncHook { get; set; }

        public string LocalPlayerName
        {
            get => string.IsNullOrWhiteSpace(localPlayerName) ? "Cleaner" : localPlayerName;
            set
            {
                localPlayerName = SanitizePlayerName(value);
                PlayerPrefs.SetString(PlayerNamePrefsKey, localPlayerName);
            }
        }

        /// <summary>
        /// Sets the local player name for this session only without persisting to <see cref="PlayerPrefs"/>
        /// (used by CLI flags and automated tests).
        /// </summary>
        public void SetSessionPlayerNameOverride(string value)
        {
            localPlayerName = SanitizePlayerName(value);
        }

        static string SanitizePlayerName(string value)
        {
            string trimmed = string.IsNullOrWhiteSpace(value) ? "Cleaner" : value.Trim();
            return trimmed.Length > 20 ? trimmed.Substring(0, 20) : trimmed;
        }

        public event Action<CoopSessionState> StateChanged;
        public event Action LobbyChanged;
        public event Action<string> StatusMessage;
        public event Action<string> ErrorOccurred;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            instance = null;
            pendingDisconnectNotice = null;
        }

        /// <summary>Returns and clears any disconnect reason recorded when returning to the menu.</summary>
        public static bool ConsumeDisconnectNotice(out string reason)
        {
            reason = pendingDisconnectNotice;
            pendingDisconnectNotice = null;
            return !string.IsNullOrEmpty(reason);
        }

        /// <summary>
        /// Maps raw Netcode for GameObjects / Unity Transport disconnect strings and approval codes to
        /// clear, human-readable messages suitable for mobile and desktop UI banners.
        /// </summary>
        public static string FormatFriendlyDisconnectReason(
            string rawReason,
            CoopSessionState stateAtDisconnect,
            int maxPlayers = 4)
        {
            if (string.IsNullOrWhiteSpace(rawReason))
            {
                return stateAtDisconnect == CoopSessionState.Connecting
                    ? "Could not connect to host. Check the code or IP and make sure the host is running."
                    : "Host left or lost connection.";
            }

            string trimmed = rawReason.Trim();

            if (string.Equals(trimmed, DisconnectCodeHostLeft, StringComparison.OrdinalIgnoreCase)
                || trimmed.IndexOf("host left", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Host left the co-op session.";
            }

            if (string.Equals(trimmed, DisconnectCodeGameInProgress, StringComparison.OrdinalIgnoreCase)
                || trimmed.IndexOf("already in progress", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Game already in progress. Join a lobby before the host starts.";
            }

            if (string.Equals(trimmed, DisconnectCodeSessionFull, StringComparison.OrdinalIgnoreCase)
                || trimmed.IndexOf("session is full", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return $"Session is full ({Mathf.Clamp(maxPlayers, 2, 4)}/{Mathf.Clamp(maxPlayers, 2, 4)} players).";
            }

            if (trimmed.IndexOf("Mismatch", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("Version", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("NetworkConfig", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Version mismatch between host and client. Make sure both players are running the same build.";
            }

            if (trimmed.IndexOf("ProtocolTimeout", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("TimedOut", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("Timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return stateAtDisconnect == CoopSessionState.Connecting
                    ? "Connection timed out. Check the join code/IP and make sure the host is reachable."
                    : "Connection lost — timed out waiting for host.";
            }

            if (trimmed.IndexOf("Disconnect Event", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("TransportFailure", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("ConnectionRequest", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("Handshake", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("Client-", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return stateAtDisconnect == CoopSessionState.Connecting
                    ? "Could not connect to host. Check the join code or IP and try again."
                    : "Host left or lost connection.";
            }

            return trimmed;
        }

        public void Configure(NetworkSettings networkSettings)
        {
            settings = networkSettings;
        }

        void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);

            localPlayerName = PlayerPrefs.GetString(PlayerNamePrefsKey, "Cleaner");
            if (settings == null)
                settings = ScriptableObject.CreateInstance<NetworkSettings>();

            Application.wantsToQuit += OnApplicationWantsToQuit;
            EnsureNetworkManager();
            EnsureInGameHud();
        }

        void Start()
        {
            ProcessCommandLineAutoConnect();
        }

        void Update()
        {
            if (state == CoopSessionState.InGame
                && NetworkStateReplicator.Active != null
                && NetworkStateReplicator.Active.IsSpawned)
            {
                sawReplicatorInGame = true;
            }

            if (state == CoopSessionState.Connecting
                && connectAttemptStartedAt > 0f
                && networkManager != null
                && networkManager.IsClient
                && !networkManager.IsServer
                && !networkManager.IsConnectedClient
                && Time.unscaledTime - connectAttemptStartedAt >= connectTimeoutSeconds)
            {
                connectAttemptStartedAt = 0f;
                string reason = FormatFriendlyDisconnectReason("Timeout", CoopSessionState.Connecting, MaxPlayers);
                LeaveSession(reason);
                return;
            }

            if (!appPaused
                && !intentionallyLeaving
                && !pendingHostShutdownFlush
                && (state == CoopSessionState.Lobby || state == CoopSessionState.LoadingGame || state == CoopSessionState.InGame)
                && (networkManager == null || !networkManager.IsListening || networkManager.ShutdownInProgress))
            {
                LeaveSession("Connection to the co-op session was lost.");
            }
        }

        void OnDestroy()
        {
            Application.wantsToQuit -= OnApplicationWantsToQuit;
            CancelActiveConnectAttempt();

            if (instance == this)
            {
                DisconnectAllClientsForHostShutdown(DisconnectCodeHostLeft);
                UnbindNetworkCallbacks();
                GameAuthority.ResetToLocal();
                instance = null;
            }
        }

        bool OnApplicationWantsToQuit()
        {
            if (quitFlushCompleted || Application.isMobilePlatform || Application.isEditor)
                return true;

            if (quitFlushInProgress)
                return false;

            if (networkManager != null && networkManager.IsListening && networkManager.IsServer)
            {
                int disconnectedRemotes = DisconnectAllClientsForHostShutdown(DisconnectCodeHostLeft);
                if (disconnectedRemotes > 0)
                {
                    intentionallyLeaving = true;
                    CancelActiveConnectAttempt();
                    quitFlushInProgress = true;
                    pendingHostShutdownFlush = true;
                    StartCoroutine(FlushHostDisconnectThenQuit());
                    return false;
                }
            }

            return true;
        }

        IEnumerator FlushHostDisconnectThenQuit()
        {
            yield return new WaitForSecondsRealtime(0.15f);
            pendingHostShutdownFlush = false;
            ShutdownNetworkQuietly();
            _ = LeaveUgsSessionSilentlyAsync();
            quitFlushCompleted = true;
            quitFlushInProgress = false;
            Application.Quit();
        }

        void OnApplicationQuit()
        {
            intentionallyLeaving = true;
            CancelActiveConnectAttempt();
            if (!quitFlushCompleted)
                DisconnectAllClientsForHostShutdown(DisconnectCodeHostLeft);
            ShutdownNetworkQuietly();
            _ = LeaveUgsSessionSilentlyAsync();
        }

        void OnApplicationPause(bool paused)
        {
            // Never tear down NGO on pause (mobile or desktop) so brief backgrounding
            // (e.g. switching to a messaging app to share/paste the join code) survives cleanly.
            // If the OS kills the socket while suspended, VerifySessionHealthOnResume cleans up on return.
            appPaused = paused;
            if (paused)
            {
                wasPausedOrUnfocused = true;
                return;
            }

            wasPausedOrUnfocused = false;
            VerifySessionHealthOnResume();
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                wasPausedOrUnfocused = true;
                return;
            }

            if (wasPausedOrUnfocused && !appPaused)
            {
                wasPausedOrUnfocused = false;
                VerifySessionHealthOnResume();
            }
        }

        /// <summary>
        /// Verifies that the active co-op session is still alive after returning from background/pause.
        /// If the host or client session died while suspended, cleanly returns to the menu scene with a banner
        /// so the player is never soft-locked in a dead lobby or gameplay scene.
        /// </summary>
        public void VerifySessionHealthOnResume()
        {
            if (intentionallyLeaving || pendingHostShutdownFlush)
                return;

            if (state == CoopSessionState.Offline || state == CoopSessionState.Connecting)
                return;

            bool alive = true;
            if (networkManager == null || !networkManager.IsListening || networkManager.ShutdownInProgress)
            {
                alive = false;
            }
            else if (networkManager.IsServer)
            {
                if (state == CoopSessionState.InGame
                    && sawReplicatorInGame
                    && (NetworkStateReplicator.Active == null || !NetworkStateReplicator.Active.IsSpawned))
                {
                    alive = false;
                }
            }
            else
            {
                if (!networkManager.IsClient || !networkManager.IsConnectedClient)
                {
                    alive = false;
                }
                else if (state == CoopSessionState.InGame
                    && sawReplicatorInGame
                    && (NetworkStateReplicator.Active == null || !NetworkStateReplicator.Active.IsSpawned))
                {
                    alive = false;
                }
            }

            if (!alive)
                LeaveSession("Connection lost while the app was in the background.");
        }

        // ---------------------------------------------------------------- Public Session API

        /// <summary>
        /// Main Menu → PLAY SOLO: makes sure no networking runs and loads the Character Creator
        /// (<see cref="NetworkSettings.CharacterSceneName"/>) offline. Confirming the look there calls
        /// <see cref="StartSoloGameplay"/>.
        /// </summary>
        public void PlaySolo()
        {
            GoOffline();
            SceneManager.LoadScene(CharacterScene, LoadSceneMode.Single);
        }

        /// <summary>
        /// Loads solo gameplay offline with zero networking started: <see cref="NetworkSettings.SoloSceneName"/>
        /// (default <c>EcosystemSandbox</c>), else <paramref name="sceneOverride"/>, else <c>EcosystemSandbox</c>.
        /// </summary>
        public void StartSoloGameplay(string sceneOverride = null)
        {
            GoOffline();
            string configuredSolo = settings != null ? settings.SoloSceneName : string.Empty;
            string target = !string.IsNullOrWhiteSpace(configuredSolo)
                ? configuredSolo
                : (!string.IsNullOrWhiteSpace(sceneOverride) ? sceneOverride : "EcosystemSandbox");
            SceneManager.LoadScene(target, LoadSceneMode.Single);
        }

        void GoOffline()
        {
            intentionallyLeaving = true;
            CancelActiveConnectAttempt();
            sawReplicatorInGame = false;
            localReady = false;
            ShutdownNetworkQuietly();
            GameAuthority.ResetToLocal();
            lobbyPlayers.Clear();
            SetState(CoopSessionState.Offline);
        }

        /// <summary>Hosts a co-op session via Relay (join code) or Direct IP:port.</summary>
        public async Task<(bool success, string error)> HostCoopAsync(
            CoopTransportMode mode,
            string directAddress = null,
            ushort directPort = 0)
        {
            await WaitForLeaveToFinishAsync();
            if (this == null)
                return (false, "Session manager destroyed.");

            if (state == CoopSessionState.Connecting)
                return (false, "Already connecting...");

            intentionallyLeaving = false;
            sawReplicatorInGame = false;
            int attempt = BeginConnectAttempt(out CancellationToken ct);
            EnsureNetworkManager();
            activeTransportMode = mode;
            joinCode = string.Empty;
            localReady = false;
            lobbyPlayers.Clear();
            SetState(CoopSessionState.Connecting);

            await WaitForPendingShutdownAsync(ct);
            if (IsConnectAttemptStale(attempt, ct))
                return CancelAttemptResult(attempt);

            if (PreConnectAsyncHook != null)
            {
                try { await PreConnectAsyncHook(ct); }
                catch (OperationCanceledException) { return CancelAttemptResult(attempt); }
                if (IsConnectAttemptStale(attempt, ct))
                    return CancelAttemptResult(attempt);
            }

            ApplyNetworkConfig();

            if (mode == CoopTransportMode.Relay)
            {
                (bool ok, string err) = await HostViaRelayAsync(attempt, ct);
                if (IsConnectAttemptStale(attempt, ct))
                {
                    ShutdownNetworkQuietly();
                    await LeaveUgsSessionSilentlyAsync();
                    return CancelAttemptResult(attempt);
                }

                if (!ok)
                {
                    SetState(CoopSessionState.Offline);
                    if (!string.IsNullOrEmpty(err))
                        ErrorOccurred?.Invoke(err);
                    return (false, err);
                }
            }
            else
            {
                string addr = string.IsNullOrWhiteSpace(directAddress) ? settings.DefaultDirectAddress : directAddress;
                ushort port = directPort >= 1024 ? directPort : settings.DefaultDirectPort;
                unityTransport.SetConnectionData(addr, port, "0.0.0.0");
                activeEndpointLabel = $"{addr}:{port}";

                if (IsConnectAttemptStale(attempt, ct))
                    return CancelAttemptResult(attempt);

                if (!networkManager.StartHost())
                {
                    string err = $"Could not start Direct Host on port {port}. Another instance may already be hosting on this port.";
                    SetState(CoopSessionState.Offline);
                    ErrorOccurred?.Invoke(err);
                    return (false, err);
                }
            }

            if (IsConnectAttemptStale(attempt, ct))
            {
                ShutdownNetworkQuietly();
                await LeaveUgsSessionSilentlyAsync();
                return CancelAttemptResult(attempt);
            }

            RegisterMessagingHandlers();
            BindSceneManagerHooks();

            lobbyPlayers.Clear();
            lobbyPlayers.Add(new CoopLobbyPlayer
            {
                clientId = networkManager.LocalClientId,
                slotNumber = 1,
                displayName = new FixedString32Bytes(LocalPlayerName),
                isHost = 1,
                appearance = NetworkCharacterAppearance.From(CharacterAppearanceStore.Current),
            });

            SetState(CoopSessionState.Lobby);
            LobbyChanged?.Invoke();
            return (true, string.Empty);
        }

        /// <summary>Joins a co-op session via Relay join code or Direct IP:port.</summary>
        public async Task<(bool success, string error)> JoinCoopAsync(
            CoopTransportMode mode,
            string codeOrEndpoint)
        {
            await WaitForLeaveToFinishAsync();
            if (this == null)
                return (false, "Session manager destroyed.");

            if (state == CoopSessionState.Connecting)
                return (false, "Already connecting...");

            intentionallyLeaving = false;
            sawReplicatorInGame = false;
            int attempt = BeginConnectAttempt(out CancellationToken ct);
            EnsureNetworkManager();
            activeTransportMode = mode;
            joinCode = string.Empty;
            localReady = false;
            lobbyPlayers.Clear();
            SetState(CoopSessionState.Connecting);

            await WaitForPendingShutdownAsync(ct);
            if (IsConnectAttemptStale(attempt, ct))
                return CancelAttemptResult(attempt);

            if (PreConnectAsyncHook != null)
            {
                try { await PreConnectAsyncHook(ct); }
                catch (OperationCanceledException) { return CancelAttemptResult(attempt); }
                if (IsConnectAttemptStale(attempt, ct))
                    return CancelAttemptResult(attempt);
            }

            ApplyNetworkConfig();

            if (mode == CoopTransportMode.Relay)
            {
                if (!NetworkSettings.IsValidJoinCode(codeOrEndpoint, out string normalizedCode))
                {
                    string err = "Enter a valid join code (4–12 letters/numbers).";
                    SetState(CoopSessionState.Offline);
                    ErrorOccurred?.Invoke(err);
                    return (false, err);
                }

                (bool ok, string errRelay) = await JoinViaRelayAsync(normalizedCode, attempt, ct);
                if (IsConnectAttemptStale(attempt, ct))
                {
                    ShutdownNetworkQuietly();
                    await LeaveUgsSessionSilentlyAsync();
                    return CancelAttemptResult(attempt);
                }

                if (!ok)
                {
                    SetState(CoopSessionState.Offline);
                    if (!string.IsNullOrEmpty(errRelay))
                        ErrorOccurred?.Invoke(errRelay);
                    return (false, errRelay);
                }
                joinCode = normalizedCode;
            }
            else
            {
                if (!NetworkSettings.TryParseDirectEndpoint(
                        codeOrEndpoint,
                        settings.DefaultDirectPort,
                        out string addr,
                        out ushort port,
                        out string parseErr))
                {
                    SetState(CoopSessionState.Offline);
                    ErrorOccurred?.Invoke(parseErr);
                    return (false, parseErr);
                }

                unityTransport.SetConnectionData(addr, port);
                activeEndpointLabel = $"{addr}:{port}";

                if (IsConnectAttemptStale(attempt, ct))
                    return CancelAttemptResult(attempt);

                if (!networkManager.StartClient())
                {
                    string err = $"Could not start Direct Client for {addr}:{port}.";
                    SetState(CoopSessionState.Offline);
                    ErrorOccurred?.Invoke(err);
                    return (false, err);
                }
            }

            if (IsConnectAttemptStale(attempt, ct))
            {
                ShutdownNetworkQuietly();
                await LeaveUgsSessionSilentlyAsync();
                return CancelAttemptResult(attempt);
            }

            RegisterMessagingHandlers();
            BindSceneManagerHooks();
            connectAttemptStartedAt = Time.unscaledTime;
            StatusMessage?.Invoke("Connecting to host...");
            return (true, string.Empty);
        }

        /// <summary>
        /// Host-only, Room → Character Creation: loads <see cref="NetworkSettings.CharacterSceneName"/> for every
        /// connected player via NGO scene management. No player objects are spawned there; everyone's ready flag
        /// is cleared so each player confirms the look they pick in the creator.
        /// </summary>
        public bool GoToCharacterCreation()
        {
            if (networkManager == null || !networkManager.IsHost || state != CoopSessionState.Lobby)
                return false;
            if (IsInCharacterCreation)
                return true;

            ClearAllReadyFlags();
            SceneEventProgressStatus status = networkManager.SceneManager.LoadScene(CharacterScene, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                ErrorOccurred?.Invoke($"Failed to load scene '{CharacterScene}' ({status}).");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Marks the local player ready (or not) in the co-op Character Creator and sends the confirmed
        /// <paramref name="look"/> to the host, which stores it in the roster and uses it to spawn this player.
        /// The host applies its own entry directly. Returns false when not in a co-op lobby.
        /// </summary>
        public bool SetLocalReady(bool ready, CharacterAppearance look)
        {
            if (state != CoopSessionState.Lobby || networkManager == null || !networkManager.IsListening)
                return false;

            localReady = ready;
            var appearance = NetworkCharacterAppearance.From(look);
            if (networkManager.IsServer)
            {
                SetLobbyPlayerReady(networkManager.LocalClientId, ready, appearance);
                BroadcastLobbyRoster();
                return true;
            }

            if (networkManager.CustomMessagingManager == null)
                return false;
            using var writer = new FastBufferWriter(64, Allocator.Temp);
            writer.WriteValueSafe(ready ? (byte)1 : (byte)0);
            writer.WriteValueSafe(appearance);
            networkManager.CustomMessagingManager.SendNamedMessage(MsgLobbyReady, NetworkManager.ServerClientId, writer);
            return true;
        }

        /// <summary>
        /// Host-only: starts the co-op run and loads <see cref="NetworkSettings.CoopSceneName"/> for every connected
        /// player via NGO SceneManager. By default every player must be ready (<see cref="AllPlayersReady"/>);
        /// tests and the <c>-coop-autostart</c> flag pass <paramref name="requireAllReady"/> = false.
        /// </summary>
        public bool StartCoopGameplay(bool requireAllReady = true)
        {
            if (networkManager == null || !networkManager.IsHost)
                return false;
            if (state != CoopSessionState.Lobby)
                return false;
            if (requireAllReady && !AllPlayersReady)
            {
                ErrorOccurred?.Invoke("Waiting for every player to press READY.");
                return false;
            }

            SetState(CoopSessionState.LoadingGame);
            string targetScene = settings != null ? settings.CoopSceneName : "EcosystemSandbox";
            SceneEventProgressStatus status = networkManager.SceneManager.LoadScene(targetScene, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
            {
                string err = $"Failed to load scene '{targetScene}' ({status}).";
                SetState(CoopSessionState.Lobby);
                ErrorOccurred?.Invoke(err);
                return false;
            }
            return true;
        }

        /// <summary>Leaves the current co-op lobby or active game cleanly and returns to the menu scene.</summary>
        public async void LeaveSession(string noticeForMenu = null)
        {
            if (!string.IsNullOrEmpty(noticeForMenu))
                pendingDisconnectNotice = noticeForMenu;

            if (pendingHostShutdownFlush || leaveInProgress)
                return;

            leaveInProgress = true;
            try
            {
                await LeaveSessionCoreAsync();
            }
            finally
            {
                leaveInProgress = false;
            }
        }

        /// <summary>True while <see cref="LeaveSession"/> is still flushing/shutting down; new host/join attempts wait for it.</summary>
        public bool IsLeaving => leaveInProgress || pendingHostShutdownFlush;

        async Task LeaveSessionCoreAsync()
        {
            intentionallyLeaving = true;
            CancelActiveConnectAttempt();
            connectAttemptStartedAt = 0f;
            sawReplicatorInGame = false;

            int disconnectedRemotes = DisconnectAllClientsForHostShutdown(DisconnectCodeHostLeft);
            if (disconnectedRemotes > 0)
            {
                pendingHostShutdownFlush = true;
                try
                {
                    // Allow UnityTransport ~120ms to flush the explicit HOST_LEFT disconnect packet before Shutdown()
                    await Task.Delay(120);
                }
                finally
                {
                    pendingHostShutdownFlush = false;
                }
                if (this == null)
                    return;
            }

            ShutdownNetworkQuietly();
            await LeaveUgsSessionSilentlyAsync();
            if (this == null)
                return;

            GameAuthority.ResetToLocal();
            lobbyPlayers.Clear();
            localReady = false;
            joinCode = string.Empty;
            activeEndpointLabel = string.Empty;
            SetState(CoopSessionState.Offline);
            LobbyChanged?.Invoke();

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            string menuScene = MenuScene;
            if (SceneManager.GetActiveScene().name != menuScene)
                SceneManager.LoadScene(menuScene, LoadSceneMode.Single);
        }

        int BeginConnectAttempt(out CancellationToken ct)
        {
            CancelActiveConnectAttempt();
            activeConnectCts = new CancellationTokenSource();
            ct = activeConnectCts.Token;
            return ++connectAttemptVersion;
        }

        void CancelActiveConnectAttempt()
        {
            connectAttemptVersion++;
            if (activeConnectCts != null)
            {
                try { activeConnectCts.Cancel(); }
                catch { /* ignore already-disposed token */ }
                activeConnectCts.Dispose();
                activeConnectCts = null;
            }
        }

        bool IsConnectAttemptStale(int attempt, CancellationToken ct)
        {
            return intentionallyLeaving
                || ct.IsCancellationRequested
                || attempt != connectAttemptVersion
                || this == null
                || networkManager == null;
        }

        (bool success, string error) CancelAttemptResult(int attempt)
        {
            if (this != null && state == CoopSessionState.Connecting && attempt == connectAttemptVersion)
                SetState(CoopSessionState.Offline);
            return (false, string.Empty);
        }

        // ---------------------------------------------------------------- Unity Multiplayer Services (Relay + Lobby)

        async Task<(bool success, string error)> EnsureUgsSignedInAsync(int attempt, CancellationToken ct)
        {
            if (IsConnectAttemptStale(attempt, ct))
                return (false, string.Empty);

            if (string.IsNullOrWhiteSpace(Application.cloudProjectId))
            {
                return (false,
                    "Online Relay requires linking this Unity project to Unity Cloud Services (Project Settings -> Services). " +
                    "Use Direct IP mode for local / LAN play right now.");
            }

            if (Application.internetReachability == NetworkReachability.NotReachable)
                return (false, "No internet connection detected. Check your network or switch to Direct IP.");

            try
            {
                int timeoutMs = Mathf.RoundToInt(settings.RelayTimeoutSeconds * 1000f);

                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    StatusMessage?.Invoke("Initializing Unity Gaming Services...");
                    Task initTask = UnityServices.InitializeAsync();
                    Task delayTask = Task.Delay(timeoutMs, ct);
                    if (await Task.WhenAny(initTask, delayTask) != initTask)
                    {
                        if (IsConnectAttemptStale(attempt, ct))
                            return (false, string.Empty);
                        return (false, "Timed out initializing Unity Gaming Services. Try again or use Direct IP.");
                    }
                    await initTask;
                    if (IsConnectAttemptStale(attempt, ct))
                        return (false, string.Empty);
                }

                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    StatusMessage?.Invoke("Signing in anonymously...");
                    Task authTask = AuthenticationService.Instance.SignInAnonymouslyAsync();
                    Task delayTask = Task.Delay(timeoutMs, ct);
                    if (await Task.WhenAny(authTask, delayTask) != authTask)
                    {
                        if (IsConnectAttemptStale(attempt, ct))
                            return (false, string.Empty);
                        return (false, "Timed out signing in to Unity Authentication. Try again or use Direct IP.");
                    }
                    await authTask;
                    if (IsConnectAttemptStale(attempt, ct))
                        return (false, string.Empty);
                }

                return (true, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                if (IsConnectAttemptStale(attempt, ct))
                    return (false, string.Empty);
                Debug.LogWarning($"[Upcycle City] UGS initialization failed: {ex.Message}");
                return (false, $"Online services unavailable: {ex.Message}. You can switch to Direct IP mode.");
            }
        }

        async Task<(bool success, string error)> HostViaRelayAsync(int attempt, CancellationToken ct)
        {
            (bool signedIn, string authErr) = await EnsureUgsSignedInAsync(attempt, ct);
            if (!signedIn || IsConnectAttemptStale(attempt, ct))
                return (false, IsConnectAttemptStale(attempt, ct) ? string.Empty : authErr);

            try
            {
                StatusMessage?.Invoke("Creating Relay co-op session...");
                int timeoutMs = Mathf.RoundToInt(settings.RelayTimeoutSeconds * 1000f);
                var options = new SessionOptions
                {
                    MaxPlayers = settings.MaxPlayers,
                    IsPrivate = false,
                }.WithRelayNetwork();

                Task<IHostSession> createTask = MultiplayerService.Instance.CreateSessionAsync(options);
                Task delayTask = Task.Delay(timeoutMs, ct);
                if (await Task.WhenAny(createTask, delayTask) != createTask)
                {
                    _ = CleanupLateHostSessionAsync(createTask);
                    if (IsConnectAttemptStale(attempt, ct))
                        return (false, string.Empty);
                    return (false, "Timed out creating Relay session. Check your connection or use Direct IP.");
                }

                IHostSession created = await createTask;
                if (IsConnectAttemptStale(attempt, ct))
                {
                    if (created != null)
                        _ = DeleteHostSessionSilentlyAsync(created);
                    return (false, string.Empty);
                }

                activeHostSession = created;
                activeSession = created;
                joinCode = created != null ? created.Code : string.Empty;

                if (!networkManager.IsListening && !networkManager.StartHost())
                {
                    await LeaveUgsSessionSilentlyAsync();
                    return (false, "Relay session created, but Netcode host failed to start.");
                }

                return (true, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                if (IsConnectAttemptStale(attempt, ct))
                {
                    await LeaveUgsSessionSilentlyAsync();
                    return (false, string.Empty);
                }
                Debug.LogWarning($"[Upcycle City] Relay host failed: {ex.Message}");
                await LeaveUgsSessionSilentlyAsync();
                return (false, $"Could not create online session: {ex.Message}");
            }
        }

        async Task<(bool success, string error)> JoinViaRelayAsync(string normalizedCode, int attempt, CancellationToken ct)
        {
            (bool signedIn, string authErr) = await EnsureUgsSignedInAsync(attempt, ct);
            if (!signedIn || IsConnectAttemptStale(attempt, ct))
                return (false, IsConnectAttemptStale(attempt, ct) ? string.Empty : authErr);

            try
            {
                StatusMessage?.Invoke($"Joining session {normalizedCode}...");
                int timeoutMs = Mathf.RoundToInt(settings.RelayTimeoutSeconds * 1000f);
                Task<ISession> joinTask = MultiplayerService.Instance.JoinSessionByCodeAsync(normalizedCode);
                Task delayTask = Task.Delay(timeoutMs, ct);
                if (await Task.WhenAny(joinTask, delayTask) != joinTask)
                {
                    _ = CleanupLateJoinSessionAsync(joinTask);
                    if (IsConnectAttemptStale(attempt, ct))
                        return (false, string.Empty);
                    return (false, "Timed out joining Relay session. Check the code or use Direct IP.");
                }

                ISession joined = await joinTask;
                if (IsConnectAttemptStale(attempt, ct))
                {
                    if (joined != null)
                        _ = LeaveJoinSessionSilentlyAsync(joined);
                    return (false, string.Empty);
                }

                activeSession = joined;
                if (!networkManager.IsListening && !networkManager.StartClient())
                {
                    await LeaveUgsSessionSilentlyAsync();
                    return (false, "Joined Relay session, but Netcode client failed to start.");
                }

                return (true, string.Empty);
            }
            catch (OperationCanceledException)
            {
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                if (IsConnectAttemptStale(attempt, ct))
                {
                    await LeaveUgsSessionSilentlyAsync();
                    return (false, string.Empty);
                }
                Debug.LogWarning($"[Upcycle City] Relay join failed: {ex.Message}");
                await LeaveUgsSessionSilentlyAsync();
                return (false, $"Could not join code '{normalizedCode}': {ex.Message}");
            }
        }

        static async Task CleanupLateHostSessionAsync(Task<IHostSession> createTask)
        {
            try
            {
                IHostSession lateSession = await createTask;
                if (lateSession != null)
                    await lateSession.DeleteAsync();
            }
            catch
            {
                // Best-effort cleanup of orphaned session after client-side timeout or cancellation
            }
        }

        static async Task DeleteHostSessionSilentlyAsync(IHostSession session)
        {
            try { await session.DeleteAsync(); }
            catch { /* best-effort cleanup */ }
        }

        static async Task CleanupLateJoinSessionAsync(Task<ISession> joinTask)
        {
            try
            {
                ISession lateSession = await joinTask;
                if (lateSession != null)
                    await lateSession.LeaveAsync();
            }
            catch
            {
                // Best-effort cleanup of orphaned session after client-side timeout or cancellation
            }
        }

        static async Task LeaveJoinSessionSilentlyAsync(ISession session)
        {
            try { await session.LeaveAsync(); }
            catch { /* best-effort cleanup */ }
        }

        async Task LeaveUgsSessionSilentlyAsync()
        {
            ISession sessionToLeave = activeSession;
            IHostSession hostToDelete = activeHostSession;
            activeSession = null;
            activeHostSession = null;

            if (hostToDelete != null)
            {
                try { await hostToDelete.DeleteAsync(); }
                catch { /* best-effort cleanup */ }
            }
            else if (sessionToLeave != null)
            {
                try { await sessionToLeave.LeaveAsync(); }
                catch { /* best-effort cleanup */ }
            }
        }

        // ---------------------------------------------------------------- NetworkManager Setup & Callbacks

        void EnsureNetworkManager()
        {
            networkManager = GetComponent<NetworkManager>();
            if (networkManager == null)
                networkManager = FindAnyObjectByType<NetworkManager>();
            if (networkManager == null)
                networkManager = gameObject.AddComponent<NetworkManager>();

            unityTransport = networkManager.GetComponent<UnityTransport>();
            if (unityTransport == null)
                unityTransport = networkManager.gameObject.AddComponent<UnityTransport>();

            BindNetworkCallbacks();
        }

        /// <summary>Waits (max 3 s) for an in-flight <see cref="LeaveSession"/> so a quick Leave → Host/Join never races the old teardown.</summary>
        async Task WaitForLeaveToFinishAsync()
        {
            float deadline = Time.unscaledTime + 3f;
            while (this != null && IsLeaving && Time.unscaledTime < deadline)
                await Task.Yield();
        }

        async Task WaitForPendingShutdownAsync(CancellationToken ct)
        {
            if (networkManager == null || !networkManager.ShutdownInProgress)
                return;

            float deadline = Time.unscaledTime + 2f;
            while (networkManager != null
                && networkManager.ShutdownInProgress
                && !ct.IsCancellationRequested
                && Time.unscaledTime < deadline)
            {
                await Task.Yield();
            }
        }

        void EnsureInGameHud()
        {
            CoopInGameHud hud = GetComponent<CoopInGameHud>();
            if (hud == null)
                hud = gameObject.AddComponent<CoopInGameHud>();
            if (settings != null)
                hud.Configure(settings.PanelSettings, settings.CoopMenuStyleSheet);

            CoopCharacterLobbyPanel lobbyPanel = GetComponent<CoopCharacterLobbyPanel>();
            if (lobbyPanel == null)
                lobbyPanel = gameObject.AddComponent<CoopCharacterLobbyPanel>();
            if (settings != null)
                lobbyPanel.Configure(settings.PanelSettings, settings.CoopMenuStyleSheet);
        }

        void ApplyNetworkConfig()
        {
            if (networkManager == null)
                return;

            networkManager.NetworkConfig ??= new NetworkConfig();
            networkManager.NetworkConfig.NetworkTransport = unityTransport;
            networkManager.NetworkConfig.TickRate = settings != null ? settings.TickRate : 30u;
            networkManager.NetworkConfig.EnableSceneManagement = true;
            networkManager.NetworkConfig.ConnectionApproval = true;
            networkManager.NetworkConfig.PlayerPrefab = settings != null ? settings.NetworkPlayerPrefab : null;

            if (unityTransport != null)
            {
                float disconnectSec = settings != null ? settings.DisconnectTimeoutSeconds : 15f;
                int disconnectMs = Mathf.RoundToInt(Mathf.Clamp(disconnectSec, 5f, 30f) * 1000f);
                unityTransport.DisconnectTimeoutMS = disconnectMs;
                unityTransport.HeartbeatTimeoutMS = Mathf.Clamp(disconnectMs / 15, 500, 1500);
            }

            // Replace NGO's default prefab list with our canonical NetworkPrefabsList so there are no duplicate hashes
            if (settings != null && settings.NetworkPrefabsList != null)
            {
                networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
                networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(settings.NetworkPrefabsList);
            }

            RegisterPrefabIfMissing(settings != null ? settings.NetworkPlayerPrefab : null);
            RegisterPrefabIfMissing(settings != null ? settings.NetworkTrashPrefab : null);
            RegisterPrefabIfMissing(settings != null ? settings.NetworkCityStatePrefab : null);
        }

        void RegisterPrefabIfMissing(GameObject prefab)
        {
            if (prefab == null || networkManager == null)
                return;
            if (networkManager.NetworkConfig.Prefabs.Contains(prefab))
                return;

            IReadOnlyList<NetworkPrefabsList> lists = networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists;
            for (int i = 0; i < lists.Count; i++)
            {
                if (lists[i] != null && lists[i].Contains(prefab))
                    return;
            }

            networkManager.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = prefab });
        }

        void BindNetworkCallbacks()
        {
            if (networkManager == null)
                return;
            networkManager.ConnectionApprovalCallback = OnConnectionApproval;
            networkManager.OnClientConnectedCallback -= OnClientConnected;
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            networkManager.OnTransportFailure -= OnTransportFailure;
            networkManager.OnTransportFailure += OnTransportFailure;
        }

        void UnbindNetworkCallbacks()
        {
            if (networkManager == null)
                return;
            networkManager.ConnectionApprovalCallback = null;
            networkManager.OnClientConnectedCallback -= OnClientConnected;
            networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            networkManager.OnTransportFailure -= OnTransportFailure;
            UnbindSceneManagerHooks();
            UnregisterMessagingHandlers();
        }

        void BindSceneManagerHooks()
        {
            if (sceneHooksBound || networkManager == null || networkManager.SceneManager == null)
                return;
            networkManager.SceneManager.OnLoadEventCompleted += OnNetworkSceneLoadCompleted;
            sceneHooksBound = true;
        }

        void UnbindSceneManagerHooks()
        {
            if (!sceneHooksBound || networkManager == null || networkManager.SceneManager == null)
                return;
            networkManager.SceneManager.OnLoadEventCompleted -= OnNetworkSceneLoadCompleted;
            sceneHooksBound = false;
        }

        void OnConnectionApproval(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            // Never auto-spawn player rigs in the Room / Character Creator; players spawn when the gameplay scene loads
            response.CreatePlayerObject = false;

            bool isHostSelf = request.ClientNetworkId == networkManager.LocalClientId && networkManager.IsHost;
            if (!isHostSelf)
            {
                if (state == CoopSessionState.LoadingGame || state == CoopSessionState.InGame)
                {
                    response.Approved = false;
                    response.Reason = DisconnectCodeGameInProgress;
                    return;
                }

                if (networkManager.ConnectedClientsIds.Count >= MaxPlayers)
                {
                    response.Approved = false;
                    response.Reason = DisconnectCodeSessionFull;
                    return;
                }
            }

            response.Approved = true;
        }

        void OnClientConnected(ulong clientId)
        {
            if (networkManager == null)
                return;

            if (networkManager.IsServer)
            {
                if (clientId != networkManager.LocalClientId)
                {
                    AddOrUpdateLobbyPlayer(
                        clientId,
                        $"Player {NextAvailableSlot()}",
                        NetworkCharacterAppearance.From(CharacterAppearance.Default),
                        isHost: false);
                    BroadcastLobbyRoster();

                    if (autoStartWhenClientJoins && state == CoopSessionState.Lobby)
                        StartCoopGameplay(requireAllReady: false);
                }
            }
            else if (clientId == networkManager.LocalClientId)
            {
                connectAttemptStartedAt = 0f;
                RegisterMessagingHandlers();
                BindSceneManagerHooks();
                SendLobbyHelloToServer();
                if (state == CoopSessionState.Connecting)
                    SetState(CoopSessionState.Lobby);
            }
        }

        void OnClientDisconnected(ulong clientId)
        {
            if (networkManager == null)
                return;

            if (networkManager.IsServer)
            {
                if (clientId == networkManager.LocalClientId)
                    return;

                NetworkedPhysicsItem.ReleaseAllHeldBy(clientId);
                for (int i = lobbyPlayers.Count - 1; i >= 0; i--)
                {
                    if (lobbyPlayers[i].clientId == clientId)
                        lobbyPlayers.RemoveAt(i);
                }
                if (!intentionallyLeaving)
                    BroadcastLobbyRoster();
                return;
            }

            // Local client disconnected from host
            if (clientId == networkManager.LocalClientId && !intentionallyLeaving)
            {
                string reason = FormatFriendlyDisconnectReason(networkManager.DisconnectReason, state, MaxPlayers);
                LeaveSession(reason);
            }
        }

        void OnTransportFailure()
        {
            if (!intentionallyLeaving)
                LeaveSession("Network connection was interrupted. Returned to menu.");
        }

        int DisconnectAllClientsForHostShutdown(string reasonCode)
        {
            if (networkManager == null || !networkManager.IsListening || !networkManager.IsServer)
                return 0;

            IReadOnlyList<ulong> ids = networkManager.ConnectedClientsIds;
            if (ids == null || ids.Count == 0)
                return 0;

            var remoteClients = new List<ulong>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                if (ids[i] != networkManager.LocalClientId)
                    remoteClients.Add(ids[i]);
            }

            for (int i = 0; i < remoteClients.Count; i++)
                networkManager.DisconnectClient(remoteClients[i], reasonCode);

            return remoteClients.Count;
        }

        void ShutdownNetworkQuietly()
        {
            UnbindSceneManagerHooks();
            UnregisterMessagingHandlers();
            if (networkManager != null && networkManager.IsListening)
                networkManager.Shutdown();
        }

        // ---------------------------------------------------------------- Lobby Custom Messages

        void RegisterMessagingHandlers()
        {
            if (messagingRegistered || networkManager == null || networkManager.CustomMessagingManager == null)
                return;
            networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MsgLobbyHello, OnLobbyHelloReceived);
            networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MsgLobbyRoster, OnLobbyRosterReceived);
            networkManager.CustomMessagingManager.RegisterNamedMessageHandler(MsgLobbyReady, OnLobbyReadyReceived);
            messagingRegistered = true;
        }

        void UnregisterMessagingHandlers()
        {
            if (!messagingRegistered || networkManager == null || networkManager.CustomMessagingManager == null)
                return;
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MsgLobbyHello);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MsgLobbyRoster);
            networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(MsgLobbyReady);
            messagingRegistered = false;
        }

        void SendLobbyHelloToServer()
        {
            if (networkManager == null || networkManager.CustomMessagingManager == null || networkManager.IsServer)
                return;

            using var writer = new FastBufferWriter(128, Allocator.Temp);
            var name = new FixedString32Bytes(LocalPlayerName);
            var appearance = NetworkCharacterAppearance.From(CharacterAppearanceStore.Current);
            writer.WriteValueSafe(name);
            writer.WriteValueSafe(appearance);
            networkManager.CustomMessagingManager.SendNamedMessage(MsgLobbyHello, NetworkManager.ServerClientId, writer);
        }

        void OnLobbyHelloReceived(ulong senderClientId, FastBufferReader reader)
        {
            if (!networkManager.IsServer)
                return;

            reader.ReadValueSafe(out FixedString32Bytes playerName);
            reader.ReadValueSafe(out NetworkCharacterAppearance appearance);
            AddOrUpdateLobbyPlayer(senderClientId, playerName.ToString(), appearance, isHost: false);
            BroadcastLobbyRoster();
        }

        void OnLobbyReadyReceived(ulong senderClientId, FastBufferReader reader)
        {
            if (!networkManager.IsServer || state != CoopSessionState.Lobby)
                return;

            reader.ReadValueSafe(out byte ready);
            reader.ReadValueSafe(out NetworkCharacterAppearance appearance);
            SetLobbyPlayerReady(senderClientId, ready != 0, appearance);
            BroadcastLobbyRoster();
        }

        void SetLobbyPlayerReady(ulong clientId, bool ready, NetworkCharacterAppearance appearance)
        {
            for (int i = 0; i < lobbyPlayers.Count; i++)
            {
                if (lobbyPlayers[i].clientId != clientId)
                    continue;
                CoopLobbyPlayer entry = lobbyPlayers[i];
                entry.isReady = ready ? (byte)1 : (byte)0;
                entry.appearance = appearance;
                lobbyPlayers[i] = entry;
                return;
            }
        }

        void ClearAllReadyFlags()
        {
            localReady = false;
            for (int i = 0; i < lobbyPlayers.Count; i++)
            {
                CoopLobbyPlayer entry = lobbyPlayers[i];
                entry.isReady = 0;
                lobbyPlayers[i] = entry;
            }
            BroadcastLobbyRoster();
        }

        void BroadcastLobbyRoster()
        {
            if (!networkManager.IsServer || networkManager.CustomMessagingManager == null)
                return;

            using var writer = new FastBufferWriter(64 + lobbyPlayers.Count * 64, Allocator.Temp);
            int count = lobbyPlayers.Count;
            writer.WriteValueSafe(count);
            for (int i = 0; i < count; i++)
                writer.WriteValueSafe(lobbyPlayers[i]);

            networkManager.CustomMessagingManager.SendNamedMessageToAll(MsgLobbyRoster, writer);
            LobbyChanged?.Invoke();
        }

        void OnLobbyRosterReceived(ulong senderClientId, FastBufferReader reader)
        {
            if (networkManager.IsServer)
                return;

            reader.ReadValueSafe(out int count);
            lobbyPlayers.Clear();
            for (int i = 0; i < count && i < 8; i++)
            {
                reader.ReadValueSafe(out CoopLobbyPlayer entry);
                lobbyPlayers.Add(entry);
                if (entry.clientId == networkManager.LocalClientId)
                    localReady = entry.IsReady;
            }
            LobbyChanged?.Invoke();
        }

        void AddOrUpdateLobbyPlayer(ulong clientId, string displayName, NetworkCharacterAppearance appearance, bool isHost)
        {
            for (int i = 0; i < lobbyPlayers.Count; i++)
            {
                if (lobbyPlayers[i].clientId == clientId)
                {
                    CoopLobbyPlayer existing = lobbyPlayers[i];
                    existing.displayName = new FixedString32Bytes(string.IsNullOrWhiteSpace(displayName) ? $"Player {existing.slotNumber}" : displayName);
                    existing.appearance = appearance;
                    lobbyPlayers[i] = existing;
                    return;
                }
            }

            byte slot = NextAvailableSlot();
            lobbyPlayers.Add(new CoopLobbyPlayer
            {
                clientId = clientId,
                slotNumber = slot,
                displayName = new FixedString32Bytes(string.IsNullOrWhiteSpace(displayName) ? $"Player {slot}" : displayName),
                isHost = isHost ? (byte)1 : (byte)0,
                appearance = appearance,
            });
        }

        byte NextAvailableSlot()
        {
            for (byte slot = 1; slot <= 4; slot++)
            {
                bool taken = false;
                for (int i = 0; i < lobbyPlayers.Count; i++)
                {
                    if (lobbyPlayers[i].slotNumber == slot)
                    {
                        taken = true;
                        break;
                    }
                }
                if (!taken)
                    return slot;
            }
            return (byte)(lobbyPlayers.Count + 1);
        }

        // ---------------------------------------------------------------- Gameplay Scene Transition & Spawning

        void OnNetworkSceneLoadCompleted(
            string sceneName,
            LoadSceneMode loadSceneMode,
            List<ulong> clientsCompleted,
            List<ulong> clientsTimedOut)
        {
            // Room (menu) and Character Creator are lobby scenes: nobody spawns there
            if (sceneName == MenuScene || sceneName == CharacterScene)
            {
                LobbyChanged?.Invoke();
                return;
            }

            // Remove pre-placed offline Player.prefab so NGO-spawned NetworkPlayer instances are the only players
            Vector3 spawnBase = new Vector3(0.5f, 0.25f, -64f);
            Quaternion spawnRot = Quaternion.identity;
            PlayerController[] prePlacedPlayers = FindObjectsByType<PlayerController>();
            for (int i = 0; i < prePlacedPlayers.Length; i++)
            {
                PlayerController pc = prePlacedPlayers[i];
                if (pc != null && pc.GetComponent<NetworkObject>() == null)
                {
                    spawnBase = pc.transform.position;
                    spawnRot = pc.transform.rotation;
                    Destroy(pc.gameObject);
                }
            }

            if (networkManager.IsServer)
            {
                NetworkStateReplicator replicator = EnsureServerCityStateSpawned();
                ConvertSceneOfflineTrashOnServer(replicator);
                SpawnAllConnectedPlayers(spawnBase, spawnRot);
            }
            else
            {
                StripSceneOfflineTrashOnClient();
            }

            SetState(CoopSessionState.InGame);
        }

        NetworkStateReplicator EnsureServerCityStateSpawned()
        {
            if (NetworkStateReplicator.Active != null && NetworkStateReplicator.Active.IsSpawned)
                return NetworkStateReplicator.Active;

            GameObject prefab = settings != null ? settings.NetworkCityStatePrefab : null;
            if (prefab == null)
            {
                Debug.LogError("[Upcycle City] NetworkSettings.NetworkCityStatePrefab is missing; cannot spawn NetworkStateReplicator.");
                return null;
            }

            GameObject go = Instantiate(prefab);
            NetworkStateReplicator replicator = go.GetComponent<NetworkStateReplicator>();
            if (replicator != null && settings != null)
                replicator.Configure(settings.NetworkTrashPrefab, settings.StateSyncIntervalSeconds, settings.StartingTeamCoins);

            NetworkObject netObj = go.GetComponent<NetworkObject>();
            netObj.Spawn(destroyWithScene: true);
            return replicator;
        }

        void ConvertSceneOfflineTrashOnServer(NetworkStateReplicator replicator)
        {
            if (replicator == null)
                return;

            WasteCatalog catalog = CityEcosystemManager.Active != null ? CityEcosystemManager.Active.DefaultCatalog : null;
            TrashItem[] sceneTrash = FindObjectsByType<TrashItem>();
            for (int i = 0; i < sceneTrash.Length; i++)
            {
                TrashItem item = sceneTrash[i];
                if (item == null || item.GetComponent<NetworkObject>() != null)
                    continue;

                replicator.ConvertOfflineTrashToNetworked(item, catalog);
                Destroy(item.gameObject);
            }
        }

        static void StripSceneOfflineTrashOnClient()
        {
            TrashItem[] sceneTrash = FindObjectsByType<TrashItem>();
            for (int i = 0; i < sceneTrash.Length; i++)
            {
                TrashItem item = sceneTrash[i];
                if (item != null && item.GetComponent<NetworkObject>() == null)
                    Destroy(item.gameObject);
            }
        }

        void SpawnAllConnectedPlayers(Vector3 basePos, Quaternion baseRot)
        {
            IReadOnlyList<ulong> clients = networkManager.ConnectedClientsIds;
            for (int i = 0; i < clients.Count; i++)
                SpawnNetworkPlayerForClient(clients[i], basePos, baseRot);
        }

        void SpawnNetworkPlayerForClient(ulong clientId, Vector3? basePosOverride = null, Quaternion? baseRotOverride = null)
        {
            if (!networkManager.IsServer || settings == null || settings.NetworkPlayerPrefab == null)
                return;
            if (networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient existingClient)
                && existingClient.PlayerObject != null)
                return;

            CoopLobbyPlayer info = FindLobbyPlayer(clientId);
            int slotIndex = Mathf.Max(0, info.slotNumber - 1);
            Vector3 anchor = basePosOverride ?? new Vector3(0.5f, 0.25f, -64f);
            Quaternion rot = baseRotOverride ?? Quaternion.identity;

            // Space up to 4 players cleanly side-by-side so rigidbodies never overlap on spawn
            float lateralOffset = (slotIndex - 1.5f) * 1.4f;
            Vector3 spawnPos = anchor + rot * new Vector3(lateralOffset, 0.05f, 0f);

            GameObject go = Instantiate(settings.NetworkPlayerPrefab, spawnPos, rot);
            NetworkObject netObj = go.GetComponent<NetworkObject>();
            netObj.SpawnAsPlayerObject(clientId, destroyWithScene: true);

            NetworkPlayerController npc = go.GetComponent<NetworkPlayerController>();
            if (npc != null)
                npc.InitializeServerIdentity(info.slotNumber, info.displayName.ToString(), info.appearance, spawnPos, rot);
        }

        CoopLobbyPlayer FindLobbyPlayer(ulong clientId)
        {
            for (int i = 0; i < lobbyPlayers.Count; i++)
            {
                if (lobbyPlayers[i].clientId == clientId)
                    return lobbyPlayers[i];
            }
            return new CoopLobbyPlayer
            {
                clientId = clientId,
                slotNumber = NextAvailableSlot(),
                displayName = new FixedString32Bytes($"Player {clientId + 1}"),
                appearance = NetworkCharacterAppearance.From(CharacterAppearance.Default),
            };
        }

        void SetState(CoopSessionState next)
        {
            if (state == next)
                return;
            state = next;
            StateChanged?.Invoke(state);
        }

        // ---------------------------------------------------------------- Optional CLI Auto-Connect for E2E Tests

        void ProcessCommandLineAutoConnect()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args == null || args.Length == 0)
                return;

            bool hostFlag = false;
            string joinEndpoint = null;
            string nameArg = null;
            int outfitArg = -1;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-coop-host", StringComparison.OrdinalIgnoreCase))
                    hostFlag = true;
                else if (string.Equals(args[i], "-coop-autostart", StringComparison.OrdinalIgnoreCase))
                    autoStartWhenClientJoins = true;
                else if (string.Equals(args[i], "-coop-join", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    joinEndpoint = args[++i];
                else if (string.Equals(args[i], "-coop-name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    nameArg = args[++i];
                else if (string.Equals(args[i], "-coop-outfit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[++i], out outfitArg);
            }

            if (!string.IsNullOrWhiteSpace(nameArg))
                SetSessionPlayerNameOverride(nameArg);

            if (outfitArg >= 0)
            {
                CharacterAppearance look = CharacterAppearanceStore.Current;
                look.outfitColor = outfitArg;
                look.hairStyle = (outfitArg + 2) % 8;
                CharacterAppearanceStore.SetSessionOverride(look);
            }

            if (hostFlag)
                _ = HostCoopAsync(CoopTransportMode.Direct);
            else if (!string.IsNullOrWhiteSpace(joinEndpoint))
                _ = JoinCoopAsync(CoopTransportMode.Direct, joinEndpoint);
        }
    }
}
