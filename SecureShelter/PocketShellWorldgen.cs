using Manifold.Api.Worldgen;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SecureShelter;

/// <summary>
/// Builds the pocket dimension as a hollow shell of "bedrock" (mantle) wrapped in an opaque dirt
/// box (mantle absorbs no light, so light would otherwise leak straight through). The shell is
/// sized to whatever the bosco schematic measures — see <see cref="PocketGeometry"/>. The interior
/// is left empty here; the bosco forest is stamped in at runtime (see
/// <see cref="SecureShelterModSystem.EnsureBoscoPlaced"/>), and the entry portal (the painting) lives in
/// the overworld.
/// </summary>
public sealed class PocketShellWorldgen : IWorldgenStrategy
{
    private readonly PocketGeometry geo;
    private int shellBlockId;
    private int wrapperBlockId;

    public PocketShellWorldgen(PocketGeometry geo)
    {
        this.geo = geo;
    }

    public void OnInitialize(IWorldgenInitContext ctx)
    {
        // IWorldgenInitContext exposes Api (not a BlockAccessor) — resolve via the world.
        shellBlockId = Resolve(ctx, geo.ShellBlockCode);
        if (shellBlockId == 0) shellBlockId = Resolve(ctx, geo.ShellBlockFallback);

        wrapperBlockId = Resolve(ctx, geo.WrapperBlockCode);
        if (wrapperBlockId == 0) wrapperBlockId = shellBlockId;
    }

    private static int Resolve(IWorldgenInitContext ctx, string code)
        => string.IsNullOrEmpty(code) ? 0 : ctx.Api.World.GetBlock(new AssetLocation(code))?.Id ?? 0;

    public void GenerateColumn(IWorldgenChunkContext ctx)
    {
        int dim = ctx.DimensionId;
        int baseX = ctx.ChunkX * 32;
        int baseZ = ctx.ChunkZ * 32;

        for (int lx = 0; lx < 32; lx++)
        {
            int wx = baseX + lx;
            if (wx < geo.DirtMinX || wx > geo.DirtMaxX) continue;

            for (int lz = 0; lz < 32; lz++)
            {
                int wz = baseZ + lz;
                if (wz < geo.DirtMinZ || wz > geo.DirtMaxZ) continue;

                // Opaque dirt wrapper over the whole footprint.
                BuildWrapperColumn(ctx, dim, wx, wz);

                // Mantle shell only inside the inner (bosco-sized) footprint.
                if (wx >= geo.ShellMinX && wx <= geo.ShellMaxX &&
                    wz >= geo.ShellMinZ && wz <= geo.ShellMaxZ)
                {
                    BuildShellColumn(ctx, dim, wx, wz);
                }
            }
        }
    }

    // The six faces of the outer dirt box — one wrapper-thickness out from the mantle on every side —
    // sealing the room so light can't leak through the (non-absorbing) mantle.
    private void BuildWrapperColumn(IWorldgenChunkContext ctx, int dim, int wx, int wz)
    {
        for (int wy = geo.DirtBottomY; wy <= geo.DirtTopY; wy++)
        {
            bool face =
                wx == geo.DirtMinX || wx == geo.DirtMaxX ||
                wz == geo.DirtMinZ || wz == geo.DirtMaxZ ||
                wy == geo.DirtBottomY || wy == geo.DirtTopY;

            if (face)
                ctx.BlockAccessor.SetBlock(wrapperBlockId, new BlockPos(wx, wy, wz, dim));
        }
    }

    // The six faces of the hollow cube: blocks on any outer x/z wall or the floor/ceiling.
    private void BuildShellColumn(IWorldgenChunkContext ctx, int dim, int wx, int wz)
    {
        for (int wy = geo.FloorY; wy <= geo.ShellTopY; wy++)
        {
            bool shell =
                wx == geo.ShellMinX || wx == geo.ShellMaxX ||
                wz == geo.ShellMinZ || wz == geo.ShellMaxZ ||
                wy == geo.FloorY    || wy == geo.ShellTopY;

            if (shell)
                ctx.BlockAccessor.SetBlock(shellBlockId, new BlockPos(wx, wy, wz, dim));
        }
    }
}
