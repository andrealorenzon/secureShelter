# Secure Shelter — Design Notes

The Secure Shelter mod adds a **painting portal**: a 1×1 "Storditi's hut" painting that a
player right-clicks to teleport into a private **pocket dimension** — a safe, static,
self-tending refuge — and a walk-through **stone arch** inside the pocket that sends them
back to exactly where they entered.

Built on the **Manifold** dimension library — see [manifold.md](manifold.md).

> **Modid / asset domain: `secureshelter`.** Everything (block/item codes, the Manifold
> dimension code, watched-attribute keys, the config file) lives under this domain. The mod
> was previously prototyped under the `portals` modid; a save created with that older build
> bakes `portals:` ids into its registry and can't be migrated across the rename — test on a
> **fresh world**.

---

## Behaviour

1. Craft or place the **Storditi's hut** painting (it orients to the wall face you place it on).
2. Right-click the painting → you arrive on the floor of the pocket, in front of the return arch.
3. The pocket is a sealed room holding a pre-built interior (the "bosco"). It's a safe zone:
   nothing can hurt you, nothing spawns, and you can't break or place blocks.
4. Walk through the **return arch** → you arrive back at the exact spot you entered from.

All paintings lead to the **same** pocket dimension (`secureshelter:<PocketDimensionCode>`,
`test10` by default). The return is per-player: the origin is stored on the player, not the
painting, so everyone returns to their own door.

---

## Components

| File | Role |
|---|---|
| `SecureShelterModSystem.cs` | The mod system. Registers the painting block class + the Manifold pocket dimension; holds the Manifold facade; owns entry/return (`EnterPocketViaPainting` / `EnterPocket` / `ReturnFromPocket`), arch-crossing detection, the static-dimension protections, relog/sleep restore, and interior/refill upkeep. |
| `BlockPaintingSecureShelter.cs` | The painting block (`secureshelter:painting-storditishut-<side>`, class `SecureShelter.painting`). Pure forwarder: right-click → `EnterPocketViaPainting`. |
| `PocketGeometry.cs` | Pure value object: computes the whole dimension-local geometry once at startup from the config **and the interior schematic's measured size**, so the shell always wraps the interior exactly (no magic numbers). |
| `PocketShellWorldgen.cs` | `IWorldgenStrategy` that builds the hollow mantle shell wrapped in an opaque dirt box. Interior left empty (stamped at runtime). |
| `SecureShelterConfig.cs` | User-editable settings (`ModConfig/SecureShelterConfig.json`); coordinates, block codes, banned/refillable/usable container prefixes. |
| `assets/secureshelter/blocktypes/painting.json` | The painting block definition (variants, shape, texture, creative tabs). |
| `assets/secureshelter/itemtypes/securesheltercomponents.json` | The **component bag** item (`secureshelter:securesheltercomponents`, "Secure Shelter Material Components") — a linensack-shaped material item. |
| `assets/secureshelter/recipes/grid/securesheltercomponents.json` | 3×3 grid recipe for the component bag. |
| `assets/secureshelter/schematics/shelter.json` | The interior "bosco" — a WorldEdit-style schematic stamped into the shell at runtime. |
| `assets/secureshelter/lang/en.json` | Block/item display names. |

---

## The pocket dimension

A single **persistent** Manifold dimension, registered in `RegisterPocketDimension`:

- Code `secureshelter:<PocketDimensionCode>` (`test10`), `SpawnBehavior.DimensionSpawn`,
  generation radius and relight height **derived from the footprint** so the whole cube
  generates and lights regardless of interior size.
- **Separate inventory** (`Hotbar | Backpack`): on entry the player's overworld held items
  and backpacks are snapshotted to player save and an empty pocket set loads, so they arrive
  carrying only worn armour/clothing; on exit everything is restored. Character (worn
  equipment) is deliberately shared.

### Geometry (`PocketGeometry.Build`)

Everything is sized to the interior schematic's measured `SizeX/SizeY/SizeZ`:

- **Inner shell** — a hollow box of unbreakable `game:mantle` ("bedrock"), footprint = the
  larger of the bosco size and `MinBoxSize` on each axis, with the bosco centred inside.
  Height = bosco height + `CeilingHeadroom`.
- **Outer wrapper** — an opaque `game:soil-medium-none` (dirt) box `WrapperThickness` blocks
  out on every face. Mantle absorbs no light, so without the dirt box light would leak
  straight through the shell; the wrapper seals it.
- The box's min corner sits at `(BoxOriginX, BoxOriginZ)` (kept positive so the −1 wrapper
  column doesn't fall off the map edge and get cut).
- **Spawn** is at the return arch's X, nudged `+4` in Z so the player doesn't arrive inside
  the doorway (which would instantly trigger a return).

### Worldgen (`PocketShellWorldgen`)

Per column in the footprint: lay the dirt wrapper on the six outer faces, and the mantle
shell on the six faces of the inner box. The interior is left empty — the bosco is stamped
at runtime.

### Interior ("bosco") — stamped at runtime

`GenerateColumn` stays cheap; the elaborate interior is a schematic placed after generation
(`EnsureBoscoPlaced`), once per dimension (guarded by the savegame flag
`secureshelter:boscoPlaced-<code>`), through a **relighting** bulk accessor. Because the
`schematics` asset category isn't indexed by the asset manager, the schematic is read
straight from the mod's source folder/zip (`ReadBoscoFromModFile`).

- **Banned blocks** (`BannedBlockPrefixes`, default `cabinet`) are stripped from the schematic
  on load — the vanilla cabinet's display slots NRE-crash the client renderer.
- **Arch marker**: if the schematic contains `ArchMarkerBlockCode` (default `creativeglow-79`),
  its position becomes the return arch centre and the marker is removed; otherwise the arch
  falls back to "near the far +Z wall" from config.

---

## Round trip

**Entry** (`EnterPocketViaPainting` → `EnterPocket`). The painting forwards the right-click to
the mod system, which records the player's current `x/y/z` and sets the persistent watched
attribute `secureshelter:inPocket = true`, then `TeleportPlayer(..., PocketDimCode, new
TransitionOptions())`. `DimensionSpawn` lands them on the fixed spawn. A repair pass then
places the bosco (first time), stamps the arch, discovers refillables, and relights.

**Return** (arch crossing → `ReturnFromPocket`). A per-tick listener (`OnServerTick`) samples
each player's movement *segment* (`ScanSegment`, step 0.25, mid-cell `+0.5`) so a fast walk
can't tunnel past the 1-block arch hole. Crossing the arch hole calls `OnArchCrossed`, which
debounces a single continuous pass (`ContinuousPassGapMs`) and calls `ReturnFromPocket`:
it clears the flag, transits to `manifold:overworld` (step 1, which also fixes the dimension
after a relog), then explicitly `TeleportToDouble`s the player one block onto the recorded
origin (step 2 — explicit because `OverridePosition` is ignored when the transit is a no-op).

`/back` and `/pocket` share the same code paths and the same `secureshelter:inPocket` flag.

---

## Static-dimension protections (this pocket only)

Every check keys off `PocketDimId`, so other dimensions are unaffected:

- **No break/place** (`OnCanPlaceOrBreak`) — except bowls (ground-storage edits) may be moved.
- **Containers locked** (`OnCanUseBlock`) — only a whitelist (`UsableContainerPrefixes`:
  barrels, jugs/jars, vessels, crocks, firepit cooking pots, shelves, cheese/pie, chests,
  ground storage…) is usable, so nothing can be stolen.
- **No natural spawns** (`OnTrySpawnEntity`); **no item drops** (`OnDropSweep`).
- **Invulnerability** — players (`HookInvulnerability`) and any non-player creature
  (`HookEntityInvulnerability`) take zero damage while inside.
- **Always dry** (`OnPocketGetClimate`) — the engine's rain-exposure check is dimension-blind
  and would snuff every lit torch/firepit (and play phantom rain), so the pocket's rainfall is
  forced to 0 whenever its climate is queried.
- **Refillables** (`RefillableBlockPrefixes`) — barrels/firepits/food/etc. are snapshotted
  full/lit just after the bosco is placed and topped back up on a timer, so the refuge tends
  itself.

---

## Relog & sleep

A login (and a sleep time-skip) is **not** a transit, so Manifold won't load the pocket's
chunks on its own and the player would spawn into void. Both are handled off the persistent
`secureshelter:inPocket` flag (not the engine dimension, which can be unreliable here):

- **Relog** (`OnPlayerNowPlaying`) — after a short delay: `EnsurePocketChunksLoaded` (**load
  only, never create** — the columns are saved; a blank create would wipe the interior and
  kill block light), transit back onto the spawn, then `SchedulePocketRepair`.
- **Sleep/wake** (`OnServerTick` mount tracking → `RestoreAfterWake`) — waking can eject the
  player from the dimension with no login event; on waking while still flagged in-pocket, the
  same restore runs, preferring the exact bed they lay on.

`EnsurePocketChunksLoaded` covers the dirt-wrapped footprint with floor-division so the −1
wrapper column lands in chunk −1, not 0. `ResendPocketChunks` force-resends the footprint to
players inside, because the bulk accessor's own sync is unreliable in this dimension.

---

## Lighting (important)

Manifold relights generated columns at generation time up to `RelightHeight`. The geometry
keeps `FloorY` low and sets `RelightHeight` to clear the whole sealed box, and the bosco/arch
are placed through **relighting** bulk accessors, so the interior is lit. Two subtleties:

- **Block-entity light sources need a post-placement relight.** The bulk-accessor relight runs
  on `Commit()`, which happens *before* `PlaceEntitiesAndBlockEntities`. Block-type light
  (torches, lanterns — `LightHsv` on the block) is included; light that comes from a block
  *entity* (e.g. oil lamps) is not, because the BE doesn't exist yet at Commit. So those
  sources arrive dark on first entry and only a relog "fixed" it (the reload recomputed light
  with the BEs present). The fix: `RelightPocket()` calls `WorldManager.FullRelight` over the
  whole box **after** the BEs are placed — wired into `EnsureBoscoPlaced` and repeated in the
  `SchedulePocketRepair` passes (so a BE that needs a tick to register still gets caught).
- **Geometry/relight-height changes only affect freshly generated chunks** — Manifold caches
  columns and never regenerates, and there's no purge/reset in 0.4.x. To force a regen: new
  world, or bump `PocketDimensionCode` (a new dimension code = a fresh dimension).

---

## Known limitations / TODO

- **Geometry / relight changes need a regen** (bump `PocketDimensionCode` or use a new world).
- **Modid rename is not save-compatible** — old `portals:` saves keep orphaned ids; use a
  fresh world.
- **Arch model** is a plain 3×2 stone frame with a lit torch on top; tune visually.
