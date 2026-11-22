# Upcycle City — Architecture

A system-by-system map of the codebase. For working conventions see [CLAUDE.md](CLAUDE.md);
for a fast quickstart see [AGENT.md](AGENT.md).

---

## 1. Overview

**Upcycle City** is a first-person, physics-driven city-cleanup game. You explore a
polluted city, pick up litter one piece at a time (by hand or with a litter-picker),
carry or throw it into colour-coded sorting crates or sell it at the RÉCUP buy-back scale,
and a **living pollution ecosystem** reacts: each district's grime, air and foliage shift
through URP shaders, the sky and fog clear, resident satisfaction rises, and citizens
litter less. There is also a UI-Toolkit character creator that themes the player's arms
and shadow body.

- **Engine:** Unity `6000.6.6f1`, Universal Render Pipeline `17.6.0`, Input System `1.20.0`,
  Burst, UI Toolkit (uGUI also present).
- **Language/paradigm:** C# MonoBehaviours. Classic OOP with an explicit player FSM,
  interface-based interaction, static registries + a service locator, event-driven audio,
  and one Burst job for the crowd. No full ECS/DOTS.
- **Target:** mobile-conscious (PC + Mobile URP renderer variants, hard performance budgets).

### Assemblies

| Asmdef | Scope | References |
|--------|-------|-----------|
| `UpcycleCity.Runtime` | all gameplay (`Assets/UpcycleCity/Scripts/Runtime`) | URP Core + Universal, Input System, Burst |
| `UpcycleCity.Editor` | builders, import rules (`Scripts/Editor`) | Runtime, Input System, URP Core, UGUI, Editor-only |

### Scenes

| Scene | Role |
|-------|------|
| `CharacterCreator.unity` | Intended first scene. UI-Toolkit body/eyes/hair creator; Start loads the gym. |
| `PlayerGym.unity` | Phase-1 proving ground: locomotion, grabbing/throwing, 4-stream sorting, HUD. |
| `EcosystemSandbox.unity` | Phase-2 full city (Avenue Bourguiba): districts, sources, crowds, shops, the whole ecosystem. Largest scene. |

---

## 2. Cross-cutting infrastructure (`Core`, `Player/PlayerContext`)

The project deliberately avoids `FindObjectsByType` in hot paths. Three mechanisms replace it:

- **`PlayerContext`** (`Player/PlayerContext.cs`) — a lightweight runtime service locator.
  The active `PlayerController` registers itself (`OnEnable`) and exposes `Active`,
  `ActiveModifiers`, `ActiveInteractor`, etc. Stations, HUDs, pollution zones and the
  ecosystem read the player through it instead of scanning the scene.
- **Static registries** — each manager keeps `static List<T>` registries that components
  join in `OnEnable` and leave in `OnDisable` (e.g. `CityEcosystemManager` tracks districts,
  sources and street bins; `PollutionController` tracks zones).
- **C# events** — e.g. `TrashItem.Spawned/Removed`, `PollutionController.Changed`,
  `CityDistrict.StateChanged`, motor `Jumped/Landed`. Systems subscribe rather than poll.

Because statics survive Unity's "enter Play mode without domain reload," managers reset them
with `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
(`CityEcosystemManager.ResetStatics`). **Follow this pattern for any new static state.**

`Core/GameLayers.cs` and `Core/GameTags.cs` centralise layer indices, names, composed
`LayerMask`s and tag constants; `Editor/ProjectLayersAndTagsSetup.cs` keeps them in sync with
`ProjectSettings/TagManager.asset`. `Core/TriggerRelay.cs` forwards child trigger events to the
owning component (Unity only messages the collider's own GameObject).

Execution order is tuned via `[DefaultExecutionOrder]` (e.g. the ecosystem manager runs early
at `-40`).

---

## 3. Player

Rooted at **`PlayerController`** — an orchestrator that `RequireComponent`s the whole player
stack and wires it together in `Awake`, then registers with `PlayerContext`.

**Composition (all on the player GameObject):**

- **`PlayerInputReader`** — reads *everything* through the Input System (`PlayerInput` +
  `InputSystem_Actions.inputactions`). Switches action maps between **Gameplay / UI /
  Placement** contexts. Gameplay code never touches raw devices.
- **`PlayerMotor`** — arcade-feel Rigidbody locomotion: acceleration/deceleration, slope
  projection, **coyote time**, **jump buffering**, variable jump gravity, ceiling-safe crouch.
  Driven by the FSM; reads tuning live from `PlayerConfig`.
- **`PlayerStateMachine`** + **`PlayerStates`** — an explicit FSM: `Idle / Walk / Sprint /
  Crouch / Jump / Fall / Locked`. Supports **override states** (`EnterOverrideState` /
  `ExitOverrideState`) for UI modals, repair holds, placement mode, riding a bike, etc.
  `PlayerController.SetMovementLocked` uses the `Locked` override and flips the input map.
- **`PlayerCameraController`** — first-person yaw/pitch, crouch eye smoothing, sprint FOV
  boost, headbob, landing dip; the motor takes its yaw from the camera each `FixedUpdate`.
- **`PlayerModifiers`** — the keyed stat stack (see §4).
- **`PlayerInventory`** — coins + a fixed tool/usable hotbar (see §6). **Never holds trash.**
- **`PhysicsGrabController`** — holds exactly **one** physical object at a time (hand or
  picker style), separate from inventory (see §5).
- **`PlayerInteractor`** — the single interaction ray (see §5).
- **`PlayerFeedbackHub`** — event bus for pluggable audio/VFX/shake/haptics modules; the
  controller emits `Jump/Land/Grab/Throw/Drop` feedback events through it.
- Cosmetic: **`FirstPersonArms`** (arms + held tool on an overlay view-model camera, tinted
  by the character creator), **`PlayerBodyShadow`** (shadow-only body so the in-head camera
  never clips it), **`LitterPickerView`** (animated picker jaws), **`PlayerBoosts`** (timed
  shop buffs written into `PlayerModifiers`).

**`PlayerConfig`** (ScriptableObject) holds all tuning (speeds, jump, camera feel, grab reach,
pickup dip/pitch) and is read live each tick, so Inspector edits apply instantly in Play mode.

---

## 4. Stat modifiers (`PlayerModifiers`)

A keyed modifier stack so independent systems can affect the player concurrently without
stepping on each other. Each modifier is filed under a **source id** string and a
`PlayerStat` / `PlayerModifierFlag`:

- `SetAdditive(source, stat, value)` / `SetMultiplier(source, stat, value)` / `SetFlag(...)`
- `RemoveAllFromSource(source)` to cleanly revoke everything a system added.

Sources in use: `"EquippedTool"` (reach/throw/speed/smog-immunity from the held tool),
`"SmogZone"` (speed penalty in thick smog — unless the `SmogImmune` flag is set), shop boosts.
**Rule:** never mutate move speed / reach / throw force directly — set a keyed modifier and
remove it by source. Stats like `MoveSpeedMultiplier`, `GrabReachBonus`, `ThrowForceBonus`,
and flags like `SmogImmune` / `BlockMove` flow from here into the motor and grabber.

---

## 5. Interaction & grabbing (`Interaction`, `Waste/TrashItem`)

**`PlayerInteractor`** casts one ray from screen centre each frame and routes the hit purely
by interface — it never special-cases concrete types:

- **`IGrabbable`** → `PhysicsGrabController` (physical objects; one at a time; never inventory)
- **`IInteractable`** → stations/buttons/crates/doors/machines (`E` to act)
- **`IPickupItem`** → `PlayerInventory` (tools, usables, coins)
- **`IToolTarget`** → objects that react to the *equipped* tool (jammed machine ↔ Wrench, grime
  ↔ Broom, bench ↔ Paint Tin)

`FocusHighlight` gives the targeted object a zero-allocation `MaterialPropertyBlock` glow.

**`TrashItem : IGrabbable`** is the central physical-litter class. Key facts:
- Trash is **grabbed by physics, one at a time** — it *never* enters inventory.
- Carries `WasteStream` (sorting type), `WasteSourceKind` (what caused it — Citizens / Bins /
  Factory / Decay / Initial), `pollutionWeight`, coin value, bulky flag, carry-speed penalty.
- Static `Spawned`/`Removed` events let the ecosystem track every live piece with zero scans.
- `IsContained` (stowed in the shopping cart) removes it from street pollution and from being
  re-collected; `Dispose()` destroys it when sorted/sold.

**`PhysicsGrabController`** holds one object with spring/velocity following, supports hand vs
picker pickup styles, and exposes a claw-provider hook so `LitterPickerView` animates its jaws
and reports where the claw tip is.

The pushable **`ShoppingCart`** (`Props`) is a rolling litter basket you grab by the handle;
collected trash is marked `IsContained`.

---

## 6. Inventory & items (`Inventory`)

`PlayerInventory` is a fixed hotbar of **tools / usables / coins only** — `ItemCategory`
deliberately excludes trash. Items implement **`IInventoryItem`** (data via the
`ItemDefinition` ScriptableObject) with pluggable **`IItemUseBehavior`** strategies, so a new
tool/consumable is authored as an asset + behaviour rather than new inventory code. Equipped
items push their bonuses into `PlayerModifiers` under the `"EquippedTool"` source (reach,
throw, speed, smog immunity) and may spawn a held view-model in the tool anchor.
`WorldPickupItem` is the collectible-in-world counterpart (bobbing/rotating to read as gear,
not litter).

---

## 7. Pollution rendering (`Pollution`, `Shaders`)

This is the **visual** half — how the city *looks* at a given pollution level — kept separate
from the ecosystem simulation that *decides* the level.

- **`PollutionController`** (`[ExecuteAlways]`, one per scene) — single source of truth for the
  scene-wide pollution level `[0..1]`. On change it:
  - pushes the level, the active **zones**, and the `PollutionProfile`'s surface look into
    shader globals (`PollutionShaderGlobals` ↔ `Shaders/Include/Pollution.hlsl`);
  - drives the **sun** (colour/intensity/elevation), ambient trilight, exponential-squared
    **fog**, and a **smog post-processing Volume** weight;
  - blends over `transitionTime` in Play mode, or snaps (`SetPollution(value, instant:true)`).
- **`PollutionProfile`** — the clean↔polluted endpoints: shared surface treatment for every
  *Pollution Lit* material, the clean-up dissolve, and the `AtmosphereState` (sun/ambient/fog)
  at each end, blended by `AtmosphereAt(level)`.
- **`PollutionZone`** — a vertical cylinder where local pollution differs from the scene level
  (a cleaned district, a filthy factory yard, a growing clean-up wave). Zones are registered
  with the controller and applied in enable order, capped at `MaxZones` (matches the shader).
  `SampleAt(worldPos)` mirrors the HLSL `SamplePollution()` so C# and shaders agree.
- **`CleanupWave`** — grows a zone outward from a centre: a radial "clean sweep" that dissolves
  litter and restores colour as it passes.
- **`PollutionResponder`** (base) / **`PollutionVisibility`** — react to pollution outside the
  shaders: smoke/sound/lights/gameplay, or make an object exist only in the dirty city (litter,
  dead trees) or only in the clean city (flowers).
- **Shaders:** `PollutionLit.shader` (grime/foliage surface that reads the globals),
  `PollutionSky.shader`, `SmogLayer.shader`, with shared HLSL includes.

---

## 8. Living ecosystem simulation (`Ecosystem`)

The **gameplay** half — the cause-and-effect model that *sets* pollution from the real world
state and feeds back into citizen behaviour. It runs on a cheap fixed tick (default 4 Hz).

- **`CityEcosystemManager`** (`[DefaultExecutionOrder(-40)]`, one per scene) — the coordinator.
  - Tracks every physical `TrashItem` via the `Spawned/Removed` events (no scans), enforces a
    **global trash cap** (`maxGlobalTrash`) for mobile.
  - Each tick: bins every live, *uncontained* trash item into its enclosing `CityDistrict` by
    pollution weight and by source kind, finalises each district, then aggregates a city-wide
    `CityPollution` and `CitySatisfaction` (district `CityWeight`-weighted).
  - Pushes `CityPollution` into `PollutionController.SetPollution`.
  - Computes **player smog exposure**: samples pollution at the player and, above
    `smogSlowThreshold`, applies a `"SmogZone"` `MoveSpeedMultiplier` penalty via
    `PlayerModifiers` (skipped if the player has the `SmogImmune` flag).
- **`CityDistrict`** (requires a `PollutionZone`) — a named circular district. Accumulates real
  trash weight + source air emissions inside its radius, blends toward a target pollution vs its
  `Capacity`, drives its zone's shader pollution, and computes **Resident Satisfaction**
  (65% cleanliness + 20% clean-bin availability + 15% air quality + service bonuses). Crucially,
  satisfaction feeds back: `LitterRateMultiplier` makes clean, well-serviced districts almost
  litter-free and dirty neglected ones litter faster. Also supports **background litter**:
  off-screen Tier-2 population adds virtual litter that materialises into real `TrashItem`s when
  the player arrives.
- **Pollution sources** — everything that *causes* pollution implements **`IPollutionSource`**
  (`PollutionSourceBase` handles registration, per-source trash caps, catalog lookups, tagging).
  The four modelled causes:
  1. **Citizens** (littering, via the citizen sim),
  2. **`OverflowingBinSource`** — street/shop bins fill from foot traffic and visibly overflow,
     spilling physical trash until the player empties them,
  3. **`FactorySmokestackSource`** — continuous air pollution + periodic scrap; mitigated by the
     Wrench (`IToolTarget`),
  4. **`UrbanDecaySource`** — neglected lots that accumulate bulky dumped items while the
     enclosing district stays dirty.
- **`WasteCatalog`** — shared archetypes of physical litter/bulky trash, with a bitmask of which
  causes may emit each archetype. **`BinFillVisual`** shows bin fullness physically (rising heap,
  lifting lid, overflow bags). **`FountainWaterResponder`** brings fountains back to life as
  pollution drops.
- **`EcosystemBalance`** (ScriptableObject, `Active` singleton) — one place to tune the whole
  city's difficulty/pacing (bin fill rate, citizen act/litter rates, trash cap, smog slowdown,
  factory output, district capacity, rewards). Overrides matching values on every system, live
  in Play mode. `Editor/EcosystemBalanceEditor` adds difficulty-preset buttons.
- **`EcosystemHUD`** — live telemetry: city cleanliness, satisfaction, per-district bars, and
  cause-attributed trash counts (Citizens / Bins / Factory / Decay).

---

## 9. Citizens & crowds (`Ecosystem/Citizen*`, `Crowd`)

A **2-tier hybrid LOD** approach so a dense city stays cheap:

- **`CitizenSimulationManager`** — **Tier 1 (near player):** a limited number of real animated
  citizens with personality profiles (`CitizenTypes`), who pause at `CitizenPointOfInterest`
  landmarks (shops, fountains, benches), react to smog and to district cleanliness, and actually
  deposit/littering trash. **Tier 2 (far):** cheap statistical agents that contribute background
  litter to districts without existing physically, materialising into Tier-1 citizens/trash as
  the player approaches.
- **`CrowdSystem`** — ambient background pedestrians run from **one** place: all walkers move in
  a single **Burst-compiled** `CrowdMovementJob`, not per-NPC `Update`s. Only the nearest visible
  NPCs (up to a budget) get a real animated rig from `CrowdRigPool`; the rest are instanced/
  batched. `CrowdAgent` is just data; `CrowdPath` is a walkable polyline (local-space, loopable);
  `CrowdSpawner` fills paths at scene start; `CrowdCharacterLibrary` supplies the visual variety.

---

## 10. Waste handling, shops & economy (`Waste`, `Shops`)

- **`WasteStream`** — the four sorting streams + bulky repair/upcycle items; colours mirror the
  (referenced but absent) `docs/game_concept.md` so bins, crates and HUD stay consistent.
- **`SortingCrate`** — colour-coded bin that accepts trash dropped/thrown into its trigger (or
  deposited with `E`), tracks **sorting Purity %**, and pays coins; wrong-stream items lower purity.
- **`RecyclingBuyBack`** (the RÉCUP scale) — the buy-back point: trash dropped/thrown/placed on
  the scale is bought for coins, paying **more for correctly sorted** material than street bins.
- **Shops** — `Shopkeeper` (idle + look-at-player IK, greets by shop name) sits with a
  `ShopCounter` (`IInteractable`, opens on `E`); `ShopUI` (UI Toolkit, `UI/Shop.uss`) shows the
  greeting, the player's coins, and one card per `ShopCatalog` offer with a Buy button. Buying
  spends coins and grants the item/boost. The RÉCUP window also lists trash buy-back prices.

---

## 11. Audio (`Audio`)

Fully event-driven and pooled:

- **`SoundManager`** — persistent; bootstraps from `Resources/UpcycleCitySoundLibrary.asset`
  before gameplay so every scene has pooled 2D/3D SFX, music crossfade and ambience.
- **`SoundLibrary`** / **`SoundCue`** (`SoundId`) — data-driven cues: clip variation (shuffle
  bag / no-repeat / sequential / random), pitch & volume randomisation, spam cooldowns,
  polyphony caps, bus routing.
- **`AudioVoicePool`** — zero-GC pooled voices with priority-based voice stealing and fades.
- **`GameAudioRouter`** — decouples gameplay from audio: it subscribes to gameplay events
  (footsteps, grab/throw, sorting, buy-back, shops, tools, bins, UI) and triggers cues, so no
  gameplay class references `AudioSource` directly. **`TrashImpactAudio`** adds material-matched
  physics-impact sounds to trash. `Editor/Audio/*` auto-enforce import settings and build the
  library idempotently.

---

## 12. Character creator (`Character`)

A UI-Toolkit screen (`CharacterCreator.unity`, `UI/CharacterCreator.uss`) for Body / Eyes /
Hair with a turntable preview. **`CharacterPartsLibrary`** holds every option (bodies per sex,
swappable face/hair/facial-hair meshes, colour palettes — all sharing one feet-on-floor, +Z
origin so parts stack). **`CharacterAppearance`** is a plain, JSON-friendly value type of
indices into that library. **`CharacterAvatar`** assembles the body/hair/face and recolours by
**material role** (`CharacterColorRoles`: `skin_`, `hair_`, `stubble_`, `iris_`, `top_` name
prefixes) using `MaterialPropertyBlock`s, so no per-character materials are created. The chosen
look themes the first-person arms and the shadow body in-game.

---

## 13. Editor tooling (`Scripts/Editor`) & art pipeline (`Tools/Blender`)

The `Editor` assembly is how scenes and content are **generated** rather than hand-placed:

- **Builders** (`*Builder.cs`, `*Setup.cs`) construct whole scenes and their assets —
  `CharacterCreatorBuilder`, `EcosystemSandboxBuilder`, `PlayerGymBuilder`,
  `PlayerCharacterSetup`, `ShopContentBuilder`, `ShoppingCartSpawner`, `SoundSystemSetup`,
  `ProjectLayersAndTagsSetup`. They are **idempotent and non-destructive**: they fill only empty
  fields and prompt before overwriting a scene, so Inspector tweaks survive a re-run. They also
  wire build-settings scene order (CharacterCreator first → gym).
- **Import rules & postprocessors** (`AudioImportRules`, `ModelImportRules`,
  `PollutionModelPostprocessor`) enforce correct import settings automatically.
- **Custom inspectors** (`EcosystemBalanceEditor`, `PlayerConfigEditor`, `SoundCueEditor`) add
  preset buttons and in-editor auditioning.

`Tools/Blender/*.py` are standalone Blender scripts that **procedurally generate and export the
art** — the Avenue Bourguiba map (`bourguiba_*.py`), characters, props and tools — into `Art/`.
They run in Blender, outside Unity. `Art/` otherwise contains third-party packs (KayKit,
Quaternius, a realistic car pack, rocks) — don't edit those sources.

---

## 14. Data-flow summary (one gameplay loop)

```
Citizen / bin / factory / decay  ──spawn──▶  TrashItem (IGrabbable, physics)
        (IPollutionSource)                        │  Spawned event
                                                  ▼
                                   CityEcosystemManager (4 Hz tick)
                                     bins trash → CityDistrict load
                                                  │
                     ┌────────────────────────────┼─────────────────────────────┐
                     ▼                             ▼                             ▼
            CityPollution/Satisfaction    district satisfaction →        player smog exposure
                     │                    LitterRateMultiplier →          → "SmogZone" speed
                     ▼                    (citizens litter less)             penalty (PlayerModifiers)
            PollutionController.SetPollution
                     │
                     ▼
        shader globals + sun/fog/ambient + smog volume + zones  ──▶  the city visibly clears

Player  ──ray──▶ PlayerInteractor ──IGrabbable──▶ PhysicsGrabController ──▶ carry/throw
                                                          │
                                    SortingCrate (purity) / RecyclingBuyBack (coins) ──▶ TrashItem.Dispose()
                                                          │  trash leaves the street
                                                          ▼
                                          district load drops → pollution falls → loop closes
```

Coins earned at crates/RÉCUP are spent in shops on tools and timed boosts (`PlayerBoosts`),
which feed back into the player through `PlayerModifiers` and the inventory — closing the
progression loop on top of the cleanup loop.
