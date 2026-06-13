using Manifold.Api.Worldgen;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Portals;

/// <summary>
/// Builds the pocket dimension as a hollow 40×40×40 shell of mantle blocks wrapped in an opaque
/// 42×42×42 dirt box (so light can't leak through the non-absorbing mantle). The interior is left
/// empty here; the bosco forest is stamped in at runtime (see PortalsModSystem.EnsureBoscoPlaced),
/// and the entry portal (the Storditi's hut painting) lives in the overworld.
/// </summary>
public sealed class PocketShellWorldgen : IWorldgenStrategy
{
    private int mantleBlockId;
    private int dirtBlockId;

    public void OnInitialize(IWorldgenInitContext ctx)
    {
        // IWorldgenInitContext exposes Api (not a BlockAccessor) — resolve via the world.
        mantleBlockId = ctx.Api.World.GetBlock(new AssetLocation("game", "mantle"))?.Id ?? 0;
        if (mantleBlockId == 0)
            mantleBlockId = ctx.Api.World.GetBlock(new AssetLocation("game", "rock-granite"))?.Id ?? 0;

        Block? dirt = ctx.Api.World.GetBlock(new AssetLocation("game", "soil-medium-none"))
                   ?? ctx.Api.World.GetBlock(new AssetLocation("game", "soil-low-none"))
                   ?? ctx.Api.World.GetBlock(new AssetLocation("game", "rock-granite"));
        dirtBlockId = dirt?.Id ?? mantleBlockId;
    }

    public void GenerateColumn(IWorldgenChunkContext ctx)
    {
        int dim = ctx.DimensionId;
        int baseX = ctx.ChunkX * 32;
        int baseZ = ctx.ChunkZ * 32;

        for (int lx = 0; lx < 32; lx++)
        {
            int wx = baseX + lx;
            if (wx < PortalsModSystem.DirtMin || wx > PortalsModSystem.DirtMax) continue;

            for (int lz = 0; lz < 32; lz++)
            {
                int wz = baseZ + lz;
                if (wz < PortalsModSystem.DirtMin || wz > PortalsModSystem.DirtMax) continue;

                // Opaque dirt wrapper (42³) over the whole footprint.
                BuildDirtColumn(ctx, dim, wx, wz);

                // Mantle shell only inside the inner 40³ footprint.
                if (wx >= PortalsModSystem.ShellMin && wx <= PortalsModSystem.ShellMax &&
                    wz >= PortalsModSystem.ShellMin && wz <= PortalsModSystem.ShellMax)
                {
                    BuildShellColumn(ctx, dim, wx, wz);
                }
            }
        }
    }

    // The six faces of the outer 42×42×42 dirt box — one block out from the mantle on every side —
    // sealing the room so light can't leak through the (non-absorbing) mantle.
    private void BuildDirtColumn(IWorldgenChunkContext ctx, int dim, int wx, int wz)
    {
        for (int wy = PortalsModSystem.DirtBottomY; wy <= PortalsModSystem.DirtTopY; wy++)
        {
            bool face =
                wx == PortalsModSystem.DirtMin || wx == PortalsModSystem.DirtMax ||
                wz == PortalsModSystem.DirtMin || wz == PortalsModSystem.DirtMax ||
                wy == PortalsModSystem.DirtBottomY || wy == PortalsModSystem.DirtTopY;

            if (face)
                ctx.BlockAccessor.SetBlock(dirtBlockId, new BlockPos(wx, wy, wz, dim));
        }
    }

    // The six faces of the hollow cube: blocks on any outer x/z wall or the floor/ceiling.
    private void BuildShellColumn(IWorldgenChunkContext ctx, int dim, int wx, int wz)
    {
        for (int wy = PortalsModSystem.FloorY; wy <= PortalsModSystem.ShellTopY; wy++)
        {
            bool shell =
                wx == PortalsModSystem.ShellMin || wx == PortalsModSystem.ShellMax ||
                wz == PortalsModSystem.ShellMin || wz == PortalsModSystem.ShellMax ||
                wy == PortalsModSystem.FloorY   || wy == PortalsModSystem.ShellTopY;

            if (shell)
                ctx.BlockAccessor.SetBlock(mantleBlockId, new BlockPos(wx, wy, wz, dim));
        }
    }
}
