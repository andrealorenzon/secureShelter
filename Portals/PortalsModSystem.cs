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

namespace Portals;

public class PortalsModSystem : ModSystem
{
    // ── Identity ─────────────────────────────────────────────────────────────
    public const string Domain = "portals";

    // Bumped whenever the dimension's baked-in layout changes — Manifold caches generated columns
    // and won't regenerate in place, so a new code forces a fresh dimension.
    // pocket5: bosco interior. pocket6: build lowered to floor y=1 so it sits inside the relight band.
    public static readonly AssetLocation PocketDimCode = new(Domain, "pocket6");
    public static readonly AssetLocation OverworldCode = new("manifold", "overworld");

    // ── Pocket geometry (dimension-local coordinates) ────────────────────────
    // A hollow 40×40×40 shell of mantle: x/z span ShellMin..ShellMax, y spans FloorY..ShellTopY.
    public const int FloorY = 1;                // kept low so the whole build fits the relight band
    public const int ShellMin = 0;
    public const int ShellMax = 39;             // 40 blocks (0..39)
    public const int ShellTopY = FloorY + 39;   // 40 → 40 tall

    // Mantle has no light absorption, so light leaks straight through the shell. We wrap it in a
    // 42×42×42 opaque dirt box (one block out on every side) to seal the interior.
    public const int DirtMin = ShellMin - 1;    // -1
    public const int DirtMax = ShellMax + 1;    // 40  → 42 blocks (-1..40)
    public const int DirtBottomY = FloorY - 1;  // 0
    public const int DirtTopY = ShellTopY + 1;  // 41 → 42 tall

    // Fixed spawn inside the bosco interior, raised 8 m above the floor so the player lands on the
    // forest surface rather than inside the terrain. (4th arg dimension 0; Manifold rebases it.)
    public const int SpawnX = 19;
    public const int SpawnZ = 31;
    public static readonly BlockPos PocketSpawn = new(SpawnX, FloorY + 8, SpawnZ, 0);   // y = 9

    // Return arch: a free-standing 3×3 stone wall with a 1-wide × 2-tall walk-through hole, a few
    // blocks behind the spawn (so the player faces into the forest), at the spawn's standing height.
    public const int ArchCenterX = SpawnX;          // 19
    public const int ArchZ = SpawnZ + 4;            // 35 — behind the spawn
    public const int ArchBaseY = FloorY + 8;        // 9 — floor of the hole, level with the spawn
    // Hole cells: (ArchCenterX, ArchBaseY, ArchZ) and (ArchCenterX, ArchBaseY+1, ArchZ).

    // The pre-built interior (a 40×40×30 forest), placed once into the shell on first entry.
    private static readonly AssetLocation BoscoAsset = new(Domain, "schematics/bosco.json");
    private BlockSchematic? boscoSchematic;

    /// <summary>Server-side Manifold facade, resolved in <see cref="StartServerSide"/>.</summary>
    public IManifoldServer? Manifold { get; private set; }

    /// <summary>Engine dimension id assigned to the pocket dimension, or -1 if unavailable.</summary>
    public int PocketDimId { get; private set; } = -1;

    private ICoreServerAPI? sapi;

    // A throwaway solid block used to force the engine's real relight (see ForceRelight): placing
    // then removing it is what a player does manually with a light to "fix" the lighting.
    private int tempBlockId;

    // Walk-through debounce: the per-tick scan can report a player inside the arch hole on many
    // consecutive ticks, so we collapse one continuous pass into a single transit.
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
        api.RegisterBlockClass("portals.painting", typeof(BlockPaintingPortal));
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

        tempBlockId = sapi.World.GetBlock(new AssetLocation("game", "rock-granite"))?.BlockId ?? 0;

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
        };

        sapi.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        // Manifold doesn't relight block light in a dimension via the FullRelight / bulk-accessor
        // APIs (confirmed: they leave light at 0). The ONLY path that works is the engine's normal
        // block place/break relight. So when a player edits a block in the pocket, we replicate the
        // "place a block then pick it up" fix near them (see ForceRelight).
        sapi.Event.DidPlaceBlock += (player, oldId, blockSel, stack) => RelightAroundPlayer(player);
        sapi.Event.DidBreakBlock += (player, oldId, blockSel) => RelightAroundPlayer(player);
    }

    private static int DimFromInternalY(double internalY) => (int)(internalY / BlockPos.DimensionBoundary);

    // ── Lighting (forced relight; see comments above) ────────────────────────

    // Replicate a player's place+pickup near someone who just edited a block, but only in the pocket.
    private void RelightAroundPlayer(IServerPlayer? player)
    {
        if (sapi == null || PocketDimId < 0 || player?.Entity == null) return;
        if (DimFromInternalY(player.Entity.Pos.InternalY) != PocketDimId) return;
        ForceRelight(PlayerLocalPos(player));
    }

    // Force the engine's real relight by doing exactly what fixes it by hand: place a solid block in
    // an empty cell, then remove it a tick later. Two genuine block changes through the DEFAULT
    // accessor trigger the lighting flood that FullRelight / bulk relight don't. Only blinks empty
    // cells (non-destructive) and keeps block-entity blocks like lanterns untouched.
    private void ForceRelight(BlockPos airCell)
    {
        if (sapi == null || tempBlockId == 0) return;
        IBlockAccessor ba = sapi.World.BlockAccessor;
        if (ba.GetBlock(airCell).BlockId != 0) return;

        ba.SetBlock(tempBlockId, airCell);                              // place (relight #1)
        sapi.World.RegisterCallback(_ =>
        {
            if (sapi.World.BlockAccessor.GetBlock(airCell).BlockId == tempBlockId)
                sapi.World.BlockAccessor.SetBlock(0, airCell);          // remove (relight #2)
        }, 150);
    }

    // Relight the whole interior by doing the manual fix to EVERY light source at once: scan the
    // room, pull out every light-emitting block, then re-place them a tick later. Block-entity
    // blocks (lanterns store their metal/glass there) have their data captured and restored so they
    // come back identical. This is what reliably lights the room on entry / login.
    private void RelightAllSources()
    {
        if (sapi == null || PocketDimId < 0) return;
        IBlockAccessor ba = sapi.World.BlockAccessor;

        var sources = new List<(BlockPos pos, int id, ITreeAttribute? be)>();
        for (int x = ShellMin + 1; x < ShellMax; x++)
        for (int z = ShellMin + 1; z < ShellMax; z++)
        for (int y = FloorY + 1; y < ShellTopY; y++)
        {
            var pos = new BlockPos(x, y, z, PocketDimId);
            Block b = ba.GetBlock(pos);
            if (b.BlockId == 0 || b.LightHsv[2] <= 0) continue;

            ITreeAttribute? tree = null;
            if (b.EntityClass != null && ba.GetBlockEntity(pos) is { } be)
            {
                tree = new TreeAttribute();
                be.ToTreeAttributes(tree);
            }
            sources.Add((pos.Copy(), b.BlockId, tree));
        }

        if (sources.Count == 0) return;

        foreach (var s in sources) ba.SetBlock(0, s.pos);                    // remove all (relight)

        sapi.World.RegisterCallback(_ =>
        {
            foreach (var s in sources)
            {
                ba.SetBlock(s.id, s.pos);                                    // re-place (relight)
                if (s.be != null && ba.GetBlockEntity(s.pos) is { } nbe)
                {
                    nbe.FromTreeAttributes(s.be, sapi.World);
                    nbe.MarkDirty(true);
                }
            }
        }, 150);
    }

    private BlockPos PlayerLocalPos(IServerPlayer player)
    {
        EntityPos ep = player.Entity.Pos;
        int dim = DimFromInternalY(ep.InternalY);
        int ly = (int)Math.Floor(ep.InternalY) - dim * BlockPos.DimensionBoundary;
        return new BlockPos((int)Math.Floor(ep.X), ly + 1, (int)Math.Floor(ep.Z), dim);
    }

    private string LightReport(BlockPos p)
    {
        IBlockAccessor ba = sapi!.World.BlockAccessor;
        int block = ba.GetLightLevel(p, EnumLightLevelType.OnlyBlockLight);
        int sun = ba.GetLightLevel(p, EnumLightLevelType.OnlySunLight);
        int max = ba.GetLightLevel(p, EnumLightLevelType.MaxLight);
        return $"block={block} sun={sun} max={max}";
    }

    // ── Relog handling ───────────────────────────────────────────────────────
    // We track whether a player is in the pocket with the persistent "portals:inPocket" watched
    // attribute. A login is not a transit, so Manifold won't load the pocket's chunks on its own
    // and the player would spawn into void. On login we therefore load the footprint, transit them
    // back onto the spawn, and run the repair pass.
    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (Manifold == null || player.Entity == null) return;
        if (!player.Entity.WatchedAttributes.GetBool("portals:inPocket")) return;

        sapi!.World.RegisterCallback(_ =>
        {
            if (player.Entity == null) return;
            if (!player.Entity.WatchedAttributes.GetBool("portals:inPocket")) return;

            EnsurePocketChunksLoaded();
            Manifold.Transitions.TeleportPlayer(
                player, PocketDimCode, new TransitionOptions { OverridePosition = PocketSpawnPos() });
            SchedulePocketRepair();
            Mod.Logger.Notification("[Portals] Restored uid={0} into pocket on login.", player.PlayerUID);
        }, 1500);
    }

    // ── Chunk loading + interior setup ───────────────────────────────────────
    private BlockPos PocketSpawnPos() => new(SpawnX, FloorY + 8, SpawnZ, PocketDimId);

    private void EnsurePocketChunksLoaded()
    {
        if (sapi == null || PocketDimId < 0) return;

        // LOAD ONLY — never Create. The columns were generated on first entry and are saved
        // (persistent dimension); loading restores them intact.
        int minChunk = ShellMin / 32 - 1;   // -1 margin
        int maxChunk = ShellMax / 32 + 1;   // 40/32 = 1 → up to chunk 2
        for (int cx = minChunk; cx <= maxChunk; cx++)
        for (int cz = minChunk; cz <= maxChunk; cz++)
            sapi.WorldManager.LoadChunkColumnForDimension(cx, cz, PocketDimId);
    }

    // After a transit into the pocket: place the bosco interior (once), stamp the return arch over
    // it, and force a real relight.
    private void SchedulePocketRepair()
    {
        if (sapi == null) return;
        sapi.World.RegisterCallback(_ => { EnsureBoscoPlaced(); StampArch(); }, 2000);
        sapi.World.RegisterCallback(_ => { EnsureBoscoPlaced(); StampArch(); RelightAllSources(); }, 3500);
    }

    // (Re)build the return arch idempotently via a relighting bulk accessor. Stamped after the bosco
    // so it sits on top of the forest; self-heals if a player breaks it, and keeps the hole open.
    private void StampArch()
    {
        if (sapi == null || PocketDimId < 0) return;

        Block? stone = sapi.World.GetBlock(new AssetLocation("game", "stonebricks-granite"))
                    ?? sapi.World.GetBlock(new AssetLocation("game", "rock-granite"));
        if (stone == null) return;
        int stoneId = stone.BlockId;

        IBulkBlockAccessor ba = sapi.World.GetBlockAccessorBulkUpdate(synchronize: true, relight: true);
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = 0; dy <= 2; dy++)
        {
            int wx = ArchCenterX + dx;
            int wy = ArchBaseY + dy;
            bool hole = dx == 0 && dy <= 1;     // centre column, lowest two cells = the doorway
            ba.SetBlock(hole ? 0 : stoneId, new BlockPos(wx, wy, ArchZ, PocketDimId));
        }
        ba.Commit();
    }

    // Stamp the bosco schematic into the shell once per dimension (guarded by a savegame flag),
    // aligned to the cube footprint with its base on the floor. Returns the number of blocks placed.
    private int EnsureBoscoPlaced(bool force = false)
    {
        if (sapi == null || PocketDimId < 0) return 0;

        string key = "portals:boscoPlaced-" + PocketDimCode.Path;
        if (!force && sapi.World.Config.GetBool(key)) return 0;

        BlockSchematic? schem = GetBoscoSchematic();
        if (schem == null) return 0;

        EnsurePocketChunksLoaded();
        var origin = new BlockPos(ShellMin, FloorY, ShellMin, PocketDimId);   // (0, 64, 0)

        int placed;
        try
        {
            IBulkBlockAccessor bulk = sapi.World.GetBlockAccessorBulkUpdate(synchronize: true, relight: false);
            schem.Init(bulk);
            placed = schem.Place(bulk, sapi.World, origin, EnumReplaceMode.ReplaceAllNoAir, replaceMetaBlocks: true);
            bulk.Commit();
            schem.PlaceEntitiesAndBlockEntities(
                sapi.World.BlockAccessor, sapi.World, origin, schem.BlockCodes, schem.ItemCodes);
        }
        catch (Exception e)
        {
            Mod.Logger.Error("[Portals] bosco placement threw: {0}", e);
            return 0;
        }

        Mod.Logger.Notification(
            "[Portals] Bosco placement: {0} blocks at ({1},{2},{3}) dim {4}.",
            placed, origin.X, origin.Y, origin.Z, PocketDimId);

        // 0 placed → the footprint chunks weren't loaded yet; leave the flag unset so a later pass
        // retries. Otherwise mark done and force a dimension-aware resend so the client sees it.
        if (placed <= 0) return 0;
        ResendPocketChunks();
        sapi.World.Config.SetBool(key, true);
        return placed;
    }

    // Force-resend the footprint chunk columns to every player currently in the pocket. The bulk
    // accessor's own sync is unreliable in this dimension (same family as the relight issue), so
    // this guarantees the freshly-placed bosco actually shows up client-side.
    private void ResendPocketChunks()
    {
        if (sapi == null || PocketDimId < 0) return;
        foreach (IPlayer p in sapi.World.AllOnlinePlayers)
        {
            if (p is not IServerPlayer sp || sp.Entity == null) continue;
            if (DimFromInternalY(sp.Entity.Pos.InternalY) != PocketDimId) continue;
            for (int cx = ShellMin / 32; cx <= ShellMax / 32; cx++)
            for (int cz = ShellMin / 32; cz <= ShellMax / 32; cz++)
                sapi.WorldManager.ForceSendChunkColumn(sp, cx, cz, PocketDimId);
        }
    }

    private BlockSchematic? GetBoscoSchematic()
    {
        if (boscoSchematic != null) return boscoSchematic;

        // "schematics" is not a scanned asset category, so Assets.TryGet won't find it. Try it
        // anyway (cheap), then fall back to reading the file straight out of the mod folder/zip.
        string? json = sapi!.Assets.TryGet(BoscoAsset, true)?.ToText() ?? ReadBoscoFromModFile();
        if (json == null)
        {
            Mod.Logger.Warning("[Portals] bosco schematic could not be loaded (asset + mod file both missing).");
            return null;
        }

        string err = "";
        boscoSchematic = BlockSchematic.LoadFromString(json, ref err);
        if (boscoSchematic == null)
            Mod.Logger.Warning("[Portals] bosco LoadFromString failed: {0}", err);
        return boscoSchematic;
    }

    // Read the schematic directly from this mod's source (folder or packed zip), bypassing the
    // asset manager — which doesn't index the "schematics" category.
    private string? ReadBoscoFromModFile()
    {
        try
        {
            string src = Mod.SourcePath;
            string rel = "assets/" + Domain + "/schematics/bosco.json";

            if (Directory.Exists(src))   // mod is an unpacked folder
            {
                string p = Path.Combine(src, rel.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(p) ? File.ReadAllText(p) : null;
            }
            if (File.Exists(src))        // mod is a packed .zip
            {
                using ZipArchive zip = ZipFile.OpenRead(src);
                ZipArchiveEntry? entry = zip.GetEntry(rel);
                if (entry == null) return null;
                using Stream s = entry.Open();
                using StreamReader r = new(s);
                return r.ReadToEnd();
            }
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("[Portals] reading bosco from mod file failed: {0}", e.Message);
        }
        return null;
    }

    private void RegisterPocketDimension(ICoreServerAPI sapi)
    {
        IDimension dim = Manifold!.Registry
            .Define(PocketDimCode)
            .Persistent()
            .WithWorldgen(new PocketShellWorldgen())
            .WithFixedSpawn(PocketSpawn)
            .WithSpawnBehavior(SpawnBehavior.DimensionSpawn)
            // Spawn lands in chunk (0,0); radius 2 covers the whole 2×2-chunk shell with margin.
            .WithGenerationRadius(2)
            .WithRelightHeight(40)
            // The pocket keeps its own hotbar + backpack: on entry the player's overworld held items
            // and backpacks are snapshotted to player save and the (initially empty) pocket set loads,
            // so they arrive carrying only worn armour/clothing; on exit everything is restored. The
            // snapshots live in player moddata ("manifold:inv"), so this survives disconnects mid-pocket.
            // Character (worn equipment) is deliberately left shared — armour/clothes stay on the player.
            .WithSeparateInventory(ManifoldInventory.Hotbar | ManifoldInventory.Backpack)
            .RegisterStatic();

        PocketDimId = dim.InternalId;
        Mod.Logger.Notification(
            "[Portals] Pocket dimension '{0}' ready (engine id {1}).", dim.Code, PocketDimId);
    }

    private void RegisterDebugCommand(ICoreServerAPI sapi)
    {
        // /pocket — enter, recording the player's current spot as the return origin.
        var pocket = sapi.ChatCommands.Create("pocket")
            .WithDescription("Teleport into the Portals pocket dimension.")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (player.Entity.WatchedAttributes.GetBool("portals:inPocket"))
                    return TextCommandResult.Error("You are already in the pocket.");

                EnterPocket(player, player.Entity.Pos.AsBlockPos.Copy());
                return TextCommandResult.Success("Entering the pocket...");
            });

        // /pocket relight — force a relight of the pocket interior and report light levels.
        pocket.BeginSubCommand("relight")
            .WithDescription("Force-relight the pocket interior (debug).")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (DimFromInternalY(player.Entity.Pos.InternalY) != PocketDimId)
                    return TextCommandResult.Error("You are not in the pocket dimension.");

                BlockPos here = PlayerLocalPos(player);
                string before = LightReport(here);

                RelightAllSources();

                sapi.World.RegisterCallback(_ =>
                {
                    string after = LightReport(here);
                    sapi.SendMessage(player, 0,
                        $"[Portals] relight @ {here.X},{here.Y},{here.Z} (dim {PocketDimId})\n  before: {before}\n  after:  {after}",
                        EnumChatType.Notification);
                }, 800);

                return TextCommandResult.Success("Relighting...");
            })
            .EndSubCommand();

        // /pocket rebuild — force (re)placement of the bosco interior, then relight.
        pocket.BeginSubCommand("rebuild")
            .WithDescription("Force re-stamp the bosco interior (debug).")
            .RequiresPrivilege("chat")
            .RequiresPlayer()
            .HandleWith(args =>
            {
                if (args.Caller.Player is not IServerPlayer player || player.Entity == null)
                    return TextCommandResult.Error("No player.");
                if (DimFromInternalY(player.Entity.Pos.InternalY) != PocketDimId)
                    return TextCommandResult.Error("You are not in the pocket dimension.");

                int placed = EnsureBoscoPlaced(force: true);
                sapi.World.RegisterCallback(_ => RelightAllSources(), 500);
                return TextCommandResult.Success($"Bosco rebuild: {placed} blocks placed.");
            })
            .EndSubCommand();

        // /back — return to the world (the way out now that there's no walk-through arch).
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

    private static bool IsArchHole(int x, int y, int z)
        => x == ArchCenterX && z == ArchZ && (y == ArchBaseY || y == ArchBaseY + 1);

    private void OnArchCrossed(IServerPlayer player)
    {
        if (Manifold == null || sapi == null || player.Entity == null) return;
        if (!player.Entity.WatchedAttributes.GetBool("portals:inPocket")) return;

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
        if (player.Entity.WatchedAttributes.GetBool("portals:inPocket")) return;

        // Record where the player is standing so the return drops them back exactly where they left.
        EnterPocket(player, player.Entity.Pos.AsBlockPos.Copy());
    }

    private void EnterPocket(IServerPlayer player, BlockPos origin)
    {
        var wa = player.Entity.WatchedAttributes;
        wa.SetInt("portals:returnX", origin.X);
        wa.SetInt("portals:returnY", origin.Y);
        wa.SetInt("portals:returnZ", origin.Z);
        wa.SetBool("portals:inPocket", true);

        Mod.Logger.Notification("[Portals] EnterPocket uid={0} origin=({1},{2},{3})",
            player.PlayerUID, origin.X, origin.Y, origin.Z);

        // DimensionSpawn lands the player at the fixed spawn. Manifold force-sends the chunks; the
        // repair pass places the bosco interior (first time) and relights.
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
