# Upcycle City — Crafting Cards & Manual Machines (`UpcycleCity.Crafting`)

Data-driven, host-authoritative recycling, refining, building, and manual machine system built for **online co-op + offline solo** parity, **mobile-first UI Toolkit**, and future **workshop property build mode** (buy, place on a 1 m grid, rotate in 90° steps, move, pick up, persist).

---

## 1. Architecture & Core Invariants

1. **Trash NEVER enters `PlayerInventory`.**
   - Physical trash (`TrashItem`) is carried and thrown with `PhysicsGrabController`.
   - Recyclables, consumables (like Catalyst), refined materials, built-object counts, and Team Energy (`NRG`) live exclusively in the shared host-authoritative [`TeamMaterialStock`](TeamMaterialStock.cs), backed by the pure C# [`MaterialStockLedger`](MaterialStockLedger.cs).
2. **Solo & Co-op use one code path (`GameAuthority.Current`).**
   - Stations (`CraftingMachine`, `ManualEnergyGenerator`) and `TeamMaterialStock` never check `IsServer` or define their own RPCs.
   - All mutations flow through `GameAuthority.Current.RequestCustomAction(...)`, `GameAuthority.Current.RequestDepositTrash(...)`, and `GameAuthority.Current.SpawnNetworkedPrefab(...)`.
   - `TeamMaterialStock`, `CraftingMachine`, and `ManualEnergyGenerator` replicate state via `ISyncedStateProvider` (`SyncedStateRegistry`).
   - Machine processing replicates **once per job** (start server timestamp + duration + active card index + completion sequence) rather than every frame; both host and clients predict progress locally using `GameAuthority.Current.ServerTime`.
3. **Stable placement identity (build-mode ready).**
   - Every `CraftingMachine` and `ManualEnergyGenerator` is keyed by a stable `placementId` (`machine:<machineId>:<placementId>` / `generator:<machineId>:<placementId>`), **never** by world position.
   - Scene-placed machines serialize a stable `placementId` (generated in `OnValidate` or the scene builder).
   - Prefab assets leave `placementId` empty; when spawned at runtime via `GameAuthority.Current.SpawnNetworkedPrefab`, [`NetworkMachineIdentity`](../Networking/NetworkMachineIdentity.cs) assigns a host-authoritative GUID on spawn and replicates it via a `NetworkVariable<FixedString64Bytes>` so clients re-register handlers, `SyncedStateRegistry`, and `TrashHopperRegistry` cleanly.
   - Both stations implement `CaptureSaveState()` / `RestoreSaveState(in MachineSaveState)` for future workshop persistence.
4. **Data-driven machine types (zero code changes to add a machine type).**
   - Machine types are [`MachineTypeDefinition`](MachineTypeDefinition.cs) ScriptableObjects (`Settings/Crafting/MachineTypes/`), referenced by both `MachineDefinition.machineType` and `CraftingCard.requiredMachineType`.
5. **Archetype-ID trash mapping & per-machine hopper filtering.**
   - `MaterialDefinition.sourceTrashArchetypeIds` matches against `TrashItem.ArchetypeId` (populated from `WasteCatalog.Entry.archetypeId` and replicated in `NetworkTrashDescriptor`), falling back to `MaterialDefinition.sourceWasteStream` — never display text.
   - Each `MachineDefinition` declares `acceptedHopperStreams` and `acceptedHopperMaterials`. Dropping an incompatible trash item into a machine hopper leaves the physical `TrashItem` intact and returns targeted feedback telling the player what the hopper accepts.
   - Sorting crates (`SortingCrate`) also credit `TeamMaterialStock` when `EcosystemBalance.sortingCratesCreditMaterialStock` is enabled (scaled by `EcosystemBalance.sortingMaterialYieldScale`).
6. **Debug cheats are gated.**
   - Debug actions (`ActionSetLevel`, `ActionGrantStarterKit`, `ActionAddEnergy`, `ActionClearStock`) are compiled and registered **only** in `#if DEVELOPMENT_BUILD || UNITY_EDITOR` and **only** when `CraftingDebugGate.IsDebugActionsAllowed` is true (default `false` in `EcosystemSandbox.unity`; enabled in the dev `CraftingWorkshop.unity` scene).
   - `CraftingUI` only builds its debug bar in dev builds / Editor when `showDebugButtons` is true, and hides it whenever `CraftingDebugGate.IsDebugActionsAllowed` is false.

---

## 2. ScriptableObject Reference (Exact Serialized Fields)

| Asset Type | Create Menu | Key Serialized Fields |
| --- | --- | --- |
| `MachineTypeDefinition` | `Upcycle City/Crafting/Machine Type` | `typeId`, `displayName`, `description`, `accentColor`, `icon` |
| `MaterialDefinition` | `Upcycle City/Crafting/Material Definition` | `materialId`, `displayName`, `description`, `category` (`Trash`, `Consumable`, `Refined`, `Built`), `unit` (`Kilograms`, `Units`), `accentColor`, `icon`, `acceptsTrashDeposits`, `sourceWasteStream`, `sourceTrashArchetypeIds`, `depositYieldPerTrashItem` |
| `MachineDefinition` | `Upcycle City/Crafting/Machine Definition` | `machineId`, `displayName`, `description`, `machineType` (`MachineTypeDefinition`), `accentColor`, `icon`, `defaultLevel`, `maxLevel`, `acceptedCardCategories`, `interactRange`, `upgradeCosts` (`MachineUpgradeCost[]`), `acceptedHopperStreams` (`WasteStream[]`), `acceptedHopperMaterials` (`MaterialDefinition[]`), `purchasePrice`, `machinePrefab`, `placementFootprint` (`Vector2` in metres on the 1.0 m build grid, rotatable in 90° steps) |
| `CraftingCard` | `Upcycle City/Crafting/Crafting Card` | `cardId`, `displayName`, `description`, `category` (`Refining`, `Building`, `Energy`), `requiredMachineType` (`MachineTypeDefinition`), `complexityLevel`, `energyCost`, `temperatureCelsius`, `processingSeconds`, `inputs` (`MaterialAmount[]`), `outputs` (`MaterialAmount[]`), `cardArtwork` (`Sprite`, optional — procedural card rendered if null), `outputPrefab` (`GameObject`, optional networked prefab spawned on completion) |
| `CraftingDatabase` | `Upcycle City/Crafting/Crafting Database` | `machineTypes`, `materials`, `cards`, `machines` (shipped at `Resources/CraftingDatabase.asset`). Includes a custom Inspector with **Validate Database** and automatic `OnValidate()` warnings. |
| `CraftingStarterKit` | `Upcycle City/Crafting/Starter Kit` | `energy`, `coins`, `materials` (`MaterialAmount[]`) |

---

## 3. Step-by-Step Recipes

### A. Add a New Material
1. In the Project window under `Assets/UpcycleCity/Settings/Crafting/Materials/`, right-click → **Create → Upcycle City → Crafting → Material Definition** (e.g. `Mat_GlassBricks.asset`).
2. Fill in:
   - `materialId`: unique snake_case ID (e.g. `glass_bricks`)
   - `displayName`, `description`, `category`, `unit`, `accentColor`
   - If physical `TrashItem`s should deposit into this material: check `acceptsTrashDeposits`, set `sourceWasteStream` and `depositYieldPerTrashItem`, and optionally list exact `WasteCatalog` archetype IDs in `sourceTrashArchetypeIds` (e.g. `broken_tv`, `old_tyre`).
3. Add the asset to `Resources/CraftingDatabase.asset` → **Materials** list and click **Validate Database**.
4. To credit the material from gameplay code on the host/local authority:
   ```csharp
   TeamMaterialStock stock = TeamMaterialStock.EnsureInstance();
   stock.TryAddMaterialAuthoritative("glass_bricks", 2.0f);
   ```

### B. Add a New Machine Type & Machine (Zero Code Changes!)
1. **Create the Machine Type asset:** under `Assets/UpcycleCity/Settings/Crafting/MachineTypes/`, right-click → **Create → Upcycle City → Crafting → Machine Type** (e.g. `Type_GlassKiln.asset`), set `typeId = "glass_kiln"` and `displayName = "Glass Kiln"`.
2. **Create the Machine Definition:** under `Assets/UpcycleCity/Settings/Crafting/Machines/`, right-click → **Create → Upcycle City → Crafting → Machine Definition** (e.g. `MachineDef_GlassKiln.asset`), and set:
   - `machineId = "machine_glass_kiln"`, `machineType = Type_GlassKiln`
   - `defaultLevel`, `maxLevel`, `acceptedCardCategories`, `upgradeCosts` (one entry per `targetLevel` in `1..maxLevel`)
   - `acceptedHopperStreams` / `acceptedHopperMaterials` (leave both empty if the machine has no physical trash hopper)
   - `purchasePrice` and `placementFootprint` (in 1.0 m grid cells, e.g. `2 × 2`)
3. **Create the networked Machine Prefab** under `Assets/UpcycleCity/Prefabs/Crafting/Machines/`:
   - Add the visual mesh (optional children named `Upgrade_Lvl1` for Lv 1+ visuals and `SpinPart` / `PistonPart` for `CraftingMachineVisuals`).
   - Add solid `BoxCollider` on layer `Interactable` (and optional child `HopperTrigger` with `isTrigger = true` on layer `TriggerZone`).
   - Add `CraftingMachineVisuals`, `CraftingMachine` (wire `definition = MachineDef_GlassKiln`, leave `placementId` **empty** on the prefab asset!), `NetworkObject`, and `NetworkMachineIdentity`.
   - Assign the saved prefab to `MachineDef_GlassKiln.machinePrefab`.
4. **Register & validate:**
   - Add `Type_GlassKiln` to `CraftingDatabase.machineTypes` and `MachineDef_GlassKiln` to `CraftingDatabase.machines`.
   - Run **Upcycle City → Networking → Refresh Network Prefabs** (updates `Settings/Networking/UpcycleCityNetworkPrefabs.asset` and ensures `GlobalObjectIdHash != 0`).
   - Select `Resources/CraftingDatabase.asset` and click **Validate Database**.

### C. Add a New Crafting Card
1. Under `Assets/UpcycleCity/Settings/Crafting/Cards/`, right-click → **Create → Upcycle City → Crafting → Crafting Card**.
2. Fill in:
   - `cardId` (unique snake_case ID), `displayName`, `description`
   - `category` (`Refining` or `Building`)
   - `requiredMachineType` (reference the `MachineTypeDefinition` asset, e.g. `Type_Pulper`)
   - `complexityLevel` (must be `<= MaxLevel` of at least one compatible `MachineDefinition`)
   - `energyCost`, `temperatureCelsius` (0 for assembly), `processingSeconds`
   - `inputs` (`MaterialAmount[]`) and `outputs` (`MaterialAmount[]`)
   - Optional `cardArtwork` (`Sprite` — if left empty, `CraftingUI` automatically renders a procedural card face from the card's data)
   - Optional `outputPrefab` (must have a `NetworkObject` and be registered via **Refresh Network Prefabs**; spawned cleanly beside the machine on completion)
3. Add the card to `Resources/CraftingDatabase.asset` → **Cards** list and click **Validate Database**.

### D. Execute a Card or Upgrade from Code (Solo & Co-op Safe)
```csharp
// Execute a card on a CraftingMachine (callback signature: Action<bool, string>)
machine.RequestExecuteCard(card, (bool success, string message) =>
{
    Debug.Log(success ? $"Started: {message}" : $"Blocked: {message}");
});

// Upgrade a CraftingMachine or ManualEnergyGenerator (+1 level, atomic coins + materials)
machine.RequestUpgrade((bool success, string message) =>
{
    Debug.Log(message);
});

// Crank a ManualEnergyGenerator
generator.RequestCrank((bool success, string message) =>
{
    Debug.Log(message);
});
```

### E. Spawn a Machine at Runtime (Workshop Build Mode)
```csharp
using UpcycleCity.Core;

// Host-authoritative spawn with a stable placement ID applied BEFORE NetworkObject.Spawn.
// No UpcycleCity.Networking reference is needed; clients receive the final ID in the initial
// spawn payload with zero double-registration, and solo mode applies it immediately too.
GameObject spawned = GameAuthority.Current.SpawnNetworkedPrefab(
    machineDef.MachinePrefab,
    gridPos,
    snappedRot,
    beforeSpawn: go => go.GetComponent<IMachineIdentityReceiver>()?.ApplyPlacementId(newPlacementId));
```
- Moving a placed machine (`transform.SetPositionAndRotation(...)`) never invalidates its `SyncKey`, `HopperKey`, or custom-action handlers because identity is bound to `PlacementId`, not position.
- Despawning a machine via `GameAuthority.Current.DespawnNetworkedObject(spawned)` automatically unregisters its `ISyncedStateProvider` and removes its replicated entry from `NetworkStateReplicator` so connected clients and late joiners never see dead records.
- Persistence snapshots use `MachineSaveState` (`schemaVersion`, `placementId`, `machineId`, `level`, `position`, `rotation`, plus `activeCardId` and `remainingProcessingSeconds` to resume in-progress jobs across saves).

---

## 4. Validation Rules & Editor Tools

- **Menu Items:**
  - **Upcycle City → Crafting → Rebuild Crafting Content** (`CraftingContentBuilder`) — idempotently ensures all Machine Types, Materials, Prefabs (with empty `placementId` and valid non-zero `GlobalObjectIdHash`), Machine Definitions, Cards, `CraftingDatabase`, Starter Kit, phone-first `Resources/CraftingPanelSettings.asset` (`1080×608` reference), UI Config, `WasteCatalog` archetype IDs, and `UpcycleCityNetworkPrefabs.asset`.
  - **Upcycle City → Crafting → Build Crafting Workshop Scene** (`CraftingWorkshopBuilder`) — builds `Scenes/CraftingWorkshop.unity` (build index 3) with all 6 stations and dev debug controls enabled.
- **`CraftingDatabase.Validate(List<string> errors)`** verifies:
  - Duplicate IDs across machine types, materials, cards, and machines.
  - Missing references (`requiredMachineType`, `machineType`, `inputs`/`outputs` materials, `machinePrefab`).
  - Card `complexityLevel` exceeds the highest `MaxLevel` of any compatible machine.
  - Invalid or duplicate `upgradeCosts` `targetLevel` entries (outside `1..maxLevel`).
  - Missing `NetworkObject` component or missing `UpcycleCityNetworkPrefabs.asset` registration on every `MachineDefinition.MachinePrefab` and `CraftingCard.OutputPrefab`.

---

## 5. Rules Evaluation Order (`CraftingRules`)

Both UI previews (`CraftingRules.ValidateQuick` — zero-alloc) and authoritative execution (`CraftingRules.Validate` / `TryExecute`) enforce the exact order:
1. Valid arguments & compatible `MachineTypeDefinition` + `CardCategory` (`IncompatibleMachine`)
2. `machineLevel >= card.ComplexityLevel` (`LevelTooLow` — `"Requires machine level X (this machine is level Y)"`, checked **before** busy so locked cards always explain their level requirement)
3. Player within interaction range (`OutOfRange` — `"Move closer to the <Machine>"`; in co-op, requests with no known sender position are rejected)
4. Machine not currently processing (`MachineBusy`)
5. All input materials and team NRG present (`InsufficientResources` — lists **every** missing material and energy shortfall; consumes nothing on failure)

---

## 6. Workshop Purchase (Buy the Garage, then each Machine)

In `EcosystemSandbox.unity` the Factory Yard `Upcycle_Workshop_Bay` is a buyable lot, the **Upcycle Garage**. Everything runs through `GameAuthority` custom actions, so solo and co-op use the same path, and the lot replicates as **one** synced record.

| Piece | Role |
| --- | --- |
| [`WorkshopConfig`](WorkshopConfig.cs) (`Settings/Crafting/WorkshopConfig.asset`) | Lot id/name/price (`120`), purchase range, and one `MachineOffer` per stand (`slotId`, `machine`, short name, `price`, `iconKey`). A stand's list index is its bit, so append to the list and never reorder it. Prices are read on the host only. |
| [`WorkshopProperty`](WorkshopProperty.cs) | `ISyncedStateProvider` (`workshop:<propertyId>`) with the owned flag + machine bitmask ([`WorkshopPropertyState`](WorkshopPropertyState.cs): `flags` bit 0, `i0` mask). Host handlers `workshop.buy_property` / `workshop.buy_machine` (IntArg = stand index) check range, owned state and duplicates, then call `TrySpendTeamCoins`. On every peer it toggles the cordon, the sign and the stands, and shows a toast + `ShopPurchase`/`MachineUpgraded` sound when the state changes (a late joiner's first record is a silent baseline). Dev flags: `devOwnedAtStart`, `devAllMachinesAtStart`. |
| [`WorkshopSaleSign`](WorkshopSaleSign.cs) | `IInteractable` FOR SALE board: `[E] Buy Upcycle Garage · 120 coins` / `… · Not enough coins (need 40 more)`. Once owned it becomes the name plate. |
| [`WorkshopMachineStand`](WorkshopMachineStand.cs) | `IInteractable` plinth + sign (name, icon, price) on each machine spot. `[E] Buy Pulper · 60 coins` once the lot is owned. Hidden after purchase. |
| [`WorkshopMachineLock`](WorkshopMachineLock.cs) | On each **scene-placed** machine: while locked, renderers, lights and colliders are off. The GameObject stays active so its in-scene `NetworkObject`, placement ID and synced state are untouched. `CraftingMachine` / `ManualEnergyGenerator` also refuse every request while locked (`Buy the X first.`): cards, upgrades, cranks and hopper deposits. |

- **Editor:** **Upcycle City → Crafting → Setup Workshop Purchase In Open Scene** (`WorkshopPurchaseSetup`) adds or rebuilds the `Workshop_Purchase` group under the bay and adds the locks. It is idempotent, and it saves the scene only if the scene was clean before. `EcosystemSandboxBuilder` calls the same `BuildInBay`. The config asset is created only when missing, so tuning survives re-runs.
- **Dev scenes:** `CraftingWorkshop.unity` has no `WorkshopProperty` or locks, so its machines stay free. A machine without a `WorkshopMachineLock` is always usable.
- **Tests:** `Tests/EditMode/WorkshopEditModeTests.cs` (codec + shipped config) and `Tests/PlayMode/WorkshopPurchasePlayModeTests.cs` (host + client over loopback incl. late join, and solo).
