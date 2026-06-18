using System;
using Vintagestory.API.Common;

namespace SecureShelter;

/// <summary>
/// The concrete dimension-local geometry of the pocket, computed once at startup from the
/// <see cref="SecureShelterConfig"/> and the interior schematic's measured size. Everything the worldgen
/// and the runtime need is precomputed here, so the shell is always sized to "whatever dimensions"
/// the bosco schematic has — there are no scattered magic numbers.
/// </summary>
public sealed class PocketGeometry
{
    public AssetLocation DimCode = null!;
    public int FloorY;

    // Inner shell footprint (the unbreakable "bedrock" walls): x in [ShellMinX..ShellMaxX],
    // z in [ShellMinZ..ShellMaxZ], y in [FloorY..ShellTopY]. Coincides with the bosco footprint.
    public int ShellMinX, ShellMaxX, ShellMinZ, ShellMaxZ, ShellTopY;

    // Outer dirt wrapper, WrapperThickness blocks out on every side (seals light leaks).
    public int DirtMinX, DirtMaxX, DirtMinZ, DirtMaxZ, DirtBottomY, DirtTopY;

    public int SpawnX, SpawnY, SpawnZ;
    public int ArchCenterX, ArchZ, ArchBaseY;
    public int RelightHeight;
    public int GenerationRadius;

    // Where the bosco schematic is stamped: centred in the (possibly larger) box footprint.
    public int BoscoOriginX, BoscoOriginZ;

    public string ShellBlockCode = "game:mantle";
    public string ShellBlockFallback = "game:rock-granite";
    public string WrapperBlockCode = "game:soil-medium-none";

    /// <summary>
    /// Builds the geometry from the config and the schematic's measured size. The box footprint is the
    /// larger of the bosco size and <see cref="SecureShelterConfig.MinBoxSize"/> on each axis, and the bosco
    /// is centred within it. A non-positive size (unreadable schematic) falls back to 40×30×40.
    /// </summary>
    public static PocketGeometry Build(SecureShelterConfig cfg, int boscoSizeX, int boscoSizeY, int boscoSizeZ,
        (int X, int Y, int Z)? archMarker = null)
    {
        if (boscoSizeX <= 0) boscoSizeX = 40;
        if (boscoSizeY <= 0) boscoSizeY = 30;
        if (boscoSizeZ <= 0) boscoSizeZ = 40;

        int t = Math.Max(0, SecureShelterConfig.WrapperThickness);
        int min = Math.Max(1, SecureShelterConfig.MinBoxSize);
        int boxX = Math.Max(boscoSizeX, min);
        int boxZ = Math.Max(boscoSizeZ, min);

        // World position of the box's min corner. The box would otherwise generate at the dimension's
        // (0,0) corner, where its outer dirt wall (at -1) falls off the map and gets cut — so it's
        // shifted into positive coordinates. Spawn/arch are relative to the box and move with it.
        int ox = SecureShelterConfig.BoxOriginX, oz = SecureShelterConfig.BoxOriginZ;

        var g = new PocketGeometry
        {
            DimCode = new AssetLocation(SecureShelterModSystem.Domain, SecureShelterConfig.PocketDimensionCode),
            FloorY = SecureShelterConfig.FloorY,

            ShellMinX = ox,
            ShellMaxX = ox + boxX - 1,
            ShellMinZ = oz,
            ShellMaxZ = oz + boxZ - 1,
            ShellTopY = SecureShelterConfig.FloorY + boscoSizeY - 1 + Math.Max(0, SecureShelterConfig.CeilingHeadroom),

            // Centre the bosco in the (possibly larger) footprint.
            BoscoOriginX = ox + (boxX - boscoSizeX) / 2,
            BoscoOriginZ = oz + (boxZ - boscoSizeZ) / 2,

            ShellBlockCode = SecureShelterConfig.ShellBlockCode,
            ShellBlockFallback = SecureShelterConfig.ShellBlockFallback,
            WrapperBlockCode = SecureShelterConfig.WrapperBlockCode,
        };

        g.DirtMinX = g.ShellMinX - t;
        g.DirtMaxX = g.ShellMaxX + t;
        g.DirtMinZ = g.ShellMinZ - t;
        g.DirtMaxZ = g.ShellMaxZ + t;
        g.DirtBottomY = g.FloorY - t;
        g.DirtTopY = g.ShellTopY + t;

        // Return arch position. If the schematic marked it (a creativeglow block at the arch centre),
        // use that exact spot; otherwise fall back to "near the far +Z wall" from config.
        if (archMarker.HasValue)
        {
            var m = archMarker.Value;     // schematic-relative position of the marker
            g.ArchCenterX = Math.Clamp(g.BoscoOriginX + m.X, g.ShellMinX + 2, g.ShellMaxX - 2);
            g.ArchZ       = Math.Clamp(g.BoscoOriginZ + m.Z, g.ShellMinZ + 1, g.ShellMaxZ - 1);
            g.ArchBaseY   = SecureShelterConfig.FloorY + m.Y;     // schematic is placed with its base at FloorY
        }
        else
        {
            g.ArchCenterX = Math.Clamp(ox + cfg.SpawnX + cfg.ArchOffsetX, g.ShellMinX + 2, g.ShellMaxX - 2);
            g.ArchZ       = g.ShellMaxZ - Math.Max(1, cfg.ArchWallInset);
            g.ArchBaseY   = SecureShelterConfig.FloorY + cfg.SpawnHeightAboveFloor;
        }

        // Arrival always matches the return arch, nudged 4 blocks north (+Z) so the player doesn't
        // spawn inside the doorway (which would instantly trigger a return).
        g.SpawnX = g.ArchCenterX;
        g.SpawnY = g.ArchBaseY;
        g.SpawnZ = Math.Clamp(g.ArchZ + 4, g.ShellMinZ + 1, g.ShellMaxZ - 1);

        // Cover the whole sealed box (relight band runs from y=0 up to this height).
        g.RelightHeight = g.DirtTopY + 1;

        // Generation radius must reach every footprint chunk from the spawn chunk (Manifold generates
        // a square of chunks around the fixed spawn). Derive it so large/off-centre boscos fully gen.
        static int Fd(int a, int b) => (int)Math.Floor((double)a / b);
        int scx = Fd(g.SpawnX, 32), scz = Fd(g.SpawnZ, 32);
        int rad = Math.Max(
            Math.Max(Math.Abs(Fd(g.DirtMaxX, 32) - scx), Math.Abs(Fd(g.DirtMinX, 32) - scx)),
            Math.Max(Math.Abs(Fd(g.DirtMaxZ, 32) - scz), Math.Abs(Fd(g.DirtMinZ, 32) - scz)));
        g.GenerationRadius = Math.Max(2, rad + 1);

        return g;
    }
}
