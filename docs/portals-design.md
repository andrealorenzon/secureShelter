# Portals — Design Notes

The Portals mod adds a **Pocket Door**: an openable door that teleports a player who
walks through it (while open) into a shared **pocket dimension**, and a matching door
inside the pocket that sends each player back to the exact door they came from.

Built on the **Manifold** dimension library — see [manifold.md](manifold.md).

---

## Behaviour

1. Place a Pocket Door (it orients to face you).
2. Right-click to open it (right-click again to close).
3. Walk through the open doorway → you arrive on a platform in the pocket dimension.
4. The pocket has one return door. Open it and walk through → you arrive back at the
   exact door you originally used, standing just in front of it.

All Pocket Doors lead to the **same** pocket dimension (`portals:pocket`). The single
return door sends each player to *their own* origin door, because the origin is stored
per-player, not on the door.

> Variants: scoped to a **single** door/dimension for v1. The variant→dimension mapping
> (one pocket per door type) is a later extension; the code paths already key off the
> dimension code so adding variants mostly means more `Define(...)` calls + asset variants.

---

## Components

| File | Role |
|---|---|
| `PortalsModSystem.cs` | Registers the block class + `portals:pocket` dimension; holds the Manifold facade; owns the crossing→transit logic (`OnDoorCrossed` / `EnterPocket` / `ReturnFromPocket`) and the per-player cooldown. |
| `BlockPocketDoor.cs` | The door block. Orientation on placement, open/close toggle, state-dependent collision, and `OnEntityInside` walk-through detection. |
| `PocketPlatformWorldgen.cs` | `IWorldgenStrategy` that builds the floating slab and places the return door. |
| `assets/portals/blocktypes/pocketdoor.json` | Block definition: variants, shapes per variant, textures, drops. |
| `assets/portals/shapes/block/pocketdoor*.json` | Closed panel + swung-open panel shapes. |

---

## How the round-trip works

**Detection.** A server tick listener (`PortalsModSystem.OnServerTick`, every ~20 ms)
samples each player's movement segment since the previous tick and looks for an open
Pocket Door anywhere along that path (`ScanSegment`). Sampling the *path* — not just the
tick endpoints — and sampling at mid-cell height (`+0.5`) makes it immune to the two
failure modes of the earlier `Block.OnEntityInside` approach: fast movement tunnelling
past a 1-block doorway, and the feet position straddling the floor/door Y-boundary.
Only open doors count (closed doors block movement anyway). Scans reset on a dimension
change so a teleport can't be misread as a crossing.

**Entry vs return — tracked by us, not the engine.** The engine does **not** reliably
restore a player's dimension on relog (both `Pos.Dimension` and `Pos.InternalY` come back
as if they were in the overworld), so any engine-side "which dimension am I in" check is
unusable across a relog. Instead we keep our own persistent watched attribute
`portals:inPocket` (a bool stored on the player entity, which survives save/relog). It is
set true in `EnterPocket` (recording the origin) and false in `ReturnFromPocket`. A door
crossing checks that flag: in the pocket → `ReturnFromPocket`; otherwise → `EnterPocket`.
`/back`, `/pocket`, and the relog handler all key off the same flag.

**Entry** (`EnterPocket`): stores the origin door's `x/y/z` + facing index +
`hasReturn=true` in the player's `WatchedAttributes` (which persist across save/relog),
then `TeleportPlayer(player, portals:pocket, new TransitionOptions())`. With the
dimension's `SpawnBehavior.DimensionSpawn`, the player lands on the platform spawn.

**Return** (`ReturnFromPocket`): reads the stored origin, computes a landing **one block
in front of** the origin door (along its facing) so the player doesn't rematerialize
inside the doorway, then `TeleportPlayer(player, manifold:overworld,
new TransitionOptions { OverridePosition = landing })`.

**Anti-loop / debounce.** A single walk-through can be detected on several consecutive
ticks, so `OnDoorCrossed` collapses one continuous pass into one transit: detections less
than `ContinuousPassGapMs` (300 ms) apart are the same pass and fire once; a longer gap
means the player left the doorway and re-arms the next crossing. This replaced a 2 s time
cooldown that wrongly blocked legitimate quick re-entries. Players also land clear of
doors (platform spawn is several blocks from the return door; return landing is one block
in front of the origin door), so arrival never sits inside a doorway.

---

## Pocket geometry (constants in `PortalsModSystem`)

- `portals:pocket`, Persistent, `SpawnBehavior.DimensionSpawn`, generation radius 1,
  `WithRelightHeight(FloorY + 16)` so the platform sits under the relit band (see below).
- Floor slab: 15×15 (`PlatformHalf = 7`) of `game:rock-granite` at `FloorY = 64`, centered
  on `(8, 8)` → spans x/z `1..15`.
- Fixed spawn: `(8, 65, 8)` — standing on the slab.
- Return door: `portals:pocketdoor-south-closed` at `(8, 65, 1)` (north edge, facing the
  spawn).

---

## Relog inside the pocket

Manifold only loads and force-sends a dimension's chunks during a **transit**, and a login
is not a transit. So a player who logs out in the pocket would otherwise spawn into empty
void; and an early attempt to just re-transit them into the pocket at login raced Manifold's
chunk send and left the return door client-desynced and *inert* (no sound, no toggle, no
walk-through), stranding them.

Decision: **you stay in the pocket across a relog**, restored with the three-step recipe the
shipped *Personal Pocket Dimension* mod uses (see [manifold.md §11](manifold.md)).
`OnPlayerNowPlaying` checks our persistent `portals:inPocket` flag (not the engine dimension,
which is unreliable on relog) and, if set, after a short delay:

1. **`EnsurePocketChunksLoaded`** — brings the platform's 3×3 chunk footprint into memory with
   `sapi.WorldManager.LoadChunkColumnForDimension`. **Load only, never Create.** The columns were
   generated by Manifold on first entry and are saved (the dimension is persistent), so a load
   restores the slab intact. `CreateChunkColumnForDimension` makes a fresh *blank, unlit* column
   (Manifold's worldgen runs only during a transit, not for manual creates) and overwrites the
   saved platform — that earlier mistake wiped the slab and killed block-light, leaving only the
   re-stamped door. Hence load-only.
2. **Transit onto the spawn** with an explicit `OverridePosition`. With the chunks already loaded
   this is reliable; if the engine actually kept the player in the pocket the transit is a
   harmless no-op (they were already standing on the now-loaded platform).
3. **`SchedulePocketRepair`** at +500 ms and +2000 ms → `RepairPocketFixtures`, which:
   - calls **`StampPlatform`** — re-lays the whole slab + frame + door via a bulk accessor with
     **`relight: true`**. This is idempotent on a healthy platform, *self-heals* any column a
     prior blank-create corrupted, and recomputes block light so placed light sources work again.
     The door is only re-placed when actually missing, preserving a player's open/closed state.
   - then `MarkBlockModified` + `MarkBlockDirty` on the door, forcing a client resend that cures
     the inert-door desync even when the block is otherwise unchanged.

`EnterPocket` schedules the same repair pass after a normal walk-through entry, so a first-entry
door can't arrive desynced and the surface gets a relight pass either way. Leaving still works via
the return door or `/back` (both keyed off the flag).

> **Lighting note:** the pocket has no skylight (Manifold dimensions only relight at generation
> time, and there is no sky), so the slab would be pitch dark on its own. `StampPlatform` therefore
> stands a lit torch a couple of blocks in from each corner, committed through the **relighting**
> bulk accessor so block light propagates across the whole slab. The torches are only placed where
> the spot is empty, so a player can remove or replace them, and they reappear after a relog repair.
>
> **Engine dimension is reliable on relog (corrected):** earlier we believed `Pos.Dimension` /
> `Pos.InternalY` came back as overworld on relog and built the `portals:inPocket` flag around that.
> Diagnostics later showed the engine **does** restore the player into the pocket (e.g. `engineDim=10`,
> `internalY = 65 + 10·32768 = 327745`). The flag is still used (it's harmless and keys `/back`),
> but the relog handler no longer depends on the engine being wrong — it just re-sends the chunk and
> repairs the door.

`/pocket` also goes through `EnterPocket` (recording the player's current spot as origin
and setting the flag), so commands and doors share one code path and one source of truth.

`ReturnFromPocket` is two-step: a transit to the overworld (to fix the dimension + load
chunks) followed by an explicit `TeleportToDouble` to one block in front of the origin.
The explicit reposition is required because `OverridePosition` is ignored when the transit
is a no-op (e.g. the engine already believes the player is in the overworld after a relog),
which previously dumped players off-map.

## Lighting (important)

Manifold only relights generated columns up to `RelightHeight` (default **20**). The
platform is at y=64, so without `WithRelightHeight(80)` the surface stays dark while
uninitialised light values read as a deceptive "lit" underside. Two gotchas:
- The setting only affects **freshly generated** chunks — Manifold caches columns and
  never regenerates, and there is **no purge/reset command in 0.4.0**. To force a regen:
  use a new world, or temporarily bump the dimension code / set `.Ephemeral()`.
- An in-game/engine relight will **not** fix old dark chunks: the dimension's skylight is
  only ever established by Manifold's generation-time relight, so the column must be
  regenerated.

## Known limitations / TODO

- **Platform size / lighting changes need a regen** (see above) — they only appear in
  freshly generated pocket chunks.
- **Model/texture**: the leaf is a full-width door-leaf shape textured with the vanilla
  `game:block/wood/door/sleek/oak` door texture (16×32), mapped onto the 1-tall leaf —
  reads as a (slightly short) wooden door. A true 2-tall door would need a multiblock.
- **Shape rotations** (`rotateY` per orientation) and the swung-open panel are
  approximate; tune visually in-game.
- **1-block-tall door** (not the 2-tall vanilla door). Fine functionally; revisit for looks.
- **Commands** (privilege `chat`): `/pocket` jumps into the pocket; `/back` returns to
  the world (origin door if known, else overworld spawn). Both are intentional.
- **No walls/ceiling** on the platform (open void by design).
