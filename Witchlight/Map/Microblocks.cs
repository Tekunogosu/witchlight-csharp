using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// What a chiselled block is really made of.
///
/// A microblock — the game's chiselled block, and what the stonework of every
/// ruin is built out of — carries no colour of its own. It is a shell: the shape
/// lives in the block entity beside it and so does the material, and the world
/// reports the same `chiseledblock` at that position whether it was cut from
/// granite or from cobblestone. The palette can only answer for the block it is
/// handed, and the answer for that one is the near-white of an untextured shell.
/// So every ruin drew as a white patch on ground that was otherwise the right
/// colour, which is the one thing on the map that stands out at every zoom.
///
/// Asked of the block entity instead, which is the only thing that knows. The
/// game already answers the exact question a map pixel is asking — what is this
/// mostly made of — so that is what is asked, rather than reading the voxels here
/// and arriving at a worse answer for more work.
/// </summary>
public sealed class Microblocks
{
    /// <summary>
    /// The block ids whose material has to be looked up.
    ///
    /// Held as a set so that the common case — every column in the world that is
    /// not a ruin — costs one lookup among a dozen ids and no block entity read
    /// at all. Only a column that really is chiselled pays for the answer.
    /// </summary>
    private readonly HashSet<int> _shells;

    private Microblocks(HashSet<int> shells) => _shells = shells;

    /// <summary>
    /// Every kind of chiselled block this world has registered.
    ///
    /// The snow-covered variants among them. They were left out while the shell
    /// still had a colour, on the grounds that snow lying over the chiselling is
    /// what somebody looking down sees — but the colour it had was the
    /// missing-texture checker rather than snow, and a ruin drawn in white
    /// against snow is a ruin that cannot be seen at all. Drawn as the stone it
    /// is cut from, it can.
    /// </summary>
    public static Microblocks In(IWorldAccessor world)
    {
        var shells = new HashSet<int>();
        foreach (var block in world.Blocks)
        {
            if (block is BlockMicroBlock)
            {
                shells.Add(block.Id);
            }
        }

        return new Microblocks(shells);
    }

    /// <summary>How many kinds of chiselled block are being looked through.</summary>
    public int Kinds => _shells.Count;

    /// <summary>
    /// What to record for the block at a position: the material a chiselled block
    /// is mostly made of, or the block itself where it is not one.
    ///
    /// `shows` is handed on rather than applied to the answer, so the majority is
    /// taken over materials the map can paint in the first place — a block
    /// chiselled partly out of something invisible answers with the part that
    /// draws, instead of answering with the part that does not and being refused.
    ///
    /// Anything that cannot be read is the block itself. A chiselled block whose
    /// entity has gone, or one made of nothing the palette knows, is still a block
    /// that is standing there, and drawing it the way it was drawn before is
    /// better than drawing a hole in the ground.
    /// </summary>
    public int MaterialAt(IBlockAccessor accessor, BlockPos at, int id, System.Func<int, bool> shows)
    {
        if (!_shells.Contains(id))
        {
            return id;
        }

        if (accessor.GetBlockEntity(at) is not BlockEntityMicroBlock shell)
        {
            return id;
        }

        var material = shell.GetMajorityMaterialId(material => shows(material));
        return material > 0 ? material : id;
    }
}
