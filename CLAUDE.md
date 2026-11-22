# CLAUDE.md

Guidance for Claude Code (and other AI agents) working in this repository.

## What this project is

**Upcycle City** — a first-person, physics-driven "clean the city" game built in
**Unity 6 (6000.6.6f1)** with the **Universal Render Pipeline (URP)** and the new
**Input System**. The player walks a polluted city (Avenue Bourguiba, Tunis), grabs
litter one piece at a time with a physics grabber / litter-picker, sorts it into
colour-coded streams, sells it back for coins, and watches a living cause-and-effect
**pollution ecosystem** respond: districts get cleaner, the sky clears, residents grow
satisfied, and citizens litter less.

The deep design/system walkthrough lives in [ARCHITECTURE.md](ARCHITECTURE.md). Read it
before changing any gameplay system.

## Environment / how to run

The Unity Editor **6000.6.6f1** is installed (Android + Web + OpenJDK modules) with a
Unity Personal license, and the `unity` CLI (the Unity agent tooling) is on PATH. The
project depends on `com.unity.pipeline`, so once an Editor is open you can drive it from
the shell.

```bash
# Open the project (first import is slow — give it a long timeout)
unity open --until-ready --timeout 900

# Confirm an Editor is connected and ready
unity status

# List the commands the live Editor exposes
unity command

# Check the console for errors (save the cursor BEFORE your first edit)
unity command console status
unity command console get -- --level error

# Enter Play mode on the open scene
unity command editor.playmode start --caller plugin --skill unity-cli

# Run arbitrary C# against the running Editor
unity command script eval --caller plugin --skill unity-cli 'Debug.Log("hi");'
```

Always pass `--caller plugin --skill <name>` on `unity command` calls. Prefer the
`unity` CLI over the Unity MCP server when you have a shell. When in doubt about a
command, search with `unity commands --grep <term>` — do **not** grep `--help`.

There is **no test suite and no CI** in this repo. "Verifying" a change means: zero
console errors after import, then entering Play mode in the relevant scene and
inspecting runtime state with `script eval` (not by adding Debug.Log everywhere).

## Scenes (there is no single "main" scene)

Build order is wired by Editor builders (`MainMenuBuilder.ApplyBuildSceneOrder`):
**Intro → MainMenu → CharacterCreator → EcosystemSandbox**, then PlayerGym / CraftingWorkshop (dev scenes).

- `Assets/UpcycleCity/Scenes/MainMenu.unity` — title screen (Play Solo / Host Room / Join Room / Quit)
  and the co-op Room; owns the persistent `CoopSessionManager`.
- `Assets/UpcycleCity/Scenes/CharacterCreator.unity` — UI Toolkit character creator
  (body / eyes / hair); Start loads the Avenue (solo) or becomes READY (co-op).
- `Assets/UpcycleCity/Scenes/PlayerGym.unity` — Phase-1 test gym: movement, physics
  grabbing/throwing, 4-stream sorting, HUD. Best scene for testing player mechanics.
- `Assets/UpcycleCity/Scenes/EcosystemSandbox.unity` — the big Phase-2 scene (the
  Avenue Bourguiba map): full living pollution ecosystem, districts, crowds, shops.
  Largest/heaviest scene; use it to test the ecosystem.

## Layout

```
Assets/UpcycleCity/
  Scripts/Runtime/   # all gameplay code (asmdef: UpcycleCity.Runtime, ns UpcycleCity.*)
  Scripts/Editor/    # scene/content builders + import rules (asmdef: UpcycleCity.Editor)
  Scenes/            # the three scenes above
  Shaders/           # URP pollution shaders (PollutionLit, PollutionSky, SmogLayer) + HLSL
  Settings/          # ScriptableObject tuning assets (EcosystemBalance, PollutionProfile, configs)
  UI/                # UI Toolkit USS/TSS + PanelSettings (character creator, shop)
  Art/               # imported model packs (KayKit, Quaternius, etc.) — third-party assets
  Audio/, Materials/, Prefabs/, Resources/, Animation/
Assets/Settings/     # URP pipeline assets (PC + Mobile renderers/RP assets, volume profiles)
Tools/Blender/       # Python scripts that generate/export the Blender art (map, props, chars)
```

Two assembly definitions: **`UpcycleCity.Runtime`** (references URP, Input System, Burst)
and **`UpcycleCity.Editor`** (references Runtime, Input System, UI). Keep runtime code free
of `UnityEditor` usings except behind `#if UNITY_EDITOR`.

## Conventions and patterns to respect

- **Namespaces mirror folders**: `UpcycleCity.Player`, `.Ecosystem`, `.Pollution`,
  `.Waste`, `.Inventory`, `.Interaction`, `.Crowd`, `.Shops`, `.Audio`, `.Character`,
  `.Core`, `.Props`.
- **No `FindObjectsByType` in hot paths.** Systems use static registries + `PlayerContext`
  (a lightweight service locator) and C# events (`TrashItem.Spawned/Removed`, etc.). Follow
  this when adding systems — register in `OnEnable`, unregister in `OnDisable`.
- **Reset statics** with `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` so domain
  reload / "enter play mode without reload" stays correct (see `CityEcosystemManager`).
- **Interface-driven interaction.** `PlayerInteractor` routes a single center-screen ray to
  `IGrabbable` / `IInteractable` / `IPickupItem` / `IToolTarget`. Add new interactable things
  by implementing these, not by special-casing the interactor.
- **Trash is never inventory.** `TrashItem : IGrabbable` is grabbed one-at-a-time by physics;
  only tools/usables/coins (`IInventoryItem`) go in `PlayerInventory`. Don't blur this line.
- **Player stats go through `PlayerModifiers`** — a keyed additive/multiplier/flag stack
  (sources like `"EquippedTool"`, `"SmogZone"`, shop boosts). Never mutate speed/reach
  directly; set a keyed modifier and remove it by source.
- **Designer tuning lives in ScriptableObjects** (`PlayerConfig`, `EcosystemBalance`,
  `PollutionProfile`, `ShopCatalog`, `WasteCatalog`, configs) and is read live each frame so
  Inspector edits apply in Play mode. Prefer adding a tunable field over a hard-coded constant.
- **Editor builders are idempotent & non-destructive** (`*Builder.cs`, `*Setup.cs`): they fill
  only empty fields and ask before replacing scenes. Scenes and tag/layer setup are generated
  by these — keep them re-runnable. `GameLayers`/`GameTags` are kept in sync with
  `ProjectSettings/TagManager.asset` by `ProjectLayersAndTagsSetup`.
- **Mobile-safe by design**: global trash cap, 4 Hz ecosystem tick, Burst crowd job,
  2-tier citizen LOD, pooled audio voices, PC vs Mobile URP renderers. Don't regress these.

## Guardrails

- This is **not a git repo with a remote you should push to** without being asked. Make a
  restore point (commit) before large changes and tell the user how to revert.
- Don't change unrelated assets. `.meta` files matter — never delete or rename an asset
  without its `.meta`, and let Unity generate GUIDs.
- Don't run `console clear` (it deletes the user's own entries).
- `Art/` is third-party asset packs; don't edit or redistribute those sources.
- `docs/game_concept.md` is referenced in code comments but is **not present** in the repo.

## Companion docs

- [AGENT.md](AGENT.md) — concise, task-oriented quickstart for coding agents.
- [ARCHITECTURE.md](ARCHITECTURE.md) — full system-by-system architecture.
