namespace SecureShelter;

/// <summary>
/// User-editable settings for the pocket dimension, loaded from <c>ModConfig/SecureShelterConfig.json</c>
/// (written with defaults on first run). All coordinates are dimension-local.
///
/// The cube's X/Z footprint and height are intentionally NOT set here — they are derived from the
/// interior schematic's own <c>SizeX/SizeY/SizeZ</c> so the shell always wraps the bosco exactly,
/// with <see cref="CeilingHeadroom"/> empty layers above it and a <see cref="WrapperThickness"/>-thick
/// dirt box sealing it from the outside.
/// </summary>
public sealed class SecureShelterConfig
{
    /// <summary>Dimension code. Bump it whenever the baked-in layout changes — Manifold caches
    /// generated columns and won't regenerate an existing dimension in place.</summary>
    public string PocketDimensionCode = "test10";

    /// <summary>Floor height of the cube interior. Kept low so the whole build sits inside the relight band.</summary>
    public int FloorY = 1;

    /// <summary>World X / Z of the box's min corner. The box (and its spawn/arch) sit here; kept positive
    /// so the outer dirt wall doesn't fall off the map edge at 0 and get cut.</summary>
    public int BoxOriginX = 100;
    public int BoxOriginZ = 100;

    /// <summary>Spawn X inside the cube, relative to the box's min corner (the arch's X follows it;
    /// the arrival Z is derived from the return arch, so there's no SpawnZ).</summary>
    public int SpawnX = 19;

    /// <summary>Player spawns this many blocks above the floor (so they land on the forest surface).</summary>
    public int SpawnHeightAboveFloor = 8;

    /// <summary>Lateral offset of the return arch from the spawn's X (clamped to stay inside the walls).</summary>
    public int ArchOffsetX = 0;

    /// <summary>How many blocks in from the far (+Z) wall the return arch sits — close to the side of
    /// the cube but inside it. Used only when no arch-marker block is found in the schematic.</summary>
    public int ArchWallInset = 2;

    /// <summary>If a block with this code is present in the schematic, its position becomes the arch
    /// centre: it's removed and the return arch is stamped there, with the arrival spot 4 blocks
    /// north (+Z) of it. Lets the builder mark the exit precisely. Matched against the block code.</summary>
    public string ArchMarkerBlockCode = "creativeglow-79";

    /// <summary>Minimum box footprint on each axis. The cube is the larger of the bosco's size and
    /// this, and the bosco is centred inside it. So a small schematic still gets a 40×40 floor.</summary>
    public int MinBoxSize = 40;

    /// <summary>Empty layers left above the interior before the ceiling (head clearance).</summary>
    public int CeilingHeadroom = 10;

    /// <summary>Thickness of the opaque dirt box wrapped around the shell (seals light leaks through mantle).</summary>
    public int WrapperThickness = 1;

    /// <summary>Inner shell block — the unbreakable "bedrock" walls. Mantle by default (VS has no literal
    /// bedrock; mantle is the engine's unbreakable bottom-of-world block and absorbs no light).</summary>
    public string ShellBlockCode = "game:mantle";
    public string ShellBlockFallback = "game:rock-granite";

    /// <summary>Outer wrapper block — opaque, seals light. Dirt by default.</summary>
    public string WrapperBlockCode = "game:soil-medium-none";

    /// <summary>Block the return arch is built from.</summary>
    public string ArchBlockCode = "game:stonebricks-granite";

    /// <summary>Path (under <c>assets/SecureShelter/</c>) to the interior schematic stamped into the cube.</summary>
    public string BoscoSchematicPath = "schematics/shelter.json";

    /// <summary>Block-code prefixes deleted from the schematic when it loads (along with their
    /// block-entities and decor), so they're never placed in the pocket. Defaults to the vanilla
    /// cabinet, whose display slots NRE-crash the client renderer — keeps the pocket safe even if a
    /// cabinet sneaks back into a re-exported bosco.</summary>
    public string[] BannedBlockPrefixes = { "cabinet" };

    /// <summary>Block-code prefixes whose contents are kept topped up (snapshotted full/lit just after
    /// the bosco is placed, then restored every 10 s). Covers liquids (barrels/jugs/jars — fill them
    /// in the schematic with your mod's liquid, e.g. rye ale / rye whisky), food (storage vessels,
    /// crocks), always-lit firepits incl. their cooking pot, cuttable cheese/pie (the cut part
    /// regrows), and ground storage placed in the schematic (e.g. apples). NOTE: only ground storage
    /// that exists when the bosco is stamped refills — bowls a player puts down later are theirs to
    /// keep/move. Matched against the block code path; add your modded container codes here.</summary>
    public string[] RefillableBlockPrefixes =
    {
        "barrel", "jug", "jar", "storagevessel", "crock", "firepit", "cheese", "pie", "shelf", "groundstorage"
    };

    /// <summary>Block-code prefixes the player may interact with inside the pocket (take liquid/food,
    /// cut cheese/pie, ladle soup from a firepit's cooking pot, place &amp; take bowls via ground
    /// storage). Every other container is locked so nothing can be stolen. Firepits stay perpetually
    /// lit and their cooking pot refills via <see cref="RefillableBlockPrefixes"/>.</summary>
    public string[] UsableContainerPrefixes =
    {
        "barrel", "jug", "jar", "storagevessel", "crock", "cheese", "pie", "firepit", "shelf",
        "chest", "labeledchest", "trunk", "stationarybasket", "groundstorage", "resonator"
    };
}
