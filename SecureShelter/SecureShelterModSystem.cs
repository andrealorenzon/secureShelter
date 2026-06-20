using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Manifold.Api;
using Manifold.Api.Server;
using Manifold.Api.Transitions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SecureShelter;

public class SecureShelterModSystem : ModSystem
{
    // ── Identity ─────────────────────────────────────────────────────────────
    public const string Domain = "secureshelter";
    private const string ConfigFile = "SecureShelterConfig.json";

    public static readonly AssetLocation OverworldCode = new("manifold", "overworld");

    // Loaded from ModConfig/SecureShelterConfig.json; all concrete coordinates live in `geo`, which is
    // computed from this config plus the shelter schematic's measured size (so the shell wraps it
    // exactly). Both are resolved in StartServerSide before the dimension is registered.
    private SecureShelterConfig config = null!;
    private PocketGeometry geo = null!;

    /// <summary>The pocket dimension code (from config). Bumped via config when the layout changes.</summary>
    private AssetLocation PocketDimCode => geo.DimCode;

    // The pre-built interior (a forest), placed once into the shell on first entry.
    private AssetLocation shelterAsset = null!;
    private BlockSchematic? shelterSchematic;

    // World positions of the shelter's static light sources (block LightHsv > 0), computed once from
    // the schematic. Relit by re-exchanging each block (see RelightLights).
    private Dictionary<BlockPos, int>? lightPositions;

    /// <summary>Server-side Manifold facade, resolved in <see cref="StartServerSide"/>.</summary>
    public IManifoldServer? Manifold { get; private set; }

    /// <summary>Engine dimension id assigned to the pocket dimension, or -1 if unavailable.</summary>
    public int PocketDimId { get; private set; } = -1;

    private ICoreServerAPI? sapi;

    // Walk-through debounce: the per-tick scan can report a player inside the arch hole on many
    // consecutive ticks, so we collapse one continuous pass into a single transit.
    private const long ContinuousPassGapMs = 300;
    private readonly Dictionary<string, long> lastInsideMs = new();
    private readonly HashSet<string> triggeredThisPass = new();

    // Per-player previous feet position + dimension, for the plane-crossing scan.
    private readonly Dictionary<string, (Vec3d pos, int dim)> prevState = new();

    // Players currently mounted (e.g. sleeping in a bed); used to detect waking in the pocket.
    private readonly HashSet<string> mountedUids = new();

    // Pocket-local position where each player lay down to sleep, so they wake right at that bed.
    private readonly Dictionary<string, BlockPos> sleepReturnPos = new();

    // Snapshots of refillable blocks (barrels / jugs / vessels / crocks / firepits) in the pocket,
    // keyed by position: the block id plus its block-entity tree (if any), captured full/lit and
    // restored every refill tick. Block id is kept too so a firepit that burns down to its cold
    // variant is set back to the lit block.
    private readonly Dictionary<BlockPos, (int blockId, byte[]? tree)> refillBackups = new();

    // Blocks with a rebuild already queued (debounce, so repeated takes collapse to one rebuild).
    private readonly HashSet<BlockPos> pendingRebuild = new();

    // How long after a player takes from a managed block before it's rebuilt.
    private const int RebuildDelayMs = 20000;

    // Firewood a pocket firepit's fuel slot is topped up to on entry, so the fire stays lit for a
    // long time without any polling (see DiscoverRefillables).
    private const int FirepitFuelRefill = 32;

    // Consumer mods must load after Manifold's own order (0.05).
    public override double ExecuteOrder() => 0.5;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.RegisterBlockClass("SecureShelter.painting", typeof(BlockPaintingSecureShelter));
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        base.StartServerSide(sapi);
        this.sapi = sapi;

        // Owner-scoped facade: passing `this` tags any dimensions we register with our mod id.
        Manifold = sapi.GetManifoldServer(this);
        if (!Manifold.IsHealthy)
        {
            Mod.Logger.Warning("[SecureShelter] Manifold is unhealthy; dimension features disabled.");
            Manifold = null;
            return;
        }

        LoadConfig(sapi);

        // Load the interior schematic up front so its measured size can drive the cube geometry, and
        // pull out the arch-marker block (if any) so its position becomes the return arch.
        shelterAsset = new AssetLocation(Domain, SecureShelterConfig.ShelterSchematicPath);
        BlockSchematic? schem = GetShelterSchematic();
        (int X, int Y, int Z)? archMarker = schem != null ? FindAndStripArchMarker(schem) : null;
        geo = PocketGeometry.Build(config, schem?.SizeX ?? 0, schem?.SizeY ?? 0, schem?.SizeZ ?? 0, archMarker);
        Mod.Logger.Notification(
            "[SecureShelter] Cube geometry: footprint x[{0}..{1}] z[{2}..{3}] y[{4}..{5}], spawn ({6},{7},{8}), relight {9}.",
            geo.ShellMinX, geo.ShellMaxX, geo.ShellMinZ, geo.ShellMaxZ, geo.FloorY, geo.ShellTopY,
            geo.SpawnX, geo.SpawnY, geo.SpawnZ, geo.RelightHeight);

        RegisterPocketDimension(sapi);
        RegisterDebugCommand(sapi);

        // Detect arch walk-throughs ourselves every tick (see OnServerTick).
        sapi.Event.RegisterGameTickListener(OnServerTick, 20);

        sapi.Event.PlayerDisconnect += p =>
        {
            string uid = p.PlayerUID;
            prevState.Remove(uid);
            lastInsideMs.Remove(uid);
            triggeredThisPass.Remove(uid);
            mountedUids.Remove(uid);
            sleepReturnPos.Remove(uid);
        };

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        // Keep the pocket permanently dry (see OnPocketGetClimate).
        ((IEventAPI)sapi.Event).OnGetClimate += OnPocketGetClimate;

        // Keep players in the pocket fully temporally stable. The stability query hook is no use here —
        // it's handed a dimension-blind, dimension-LOCAL position, so it can't tell pocket from
        // overworld — so instead we pin each pocket player's own stability to max on a slow tick.
        sapi.Event.RegisterGameTickListener(_ => ForcePocketStability(), 1000);

        // Make the pocket a safe zone — no HP loss while inside (see HookInvulnerability).
        sapi.Event.PlayerNowPlaying += HookInvulnerability;

        // Static-dimension protections (all scoped to THIS pocket only — see the handlers).
        sapi.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreak;   // no breaking/placing blocks
        sapi.Event.CanUseBlock += OnCanUseBlock;                // doors free; loot locked bar barrels/vessels
        sapi.Event.OnTrySpawnEntity += OnPocketTrySpawnEntity;  // no natural spawns (no enemies)
        sapi.Event.DidUseBlock += OnDidUseBlock;                // refill a managed block ~20s after a take
        sapi.Event.RegisterGameTickListener(OnDropSweep, 1000);          // can't drop items

        // Animals in the pocket are unkillable (hooked when they spawn or load there).
        ((IEventAPI)sapi.Event).OnEntitySpawn += HookEntityInvulnerability;
        ((IEventAPI)sapi.Event).OnEntityLoaded += HookEntityInvulnerability;
    }

    // ── Static-dimension protections (this pocket only — every check keys off PocketDimId, so
    //    other dimensions this mod may add later are unaffected) ──────────────────────────────

    private bool IsPocketInternalY(double internalY) => PocketDimId >= 0 && DimFromInternalY(internalY) == PocketDimId;
    private bool IsPlayerInPocket(IServerPlayer? p) => p?.Entity != null && IsPocketInternalY(p.Entity.Pos.InternalY);

    // No breaking or placing blocks inside the pocket — it's static.
    private bool OnCanPlaceOrBreak(IServerPlayer byPlayer, BlockSelection blockSel, out string claimant)
    {
        claimant = null!;
        if (!IsPlayerInPocket(byPlayer)) return true;   // not this pocket → no objection
        if (IsBowlEdit(byPlayer, blockSel)) return true; // exception: bowls may be placed/picked up
        claimant = "SecureShelter";
        return false;
    }

    // Doors/levers/beds and harmless interactions stay free. Whitelisted containers (barrels, jugs/
    // jars, vessels, crocks, firepit pots, shelves, cheese/pie, chests, ground storage) are usable;
    // every other container is locked. "Takeable" decor — whose right-click removes the block or hands
    // out an item (oil lamps via RightClickPickup/GroundStorable, candles via BlockBunchOCandles) — is
    // also locked: it bypasses the place/break lock and taking it has desynced the dimension (void).
    private bool OnCanUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        if (!IsPlayerInPocket(byPlayer) || blockSel?.Position == null) return true;

        Block block = sapi!.World.BlockAccessor.GetBlock(blockSel.Position);
        string path = block.Code?.Path ?? "";

        if (IsUsableContainerPath(path))
        {
            // Capture full state before the take. Ground storage is excluded: only the schematic's
            // own ground storage (snapshotted at entry by DiscoverRefillables — e.g. the apples)
            // refills; bowls a player puts down later must stay theirs to keep/move.
            if (IsRefillablePath(path) && !path.StartsWith("groundstorage"))
                SnapshotRefillable(blockSel.Position);
            return true;
        }

        if (IsTakeableDecor(block)) return false;   // candles / oil lamps / ground-storables: locked

        // Lock any other (non-whitelisted) container; leave plain blocks (doors, levers, beds …) free.
        return sapi.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not IBlockEntityContainer;
    }

    // True for blocks whose right-click takes the block or hands out an item, so they'd otherwise slip
    // past the place/break lock. Behavior-based (covers oil lamps and anything with RightClickPickup or
    // GroundStorable) plus an explicit check for the class-based candle stacks.
    private static bool IsTakeableDecor(Block block)
    {
        if (block?.BlockBehaviors != null)
            foreach (BlockBehavior b in block.BlockBehaviors)
            {
                string n = b.GetType().Name;
                if (n.Contains("RightClickPickup") || n.Contains("GroundStorable")) return true;
            }
        string path = block?.Code?.Path ?? "";
        return path.StartsWith("bunchocandles") || path.StartsWith("candle");
    }

    // Bowls (incl. meal-filled bowls) may be placed from hand or picked up off a table (ground
    // storage) — the only block edit allowed in the pocket.
    private bool IsBowlEdit(IServerPlayer byPlayer, BlockSelection? blockSel)
    {
        string held = byPlayer.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code?.Path ?? "";
        if (held.StartsWith("bowl")) return true;
        if (blockSel?.Position == null) return false;
        string tgt = sapi!.World.BlockAccessor.GetBlock(blockSel.Position).Code?.Path ?? "";
        return tgt.StartsWith("bowl") || tgt.StartsWith("groundstorage");
    }

    // Player may interact with these (take liquid/food, cut cheese/pie, ground-storage bowls).
    private bool IsUsableContainerPath(string path) => MatchesAnyPrefix(path, SecureShelterConfig.UsableContainerPrefixes);

    // Containers/blocks whose contents we keep topped up (incl. cheese/pie slice regrowth).
    private bool IsRefillablePath(string path) => MatchesAnyPrefix(path, SecureShelterConfig.RefillableBlockPrefixes);

    private static bool MatchesAnyPrefix(string path, string[]? prefixes)
    {
        if (string.IsNullOrEmpty(path) || prefixes == null) return false;
        foreach (string p in prefixes)
            if (!string.IsNullOrEmpty(p) && path.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    // No natural spawns in the pocket — covers every enemy and keeps the dimension static.
    private bool OnPocketTrySpawnEntity(IBlockAccessor blockAccessor, ref EntityProperties properties, Vec3d spawnPosition, long herdId)
        => !IsPocketInternalY(spawnPosition.Y);

    // Find all refillable blocks in the pocket and snapshot their full/lit state. Walks only the
    // footprint chunks' block-entity lists (a handful of chunks), not the whole volume. Run during
    // the post-entry repair pass, when the shelter has just been (re)placed and everything is full.
    private void DiscoverRefillables()
    {
        if (sapi == null || PocketDimId < 0) return;

        int cxMin = FloorDiv(geo.ShellMinX, 32), cxMax = FloorDiv(geo.ShellMaxX, 32);
        int czMin = FloorDiv(geo.ShellMinZ, 32), czMax = FloorDiv(geo.ShellMaxZ, 32);

        // Probe one block in each chunk-Y layer the build spans; GetChunk(BlockPos) resolves the
        // dimension from the position. SnapshotRefillable is idempotent, so revisiting is harmless.
        for (int cx = cxMin; cx <= cxMax; cx++)
        for (int cz = czMin; cz <= czMax; cz++)
        foreach (int ly in new[] { geo.FloorY, geo.ShellTopY })
        {
            IServerChunk? chunk = sapi.WorldManager.GetChunk(new BlockPos(cx * 32, ly, cz * 32, PocketDimId));
            if (chunk?.BlockEntities == null) continue;
            foreach (var kv in chunk.BlockEntities)
            {
                string path = kv.Value?.Block?.Code?.Path ?? "";
                if (!IsRefillablePath(path)) continue;
                // Firepits aren't player-taken (the fire burns on its own), so there's no use-event to
                // trigger a rebuild — instead top their firewood right up on entry so they stay lit.
                if (path.StartsWith("firepit")) TopFirepitFuel(kv.Key);
                SnapshotRefillable(kv.Key);
            }
        }
    }

    private void SnapshotRefillable(BlockPos pos)
    {
        BlockPos key = pos.Copy();
        if (refillBackups.ContainsKey(key)) return;          // first sight = full/lit, before any take
        Block block = sapi!.World.BlockAccessor.GetBlock(pos);
        byte[]? tree = null;
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is { } be)
        {
            var t = new TreeAttribute();
            be.ToTreeAttributes(t);
            tree = t.ToBytes();
        }
        refillBackups[key] = (block.BlockId, tree);
    }

    // Top a firepit's firewood (fuel slot 0) up to a big stack so it burns for a long time.
    private void TopFirepitFuel(BlockPos pos)
    {
        if (sapi!.World.BlockAccessor.GetBlockEntity(pos) is IBlockEntityContainer cont &&
            cont.Inventory.Count > 0)
        {
            ItemSlot fuel = cont.Inventory[0];
            if (fuel?.Itemstack != null && fuel.Itemstack.StackSize < FirepitFuelRefill)
            {
                fuel.Itemstack.StackSize = FirepitFuelRefill;
                fuel.MarkDirty();
            }
        }
    }

    // A player used a managed block (table/ground storage, vessel, crock, firepit, cheese/pie, …).
    // Rather than poll, we schedule a one-off rebuild of just that block 20 s later — and the rebuild
    // only actually restores if something was taken (state differs from the snapshot).
    private void OnDidUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        if (!IsPlayerInPocket(byPlayer) || blockSel?.Position == null) return;
        BlockPos pos = blockSel.Position.Copy();
        if (!refillBackups.ContainsKey(pos)) return;   // not a snapshotted/managed block
        ScheduleRebuild(pos);
    }

    // Debounced 20 s delayed rebuild for one block (multiple takes within the window collapse to one).
    private void ScheduleRebuild(BlockPos pos)
    {
        if (sapi == null || !pendingRebuild.Add(pos)) return;
        sapi.World.RegisterCallback(_ =>
        {
            pendingRebuild.Remove(pos);
            RebuildBlock(pos);
        }, RebuildDelayMs);
    }

    // Restore one snapshotted block, but only if something was actually taken (its state differs from
    // the snapshot) — so untouched blocks never churn or re-render. Re-places the block too if it was
    // removed (e.g. a fully-eaten cheese/pie) before restoring its contents.
    private void RebuildBlock(BlockPos pos)
    {
        if (sapi == null || PocketDimId < 0) return;
        if (!refillBackups.TryGetValue(pos, out (int blockId, byte[]? tree) snap)) return;
        IBlockAccessor ba = sapi.World.BlockAccessor;

        bool blockChanged = ba.GetBlock(pos).BlockId != snap.blockId;
        if (snap.tree == null)
        {
            if (blockChanged) ba.SetBlock(snap.blockId, pos);
            return;
        }
        if (!blockChanged && ba.GetBlockEntity(pos) is { } probe)
        {
            var curT = new TreeAttribute();
            probe.ToTreeAttributes(curT);
            if (BytesEqual(curT.ToBytes(), snap.tree)) return;   // nothing taken → leave it be
        }

        if (blockChanged) ba.SetBlock(snap.blockId, pos);
        if (ba.GetBlockEntity(pos) is { } be)
        {
            be.FromTreeAttributes(TreeAttribute.CreateFromBytes(snap.tree), sapi.World);
            be.MarkDirty(true);
        }
    }

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a == null || b == null) return ReferenceEquals(a, b);
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // Players can't drop items in the pocket: anything that lands on the floor is returned to the
    // nearest player there, so it snaps straight back into their (pocket) inventory.
    private void OnDropSweep(float dt)
    {
        if (sapi == null || PocketDimId < 0) return;

        List<EntityItem>? items = null;
        foreach (Entity e in sapi.World.LoadedEntities.Values)
        {
            if (e is EntityItem it && it.Alive && IsPocketInternalY(it.Pos.InternalY))
                (items ??= new()).Add(it);
        }
        if (items == null) return;

        foreach (EntityItem it in items)
        {
            ItemStack? stack = it.Itemstack;
            if (stack == null) continue;
            IServerPlayer? near = NearestPocketPlayer(it.Pos.XYZ);
            if (near?.InventoryManager.TryGiveItemstack(stack, true) == true)
                it.Die(EnumDespawnReason.PickedUp);
        }
    }

    private IServerPlayer? NearestPocketPlayer(Vec3d pos)
    {
        IServerPlayer? best = null;
        double bestSq = double.MaxValue;
        foreach (IPlayer p in sapi!.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || sp.Entity == null) continue;
            if (!IsPocketInternalY(sp.Entity.Pos.InternalY)) continue;
            double d = sp.Entity.Pos.XYZ.SquareDistanceTo(pos);
            if (d < bestSq) { bestSq = d; best = sp; }
        }
        return best;
    }

    // The pocket is a safe zone: no player can lose HP while inside it. We add a damage modifier to
    // each player's health behavior that zeroes ALL incoming damage (mobs, fall, starvation, etc.)
    // whenever they are in the pocket dimension. The check is dynamic, so damage is only cancelled
    // while actually inside. The handler is added once per login; the player's entity (and its
    // health behavior) is rebuilt on each join, so handlers don't accumulate across relogs.
    private void HookInvulnerability(IServerPlayer player)
    {
        EntityBehaviorHealth? hp = player.Entity?.GetBehavior<EntityBehaviorHealth>();
        if (hp == null) return;
        hp.onDamaged += (dmg, src) =>
            PocketDimId >= 0 && player.Entity != null &&
            DimFromInternalY(player.Entity.Pos.InternalY) == PocketDimId
                ? 0f
                : dmg;
    }

    // Animals (and any non-player creature) in the pocket are unkillable: when one spawns or loads
    // there, we add the same zero-damage modifier to its health behavior. Only entities that appear
    // inside the pocket are hooked, so overworld creatures are unaffected.
    private void HookEntityInvulnerability(Entity entity)
    {
        if (entity is EntityPlayer) return;                  // players handled in HookInvulnerability
        if (!IsPocketInternalY(entity.Pos.InternalY)) return;
        EntityBehaviorHealth? hp = entity.GetBehavior<EntityBehaviorHealth>();
        if (hp == null) return;
        hp.onDamaged += (dmg, src) => IsPocketInternalY(entity.Pos.InternalY) ? 0f : dmg;
    }

    // Temperature-sensitive blocks (lit torches, firepits) extinguish when it "rains" on them. In a
    // Manifold dimension the engine's rain-exposure check is dimension-blind: it reads the overworld
    // heightmap via the position-less GetRainMapHeightAt(x,z) overload, so nothing you build in the
    // pocket — not even a solid ceiling — can shelter a block, and overworld precipitation snuffs
    // every lit block (you even hear phantom rain). The rain intensity is GetPrecipitation, which
    // scales with the position's climate Rainfall — so we force the pocket's rainfall to zero
    // whenever its climate is queried, collapsing precipitation there to ~0. Only pocket-dimension
    // positions match (the BlockPos encodes the dimension in its Y / dimension field), so overworld
    // weather is left completely untouched.
    private void OnPocketGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
    {
        if (climate == null || PocketDimId < 0) return;
        if (pos.dimension != PocketDimId && pos.Y / BlockPos.DimensionBoundary != PocketDimId) return;
        climate.Rainfall = 0f;
        climate.RainCloudOverlay = 0f;
    }

    // Pin every player currently inside the pocket to full temporal stability (1.0), so the refuge is
    // never a low/negative-stability zone — covers both the dimension-blind positional calc AND active
    // temporal storms (which drain stability everywhere). We can't fix the world-stability query (it's
    // handed a dimension-local position, so it can't identify the pocket), so we override the player's
    // own stability stat directly. Runs on a 1 s tick; the per-tick drain between resets is negligible.
    private void ForcePocketStability()
    {
        if (sapi == null || PocketDimId < 0) return;
        foreach (IPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || sp.Entity == null) continue;
            if (DimFromInternalY(sp.Entity.Pos.InternalY) != PocketDimId) continue;
            sp.Entity.WatchedAttributes.SetDouble("temporalStability", 1.0);
            sp.Entity.WatchedAttributes.MarkPathDirty("temporalStability");
        }
    }

    private void LoadConfig(ICoreServerAPI sapi)
    {
        try
        {
            config = sapi.LoadModConfig<SecureShelterConfig>(ConfigFile) ?? new SecureShelterConfig();
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("[SecureShelter] config load failed ({0}); using defaults.", e.Message);
            config = new SecureShelterConfig();
        }
        // Write it back so the file exists (and any new fields get their defaults persisted).
        sapi.StoreModConfig(config, ConfigFile);
    }

    private static int DimFromInternalY(double internalY) => (int)(internalY / BlockPos.DimensionBoundary);

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    // ── Relog handling ───────────────────────────────────────────────────────
    // We track whether a player is in the pocket with the persistent "secureshelter:inPocket" watched
    // attribute. A login is not a transit, so Manifold won't load the pocket's chunks on its own
    // and the player would spawn into void. On login we therefore load the footprint, transit them
    // back onto the spawn, and run the repair pass.
    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (Manifold == null || player.Entity == null) return;
        if (!player.Entity.WatchedAttributes.GetBool("secureshelter:inPocket")) return;

        sapi!.World.RegisterCallback(_ =>
        {
            if (player.Entity == null) return;
            if (!player.Entity.WatchedAttributes.GetBool("secureshelter:inPocket")) return;

            EnsurePocketChunksLoaded();
            Manifold.Transitions.TeleportPlayer(
                player, PocketDimCode, new TransitionOptions { OverridePosition = PocketSpawnPos() });
            SchedulePocketRepair();
            sapi.World.RegisterCallback(_ => LiftToAir(player), 1000);
            Mod.Logger.Notification("[SecureShelter] Restored uid={0} into pocket on login.", player.PlayerUID);
        }, 1500);
    }

    // Restore a player who just woke from sleeping in the pocket. The time-skip can either just drop
    // the chunks client-side or eject the player from the dimension entirely; re-teleporting handles
    // both. Keeps them in place if still inside; otherwise puts them back on the spawn.
    private void RestoreAfterWake(IServerPlayer player)
    {
        if (Manifold == null || player.Entity == null) return;
        if (!player.Entity.WatchedAttributes.GetBool("secureshelter:inPocket")) return;

        EnsurePocketChunksLoaded();

        // Prefer the exact bed they lay down on; else keep them in place (if still inside); else spawn.
        BlockPos target;
        if (sleepReturnPos.TryGetValue(player.PlayerUID, out BlockPos? bed))
        {
            target = bed;
        }
        else if (DimFromInternalY(player.Entity.Pos.InternalY) == PocketDimId)
        {
            EntityPos ep = player.Entity.Pos;
            int ly = (int)Math.Floor(ep.InternalY) - PocketDimId * BlockPos.DimensionBoundary;
            target = new BlockPos((int)Math.Floor(ep.X), ly, (int)Math.Floor(ep.Z), PocketDimId);
        }
        else target = PocketSpawnPos();

        sleepReturnPos.Remove(player.PlayerUID);
        Manifold.Transitions.TeleportPlayer(player, PocketDimCode, new TransitionOptions { OverridePosition = target });
        SchedulePocketRepair();
        ResendPocketChunks();
    }

    // ── Chunk loading + interior setup ───────────────────────────────────────
    private BlockPos PocketSpawnPos() => new(geo.SpawnX, geo.SpawnY, geo.SpawnZ, PocketDimId);

    // The shelter's surface height varies, so the fixed spawn can land the player inside the terrain.
    // After arrival, if the player's feet/head cell is solid, raise them to the first spot with two
    // passable cells (feet + head) so they stand in the open instead of suffocating. No-op if already
    // clear. Must run after the shelter is placed, so the terrain exists to test against.
    private void LiftToAir(IServerPlayer player)
    {
        if (sapi == null || PocketDimId < 0 || player.Entity == null) return;
        EntityPos ep = player.Entity.Pos;
        if (DimFromInternalY(ep.InternalY) != PocketDimId) return;

        IBlockAccessor ba = sapi.World.BlockAccessor;
        int x = (int)Math.Floor(ep.X), z = (int)Math.Floor(ep.Z);
        int feetY = (int)Math.Floor(ep.InternalY) - PocketDimId * BlockPos.DimensionBoundary;

        int y = feetY;
        bool found = false;
        for (; y <= geo.ShellTopY; y++)
            if (IsPassable(ba, x, y, z) && IsPassable(ba, x, y + 1, z)) { found = true; break; }

        if (!found || y <= feetY) return;

        // Raise by the block delta in the entity's own coordinate space (Pos.Y), so it's correct
        // whether Pos.Y is dimension-local or internal — passing an absolute internal Y here flung the
        // player out of the pocket into the void.
        player.Entity.TeleportToDouble(ep.X, ep.Y + (y - feetY), ep.Z);
        Mod.Logger.Notification("[SecureShelter] Lifted uid={0} y {1}->{2} (spawn was inside a block).",
            player.PlayerUID, feetY, y);
    }

    // A cell a player can occupy: air, or any block without a collision box (grass, flowers, …).
    private bool IsPassable(IBlockAccessor ba, int x, int localY, int z)
    {
        Block b = ba.GetBlock(new BlockPos(x, localY, z, PocketDimId));
        return b == null || b.Id == 0 || b.CollisionBoxes == null || b.CollisionBoxes.Length == 0;
    }

    private void EnsurePocketChunksLoaded()
    {
        if (sapi == null || PocketDimId < 0) return;

        // LOAD ONLY — never Create. The columns were generated on first entry and are saved
        // (persistent dimension); loading restores them intact. Cover the dirt-wrapped footprint
        // (uses floor-division so the -1 wrapper column lands in chunk -1, not 0).
        int minCX = FloorDiv(geo.DirtMinX, 32), maxCX = FloorDiv(geo.DirtMaxX, 32);
        int minCZ = FloorDiv(geo.DirtMinZ, 32), maxCZ = FloorDiv(geo.DirtMaxZ, 32);
        for (int cx = minCX; cx <= maxCX; cx++)
        for (int cz = minCZ; cz <= maxCZ; cz++)
            sapi.WorldManager.LoadChunkColumnForDimension(cx, cz, PocketDimId);
    }

    // After a transit into the pocket: place the shelter interior (once) and snapshot refillables. Two
    // staggered passes because the footprint chunks may not all be loaded on the first. The return
    // portal needs no stamping: the arch is baked into the schematic, and the arch-marker block's
    // position (see FindAndStripArchMarker) is the walk-through trigger.
    //
    // First-entry lighting: the place-time relight doesn't reach a remote client, so once the player
    // has settled (chunks tracked) we re-trigger a per-light-source relight via ExchangeBlock — the
    // same engine block-change path that works when a player places/removes a torch. Runs a couple of
    // seconds after placement; cheap (no full-chunk resends), so no lag.
    private void SchedulePocketRepair()
    {
        if (sapi == null) return;
        sapi.World.RegisterCallback(_ => { EnsureShelterPlaced(); DiscoverRefillables(); }, 2000);
        sapi.World.RegisterCallback(_ => { EnsureShelterPlaced(); DiscoverRefillables(); }, 3500);
        sapi.World.RegisterCallback(_ => RelightLights(), 5000);
    }

    // Stamp the shelter schematic into the shell once per dimension (guarded by a savegame flag),
    // aligned to the cube footprint with its base on the floor. Returns the number of blocks placed.
    internal int EnsureShelterPlaced(bool force = false)
    {
        if (sapi == null || PocketDimId < 0) return 0;

        string key = "secureshelter:shelterPlaced-" + PocketDimCode.Path;
        if (!force && sapi.World.Config.GetBool(key)) return 0;

        BlockSchematic? schem = GetShelterSchematic();
        if (schem == null) return 0;

        EnsurePocketChunksLoaded();
        var origin = new BlockPos(geo.ShelterOriginX, geo.FloorY, geo.ShelterOriginZ, PocketDimId);

        int placed;
        try
        {
            IBulkBlockAccessor bulk = sapi.World.GetBlockAccessorBulkUpdate(synchronize: true, relight: true);
            schem.Init(bulk);
            placed = schem.Place(bulk, sapi.World, origin, EnumReplaceMode.ReplaceAllNoAir, replaceMetaBlocks: true);
            bulk.Commit();
            schem.PlaceEntitiesAndBlockEntities(
                sapi.World.BlockAccessor, sapi.World, origin, schem.BlockCodes, schem.ItemCodes);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("[SecureShelter] shelter placement threw: {0}", e);
            return 0;
        }

        Mod.Logger.Notification(
            "[SecureShelter] Shelter placement: {0} blocks at ({1},{2},{3}) dim {4}.",
            placed, origin.X, origin.Y, origin.Z, PocketDimId);

        // 0 placed → the footprint chunks weren't loaded yet; leave the flag unset so a later pass
        // retries. The bulk Commit above already synced the blocks; lighting is handled by the
        // delayed RelightLights pass (see SchedulePocketRepair).
        if (placed <= 0) return 0;
        sapi.World.Config.SetBool(key, true);
        return placed;
    }

    // If the schematic contains the configured arch-marker block, return its position (relative to
    // the schematic) and replace it with the surrounding floor block — the marker sits in the doorway
    // FLOOR, so stripping it to air would gouge a 1-deep hole. The walk-through trigger is the opening
    // above it (see IsArchHole). Returns null if no marker is present.
    private (int X, int Y, int Z)? FindAndStripArchMarker(BlockSchematic schem)
    {
        string code = SecureShelterConfig.ArchMarkerBlockCode;
        if (string.IsNullOrEmpty(code)) return null;

        int markerId = -1;
        foreach (var kv in schem.BlockCodes)
        {
            AssetLocation? c = kv.Value;
            if (c != null && (c.Path == code || c.ToShortString() == code)) { markerId = kv.Key; break; }
        }
        if (markerId < 0) return null;

        // Index → block id, to sample the floor block around the marker.
        var byIndex = new Dictionary<uint, int>(schem.Indices.Count);
        for (int i = 0; i < schem.Indices.Count; i++) byIndex[schem.Indices[i]] = schem.BlockIds[i];

        static uint Pack(int x, int y, int z) => (uint)(x | (z << 10) | (y << 20));

        for (int i = 0; i < schem.BlockIds.Count; i++)
        {
            if (schem.BlockIds[i] != markerId) continue;

            uint idx = schem.Indices[i];
            int rx = (int)(idx & 0x3FF), rz = (int)((idx >> 10) & 0x3FF), ry = (int)((idx >> 20) & 0x3FF);

            // Fill the marker cell with an adjacent floor block (same Y), else the block below, so the
            // doorway floor stays flush. If nothing's found, fall back to leaving it as air.
            int fillId = -1;
            foreach (uint n in new[] { Pack(rx - 1, ry, rz), Pack(rx + 1, ry, rz), Pack(rx, ry, rz - 1), Pack(rx, ry, rz + 1), Pack(rx, ry - 1, rz) })
                if (byIndex.TryGetValue(n, out int got)) { fillId = got; break; }

            if (fillId >= 0)
            {
                schem.BlockIds[i] = fillId;
            }
            else
            {
                schem.Indices.RemoveAt(i);
                schem.BlockIds.RemoveAt(i);
            }
            schem.BlockEntities?.Remove(idx);

            Mod.Logger.Notification("[SecureShelter] Arch marker '{0}' at schematic ({1},{2},{3}); portal trigger set above it.",
                code, rx, ry, rz);
            return (rx, ry, rz);
        }
        return null;
    }

    // Decode the schematic once into world positions of its static light sources (blocks whose own
    // LightHsv > 0 — candles, lanterns, firepit, torch holders, creativeglow; NOT the BE-driven
    // ground-storage lamps, whose block LightHsv is 0). Index packing matches FindAndStripArchMarker.
    private void BuildLightIndex()
    {
        if (lightPositions != null) return;
        if (sapi == null || PocketDimId < 0) return;
        BlockSchematic? schem = GetShelterSchematic();
        if (schem == null) return;

        lightPositions = new Dictionary<BlockPos, int>();
        var lightIds = new Dictionary<int, int>();   // schematic block id -> world block id
        foreach (var kv in schem.BlockCodes)
        {
            Block? b = kv.Value == null ? null : sapi.World.GetBlock(kv.Value);
            if (b != null && b.LightHsv[2] > 0) lightIds[kv.Key] = b.BlockId;
        }
        if (lightIds.Count == 0) return;

        for (int i = 0; i < schem.BlockIds.Count; i++)
        {
            if (!lightIds.ContainsKey(schem.BlockIds[i])) continue;
            uint idx = schem.Indices[i];
            int rx = (int)(idx & 0x3FF), rz = (int)((idx >> 10) & 0x3FF), ry = (int)((idx >> 20) & 0x3FF);
            lightPositions[new BlockPos(geo.ShelterOriginX + rx, geo.FloorY + ry, geo.ShelterOriginZ + rz, PocketDimId)] = 0;
        }
    }

    // Re-trigger a relight at each static light source by exchanging the block with itself. ExchangeBlock
    // preserves the block entity and goes through the engine's block-change relight+sync path — the SAME
    // path a player placing/removing a torch uses, which DOES propagate light to remote clients (unlike
    // WorldManager.FullRelight, which silently failed on a dedicated server). Cheap: only the handful of
    // light blocks, no full-chunk resends.
    private void RelightLights()
    {
        if (sapi == null || PocketDimId < 0) return;
        BuildLightIndex();
        if (lightPositions == null || lightPositions.Count == 0) return;

        IBlockAccessor ba = sapi.World.BlockAccessor;
        foreach (BlockPos pos in lightPositions.Keys)
        {
            Block cur = ba.GetBlock(pos);
            if (cur != null && cur.BlockId != 0) ba.ExchangeBlock(cur.BlockId, pos);
        }
    }

    // Force-resend the footprint chunk columns to every player currently in the pocket. The bulk
    // accessor's own sync is unreliable in this dimension (same family as the relight issue), so
    // this guarantees the freshly-placed shelter actually shows up client-side.
    private void ResendPocketChunks()
    {
        if (sapi == null || PocketDimId < 0) return;
        int minCX = FloorDiv(geo.ShellMinX, 32), maxCX = FloorDiv(geo.ShellMaxX, 32);
        int minCZ = FloorDiv(geo.ShellMinZ, 32), maxCZ = FloorDiv(geo.ShellMaxZ, 32);
        foreach (IPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || sp.Entity == null) continue;
            if (DimFromInternalY(sp.Entity.Pos.InternalY) != PocketDimId) continue;
            for (int cx = minCX; cx <= maxCX; cx++)
            for (int cz = minCZ; cz <= maxCZ; cz++)
                sapi.WorldManager.ForceSendChunkColumn(sp, cx, cz, PocketDimId);
        }
    }

    // Entry requires the player to be holding the components item in hand — a key, never consumed.
    private bool HasComponentsKey(IServerPlayer player)
    {
        AssetLocation? code = player.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code;
        return code != null && code.Domain == Domain && code.Path == "securesheltercomponents";
    }

    private BlockSchematic? GetShelterSchematic()
    {
        if (shelterSchematic != null) return shelterSchematic;

        // "schematics" is not a scanned asset category, so Assets.TryGet won't find it. Try it
        // anyway (cheap), then fall back to reading the file straight out of the mod folder/zip.
        string? json = sapi!.Assets.TryGet(shelterAsset, true)?.ToText() ?? ReadShelterFromModFile();
        if (json == null)
        {
            Mod.Logger.Warning("[SecureShelter] shelter schematic could not be loaded (asset + mod file both missing).");
            return null;
        }

        string err = "";
        shelterSchematic = BlockSchematic.LoadFromString(json, ref err);
        if (shelterSchematic == null)
            Mod.Logger.Warning("[SecureShelter] shelter LoadFromString failed: {0}", err);
        else
            StripBannedBlocks(shelterSchematic);
        return shelterSchematic;
    }

    // Delete banned blocks (e.g. the crash-prone cabinet) from a freshly loaded schematic, along with
    // their block-entities and any decor, so they're never placed in the pocket — regardless of what
    // ends up in a re-exported shelter.
    private void StripBannedBlocks(BlockSchematic schem)
    {
        if (SecureShelterConfig.BannedBlockPrefixes == null || SecureShelterConfig.BannedBlockPrefixes.Length == 0) return;

        var bannedIds = new HashSet<int>();
        foreach (var kv in schem.BlockCodes)
            if (MatchesAnyPrefix(kv.Value?.Path ?? "", SecureShelterConfig.BannedBlockPrefixes)) bannedIds.Add(kv.Key);
        if (bannedIds.Count == 0) return;

        var removed = new HashSet<uint>();
        var ni = new List<uint>(schem.Indices.Count);
        var nb = new List<int>(schem.BlockIds.Count);
        for (int i = 0; i < schem.Indices.Count; i++)
        {
            if (bannedIds.Contains(schem.BlockIds[i])) removed.Add(schem.Indices[i]);
            else { ni.Add(schem.Indices[i]); nb.Add(schem.BlockIds[i]); }
        }
        schem.Indices = ni;
        schem.BlockIds = nb;

        foreach (uint p in removed) schem.BlockEntities.Remove(p);

        if (schem.DecorIndices != null && schem.DecorIds != null &&
            schem.DecorIndices.Count == schem.DecorIds.Count)
        {
            var di = new List<uint>();
            var dd = new List<long>();
            for (int i = 0; i < schem.DecorIndices.Count; i++)
                if (!removed.Contains(schem.DecorIndices[i])) { di.Add(schem.DecorIndices[i]); dd.Add(schem.DecorIds[i]); }
            schem.DecorIndices = di;
            schem.DecorIds = dd;
        }

        Mod.Logger.Notification("[SecureShelter] Stripped {0} banned block(s) from the shelter schematic.", removed.Count);
    }

    // Read the schematic directly from this mod's source (folder or packed zip), bypassing the
    // asset manager — which doesn't index the "schematics" category.
    private string? ReadShelterFromModFile()
    {
        try
        {
            string src = Mod.SourcePath;
            string rel = "assets/" + Domain + "/" + SecureShelterConfig.ShelterSchematicPath;

            if (Directory.Exists(src))   // mod is an unpacked folder
            {
                string p = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(p) ? File.ReadAllText(p) : null;
            }
            if (File.Exists(src))        // mod is a packed .zip
            {
                using ZipArchive zip = ZipFile.OpenRead(src);
                // Match the entry regardless of path-separator style: some zip tools (e.g. PowerShell's
                // Compress-Archive) write backslash separators, which a forward-slash GetEntry misses.
                ZipArchiveEntry? entry = zip.GetEntry(rel);
                if (entry == null)
                    foreach (ZipArchiveEntry e in zip.Entries)
                        if (e.FullName.Replace('\\', '/').Equals(rel, StringComparison.OrdinalIgnoreCase))
                        { entry = e; break; }

                if (entry == null)
                {
                    Mod.Logger.Warning("[SecureShelter] shelter entry '{0}' not found in mod zip '{1}'.", rel, src);
                    return null;
                }
                using Stream s = entry.Open();
                using StreamReader r = new(s);
                return r.ReadToEnd();
            }
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("[SecureShelter] reading shelter from mod file failed: {0}", e.Message);
        }
        return null;
    }

    private void RegisterPocketDimension(ICoreServerAPI sapi)
    {
        IDimension dim;
        try
        {
            dim = Manifold!.Registry
                .Define(geo.DimCode)
                .Persistent()
                .WithWorldgen(new PocketShellWorldgen(geo))
                .WithFixedSpawn(new BlockPos(geo.SpawnX, geo.SpawnY, geo.SpawnZ, 0))
                .WithSpawnBehavior(SpawnBehavior.DimensionSpawn)
                // Radius derived from the footprint so the whole cube generates, even for large shelters.
                .WithGenerationRadius(geo.GenerationRadius)
                .WithRelightHeight(geo.RelightHeight)
                // The pocket keeps its own hotbar + backpack: on entry the player's overworld held items
                // and backpacks are snapshotted to player save and the (initially empty) pocket set loads,
                // so they arrive carrying only worn armour/clothing; on exit everything is restored. The
                // snapshots live in player moddata ("manifold:inv"), so this survives disconnects mid-pocket.
                // Character (worn equipment) is deliberately left shared — armour/clothes stay on the player.
                .WithSeparateInventory(ManifoldInventory.Hotbar | ManifoldInventory.Backpack)
                .RegisterStatic();
        }
        catch (Manifold.Api.DimensionAlreadyRegisteredException)
        {
            // Dimension already registered (e.g., from a previous boot or migration). Fetch the existing one.
            var existing = Manifold!.Registry.Get(geo.DimCode);
            if (existing == null)
                throw new InvalidOperationException($"Dimension {geo.DimCode} is registered but cannot be retrieved.");
            dim = existing;
        }

        PocketDimId = dim.InternalId;
        Mod.Logger.Notification(
            "[SecureShelter] Pocket dimension '{0}' ready (engine id {1}).", dim.Code, PocketDimId);
    }

    private void RegisterDebugCommand(ICoreServerAPI sapi)
    {
        // /pocket — enter, recording the player's current spot as the return origin.
        var pocket = sapi.ChatCommands.Create("pocket")
            .WithDescription("Teleport into the Secure Shelter pocket dimension.")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (player.Entity.WatchedAttributes.GetBool("secureshelter:inPocket"))
                    return TextCommandResult.Error("You are already in the pocket.");

                EnterPocket(player, player.Entity.Pos.AsBlockPos.Copy());
                return TextCommandResult.Success("Entering the pocket...");
            });

        // /pocket rebuild — force (re)placement of the shelter interior.
        pocket.BeginSubCommand("rebuild")
            .WithDescription("Force re-stamp the shelter interior (debug).")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (DimFromInternalY(player.Entity.Pos.InternalY) != PocketDimId)
                    return TextCommandResult.Error("You are not in the pocket dimension.");

                int placed = EnsureShelterPlaced(force: true);
                return TextCommandResult.Success($"Shelter rebuild: {placed} blocks placed.");
            })
            .EndSubCommand();

        // /pocket relight — force a full interior relight + resend (fixes dark static lights, esp. on
        // a dedicated server where the first-entry resend didn't carry the light update).
        pocket.BeginSubCommand("relight")
            .WithDescription("Force a full relight of the shelter interior and resend to clients (debug).")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (DimFromInternalY(player.Entity.Pos.InternalY) != PocketDimId)
                    return TextCommandResult.Error("You are not in the pocket dimension.");

                RelightLights();
                return TextCommandResult.Success("Relit the shelter light sources.");
            })
            .EndSubCommand();

        // /back — return to the world.
        sapi.ChatCommands.Create("back")
            .WithDescription("Return to the world from the SecureShelter pocket dimension.")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (!player.Entity.WatchedAttributes.GetBool("secureshelter:inPocket"))
                    return TextCommandResult.Error("You are not in the pocket dimension.");

                ReturnFromPocket(player);
                return TextCommandResult.Success("Returning to the world...");
            });
    }

    // ── Plane-crossing detection (the return arch) ───────────────────────────
    private const double SampleStep = 0.25;
    private const double MaxScanDist = 8.0;

    private void OnServerTick(float dt)
    {
        if (Manifold == null || sapi == null) return;

        foreach (IPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || sp.Entity == null) continue;

            EntityPos ep = sp.Entity.Pos;
            Vec3d cur = new(ep.X, ep.InternalY, ep.Z);
            int dim = DimFromInternalY(ep.InternalY);
            string uid = sp.PlayerUID;

            if (prevState.TryGetValue(uid, out var prev) && prev.dim == dim)
                ScanSegment(sp, prev.pos, cur);
            prevState[uid] = (cur, dim);

            // Sleeping in a bed mounts the player; waking unmounts them. The sleep time-skip can drop
            // the player out of the pocket entirely (waking into empty overworld/void) and fires no
            // login event to restore it — so on waking while still flagged in-pocket, re-run the full
            // relog-style restore. We check the in-pocket flag, NOT the current dimension, precisely
            // because they may have been ejected from it.
            bool mounted = sp.Entity.MountedOn != null;
            if (mounted && !mountedUids.Contains(uid) && dim == PocketDimId)
            {
                // Just lay down in the pocket — remember the bed spot so we wake them right there.
                int bly = (int)Math.Floor(ep.InternalY) - PocketDimId * BlockPos.DimensionBoundary;
                sleepReturnPos[uid] = new BlockPos((int)Math.Floor(ep.X), bly, (int)Math.Floor(ep.Z), PocketDimId);
            }
            if (mountedUids.Contains(uid) && !mounted &&
                sp.Entity.WatchedAttributes.GetBool("secureshelter:inPocket"))
            {
                Mod.Logger.Notification("[SecureShelter] uid={0} woke in pocket — restoring.", uid);
                sapi.World.RegisterCallback(_ => RestoreAfterWake(sp), 300);
            }
            if (mounted) mountedUids.Add(uid); else mountedUids.Remove(uid);
        }
    }

    // a and b carry INTERNAL Y. Looks for the player crossing the arch hole inside the pocket.
    private void ScanSegment(IServerPlayer sp, Vec3d a, Vec3d b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        double horiz = Math.Sqrt(dx * dx + dz * dz);
        if (horiz > MaxScanDist) return;

        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(horiz, Math.Abs(dy)) / SampleStep));
        steps = Math.Min(steps, 64);

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;

            int internalY = (int)Math.Floor(a.Y + dy * t + 0.5);
            int dim = internalY / BlockPos.DimensionBoundary;
            int localY = internalY - dim * BlockPos.DimensionBoundary;
            int x = (int)Math.Floor(a.X + dx * t);
            int z = (int)Math.Floor(a.Z + dz * t);

            if (dim == PocketDimId && IsArchHole(x, localY, z))
            {
                OnArchCrossed(sp);
                return;
            }
        }
    }

    // ArchBaseY is the doorway FLOOR tile (the marker's cell, refilled with floor); the player walks
    // through the 2-tall opening directly above it.
    private bool IsArchHole(int x, int y, int z)
        => x == geo.ArchCenterX && z == geo.ArchZ && (y == geo.ArchBaseY + 1 || y == geo.ArchBaseY + 2);

    private void OnArchCrossed(IServerPlayer player)
    {
        if (Manifold == null || sapi == null || player.Entity == null) return;
        if (!player.Entity.WatchedAttributes.GetBool("secureshelter:inPocket")) return;

        string uid = player.PlayerUID;
        long now = sapi.World.ElapsedMilliseconds;

        bool samePass = lastInsideMs.TryGetValue(uid, out long last) && (now - last) < ContinuousPassGapMs;
        lastInsideMs[uid] = now;
        if (samePass)
        {
            if (triggeredThisPass.Contains(uid)) return;
        }
        else
        {
            triggeredThisPass.Remove(uid);
        }
        triggeredThisPass.Add(uid);

        ReturnFromPocket(player);
    }

    // ── Entry / return ───────────────────────────────────────────────────────

    /// <summary>Called by the painting block when a player right-clicks it.</summary>
    public void EnterPocketViaPainting(IServerPlayer player, BlockPos paintingPos, BlockFacing facing)
    {
        if (Manifold == null || player.Entity == null) return;
        if (player.Entity.WatchedAttributes.GetBool("secureshelter:inPocket")) return;

        // Entry requires the components item held in hand as a key (not consumed).
        if (!HasComponentsKey(player))
        {
            sapi!.SendMessage(player, 0,
                "You need the Secure Shelter components to step through the painting.", EnumChatType.Notification);
            return;
        }

        // Record where the player is standing so the return drops them back exactly where they left.
        EnterPocket(player, player.Entity.Pos.AsBlockPos.Copy());
    }

    private void EnterPocket(IServerPlayer player, BlockPos origin)
    {
        var wa = player.Entity.WatchedAttributes;
        wa.SetInt("secureshelter:returnX", origin.X);
        wa.SetInt("secureshelter:returnY", origin.Y);
        wa.SetInt("secureshelter:returnZ", origin.Z);
        wa.SetBool("secureshelter:inPocket", true);

        Mod.Logger.Notification("[SecureShelter] EnterPocket uid={0} origin=({1},{2},{3})",
            player.PlayerUID, origin.X, origin.Y, origin.Z);

        // DimensionSpawn lands the player at the fixed spawn. Manifold force-sends the chunks; the
        // repair pass places the shelter interior (first time).
        Manifold!.Transitions.TeleportPlayer(player, PocketDimCode, new TransitionOptions());
        SchedulePocketRepair();
        // Raise the player out of the terrain if the spawn landed inside it (after the shelter is placed).
        sapi!.World.RegisterCallback(_ => LiftToAir(player), 4000);
        sapi.SendMessage(player, 0, "You step through into the pocket.", EnumChatType.Notification);
    }

    private void ReturnFromPocket(IServerPlayer player)
    {
        var wa = player.Entity.WatchedAttributes;
        wa.SetBool("secureshelter:inPocket", false);

        int rx = wa.GetInt("secureshelter:returnX");
        int ry = wa.GetInt("secureshelter:returnY");
        int rz = wa.GetInt("secureshelter:returnZ");
        bool hasOrigin = !(rx == 0 && ry == 0 && rz == 0);

        Mod.Logger.Notification("[SecureShelter] ReturnFromPocket uid={0} origin=({1},{2},{3}) hasOrigin={4}",
            player.PlayerUID, rx, ry, rz, hasOrigin);

        // Step 1: ensure the player is in the overworld dimension (loads chunks). Also recovers the
        // relog case where the engine has lost the player's dimension.
        Manifold!.Transitions.TeleportPlayer(player, OverworldCode, new TransitionOptions());

        if (!hasOrigin)
        {
            sapi!.SendMessage(player, 0, "You step back into the world.", EnumChatType.Notification);
            return;
        }

        // Step 2: place the player back exactly where they entered from. Explicit (not via
        // OverridePosition) because the transit is a no-op when the engine already believes the
        // player is in the overworld (post-relog), in which case OverridePosition is ignored.
        double lx = rx + 0.5, ly = ry, lz = rz + 0.5;
        sapi!.World.RegisterCallback(_ =>
        {
            if (player.Entity == null) return;
            player.Entity.TeleportToDouble(lx, ly, lz);
        }, 300);

        sapi.SendMessage(player, 0, "You step back through the arch.", EnumChatType.Notification);
    }
}
