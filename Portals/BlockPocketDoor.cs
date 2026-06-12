using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Portals;

/// <summary>
/// A simple door that, while open, teleports a player who walks through it to the
/// pocket dimension (and, for the copy that lives inside the pocket, back again).
/// Crossing logic lives in <c>OnEntityInside</c> (added in M4).
///
/// Variants: <c>horizontalorientation</c> (north/east/south/west) × <c>state</c>
/// (closed/opened). Code form: <c>portals:pocketdoor-&lt;orientation&gt;-&lt;state&gt;</c>.
/// </summary>
public class BlockPocketDoor : Block
{
    private static readonly Cuboidf[] None = System.Array.Empty<Cuboidf>();

    /// <summary>True when this door variant is in its open (walkable) state.</summary>
    public bool IsOpen => Variant["state"] == "opened";

    /// <summary>The direction this door faces, derived from its orientation variant.</summary>
    public BlockFacing Facing => BlockFacing.FromCode(Variant["horizontalorientation"]);

    /// <summary>Thin panel spanning the doorway, oriented to the block's facing.</summary>
    private Cuboidf[] PanelBox()
    {
        string o = Variant["horizontalorientation"];
        Cuboidf box = (o == "north" || o == "south")
            ? new Cuboidf(0f, 0f, 0.4375f, 1f, 1f, 0.5625f)   // spans X, thin in Z
            : new Cuboidf(0.4375f, 0f, 0f, 0.5625f, 1f, 1f);  // spans Z, thin in X
        return new[] { box };
    }

    // Solid when closed, freely walkable when open (that is what lets a player cross).
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        => IsOpen ? None : PanelBox();

    // Always keep a clickable target in the doorway so the door can be toggled either way.
    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        => PanelBox();

    // Orient the placed block toward the player.
    public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack,
        BlockSelection blockSel, ref string failureCode)
    {
        if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;

        BlockFacing[] horVer = SuggestedHVOrientation(byPlayer, blockSel);
        string orient = horVer[0].Code;

        Block oriented = world.BlockAccessor.GetBlock(CodeWithVariant("horizontalorientation", orient)) ?? this;
        world.BlockAccessor.SetBlock(oriented.BlockId, blockSel.Position);
        return true;
    }

    // Right-click toggles open/closed in place.
    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        string next = IsOpen ? "closed" : "opened";
        Block nextBlock = world.BlockAccessor.GetBlock(CodeWithVariant("state", next));
        if (nextBlock != null)
        {
            world.BlockAccessor.ExchangeBlock(nextBlock.BlockId, blockSel.Position);
            world.BlockAccessor.MarkBlockDirty(blockSel.Position);
            world.PlaySoundAt(new AssetLocation("game:sounds/block/door"),
                blockSel.Position.X + 0.5, blockSel.Position.Y + 0.5, blockSel.Position.Z + 0.5, byPlayer);
        }
        return true;
    }
}
