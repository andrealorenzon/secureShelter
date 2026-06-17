using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SecureShelter;

/// <summary>
/// The "Storditi's hut" painting — a 1×1 painting that serves as the entry portal.
/// Right-clicking it teleports the player into the pocket dimension (the matching return
/// is the walk-through stone arch inside the pocket). All teleport logic lives in
/// <see cref="SecureShelterModSystem"/>; this block only forwards the interaction.
///
/// Code form: <c>secureshelter:painting-storditishut-&lt;side&gt;</c>.
/// </summary>
public class BlockPaintingSecureShelter : Block
{
    /// <summary>The direction the painting faces, from its <c>side</c> variant.</summary>
    public BlockFacing Facing => BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        // Server authoritative: only the server performs the transit.
        if (world.Side == EnumAppSide.Server && byPlayer is IServerPlayer sp)
        {
            world.Api.ModLoader
                .GetModSystem<SecureShelterModSystem>()?
                .EnterPocketViaPainting(sp, blockSel.Position, Facing);
        }

        // Return true so the client plays the normal interaction (and doesn't fall through to
        // block placement); the actual teleport is queued server-side above.
        return true;
    }
}
