using Manifold.Api.Worldgen;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Portals;

/// <summary>
/// Builds the pocket dimension as a single flat slab floating in the void, with the
/// return door standing on its north edge facing the spawn. Everything outside the
/// slab footprint is left as air.
/// </summary>
public sealed class PocketPlatformWorldgen : IWorldgenStrategy
{
    private int floorBlockId;
    private int doorBlockId = -1;

    public void OnInitialize(IWorldgenInitContext ctx)
    {
        // Resolve block ids once. NOTE: IWorldgenInitContext exposes Api (not a
        // BlockAccessor), contrary to some docs — resolve via the world accessor.
        Block? floor = ctx.Api.World.GetBlock(new AssetLocation("game", "rock-granite"));
        floorBlockId = floor?.Id ?? 0;

        Block? door = ctx.Api.World.GetBlock(
            new AssetLocation(PortalsModSystem.Domain, "pocketdoor-south-closed"));
        if (door != null)
        {
            doorBlockId = door.Id;
        }
        else
        {
            ctx.Api.Logger.Warning(
                "[Portals] return door 'portals:pocketdoor-south-closed' not found during worldgen init; platform will have no exit.");
        }
    }

    public void GenerateColumn(IWorldgenChunkContext ctx)
    {
        int dim = ctx.DimensionId;
        int baseX = ctx.ChunkX * 32;
        int baseZ = ctx.ChunkZ * 32;

        int minX = PortalsModSystem.PlatformCenterX - PortalsModSystem.PlatformHalf;
        int maxX = PortalsModSystem.PlatformCenterX + PortalsModSystem.PlatformHalf;
        int minZ = PortalsModSystem.PlatformCenterZ - PortalsModSystem.PlatformHalf;
        int maxZ = PortalsModSystem.PlatformCenterZ + PortalsModSystem.PlatformHalf;

        int doorX = PortalsModSystem.PlatformCenterX;
        int doorZ = minZ;                              // north edge
        int doorY = PortalsModSystem.FloorY + 1;

        for (int lx = 0; lx < 32; lx++)
        {
            int wx = baseX + lx;
            if (wx < minX || wx > maxX) continue;

            for (int lz = 0; lz < 32; lz++)
            {
                int wz = baseZ + lz;
                if (wz < minZ || wz > maxZ) continue;

                // Always dimension-encode positions, or the engine writes to dim 0.
                ctx.BlockAccessor.SetBlock(floorBlockId, new BlockPos(wx, PortalsModSystem.FloorY, wz, dim));

                // The return door stands free on the north edge — no stone frame around it.
                if (wz == doorZ && wx == doorX && doorBlockId > 0)
                {
                    ctx.BlockAccessor.SetBlock(doorBlockId, new BlockPos(wx, doorY, wz, dim));
                }
            }
        }
    }
}
