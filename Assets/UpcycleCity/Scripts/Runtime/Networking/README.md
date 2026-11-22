# Upcycle City Networking Guide (`UpcycleCity.Networking`)

Online co-op multiplayer (up to **4 players**, client-hosted on one phone, host-authoritative game state) built on **Netcode for GameObjects** (`com.unity.netcode.gameobjects`), **Unity Transport**, and **Unity Multiplayer Services** Sessions + Relay (with a **Direct IP:port** fallback for local/LAN testing and automated tests).

---

## Architecture Overview

Networking is isolated in its own assembly (`UpcycleCity.Networking.asmdef`). Core gameplay classes in `UpcycleCity.Runtime` do **not** reference `Unity.Netcode` and never contain `if (IsServer)` or RPC boilerplate. Instead, gameplay code talks to three small contracts in `UpcycleCity.Core` / `UpcycleCity.Interaction`:

1. **Single Authoritative Action Path (`IGameAuthority` / `GameAuthority.Current`)**
   - In **solo play**, `GameAuthority.Current` is `LocalGameAuthority.Instance` (executes directly with zero Netcode overhead).
   - In **co-op**, `NetworkStateReplicator` spawns on the host, installs itself as `GameAuthority.Current`, routes client requests to host-validated RPCs, executes the **same** authoritative gameplay methods, resolves shop prices server-side from `ShopCatalog` (clients check `CanAddItem` locally before requesting; the host only charges), updates the **`SharedTeamWallet`**, and restores `LocalGameAuthority.Instance` on despawn.
2. **Generic Data-Driven State Sync (`ISyncedStateProvider` + `SyncedStateRegistry` + `NetworkStateReplicator`)**
   - Any world object or manager (districts, street bins, factory smokestacks, sorting crates, buy-back scales, city ecosystem, future automated machines) registers with `SyncedStateRegistry` and packs its live values into a compact `SyncedStateRecord`.
   - `NetworkStateReplicator` polls providers on the host at 4 Hz (`NetworkSettings.StateSyncIntervalSeconds`) and replicates **only changed deltas** into a single `NetworkList<ReplicatedStateEntry>` keyed by deterministic FNV-1a hash (collisions log an error and preserve the existing provider).
3. **Reusable Networked Physics Items (`NetworkedPhysicsItem` + `INetworkedPhysicsGrabbable`)**
   - Any grabbable/throwable `Rigidbody` prefab becomes co-op ready by adding `NetworkedPhysicsItem` (plus `NetworkObject` with **`DontDestroyWithOwner = true`** and owner-authoritative `NetworkTransform`).
   - Handles host-validated mutual exclusion and range checks (two players can never hold the same item), early ownership transfer during pickup anticipation so local holding feels lag-free, holder-only server ownership reclaim + sanitized velocity replication on drop/throw, thrower attribution (`LastHolderClientId`) for passive sorting/buy-back triggers, and clean drops if a holder disconnects.

### Key Files & Assets
- **Config (`ScriptableObject`):** `Assets/UpcycleCity/Settings/Networking/NetworkSettings.asset` (`NetworkSettings.cs`) — tick rate (30 Hz), max players (4), Relay vs Direct defaults, default port (`7777`), Relay timeout, disconnect timeout (`15s` default, configurable 5–30s; applied to `UnityTransport.DisconnectTimeoutMS` and `HeartbeatTimeoutMS`), optional solo scene override (empty = uses `CharacterCreatorController.GameplayScene`, i.e. `PlayerGym`), prefab references.
- **Auto-Registered Prefab List:** `Assets/UpcycleCity/Settings/Networking/UpcycleCityNetworkPrefabs.asset` — maintained automatically by **Upcycle City → Networking → Refresh Network Prefabs** (`NetworkPrefabRegistryTool.cs`) and its `AssetPostprocessor` (NGO's default `Assets/DefaultNetworkPrefabs.asset` auto-generation is disabled in `ProjectSettings/NetcodeForGameObjects.asset`).
- **Session & Lobby:** `CoopSessionManager.cs` (`DontDestroyOnLoad`), `CoopMenuController.cs` + `Assets/UpcycleCity/UI/CoopMenu.uss` (with 1-tap **Copy** button next to the join code), and `CoopInGameHud.cs` (mobile-scaled UI Toolkit overlay using `UpcycleCityPanelSettings.asset` with cached strings, 1-tap **Copy** button, and pooled remote player nameplates).
- **Players:** `Assets/UpcycleCity/Prefabs/Player/NetworkPlayer.prefab` (variant of `Player.prefab`) + `NetworkPlayerController.cs` (owner-authoritative movement, local-only camera/arms/HUD/audio listener, synced equipped tool ID for host validation, visible third-person `CharacterAvatar` with synced `CharacterAppearance` + overhead `"P2 · Name"` label on remote players).
- **Runtime Trash:** `Assets/UpcycleCity/Prefabs/Networking/NetworkTrashItem.prefab` (`NetworkedPhysicsItem` + `NetworkTrashArchetype`, `DontDestroyWithOwner = true`) — replicates `WasteCatalog` descriptors without needing a separate network prefab per litter mesh.
- **Tests:** `Assets/UpcycleCity/Tests/EditMode/` and `Assets/UpcycleCity/Tests/PlayMode/`.

---

## Step-by-Step Developer Recipes

Every code snippet below compiles directly inside `UpcycleCity.Runtime` without referencing `Unity.Netcode` or `UpcycleCity.Networking`.

### 1. How to Add a New Networked Grabbable Item (Tool, Crafted Item, Prop)
1. Create your prefab with a `Rigidbody`, collider, and an `IGrabbable` component (e.g. `TrashItem` or your new item script).
2. Add **`NetworkObject`** (enable **`DontDestroyWithOwner = true`** so a disconnecting holder drops the item instead of destroying it — `NetworkedPhysicsItem` also enforces this automatically), **`NetworkTransform`** (set `Authority Mode = Owner`, disable Scale sync), and **`NetworkedPhysicsItem`**.
3. Run **Upcycle City → Networking → Refresh Network Prefabs** (also runs automatically on prefab import). Validation will warn immediately if `DontDestroyWithOwner`, `Rigidbody`, `NetworkTransform`, or `Owner` authority is missing.
4. Spawn and despawn **authoritatively** (on the host in co-op, or locally in solo) through `GameAuthority.Current`. Calling `SpawnNetworkedPrefab` or `DespawnNetworkedObject` on a non-host client logs an error and returns `null` / `false` — when a client triggers a spawn (e.g. crafting), route through a validated custom action (see Recipe 3). Passing an optional `parent` in co-op parents the spawned object via `NetworkObject.TrySetParent` when `parent` has a spawned `NetworkObject`; non-networked parents log a warning and spawn at scene root (NGO constraint):

```csharp
using UnityEngine;
using UpcycleCity.Core;

public sealed class CraftedItemSpawner : MonoBehaviour
{
    [SerializeField] GameObject craftedItemPrefab;

    // Call from host / solo authoritative gameplay code (e.g. a custom action handler or machine tick):
    public GameObject SpawnCraftedItemAuthoritative(Vector3 position, Quaternion rotation)
    {
        if (!GameAuthority.IsAuthoritative)
            return null;

        return GameAuthority.Current.SpawnNetworkedPrefab(craftedItemPrefab, position, rotation);
    }

    public bool RecycleCraftedItemAuthoritative(GameObject instance)
    {
        if (!GameAuthority.IsAuthoritative)
            return false;

        return GameAuthority.Current.DespawnNetworkedObject(instance);
    }
}
```

### 2. How to Add a New Synced Machine or World Object State
Implement `ISyncedStateProvider` (3 members) and register in `OnEnable` / `OnDisable`. No new `NetworkVariable` or RPC is required:

```csharp
using UnityEngine;
using UpcycleCity.Core;

public sealed class PlasticCompactorMachine : MonoBehaviour, ISyncedStateProvider
{
    [SerializeField] string machineId = "compactor_01";
    float progress;
    int storedBales;
    bool isRunning;
    string syncKey;

    public string SyncKey => syncKey ??= SyncedStateRegistry.BuildSpatialKey("compactor", machineId, transform.position);

    void OnEnable() => SyncedStateRegistry.Register(this);
    void OnDisable() => SyncedStateRegistry.Unregister(this);

    void Update()
    {
        if (!GameAuthority.IsAuthoritative || !isRunning)
            return;
        // Advance machine simulation on host / solo only
        progress += Time.deltaTime * 0.2f;
    }

    public SyncedStateRecord CaptureSyncState() => new SyncedStateRecord
    {
        v0 = progress,
        i0 = storedBales,
        flags = isRunning ? (byte)1 : (byte)0,
    };

    public void ApplySyncState(in SyncedStateRecord state)
    {
        progress = state.v0;
        storedBales = state.i0;
        isRunning = (state.flags & 1) != 0;
        // Update visuals / animations on clients
    }
}
```

### 3. How to Add a New Authoritative Player Action (Crafting, Upgrading, Bikes)
Register a handler keyed by `(actionId, targetId)` so multiple instances (e.g. several crafting benches or machines in the city) coexist cleanly without clobbering each other. Handlers receive a `CustomGameActionContext` with the transport-verified `SenderClientId`, the sender's `SenderPosition` (owner-reported — see *Cooperative PvE Trust Model* below), and an `IsWithinRange(...)` helper, and return a `CustomGameActionResult` (`Ok(...)` / `Fail(...)`) that is sent back to the requesting player's optional `onResult` callback (synchronously in solo, or via targeted client RPC in co-op):

```csharp
using UnityEngine;
using UpcycleCity.Core;

public sealed class CraftingBench : MonoBehaviour
{
    const string CraftActionId = "craft_bicycle";

    [SerializeField] GameObject craftedBicyclePrefab;
    [SerializeField] Transform outputSpawnPoint;
    [SerializeField] float maxUseRange = 4.5f;

    string benchId;
    string BenchId => benchId ??= SyncedStateRegistry.BuildSpatialKey("bench", gameObject.name, transform.position);

    void OnEnable()
    {
        GameAuthority.RegisterCustomActionHandler(CraftActionId, BenchId, HandleCraftAuthoritative);
    }

    void OnDisable()
    {
        GameAuthority.UnregisterCustomActionHandler(CraftActionId, BenchId, HandleCraftAuthoritative);
    }

    // Called from UI button or [E] interact on any player (solo, host, or client):
    public void RequestCraftBicycle(int recipeIndex)
    {
        GameAuthority.Current.RequestCustomAction(
            CraftActionId,
            BenchId,
            intArg: recipeIndex,
            onResult: (success, message) =>
            {
                if (!string.IsNullOrEmpty(message))
                    Debug.Log(success ? $"[Craft] {message}" : $"[Craft Rejected] {message}");
            });
    }

    // Runs locally in solo, or on the host ONLY in co-op:
    CustomGameActionResult HandleCraftAuthoritative(in CustomGameActionContext context)
    {
        // Reject out-of-range requests using server-verified player position (or local player in solo)
        if (!context.IsWithinRange(transform.position, maxUseRange))
            return CustomGameActionResult.Fail("Move closer to the crafting bench.");

        // Validate materials/coins, then spawn authoritatively on host/solo:
        Transform spawn = outputSpawnPoint != null ? outputSpawnPoint : transform;
        GameObject spawned = GameAuthority.Current.SpawnNetworkedPrefab(craftedBicyclePrefab, spawn.position, spawn.rotation);
        return spawned != null
            ? CustomGameActionResult.Ok("Crafted Bicycle!")
            : CustomGameActionResult.Fail("Could not spawn crafted item.");
    }
}
```

### 4. How to Trigger a Replicated City Event Warning & Countdown
On the host (or in solo, e.g. from a future `CityEventDirector`), call `GameAuthority.Current.TriggerCityEvent(...)`:

```csharp
using UpcycleCity.Core;

public static class CityEventHelper
{
    public static void StartMarketSurge()
    {
        if (!GameAuthority.IsAuthoritative)
            return;

        GameAuthority.Current.TriggerCityEvent(
            "Street Food Market Surge",
            durationSeconds: 20f,
            severity: 1);
    }
}
```

`NetworkStateReplicator` replicates the event end timestamp (`ServerTime`) once — zero per-frame `NetworkVariable` churn — and `CoopInGameHud` computes and renders the live countdown banner on every connected player's screen.

---

## Known Limitations

- **No Mid-Game Late Join:** Players must join the co-op session while the host is in the lobby (`CharacterCreator`). Once the host starts gameplay (`LoadingGame` / `InGame`), `CoopSessionManager` rejects new connections during connection approval with `"Game already in progress. Join a lobby before the host starts."` so late joiners never enter a partially synchronized scene without stripped offline actors.
- **No Host Migration & Mobile Backgrounding (`DisconnectTimeoutSeconds` Trade-Off):** Sessions are client-hosted. When a host explicitly leaves or closes the desktop app, `CoopSessionManager` sends `HOST_LEFT` to all remote clients and briefly flushes the transport (~120–150 ms) before `Shutdown()` so clients receive `"Host left the co-op session."` immediately. On mobile, brief app backgrounding (such as switching to a messaging app to paste the join code) does **not** tear down Netcode — both host and client survive short interruptions, and on resume (`OnApplicationPause(false)` / focus regained) `CoopSessionManager.VerifySessionHealthOnResume()` checks whether the session is still alive and cleanly returns to `CharacterCreator` with a banner if the OS killed it while backgrounded. For ungraceful drops (crash, killed mobile app, network loss), `NetworkSettings.DisconnectTimeoutSeconds` defaults to **15 seconds** (with `1000 ms` heartbeats) instead of Unity Transport's 30 s default — cutting hang time in half while still tolerating brief cellular/Wi-Fi hiccups and quick app switches (configurable 5–30 s in `NetworkSettings.asset`).
- **Cooperative PvE Trust Model (Movement, Inventory & Equipped Tool):** Coins are strictly host-authoritative (`SharedTeamWallet`) and shop prices are resolved from the host's `ShopCatalog`. However, **player movement is owner-authoritative** (`NetworkTransform` in `Owner` mode), so server range checks trust client-reported player positions; likewise, each player's backpack items live in their local `PlayerInventory` and `NetworkPlayerController.EquippedItemId` is an owner-written `NetworkVariable` that the host reads to gate tool-required interactions (e.g. factory smokestack repair). This cooperative PvE trust model delivers zero-latency movement on mobile and avoids per-slot inventory replication complexity for friends-only co-op; if competitive or untrusted public lobbies are ever added, movement validation, inventories, and tool equips should move to host-authoritative storage.

---

## Unity Cloud Services Setup (To Enable Online Relay Join Codes)
The Relay + Lobby session flow (`MultiplayerService.Instance.CreateSessionAsync` / `JoinSessionByCodeAsync`) is fully wired, but requires linking this Unity project to a Unity Cloud project:
1. Open **Edit → Project Settings → Services** in the Unity Editor and link your Unity Organization & Project ID.
2. In the Unity Cloud Dashboard for that project, enable **Authentication** (Anonymous), **Relay**, and **Multiplayer Services (Sessions)**.
3. Until linked, choosing Relay displays a friendly error and offers a 1-tap switch to **Direct IP (`127.0.0.1:7777`)** for local/LAN play and testing.
