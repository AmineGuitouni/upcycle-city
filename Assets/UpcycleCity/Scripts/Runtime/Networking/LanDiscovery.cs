using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Same-Wi-Fi co-op without any cloud account: a hosting phone announces its room on the local network
    /// (a tiny UDP broadcast every second) and phones on the Join screen list the rooms they hear, so friends
    /// tap "Amine's room · 2/4" instead of typing an IP. Also finds this device's Wi-Fi IPv4 for the Room screen.
    /// Polled from the main thread (no threads, no sockets kept open when not needed).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Upcycle City/Networking/LAN Discovery")]
    public sealed class LanDiscovery : MonoBehaviour
    {
        public const int DiscoveryPort = 47777;
        const string Magic = "UPCYC1";
        const float BeaconInterval = 1f;
        const float RoomTimeout = 4f;

        /// <summary>A room heard on the local network.</summary>
        public sealed class Room
        {
            public string Address;
            public ushort Port;
            public string HostName;
            public int Players;
            public int MaxPlayers;
            public float LastSeen;
            public string Endpoint => $"{Address}:{Port}";
        }

        public static LanDiscovery Instance { get; private set; }

        readonly List<Room> rooms = new List<Room>(8);
        readonly byte[] scratch = new byte[256];
        UdpClient sender;
        UdpClient listener;
        float beaconTimer;
        string cachedIp;
        float ipCheckTime = -10f;

        /// <summary>Rooms heard in the last few seconds (on the Join screen).</summary>
        public IReadOnlyList<Room> Rooms => rooms;

        /// <summary>True while listening for rooms (the Join screen is open).</summary>
        public bool Listening => listener != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        public static LanDiscovery EnsureInstance(GameObject host)
        {
            if (Instance != null)
                return Instance;
            return host.GetComponent<LanDiscovery>() ?? host.AddComponent<LanDiscovery>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        void OnDisable()
        {
            StopListening();
            CloseSender();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        // ------------------------------------------------------------ this device's Wi-Fi address

        /// <summary>This device's local IPv4 on the Wi-Fi / LAN (e.g. 192.168.1.23), or null if offline.</summary>
        public string LocalIPv4
        {
            get
            {
                if (Time.unscaledTime - ipCheckTime > 5f || cachedIp == null)
                {
                    ipCheckTime = Time.unscaledTime;
                    cachedIp = FindLocalIPv4();
                }
                return cachedIp;
            }
        }

        public static string FindLocalIPv4()
        {
            string best = null;
            int bestScore = -1;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(info.Address))
                            continue;
                        byte[] b = info.Address.GetAddressBytes();
                        // Private LAN ranges first (Wi-Fi / hotspot), then anything else
                        int score = b[0] == 192 && b[1] == 168 ? 3 : b[0] == 10 ? 2 : b[0] == 172 && b[1] >= 16 && b[1] <= 31 ? 2 : b[0] == 169 ? 0 : 1;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || nic.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase))
                            score += 2;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = info.Address.ToString();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Some platforms restrict interface enumeration: fall through to the routing trick
            }

            if (best != null)
                return best;
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("10.255.255.255", 1); // no packet is sent: this only picks the outgoing interface
                return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------ hosting: announce the room

        /// <summary>Called every frame by the session manager while a joinable Wi-Fi room is hosted.</summary>
        public void TickBeacon(string hostName, ushort port, int players, int maxPlayers)
        {
            beaconTimer -= Time.unscaledDeltaTime;
            if (beaconTimer > 0f)
                return;
            beaconTimer = BeaconInterval;
            try
            {
                if (sender == null)
                {
                    sender = new UdpClient { EnableBroadcast = true };
                }
                string safeName = (hostName ?? "Host").Replace('|', ' ');
                byte[] payload = Encoding.UTF8.GetBytes($"{Magic}|{safeName}|{port}|{players}|{maxPlayers}");
                sender.Send(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                // Also the subnet broadcast (some routers drop 255.255.255.255)
                string ip = LocalIPv4;
                if (ip != null && IPAddress.TryParse(ip, out IPAddress addr))
                {
                    byte[] b = addr.GetAddressBytes();
                    b[3] = 255;
                    sender.Send(payload, payload.Length, new IPEndPoint(new IPAddress(b), DiscoveryPort));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Upcycle City] LAN beacon failed: {e.Message}");
                CloseSender();
                beaconTimer = 3f;
            }
        }

        public void StopBeacon() => CloseSender();

        void CloseSender()
        {
            sender?.Close();
            sender = null;
        }

        // ------------------------------------------------------------ joining: hear rooms

        public void StartListening()
        {
            if (listener != null)
                return;
            rooms.Clear();
            try
            {
                listener = new UdpClient();
                listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.EnableBroadcast = true;
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Upcycle City] Can't listen for LAN rooms: {e.Message}");
                listener?.Close();
                listener = null;
            }
        }

        public void StopListening()
        {
            listener?.Close();
            listener = null;
            rooms.Clear();
        }

        void Update()
        {
            if (listener == null)
                return;
            try
            {
                while (listener.Available > 0)
                {
                    IPEndPoint from = null;
                    byte[] data = listener.Receive(ref from);
                    Parse(data, from);
                }
            }
            catch (Exception)
            {
                // Ignore a bad packet; keep listening
            }

            float now = Time.unscaledTime;
            for (int i = rooms.Count - 1; i >= 0; i--)
            {
                if (now - rooms[i].LastSeen > RoomTimeout)
                    rooms.RemoveAt(i);
            }
        }

        void Parse(byte[] data, IPEndPoint from)
        {
            if (data == null || data.Length == 0 || from == null)
                return;
            string text = Encoding.UTF8.GetString(data);
            string[] parts = text.Split('|');
            if (parts.Length < 5 || parts[0] != Magic || !ushort.TryParse(parts[2], out ushort port))
                return;
            int.TryParse(parts[3], out int players);
            int.TryParse(parts[4], out int max);
            string address = from.Address.ToString();

            Room room = null;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].Address == address && rooms[i].Port == port)
                {
                    room = rooms[i];
                    break;
                }
            }
            if (room == null)
            {
                room = new Room { Address = address, Port = port };
                rooms.Add(room);
            }
            room.HostName = parts[1];
            room.Players = players;
            room.MaxPlayers = max;
            room.LastSeen = Time.unscaledTime;
        }
    }
}
