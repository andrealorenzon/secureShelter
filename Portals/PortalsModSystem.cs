using System;
using System.Collections.Generic;
using Manifold.Api;
using Manifold.Api.Server;
using Manifold.Api.Transitions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Portals;

public class PortalsModSystem : ModSystem
{
    // ── Identity / geometry constants (shared with worldgen + the door block) ────
    public const string Domain = "portals";

    public static readonly AssetLocation PocketDimCode = new(Domain, "pocket");
    public static readonly AssetLocation OverworldCode = new("manifold", "overworld");

    // Pocket platform layout (dimension-local coordinates). A flat slab centered on
    // (PlatformCenterX, PlatformCenterZ) with its top surface walkable at FloorY+1.
    public const int FloorY = 64;
    public const int PlatformCenterX = 8;
    public const int PlatformCenterZ = 8;
    public const int PlatformHalf = 7;            // → 15×15 slab (half*2+1)

    // Fixed spawn: standing on top of the slab. The 4th arg (dimension) is 0 here —
    // Manifold rebases the spawn into the pocket dimension on registration.
    public static readonly BlockPos PocketSpawn =
        new(PlatformCenterX, FloorY + 1, PlatformCenterZ, 0);

    /// <summary>Server-side Manifold facade, resolved in <see cref="StartServerSide"/>.</summary>
    public IManifoldServer? Manifold { get; private set; }

    /// <summary>Engine dimension id assigned to the pocket dimension, or -1 if unavailable.</summary>
    public int PocketDimId { get; private set; } = -1;

    private ICoreServerAPI? sapi;

    // Walk-through debounce. The detection scan can report a player inside a doorway on
    // many consecutive ticks, so we collapse one continuous pass into a single transit.
    // Consecutive detections (gap < ContinuousPassGapMs) are the same pass; a longer gap
    // means the player left the doorway, re-arming the next crossing.
    private const long ContinuousPassGapMs = 300;
    private readonly Dictionary<string, long> lastInsideMs = new();
    private readonly HashSet<string> triggeredThisPass = new();

    // Per-player previous feet position + dimension, for the plane-crossing scan.
    private readonly Dictionary<string, (Vec3d pos, int dim)> prevState = new();

    // Consumer mods must load after Manifold's own order (0.05).
    public override double ExecuteOrder() => 0.5;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        api.RegisterBlockClass("portals.pocketdoor", typeof(BlockPocketDoor));
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        base.StartServerSide(sapi);
        this.sapi = sapi;

        // Owner-scoped facade: passing `this` tags any dimensions we register with our mod id.
        Manifold = sapi.GetManifoldServer(this);
        if (!Manifold.IsHealthy)
        {
            Mod.Logger.Warning("[Portals] Manifold is unhealthy; dimension features disabled.");
            Manifold = null;
            return;
        }

        RegisterPocketDimension(sapi);
        RegisterDebugCommand(sapi);

        // Detect door crossings ourselves every tick (see OnServerTick) rather than via
        // Block.OnEntityInside, which samples only the feet block and intermittently
        // misses fast or boundary-straddling walk-throughs.
        sapi.Event.RegisterGameTickListener(OnServerTick, 20);

        sapi.Event.PlayerDisconnect += p =>
        {
            string uid = p.PlayerUID;
            prevState.Remove(uid);
            lastInsideMs.Remove(uid);
            triggeredThisPass.Remove(uid);
        };

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;
    }

    // The engine does not reliably restore a player's dimension on relog, so we never ask
    // it where the player is — we track that ourselves in the persistent "portals:inPocket"
    // watched attribute (set on entry, cleared on return). A login is also not a transit, so
    // Manifold never loads the pocket's chunks on its own: a player who logged out in the
    // pocket would otherwise spawn into empty void.
    //
    // Recipe (mirrors the shipped Personal Pocket Dimension mod — see docs/manifold.md §11):
    //   1. Load the platform's chunk footprint ourselves via WorldManager (no transit needed).
    //   2. Transit the player onto the spawn with an explicit OverridePosition. With the chunks
    //      already loaded this is reliable; if the engine actually kept them in the pocket the
    //      transit is a harmless no-op (they were already standing on the now-loaded platform).
    //   3. Re-stamp the return door after the chunk settles (+500ms, +2000ms) so it can't come
    //      back client-desynced and inert — the bug that previously stranded players on relog.
    // They stay in the pocket; the return door and /back still work to leave.
    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (Manifold == null || player.Entity == null) return;
        if (!player.Entity.WatchedAttributes.GetBool("portals:inPocket")) return;

        // Longer delay so the login handshake fully completes before we transit. Transiting too
        // early races chunk delivery and was the cause of the client-side "inert door" desync;
        // 1500 ms reliably clears it. (Diagnostics confirmed the engine restores the player into
        // the pocket dimension correctly on login, so this transit lands them back on the spawn
        // with the chunk freshly sent, and the repair pass re-syncs the door.)
        sapi!.World.RegisterCallback(_ =>
        {
            if (player.Entity == null) return;
            if (!player.Entity.WatchedAttributes.GetBool("portals:inPocket")) return;

            EnsurePocketChunksLoaded();
            Manifold.Transitions.TeleportPlayer(
                player, PocketDimCode, new TransitionOptions { OverridePosition = PocketSpawnPos() });
            SchedulePocketRepair();
            Mod.Logger.Notification(
                "[Portals] Restored uid={0} into pocket on login.", player.PlayerUID);
        }, 1500);
    }

    // ── Chunk loading + fixture repair (the relog/desync hardening) ───────────
    // Manifold force-sends a dimension's chunks only during a transit, so we load the pocket's
    // footprint ourselves whenever we need it present without one (e.g. on login). Generating a
    // missing column also re-runs PocketPlatformWorldgen, rebuilding the slab + return door.

    private BlockPos PocketSpawnPos() => new(PlatformCenterX, FloorY + 1, PlatformCenterZ, PocketDimId);
    private BlockPos ReturnDoorPos() => new(PlatformCenterX, FloorY + 1, PlatformCenterZ - PlatformHalf, PocketDimId);

    private void EnsurePocketChunksLoaded()
    {
        if (sapi == null || PocketDimId < 0) return;

        int centerChunkX = PlatformCenterX / 32;
        int centerChunkZ = PlatformCenterZ / 32;

        // LOAD ONLY — never Create. The platform columns were generated by Manifold on first
        // entry and are saved (persistent dimension); LoadChunkColumnForDimension brings those
        // saved columns back into memory with the slab intact. CreateChunkColumnForDimension, by
        // contrast, makes a fresh BLANK, unlit column (Manifold's worldgen only runs during a
        // transit, not for manual creates) and overwrites the saved platform — which is what
        // wiped the slab and killed block-light. So we only ever load here.
        for (int cx = centerChunkX - 1; cx <= centerChunkX + 1; cx++)
        for (int cz = centerChunkZ - 1; cz <= centerChunkZ + 1; cz++)
        {
            sapi.WorldManager.LoadChunkColumnForDimension(cx, cz, PocketDimId);
        }
    }

    // Schedule two repair passes after a transit into the pocket: one once the chunk has had a
    // moment to settle, one well after, matching the shipped PPD mod's +500/+2000ms cadence.
    private void SchedulePocketRepair()
    {
        if (sapi == null) return;
        sapi.World.RegisterCallback(_ => RepairPocketFixtures(), 500);
        sapi.World.RegisterCallback(_ => RepairPocketFixtures(), 2000);
    }

    // Rebuild the platform (self-healing) and force the return door to resync.
    //   • StampPlatform re-lays the slab + frame + door with a RELIGHTING bulk accessor, which
    //     both restores any column a prior blank-create corrupted AND recomputes block light so
    //     placed light sources work again.
    //   • A bare MarkBlockDirty on the door forces a client resend even when the block is
    //     unchanged — the cure for the "inert door" (no sound / no toggle / no walk-through) desync.
    private void RepairPocketFixtures()
    {
        if (sapi == null || PocketDimId < 0) return;

        StampPlatform();

        BlockPos pos = ReturnDoorPos();
        sapi.World.BlockAccessor.MarkBlockModified(pos);
        sapi.World.BlockAccessor.MarkBlockDirty(pos);
    }

    // Re-lay the whole platform via direct block edits, idempotently. This mirrors what
    // PocketPlatformWorldgen builds, but runs at runtime against an already-loaded chunk so it
    // can heal a slab that was wiped (e.g. by an earlier blank-create) without a world regen.
    // The bulk accessor commits with relight + synchronize, so the surface is re-lit and resent.
    private void StampPlatform()
    {
        if (sapi == null || PocketDimId < 0) return;

        Block? floor = sapi.World.GetBlock(new AssetLocation("game", "rock-granite"));
        Block? door = sapi.World.GetBlock(new AssetLocation(Domain, "pocketdoor-south-closed"));
        Block? torch = sapi.World.GetBlock(new AssetLocation("game", "torch-basic-lit-up"));
        if (floor == null) return;
        int floorId = floor.BlockId;

        int minX = PlatformCenterX - PlatformHalf, maxX = PlatformCenterX + PlatformHalf;
        int minZ = PlatformCenterZ - PlatformHalf, maxZ = PlatformCenterZ + PlatformHalf;
        int doorX = PlatformCenterX, doorZ = minZ, doorY = FloorY + 1;

        IBulkBlockAccessor ba = sapi.World.GetBlockAccessorBulkUpdate(synchronize: true, relight: true);

        for (int wx = minX; wx <= maxX; wx++)
        for (int wz = minZ; wz <= maxZ; wz++)
        {
            // Floor slab.
            ba.SetBlock(floorId, new BlockPos(wx, FloorY, wz, PocketDimId));

            if (wz == doorZ && wx >= doorX - 1 && wx <= doorX + 1)
            {
                var bottom = new BlockPos(wx, doorY, wz, PocketDimId);
                if (wx == doorX)
                {
                    // The free-standing return door. Only (re)place it when actually missing,
                    // so a player's open/closed state survives a relog.
                    if (door != null && sapi.World.BlockAccessor.GetBlock(bottom) is not BlockPocketDoor)
                        ba.SetBlock(door.BlockId, bottom);
                }
                else
                {
                    ba.SetBlock(0, bottom);                                   // clear legacy frame post
                }
                ba.SetBlock(0, new BlockPos(wx, doorY + 1, wz, PocketDimId)); // clear legacy lintel
            }
        }

        // The pocket dimension has no skylight, so light it ourselves: stand a lit torch a couple
        // of blocks in from each corner. Committed through the relighting bulk accessor so block
        // light propagates across the slab. Only placed when the spot is empty, so a player can
        // remove them. Spawn (centre) is left clear.
        if (torch != null)
        {
            foreach (var (cx, cz) in new[] { (minX + 2, minZ + 2), (maxX - 2, minZ + 2),
                                             (minX + 2, maxZ - 2), (maxX - 2, maxZ - 2) })
            {
                var tp = new BlockPos(cx, FloorY + 1, cz, PocketDimId);
                if (sapi.World.BlockAccessor.GetBlock(tp).BlockId == 0)
                    ba.SetBlock(torch.BlockId, tp);
            }
        }

        ba.Commit();
    }

    private void RegisterPocketDimension(ICoreServerAPI sapi)
    {
        IDimension dim = Manifold!.Registry
            .Define(PocketDimCode)
            .Persistent()
            .WithWorldgen(new PocketPlatformWorldgen())
            .WithFixedSpawn(PocketSpawn)
            .WithSpawnBehavior(SpawnBehavior.DimensionSpawn)
            .WithGenerationRadius(1)
            // Relight up to well above the platform. The default (20) is below our
            // FloorY (64), so without this the platform blocks never get a relight
            // pass and the walkable surface stays dark while skylight only reaches
            // the underside.
            .WithRelightHeight(FloorY + 16)
            .RegisterStatic();

        PocketDimId = dim.InternalId;
        Mod.Logger.Notification(
            "[Portals] Pocket dimension '{0}' ready (engine id {1}).", dim.Code, PocketDimId);
    }

    private void RegisterDebugCommand(ICoreServerAPI sapi)
    {
        // /pocket — enter the pocket, recording the player's current spot as the origin so
        // /back (and the return door) can bring them home. Goes through our own EnterPocket
        // so the persistent "in pocket" flag is set, exactly like walking through a door.
        sapi.ChatCommands.Create("pocket")
            .WithDescription("Teleport into the Portals pocket dimension.")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (player.Entity.WatchedAttributes.GetBool("portals:inPocket"))
                    return TextCommandResult.Error("You are already in the pocket.");

                EnterPocket(player, player.Entity.Pos.AsBlockPos.Copy(), BlockFacing.NORTH);
                return TextCommandResult.Success("Entering the pocket...");
            });

        // /back — manually return to the world (origin, or overworld spawn).
        sapi.ChatCommands.Create("back")
            .WithDescription("Return to the world from the Portals pocket dimension.")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (!player.Entity.WatchedAttributes.GetBool("portals:inPocket"))
                    return TextCommandResult.Error("You are not in the pocket dimension.");

                ReturnFromPocket(player);
                return TextCommandResult.Success("Returning to the world...");
            });
    }

    // ── Plane-crossing detection ─────────────────────────────────────────────
    // Each tick, sample the segment a player moved along since last tick and look for an
    // open Pocket Door anywhere on that path. Sampling the path (not just tick endpoints)
    // catches fast movement that would tunnel past a single-block doorway.

    private const double SampleStep = 0.25;   // blocks between samples along the path
    private const double MaxScanDist = 8.0;   // longer moves are treated as teleports, skipped

    // Dimensions are stacked in internal Y bands of BlockPos.DimensionBoundary (32768).
    // EntityPos.Dimension is a derived field that is NOT reliably restored after a relog,
    // so we always derive the dimension from the authoritative InternalY instead.
    private static int DimFromInternalY(double internalY) => (int)(internalY / BlockPos.DimensionBoundary);

    private void OnServerTick(float dt)
    {
        if (Manifold == null || sapi == null) return;
        IBlockAccessor ba = sapi.World.BlockAccessor;

        foreach (IPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || sp.Entity == null) continue;

            EntityPos ep = sp.Entity.Pos;
            // Work in INTERNAL coordinates (Y carries the dimension band) so we never depend
            // on the unreliable Pos.Dimension field.
            Vec3d cur = new(ep.X, ep.InternalY, ep.Z);
            int dim = DimFromInternalY(ep.InternalY);
            string uid = sp.PlayerUID;

            // Only scan within a single uninterrupted same-dimension move; a dimension
            // change means we just teleported them, so reset and skip this tick.
            if (prevState.TryGetValue(uid, out var prev) && prev.dim == dim)
            {
                ScanSegment(sp, prev.pos, cur, ba);
            }
            prevState[uid] = (cur, dim);
        }
    }

    // a and b carry INTERNAL Y (Y includes the dimension band).
    private void ScanSegment(IServerPlayer sp, Vec3d a, Vec3d b, IBlockAccessor ba)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
        double horiz = Math.Sqrt(dx * dx + dz * dz);
        if (horiz > MaxScanDist) return;

        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(horiz, Math.Abs(dy)) / SampleStep));
        steps = Math.Min(steps, 64);

        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;

            // Sample at mid-cell height (+0.5), then split the internal Y back into a
            // dimension id + local Y so GetBlock resolves the door in the right dimension.
            int internalY = (int)Math.Floor(a.Y + dy * t + 0.5);
            int dim = internalY / BlockPos.DimensionBoundary;
            int localY = internalY - dim * BlockPos.DimensionBoundary;

            var bp = new BlockPos(
                (int)Math.Floor(a.X + dx * t),
                localY,
                (int)Math.Floor(a.Z + dz * t),
                dim);

            if (ba.GetBlock(bp) is BlockPocketDoor door && door.IsOpen)
            {
                OnDoorCrossed(sp, bp, door.Facing);
                return; // at most one crossing per tick
            }
        }
    }

    // ── Door crossing → transit ──────────────────────────────────────────────

    public void OnDoorCrossed(IServerPlayer player, BlockPos doorPos, BlockFacing facing)
    {
        if (Manifold == null || sapi == null || player.Entity == null) return;

        string uid = player.PlayerUID;
        long now = sapi.World.ElapsedMilliseconds;

        // Debounce one walk-through into one transit (see field comment).
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

        // We decide entry vs return from our own persistent flag, never the engine's
        // dimension (which isn't restored on relog). In the pocket → go home; otherwise →
        // go in, recording this door as the origin.
        if (player.Entity.WatchedAttributes.GetBool("portals:inPocket"))
        {
            ReturnFromPocket(player);
        }
        else
        {
            EnterPocket(player, doorPos, facing);
        }
    }

    // origin is the world position to come back to (the entry door, or the player's spot
    // for /pocket). Stored in watched attributes, which persist across save/relog.
    private void EnterPocket(IServerPlayer player, BlockPos origin, BlockFacing facing)
    {
        var wa = player.Entity.WatchedAttributes;
        wa.SetInt("portals:returnX", origin.X);
        wa.SetInt("portals:returnY", origin.Y);
        wa.SetInt("portals:returnZ", origin.Z);
        wa.SetInt("portals:returnFacing", facing.Index);
        wa.SetBool("portals:inPocket", true);

        Mod.Logger.Notification("[Portals] EnterPocket uid={0} origin=({1},{2},{3}) facing={4}",
            player.PlayerUID, origin.X, origin.Y, origin.Z, facing.Code);

        // Empty options → the dimension's own SpawnBehavior (DimensionSpawn) applies,
        // landing the player on the platform spawn (centre). Manifold force-sends the chunks
        // as part of this transit, but we still schedule the same post-transit door repair as
        // the relog path so a first-entry door can't arrive client-desynced and inert.
        Manifold!.Transitions.TeleportPlayer(player, PocketDimCode, new TransitionOptions());
        SchedulePocketRepair();
        sapi!.SendMessage(player, 0, "You step through into the pocket.", EnumChatType.Notification);
    }

    private void ReturnFromPocket(IServerPlayer player)
    {
        var wa = player.Entity.WatchedAttributes;
        wa.SetBool("portals:inPocket", false);

        int rx = wa.GetInt("portals:returnX");
        int ry = wa.GetInt("portals:returnY");
        int rz = wa.GetInt("portals:returnZ");
        bool hasOrigin = !(rx == 0 && ry == 0 && rz == 0);

        Mod.Logger.Notification("[Portals] ReturnFromPocket uid={0} origin=({1},{2},{3}) hasOrigin={4}",
            player.PlayerUID, rx, ry, rz, hasOrigin);

        // Step 1: ensure the player is in the overworld dimension (loads chunks). This also
        // recovers the relog case where the engine has lost the player's dimension.
        Manifold!.Transitions.TeleportPlayer(player, OverworldCode, new TransitionOptions());

        if (!hasOrigin)
        {
            sapi!.SendMessage(player, 0, "You step back into the world.", EnumChatType.Notification);
            return;
        }

        // Step 2: place the player exactly, one block in front of the origin. We do this
        // explicitly (not via OverridePosition) because the transit above is a no-op when
        // the engine already believes the player is in the overworld (post-relog), in which
        // case OverridePosition is ignored and the player ends up off-map.
        BlockFacing facing = BlockFacing.ALLFACES[wa.GetInt("portals:returnFacing")];
        double lx = rx + facing.Normali.X + 0.5;
        double ly = ry;
        double lz = rz + facing.Normali.Z + 0.5;

        sapi!.World.RegisterCallback(_ =>
        {
            if (player.Entity == null) return;
            player.Entity.TeleportToDouble(lx, ly, lz);
        }, 300);

        sapi.SendMessage(player, 0, "You step back through the door.", EnumChatType.Notification);
    }
}
