using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;
using UpcycleCity.Character;
using UpcycleCity.Cinematics;
using UpcycleCity.Inventory;
using UpcycleCity.Networking;

namespace UpcycleCity.Tests.EditMode
{
    /// <summary>
    /// EditMode checks for the MVP session flow (Intro → MainMenu → Room → CharacterCreator → EcosystemSandbox) and the
    /// "start with nothing" rule: scene-name settings, build order, intro hand-off, ready-flag serialization, and
    /// zero starting coins / no starting tools in the player prefabs, the team wallet and the city-state prefab.
    /// </summary>
    public sealed class SessionFlowEditModeTests
    {
        const string NetworkSettingsPath = "Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset";

        static NetworkSettings LoadSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(NetworkSettingsPath);
            Assert.IsNotNull(settings, $"Missing {NetworkSettingsPath}.");
            return settings;
        }

        [Test]
        public void NetworkSettings_SessionFlowScenesAndStartingCoins()
        {
            NetworkSettings settings = LoadSettings();
            Assert.AreEqual("MainMenu", settings.MenuSceneName);
            Assert.AreEqual("CharacterCreator", settings.CharacterSceneName);
            Assert.AreEqual("EcosystemSandbox", settings.SoloSceneName);
            Assert.AreEqual("EcosystemSandbox", settings.CoopSceneName);
            Assert.AreEqual(0, settings.StartingTeamCoins, "Runs must start with 0 coins.");

            var fresh = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                Assert.AreEqual(0, fresh.StartingTeamCoins);
                Assert.AreEqual("MainMenu", fresh.MenuSceneName);
                fresh.SetStartingTeamCoins(-5);
                Assert.AreEqual(0, fresh.StartingTeamCoins, "Negative starting coins clamp to 0.");
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }

        [Test]
        public void BuildSettings_FollowTheSessionFlowOrder()
        {
            string[] expected =
            {
                "Assets/UpcycleCity/Scenes/Intro.unity",
                "Assets/UpcycleCity/Scenes/MainMenu.unity",
                "Assets/UpcycleCity/Scenes/CharacterCreator.unity",
                "Assets/UpcycleCity/Scenes/EcosystemSandbox.unity",
            };
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            Assert.GreaterOrEqual(scenes.Length, expected.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], scenes[i].path, $"Build index {i}.");
                Assert.IsTrue(scenes[i].enabled, $"{expected[i]} must be enabled.");
            }

            var intro = AssetDatabase.LoadAssetAtPath<IntroConfig>("Assets/UpcycleCity/Settings/Intro/IntroConfig.asset");
            Assert.IsNotNull(intro);
            Assert.AreEqual("MainMenu", intro.nextScene, "The intro must hand over to the Main Menu.");
        }

        [Test]
        public void MainMenuScene_OwnsSessionAndCreatorHasNoNetworking()
        {
            string menuYaml = System.IO.File.ReadAllText("Assets/UpcycleCity/Scenes/MainMenu.unity");
            StringAssert.Contains("UpcycleCity.Networking.CoopSessionManager", menuYaml);
            StringAssert.Contains("UpcycleCity.Networking.CoopMenuController", menuYaml);

            string creatorYaml = System.IO.File.ReadAllText("Assets/UpcycleCity/Scenes/CharacterCreator.unity");
            StringAssert.DoesNotContain("UpcycleCity.Networking.CoopSessionManager", creatorYaml,
                "The creator must not carry its own session: the persistent one comes from the Main Menu.");
            StringAssert.DoesNotContain("UpcycleCity.Networking.CoopMenuController", creatorYaml);
            StringAssert.Contains("gameplayScene: EcosystemSandbox", creatorYaml);
        }

        [Test]
        public void PlayerPrefabs_StartWithNoCoinsAndNoTools()
        {
            foreach (string path in new[]
                     {
                         "Assets/UpcycleCity/Prefabs/Player/Player.prefab",
                         "Assets/UpcycleCity/Prefabs/Player/NetworkPlayer.prefab",
                     })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsNotNull(prefab, path);
                PlayerInventory inv = prefab.GetComponentInChildren<PlayerInventory>(true);
                Assert.IsNotNull(inv, $"{path} has no PlayerInventory.");
                using var so = new SerializedObject(inv);
                Assert.AreEqual(0, so.FindProperty("startingCoins").intValue, $"{path} startingCoins.");
                Assert.AreEqual(0, so.FindProperty("startingItems").arraySize, $"{path} must start without tools.");
            }

            var cityState = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/UpcycleCity/Prefabs/Networking/NetworkCityState.prefab");
            Assert.IsNotNull(cityState);
            using var repSo = new SerializedObject(cityState.GetComponent<NetworkStateReplicator>());
            Assert.AreEqual(0, repSo.FindProperty("startingTeamCoins").intValue);
            Assert.AreEqual(0, new SharedTeamWallet().Balance, "SharedTeamWallet defaults to 0 coins.");
        }

        [Test]
        public void CoopLobbyPlayer_ReadyFlagSerializesWithAppearance()
        {
            var look = new CharacterAppearance { sex = CharacterSex.Female, skinTone = 4, outfitColor = 2, hairStyle = 6, hairColor = 3 };
            var entry = new CoopLobbyPlayer
            {
                clientId = 3,
                slotNumber = 2,
                displayName = new FixedString32Bytes("Maya"),
                isHost = 0,
                appearance = NetworkCharacterAppearance.From(look),
                isReady = 1,
            };

            using var writer = new FastBufferWriter(128, Allocator.Temp);
            writer.WriteValueSafe(entry);
            using var reader = new FastBufferReader(writer, Allocator.Temp);
            reader.ReadValueSafe(out CoopLobbyPlayer restored);

            Assert.AreEqual(entry, restored);
            Assert.IsTrue(restored.IsReady);
            Assert.AreEqual(look, restored.appearance.ToAppearance());

            CoopLobbyPlayer notReady = restored;
            notReady.isReady = 0;
            Assert.AreNotEqual(entry, notReady, "Ready state must take part in roster equality.");
        }
    }
}
