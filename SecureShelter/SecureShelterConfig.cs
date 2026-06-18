namespace SecureShelter;

/// <summary>
/// User-editable settings, loaded from <c>ModConfig/SecureShelterConfig.json</c> (written with
/// defaults on first run). Deliberately minimal: only the portal's arrival position relative to the
/// box is exposed. Everything else (box placement/size, materials, dimension code, schematic path,
/// container behaviour) is hardcoded below as <c>const</c>/<c>static readonly</c>, so it is NOT
/// serialized into the config file and players can't fiddle with it.
///
/// The four exposed values are only used as the fallback when the schematic has no arch-marker block
/// (<see cref="ArchMarkerBlockCode"/>); when a marker is present its position drives the arch/arrival.
/// </summary>
public sealed class SecureShelterConfig
{
    /// <summary>Spawn / return-arch X inside the cube, relative to the box's min corner (the arch's X
    /// follows it; the arrival Z is derived from the return arch, so there's no SpawnZ).</summary>
    public int SpawnX = 19;

    /// <summary>Player spawns this many blocks above the floor (so they land on the forest surface).</summary>
    public int SpawnHeightAboveFloor = 8;

    /// <summary>Lateral offset of the return arch from the spawn's X (clamped to stay inside the walls).</summary>
    public int ArchOffsetX = 0;

    /// <summary>How many blocks in from the far (+Z) wall the return arch sits — close to the side of
    /// the cube but inside it.</summary>
    public int ArchWallInset = 2;

    // ── Hardcoded (const / static readonly → never written to the config file) ──────────────────

    /// <summary>Dimension code. Bump it whenever the baked-in layout changes — Manifold caches
    /// generated columns and won't regenerate an existing dimension in place.</summary>
    public const string PocketDimensionCode = "stordihut";

    /// <summary>Floor height of the cube interior. Kept low so the whole build sits inside the relight band.</summary>
    public const int FloorY = 1;

    /// <summary>World X / Z of the box's min corner. Kept positive so the outer dirt wall doesn't fall
    /// off the map edge at 0 and get cut.</summary>
    public const int BoxOriginX = 100;
    public const int BoxOriginZ = 100;

    /// <summary>If a block with this code is present in the schematic, its position becomes the arch
    /// centre: it's removed and the return arch is stamped there, with the arrival spot 4 blocks
    /// north (+Z) of it. Lets the builder mark the exit precisely. Matched against the block code.</summary>
    public const string ArchMarkerBlockCode = "creativeglow-79";

    /// <summary>Minimum box footprint on each axis. The cube is the larger of the bosco's size and
    /// this, and the bosco is centred inside it. So a small schematic still gets a 40×40 floor.</summary>
    public const int MinBoxSize = 40;

    /// <summary>Empty layers left above the interior before the ceiling (head clearance).</summary>
    public const int CeilingHeadroom = 10;

    /// <summary>Thickness of the opaque dirt box wrapped around the shell (seals light leaks through mantle).</summary>
    public const int WrapperThickness = 1;

    /// <summary>Inner shell block — the unbreakable "bedrock" walls. Mantle absorbs no light.</summary>
    public const string ShellBlockCode = "game:mantle";
    public const string ShellBlockFallback = "game:rock-granite";

    /// <summary>Outer wrapper block — opaque, seals light. Dirt by default.</summary>
    public const string WrapperBlockCode = "game:soil-medium-none";

    /// <summary>Path (under <c>assets/secureshelter/</c>) to the interior schematic stamped into the cube.</summary>
    public const string BoscoSchematicPath = "schematics/shelter.json";

    /// <summary>Block-code prefixes deleted from the schematic when it loads (along with their
    /// block-entities and decor). Defaults to the vanilla cabinet, whose display slots NRE-crash the
    /// client renderer.</summary>
    public static readonly string[] BannedBlockPrefixes = { "cabinet" };

    /// <summary>Block-code prefixes whose contents are kept topped up (snapshotted full/lit just after
    /// the bosco is placed, then restored on a take). Covers liquids, food, always-lit firepits,
    /// cuttable cheese/pie, and ground storage placed in the schematic.</summary>
    public static readonly string[] RefillableBlockPrefixes =
    {
        "barrel", "jug", "jar", "storagevessel", "crock", "firepit", "cheese", "pie", "shelf", "groundstorage"
    };

    /// <summary>Block-code prefixes the player may interact with inside the pocket. Every other
    /// container is locked so nothing can be stolen.</summary>
    public static readonly string[] UsableContainerPrefixes =
    {
        "barrel", "jug", "jar", "storagevessel", "crock", "cheese", "pie", "firepit", "shelf",
        "chest", "labeledchest", "trunk", "stationarybasket", "groundstorage", "resonator"
    };
}
