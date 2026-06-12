# Manifold — Reference Notes

Working notes on the **Manifold** library mod, compiled for building the Portals mod on top of it.
Source: <https://leonfvt.fr/Manifold/> (articles + DocFX API reference).

---

## 1. What it is

A **library mod for Vintage Story 1.21+** that lets other mods declare and manage **custom dimensions** — isolated worlds, each with its own terrain, player positions, and travel policy.

- It is a **dependency**, not a standalone mod. Consumer mods integrate against its public API.
- License: **MIT**. Targets **.NET 10**. NuGet package: **`Pixnop.Manifold`**.
- **No Harmony patches** (per the Architecture page) — built on public `VintagestoryAPI`. The only non-public access is a reflection read of chunk dimension IDs during generation. ⚠️ See [Caveats](#9-caveats--open-questions) — the docs are internally inconsistent on this.

---

## 2. Setup checklist (consumer mod)

1. **modinfo.json** — add the dependency:
   ```json
   {
     "modid": "mymod",
     "name": "My Mod",
     "version": "1.0.0",
     "dependencies": {
       "game": "1.21.0",
       "manifold": ""
     }
   }
   ```
2. **NuGet:** `dotnet add package Pixnop.Manifold`
3. **Target framework:** `<TargetFramework>net10.0</TargetFramework>`
4. **Load order:** override `ExecuteOrder()` to return **> 0.05** (Manifold's own order):
   ```csharp
   public override double ExecuteOrder() => 0.5;
   ```
5. **Get the facade** in `StartServerSide`, passing `this` so your mod is recorded as the dimension owner:
   ```csharp
   var manifold = sapi.GetManifoldServer(this);
   if (!manifold.IsHealthy)
   {
       Mod.Logger.Warning("[MyMod] Manifold unhealthy; dimension features disabled.");
       return;
   }
   ```

Isolate server logic in `StartServerSide` and client logic in `StartClientSide`.

---

## 3. Architecture

`ManifoldModSystem` loads at order **0.05** (before consumers) and wires up internal services (namespace `Manifold.Internal`), exposed only via public interfaces.

| Service | Responsibility |
|---|---|
| `DimensionAllocator`   | Hands out VS engine dimension IDs (usable range **10–1023**) |
| `DimensionRegistry`    | Authoritative dimension state + event dispatch |
| `DimensionPersistence` | Savegame serialization for dimensions and per-player positions |
| `WorldgenDispatcher`   | Bounded-region generation during transit |
| `TransitService`       | Player teleport, events, game-mode enforcement |
| `NetworkingService`    | Replicates dimension list to clients |

**Facades:**

- **Server:** `IManifoldServer` → `Registry`, `Transitions`, `IsHealthy`.
  `sapi.GetManifoldServer(this)` returns an **owner-scoped** facade (`OwnerScopedManifoldServer`) that tags registered dimensions with the caller's mod ID.
- **Client:** `IManifoldClient` → read-only replicated snapshot of available dimensions.

**Server vs client split:**

| Concern | Server | Client |
|---|---|---|
| Registry mutations | `Define`, `Create`, `TryRemove` | read-only mirror |
| Transit | `ITransitionService.TeleportPlayer` | — |
| Worldgen | `IWorldgenStrategy` execution | — |
| Dimension list | authoritative | replicated via networking |

---

## 4. Dimensions

A dimension is a named, isolated world region identified by an `AssetLocation` code (`domain:path`, e.g. `mymod:nether`) mapped to a VS engine dimension ID (0–1023). Use your mod ID as the domain to avoid collisions. The built-in overworld is **`manifold:overworld`** (ID 0, immutable).

### Registration

**Boot-time (static):** for dimensions that always exist while your mod is installed. Idempotent — reuses the same internal ID on subsequent starts.
```csharp
IDimension dim = manifold.Registry
    .Define(new AssetLocation("mymod", "nether"))
    .Persistent()
    .WithWorldgen(new MyNetherWorldgenStrategy())
    .RegisterStatic();
```

**Runtime (dynamic):** for player-created / event-driven dimensions. Must specify a lifetime.
```csharp
manifold.Registry.Create(/* ... */);   // Ephemeral or Persistent
```

### Lifetime

| Lifetime       | Chunks persisted | Survives shutdown | Removable at runtime |
|----------------|:---:|:---:|---|
| `Persistent()` | yes | yes | No (admin `/manifold purge <code>`) |
| `Ephemeral()`  | no  | no (removed on shutdown) | `Registry.TryRemove(code)` |

### States

| State | Meaning |
|---|---|
| **Active**      | Normal operation; transit + worldgen permitted |
| **Pending**     | Persisted in a prior savegame, owning mod not yet re-registered this session |
| **Quarantined** | Owning mod no longer installed; chunks preserved on disk, transit refused |

Reinstalling a missing mod auto-transitions its dimensions **Pending → Active** on next start.

### Reading & events

```csharp
IDimension? dim = manifold.Registry.Get(new AssetLocation("mymod", "nether"));

foreach (IDimension d in manifold.Registry.All)   // Active, Pending, Quarantined
    Console.WriteLine($"{d.Code} [{d.State}] owner={d.OwnerModId}");

manifold.Registry.Created   += (_, e) => Mod.Logger.Notification($"created: {e.Dimension.Code}");
manifold.Registry.Destroyed += (_, e) => Mod.Logger.Notification($"removed: {e.DimensionCode}");
```

---

## 5. Worldgen

Every dimension needs exactly one strategy via `IDimensionBuilder.WithWorldgen`.

```csharp
public interface IWorldgenStrategy
{
    void OnInitialize(IWorldgenInitContext ctx);    // called ONCE on first transit (lazy)
    void GenerateColumn(IWorldgenChunkContext ctx); // once per 32×32 chunk column in radius
}
```

**`IWorldgenInitContext`** (verified against Manifold.dll 0.4.0):

| Member | Purpose |
|---|---|
| `Api`         | `ICoreServerAPI` — resolve blocks via `ctx.Api.World.GetBlock(loc)` |
| `Seed`        | `int` world seed |
| `DimensionId` | engine dimension id |

> ⚠️ **Doc error:** the website's getting-started example calls `ctx.BlockAccessor.GetBlock(...)` inside `OnInitialize`. The real `IWorldgenInitContext` has **no `BlockAccessor`** — use `ctx.Api.World.GetBlock(...)` instead. (A `BlockAccessor` only exists on `IWorldgenChunkContext`.)

**`IWorldgenChunkContext`** (verified):

| Member | Purpose |
|---|---|
| `DimensionId`       | Engine dimension ID — **required** for BlockPos construction |
| `ChunkX` / `ChunkZ` | Chunk-grid coords (× 32 → world-space origin) |
| `BlockAccessor`     | `IBlockAccessor` — `SetBlock(blockId, pos)` |
| `Rng`               | `LCGRandom`, seeded deterministically per column |

**Two rules the docs stress:**
1. Resolve block IDs in `OnInitialize` and cache as fields — **never** inside `GenerateColumn` (perf).
2. **Always dimension-encode positions.** A `BlockPos` without `DimensionId` defaults to dimension 0 (overworld) and silently writes to the wrong world.

```csharp
public sealed class MyFloorStrategy : IWorldgenStrategy
{
    private int _stoneBlockId;

    public void OnInitialize(IWorldgenInitContext ctx)
        => _stoneBlockId = ctx.BlockAccessor.GetBlock(new AssetLocation("game", "rock-granite")).Id;

    public void GenerateColumn(IWorldgenChunkContext ctx)
    {
        for (int x = 0; x < 32; x++)
        for (int z = 0; z < 32; z++)
        for (int y = 0; y < 64; y++)
        {
            var pos = new BlockPos(ctx.ChunkX * 32 + x, y, ctx.ChunkZ * 32 + z, ctx.DimensionId);
            ctx.BlockAccessor.SetBlock(_stoneBlockId, pos);
        }
    }
}
```

### Active bounded-region generation

On transit into a dimension, Manifold **synchronously** (main thread) generates a square region around the landing column **before** the player arrives, so they never see void:
1. Compute target chunk column from destination.
2. Iterate all columns within `generationRadius` (X and Z).
3. For each: create chunk column → `GenerateColumn` → relight → send to client.
4. Teleport completes only after generation finishes.

### Radius & streaming

```csharp
.WithGenerationRadius(3)   // (2·r+1)² = 7×7 columns. Default 2 (5×5). Range 0–16.
.Streaming(8)              // optional: async streaming window (1–32) around moving players
.WithRelightHeight(20)     // default 20; content above stays under-lit until engine relight pass
```
- Already-generated columns are tracked in the savegame and not regenerated on restart.
- `Streaming`: generates the bounded region synchronously first, then streams surrounding chunks async over ticks; out-of-window chunks unload but placed blocks persist.

### Built-in strategy

```csharp
.WithWorldgen(new BasicVoidWorldgenStrategy())   // no-op; chunks default to air
```

---

## 6. Transit & travel policy

### Core method

```csharp
// Verified signature (Manifold.dll 0.4.0): options is REQUIRED, not optional.
void TeleportPlayer(IServerPlayer player, AssetLocation targetDim, TransitionOptions options);
// also: TeleportEntity(Entity, AssetLocation, TransitionOptions)
//       bool TeleportBlock(BlockPos source, AssetLocation targetDim, BlockPos targetLocal)
```

- **Always pass options.** Use `new TransitionOptions()` (its parameterless ctor sets `PreserveInventory = true`); `default(TransitionOptions)` would leave it `false`. Leaving `SpawnBehavior` null makes the dimension's configured behavior apply.
- **Main thread only.**
- Sequence: cancellable **`PlayerEntering`** → resolve landing position → pre-generate terrain if needed → teleport → force game mode (if set) → **`PlayerLeft`** + **`PlayerEntered`**.
- Throws `DimensionNotFoundException`, `DimensionStateException`, or `ManifoldUnhealthyException`.

### Spawn behavior

| `SpawnBehavior` | Effect |
|---|---|
| `SameCoordinates` | keep current X/Z, land on surface at that column (**default**) |
| `DimensionSpawn`  | always land at the dimension's fixed spawn point |
| `LastVisited`     | return to previous location in that dimension; falls back to `SameCoordinates` on first entry (persists across restarts) |

### Builder policy knobs

```csharp
manifold.Registry
    .Define(new AssetLocation("mymod", "survival"))
    .Persistent()
    .WithFixedSpawn(new BlockPos(1024, 64, 1024, 0))
    .WithSpawnBehavior(SpawnBehavior.LastVisited)
    .WithForcedGameMode(EnumGameMode.Creative)
    .WithSeparateInventory(ManifoldInventory.Hotbar | ManifoldInventory.Backpack)
    .WithGenerationRadius(5)
    .RegisterStatic();
```
Separate inventories store items per owner key (the dimension code or `"shared"`); profiles save alongside physical inventory.

### TransitionOptions (immutable record struct, per-transit overrides)

| Field | Meaning |
|---|---|
| `OverridePosition`  | hard-coded landing `BlockPos` (skips resolver) |
| `Resolver`          | custom `ITargetPositionResolver` |
| `SpawnBehavior`     | per-transit spawn override |
| `PreserveInventory` | keep inventory across transit (default `true`) |

`TargetPositionResolvers` provides built-in `ITargetPositionResolver` implementations.

### Triggers

**Portal block** (the natural fit for the Portals mod) — subclass `PortalBlockBase`, triggers transit on collision:
```csharp
// override TargetDimensionCode (and optionally Options)
```

**Chat command:**
```csharp
new DimensionCommandBuilder()
    .Command("nether")
    .TargetDimension(new AssetLocation("mymod", "nether"))
    .RequiresPrivilege("chat")
    .WithSpawnBehavior(SpawnBehavior.LastVisited)
    .DescribedAs("Teleport to the nether dimension.")
    .Register(sapi);
```

---

## 7. Persistence & replication

- `DimensionPersistence` reconstructs dimensions from savegame manifest data on load.
- Unregistered-but-persisted dims → **Pending**; if owner mod absent after all mods load → **Quarantined** (engine slot + chunks preserved).
- Generated chunks cached to skip regeneration.
- Per-player positions indexed by `(playerUid, dimensionCode)`.
- `NetworkingService` continuously replicates dimension state to clients over standard VS network channels.

---

## 8. API surface (namespaces & types)

**`Manifold`**
- `ManifoldModSystem` — entry point / orchestration.

**`Manifold.Api`** (verified against 0.4.0)
- `IDimension` — `AssetLocation Code`, **`int InternalId`** (engine dim id), `bool IsBuiltIn`, `DimensionLifetime Lifetime`, `string OwnerModId`, `DimensionState State`, `IReadOnlyDictionary<string,object> Metadata`
- Enums: `DimensionLifetime`, `DimensionState`, `ManifoldInventory` (`None`/`Hotbar`/`Backpack`/`Character`/`All`)
- `Manifold.Api.Worldgen`: `IWorldgenStrategy`, `IWorldgenInitContext` (`Api`/`Seed`/`DimensionId`), `IWorldgenChunkContext` (`DimensionId`/`ChunkX`/`ChunkZ`/`BlockAccessor`/`Rng`)
- `Manifold.Api.Events`: `PlayerEnteringDimensionEventArgs` (has `Cancel`), `PlayerArriving/Entered/Left`, `EntityChangedDimensionEventArgs`
- `Manifold.Api.Helpers`: `BasicVoidWorldgenStrategy`, `DimensionCommandBuilder`, `PortalBlockBase` (overrides **`OnEntityCollide`** — collision-based, so it only fires on blocks that have a collision box), `ManifoldAccess.GetServer/GetClient`
- `IWorldgenStrategy` (+ worldgen contexts `IWorldgenInitContext`, `IWorldgenChunkContext`)
- `DimensionMetadataExtensions` — typed accessors over dimension metadata
- Exceptions (root `ManifoldException`): `DimensionAlreadyRegisteredException`, `DimensionBuiltInImmutableException`, `DimensionCapacityExceededException` (IDs 10–1023 exhausted), `DimensionLifetimeUnspecifiedException`, `DimensionNotFoundException`, `DimensionOwnerRequiredException`, `DimensionStateException`, `ManifoldNotInitializedException`, `ManifoldUnhealthyException`, `WorldgenStrategyContractException`

**`Manifold.Api.Server`**
- `IManifoldServer`, `IDimensionRegistry`, `IDimensionBuilder`, `ITransitionService`
- `CoreServerApiExtensions` — provides `GetManifoldServer(this)`

**`Manifold.Api.Transitions`**
- `ITransitionService`, `TransitionOptions` (struct), `SpawnBehavior` (enum)
- `ITargetPositionResolver`, `TargetPositionResolvers`

**`Manifold.Api.Helpers`**
- builder helpers: `DimensionCommandBuilder`, etc.

---

## 9. Caveats & open questions

- **Harmony inconsistency:** the Architecture page states Manifold uses **no Harmony patches**, yet `ManifoldUnhealthyException` is documented as "raised during mutations when Harmony patches failed" and the `IsHealthy` flag exists. Likely stale wording from an earlier design — treat `IsHealthy` as a generic "did Manifold initialize correctly" guard and check it before using the facade.
- **Version target:** Manifold requires game **1.21+**. The Portals scaffold's `modinfo.json` currently pins `game: 1.22.0` (inherited from Palantir). 1.22 satisfies "1.21+", but lower it if 1.21 support is wanted. Still need to add `"manifold": ""` to dependencies + `dotnet add package Pixnop.Manifold`.
- DocFX per-type pages were not fully scraped — exact member signatures for some interfaces (`IDimensionRegistry`, `IDimensionBuilder`, etc.) come from the article examples, not the API reference. Confirm signatures against the package/IntelliSense when coding.

---

## 10. Minimal end-to-end example

```csharp
using Manifold.Api.Helpers;
using Manifold.Api.Server;
using Manifold.Api.Transitions;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace MyMod;

public sealed class MyModSystem : ModSystem
{
    public override double ExecuteOrder() => 0.5;

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        base.StartServerSide(sapi);

        var manifold = sapi.GetManifoldServer(this);
        if (!manifold.IsHealthy)
        {
            Mod.Logger.Warning("[MyMod] Manifold unhealthy; dimension features disabled.");
            return;
        }

        manifold.Registry
            .Define(new AssetLocation("mymod", "void"))
            .Persistent()
            .WithWorldgen(new BasicVoidWorldgenStrategy())
            .WithFixedSpawn(new BlockPos(1024, 64, 1024, 0))
            .WithGenerationRadius(5)
            .RegisterStatic();

        new DimensionCommandBuilder()
            .Command("voiddim")
            .TargetDimension(new AssetLocation("mymod", "void"))
            .RequiresPrivilege("chat")
            .DescribedAs("Teleport to the void dimension.")
            .Register(sapi);
    }
}
```

---

## 11. Patterns from a shipped Manifold consumer (Personal Pocket Dimension 0.1.0)

Observations from decompiling **Personal Pocket Dimension** by *LilianaXolkiyr* (`personalpocketdimension`,
game 1.22.2, Manifold dependency) — a much larger mod than ours. These are real, in-the-wild
techniques, several of which directly answer problems we hit. (Decompiled source kept at
`C:\Users\andrea\Downloads\ppd_extract\decompiled\` for reference; not part of this repo.)

### 11.1 One shared persistent dimension, many *cells* (not one dim per pocket)
They register **a single** `Persistent()` dimension and partition it **spatially** instead of calling
`Define()` per player. Each personal pocket is a cell whose centre is derived by hashing the pocket's
id (FNV-1a) into a 64×64 grid, with cell spacing `cellChunkRadius * 3 * 32` blocks:
```csharp
uint h = 2166136261u;
foreach (char c in ppdId) { h ^= c; h *= 16777619; }
uint slot = h % 4096;
int gx = (int)slot % 64, gz = (int)slot / 64;
int spacing = Math.Max(1, cellChunkRadius) * 3 * 32;
return (1040 + gx * spacing, 1040 + gz * spacing);
```
This sidesteps the 10–1023 dimension-id cap and the per-dimension registration cost. **Relevant to our
"variants → many pockets" extension:** prefer cells in one dimension over a dimension per door type.

### 11.2 Load dimension chunks WITHOUT a transit — `WorldManager`
The fix for our **relog-into-void** problem. Manifold only force-sends chunks on transit, but you can
load a dimension's chunks yourself at any time:
```csharp
sapi.WorldManager.CreateChunkColumnForDimension(chunkX, chunkZ, dimId); // generate if missing
sapi.WorldManager.LoadChunkColumnForDimension(chunkX, chunkZ, dimId);   // bring into memory + send
bool ready = sapi.World.IsFullyLoadedChunk(new BlockPos(x, y, z, dimId)); // gate before relying on it
```
They load a *footprint* of columns around a pocket's centre (`(center ± N) / 32`) on init, on entry,
and on demand. With this, a player who logs in inside the dimension gets real chunks around them — **no
re-transit hack needed**, and (see 11.4) no transit-induced block desync.

### 11.3 Worldgen builds only a shell; real structures placed at runtime
`StaticPpdWorldgenStrategy.GenerateColumn` only paints a small hollow boundary box for the cell that the
current chunk overlaps (it early-outs unless the chunk intersects the cell's X/Z window). The actual
rooms are WorldEdit-style `.json` **schematics** stamped in *after* generation by a runtime placer that
does manual chunk create/load → `BlockAccessor.SetBlock` → `MarkBlockModified` → `Commit`. Takeaway:
keep `GenerateColumn` cheap and bounded; place anything elaborate at runtime against loaded chunks.

### 11.4 Post-transit "repair" pass fixes block desync
The fix for our **inert-door-after-relog** problem. After every `TeleportPlayer` *into* the dimension
they re-run fixture placement at **+500 ms and +2000 ms** (`RepairStaticPpdAfterTeleport` →
`EnsureStaticPpdFixtures`), which reloads the chunk footprint and re-`SetBlock`s the key fixtures
(portal, return pad, torches), each with `MarkBlockModified` + `Commit`. This server-side re-stamp after
the chunk has settled is what keeps arrival-point blocks interactive instead of client-desynced.

### 11.5 They TRUST `Pos.Dimension`
Throughout (`TryTransitPlayer`, the 1 s upkeep tick, exit-target resolution) they branch on
`player.Entity.Pos.Dimension == staticPpdDimension.InternalId` with **no relog rescue** — `OnPlayerNowPlaying`
only wires up HUD/listeners, it does not re-transit or correct the dimension. This contradicts our
earlier finding that `Pos.Dimension`/`InternalY` are unreliable on relog. The likely reconciliation: with
a **persistent** dimension whose chunks are actively kept loaded (11.2), the engine restores the player's
dimension correctly; our unreliability may have come from the chunks never being loaded at login. Worth
re-testing whether trusting `Pos.Dimension` is fine once we load chunks ourselves.

### 11.6 Transit positioning: always `OverridePosition`, bidirectional by current dim
They never rely on `SpawnBehavior` for placement — every transit passes
`new TransitionOptions { OverridePosition = target }`, where `target` is the overworld anchor or the
pocket landing pad chosen by reading the *current* `Pos.Dimension`. A 2 s per-player portal cooldown
guards re-triggers. Entry path also pre-places the exit portal and, if the endpoint isn't generated yet,
teleports first then retries portal placement (`PlaceExitPortalAfterGeneration`).

### 11.7 Misc
- `WithRelightHeight(144)` for a platform at Y 128 — confirms our "relight must clear the build height" note.
- Registry/layout/occupant state is persisted to the mod's **own JSON file** in the save folder, keyed by
  `WorldManager.SaveGame.SavegameIdentifier`, rather than watched attributes.
- They cancel maps and temporal-stability drain inside the pocket (client packet + stability override).
- Two `ModSystem`s: gameplay (`PersonalPocketDimensionModSystem`) and visuals (`PortalEffectSystem`).

### What this means for Portals
1. Replace the relog **re-transit-into-pocket** hack with a **manual chunk load** of the pocket footprint
   in `OnPlayerNowPlaying` (11.2) so the player genuinely stays in the pocket with working doors.
2. Add a **post-transit repair** (re-`SetBlock` the return door + `MarkBlockModified`/`Commit` at +500 ms)
   to kill the inert-door desync (11.4).
3. Re-evaluate whether we can **trust `Pos.Dimension`** once chunks are loaded (11.5), simplifying the
   `portals:inPocket` watched-attribute bookkeeping.

---

## Reference links

- Overview: <https://leonfvt.fr/Manifold/>
- Getting started: <https://leonfvt.fr/Manifold/articles/getting-started.html>
- Dimensions: <https://leonfvt.fr/Manifold/articles/dimensions.html>
- Worldgen: <https://leonfvt.fr/Manifold/articles/worldgen.html>
- Transit & travel policy: <https://leonfvt.fr/Manifold/articles/transit-and-travel-policy.html>
- Architecture: <https://leonfvt.fr/Manifold/articles/architecture.html>
- API reference: <https://leonfvt.fr/Manifold/api/Manifold.html>
