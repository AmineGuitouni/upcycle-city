using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UpcycleCity.Networking;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>Same-Wi-Fi rooms: a host's UDP beacon is heard and listed by a listener; the device finds its LAN IPv4.</summary>
    public sealed class LanDiscoveryPlayModeTests
    {
        GameObject go;

        [TearDown]
        public void TearDown()
        {
            if (go != null)
                Object.Destroy(go);
        }

        [Test]
        public void FindsThisDevicesLanAddress()
        {
            string ip = LanDiscovery.FindLocalIPv4();
            Assert.IsFalse(string.IsNullOrEmpty(ip), "This machine should have a LAN/Wi-Fi IPv4.");
            Assert.IsTrue(System.Net.IPAddress.TryParse(ip, out System.Net.IPAddress addr));
            Assert.AreEqual(System.Net.Sockets.AddressFamily.InterNetwork, addr.AddressFamily);
            Assert.IsFalse(System.Net.IPAddress.IsLoopback(addr));
        }

        [UnityTest]
        public IEnumerator HostBeacon_IsHeardAndListedAsARoom()
        {
            go = new GameObject("LanDiscoveryTest");
            LanDiscovery lan = LanDiscovery.Instance != null ? LanDiscovery.Instance : go.AddComponent<LanDiscovery>();
            lan.StartListening();
            Assert.IsTrue(lan.Listening, "Could not bind the discovery port.");

            float deadline = Time.realtimeSinceStartup + 6f;
            while (lan.Rooms.Count == 0 && Time.realtimeSinceStartup < deadline)
            {
                lan.TickBeacon("Amine", 7777, 2, 4);
                yield return null;
            }
            Assert.Greater(lan.Rooms.Count, 0, "The room announcement was not heard.");
            LanDiscovery.Room room = lan.Rooms[0];
            Assert.AreEqual("Amine", room.HostName);
            Assert.AreEqual(7777, room.Port);
            Assert.AreEqual(2, room.Players);
            Assert.AreEqual(4, room.MaxPlayers);
            lan.StopListening();
            lan.StopBeacon();
        }
    }
}
