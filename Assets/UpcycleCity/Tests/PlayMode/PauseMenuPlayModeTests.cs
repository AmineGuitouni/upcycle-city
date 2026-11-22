using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UpcycleCity.Character;
using UpcycleCity.Core;
using UpcycleCity.Networking;
using UpcycleCity.UI;

namespace UpcycleCity.Tests.PlayMode
{
    /// <summary>
    /// Pause menu data over a real host + client (loopback UnityTransport): every spawned player shows up in
    /// <see cref="GameSession"/> with its name, slot, host/you flags and a ping; the host's ping is 0 and the
    /// host-measured RTT of the client replicates. Solo: the list is empty and the menu still opens and pauses.
    /// </summary>
    public sealed class PauseMenuPlayModeTests
    {
        static ushort nextPort = 18610;

        readonly List<GameObject> spawnedRoots = new List<GameObject>();
        NetworkManager hostManager;
        NetworkManager clientManager;

        [TearDown]
        public void TearDown()
        {
            if (PauseMenu.Instance != null && PauseMenu.Instance.IsOpen)
                PauseMenu.Instance.Close();
            Time.timeScale = 1f;
            AudioListener.pause = false;
            foreach (NetworkManager nm in new[] { clientManager, hostManager })
            {
                if (nm == null) continue;
                if (nm.IsListening) nm.Shutdown();
                UnityEngine.Object.DestroyImmediate(nm.gameObject);
            }
            clientManager = hostManager = null;
            for (int i = spawnedRoots.Count - 1; i >= 0; i--)
                if (spawnedRoots[i] != null)
                    UnityEngine.Object.DestroyImmediate(spawnedRoots[i]);
            spawnedRoots.Clear();
            GameAuthority.ResetToLocal();
            GameAuthority.LocalClientIdResolver = null;
            SyncedStateRegistry.Clear();
        }

        [UnityTest]
        public IEnumerator HostAndClient_SessionListsPlayersWithNamesHostFlagAndPing()
        {
            NetworkSettings settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>("Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset");
            ushort port = nextPort++;
            hostManager = CreateNetworkManager("PauseHostNM", settings, port, true);
            Assert.IsTrue(hostManager.StartHost());
            clientManager = CreateNetworkManager("PauseClientNM", settings, port, false);
            Assert.IsTrue(clientManager.StartClient());
            yield return WaitUntil(() => hostManager.ConnectedClientsIds.Count == 2 && clientManager.IsConnectedClient, 6f, "Client did not connect.");
            ulong clientId = clientManager.LocalClientId;

            NetworkPlayerController hostPlayer = SpawnPlayer(settings, NetworkManager.ServerClientId, 1, "Amine", new Vector3(0f, 0.5f, 0f));
            NetworkPlayerController clientPlayer = SpawnPlayer(settings, clientId, 2, "Yasmine", new Vector3(3f, 0.5f, 0f));

            // Host-side view: both players, names, slots, host flag; the client's ping is measured by the host
            ISessionPlayer hostView = hostPlayer, clientView = clientPlayer;
            yield return WaitUntil(() => hostView.DisplayName == "Amine" && clientView.DisplayName == "Yasmine", 4f, "Names did not resolve.");
            Assert.IsTrue(hostView.IsHost);
            Assert.IsTrue(hostView.IsLocal, "The host owns its own player.");
            Assert.IsFalse(clientView.IsHost);
            Assert.AreEqual(1, hostView.Slot);
            Assert.AreEqual(2, clientView.Slot);
            Assert.AreEqual(0, hostView.PingMs, "The host has no ping to itself.");
            Assert.GreaterOrEqual(clientView.PingMs, 0);

            // Client-side view: its own player is "you", the host is "host", and the replicated list has both
            ISessionPlayer clientOwn = null, clientSeesHost = null;
            yield return WaitUntil(() =>
            {
                foreach (NetworkObject obj in clientManager.SpawnManager.SpawnedObjectsList)
                {
                    if (obj == null || !obj.TryGetComponent(out NetworkPlayerController p))
                        continue;
                    if (p.OwnerClientId == clientId) clientOwn = p;
                    else clientSeesHost = p;
                }
                return clientOwn != null && clientSeesHost != null
                       && clientOwn.DisplayName == "Yasmine" && clientSeesHost.DisplayName == "Amine";
            }, 5f, "Players did not replicate to the client.");
            Assert.IsTrue(clientOwn.IsLocal);
            Assert.IsFalse(clientOwn.IsHost);
            Assert.IsTrue(clientSeesHost.IsHost);
            Assert.IsFalse(clientSeesHost.IsLocal);
            Assert.GreaterOrEqual(clientOwn.PingMs, 0, "A client measures its own ping to the host.");
            Assert.AreEqual(0, clientSeesHost.PingMs);

            // The session registry has every spawned player copy (host + client copies live in this one process)
            var listed = new List<ISessionPlayer>();
            GameSession.CopyPlayers(listed);
            Assert.GreaterOrEqual(listed.Count, 2);
            for (int i = 1; i < listed.Count; i++)
                Assert.LessOrEqual(listed[i - 1].Slot, listed[i].Slot, "Listed by slot.");
        }

        [UnityTest]
        public IEnumerator Solo_MenuPausesAndResumes()
        {
            Assert.IsFalse(GameAuthority.IsMultiplayer);
            PauseMenu menu = PauseMenu.Instance;
            if (menu == null)
            {
                var go = new GameObject("PauseMenu");
                spawnedRoots.Add(go);
                menu = go.AddComponent<PauseMenu>();
            }
            yield return null;
            menu.Open();
            Assert.IsTrue(menu.IsOpen);
            Assert.AreEqual(0f, Time.timeScale, "Solo pauses the game.");
            menu.Close();
            Assert.IsFalse(menu.IsOpen);
            Assert.AreEqual(1f, Time.timeScale, "Resume restores time.");
        }

        NetworkPlayerController SpawnPlayer(NetworkSettings settings, ulong owner, byte slot, string name, Vector3 pos)
        {
            GameObject go = UnityEngine.Object.Instantiate(settings.NetworkPlayerPrefab, pos, Quaternion.identity);
            spawnedRoots.Add(go);
            go.GetComponent<NetworkObject>().SpawnAsPlayerObject(owner);
            var controller = go.GetComponent<NetworkPlayerController>();
            controller.InitializeServerIdentity(slot, name, NetworkCharacterAppearance.From(CharacterAppearance.Default), pos, Quaternion.identity);
            Rigidbody body = go.GetComponent<Rigidbody>();
            if (body != null) body.isKinematic = true;
            return controller;
        }

        NetworkManager CreateNetworkManager(string name, NetworkSettings settings, ushort port, bool isServer)
        {
            var go = new GameObject(name);
            spawnedRoots.Add(go);
            var nm = go.AddComponent<NetworkManager>();
            var utp = go.AddComponent<UnityTransport>();
            if (isServer) utp.SetConnectionData("127.0.0.1", port, "0.0.0.0");
            else utp.SetConnectionData("127.0.0.1", port);
            nm.NetworkConfig = new NetworkConfig { NetworkTransport = utp, TickRate = 30, EnableSceneManagement = false, ConnectionApproval = false };
            if (settings.NetworkPrefabsList != null)
                nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(settings.NetworkPrefabsList);
            return nm;
        }

        static IEnumerator WaitUntil(Func<bool> predicate, float timeoutSeconds, string failMessage)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup >= deadline)
                    Assert.Fail(failMessage);
                yield return null;
            }
        }
    }
}
