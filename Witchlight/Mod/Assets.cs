using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// What the mod takes from the game's assets, while they are still there to take.
///
/// The same class as the rest of the mod system, for the reason the commands and
/// the greeting are: this is a view of the whole system rather than a thing of
/// its own.
///
/// It is kept apart because it answers to a different clock. Everything else here
/// runs once a world is up; this runs while the game is still loading, in the one
/// window between the textures being read and the server freeing them — which is
/// why the palette is built at a moment that otherwise looks arbitrary.
/// </summary>
public partial class WitchlightSystem
{

    public override void AssetsLoaded(ICoreAPI api) => Report(api, "AssetsLoaded");

    /// <summary>
    /// Everything that has to be read while the assets are still in memory.
    ///
    /// The server frees block textures once assets are loaded, so a palette not
    /// built here cannot be built at all — and the icons, colour maps and block
    /// names come out of the same assets, so they are read in the same pass.
    /// </summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        Report(api, "AssetsFinalize");
        _raw = PaletteExchange.BuildFromAssets(api);
    }

    /// <summary>
    /// Writes everything the assets said, once there is a directory to write it
    /// to.
    ///
    /// The palette was built at asset load because the textures behind it are
    /// freed straight afterwards. The rest is read here: the colour maps, the
    /// marker pictures and the block names come out of assets the server keeps,
    /// so the only reason they were up there was that the palette had to be.
    /// </summary>
    private void WriteWhatTheAssetsSaid(ICoreServerAPI api)
    {
        if (_raw is not { } raw)
        {
            api.Logger.Warning(
                "[witchlight] no palette was built while the assets were loading, so the map "
                + "has no colours. This is a bug in this mod.");
            return;
        }

        _built = PaletteExchange.Settle(api, Settings.Exports, raw);
        var palette = _built.Palette;

        var (maps, mapsFound) = ColourMaps.Export(api, Settings.Exports);
        api.Logger.Notification("[witchlight] colour maps: {0} of {1} written", maps, mapsFound);

        var (icons, iconsFound, from) = Icons.Export(api, Settings.Exports);
        api.Logger.Notification(
            "[witchlight] marker icons: {0} of {1} written, from {2}", icons, iconsFound, from);

        var named = BlockNames.Export(api, Settings.Exports, palette);
        api.Logger.Notification(
            "[witchlight] block names: {0} of {1} blocks named, {2} of them in the palette{3}",
            named.Named,
            named.Blocks,
            named.Known,
            named.Written ? "" : " — unchanged, not rewritten");
        if (named.Named > 0 && named.Known == 0)
        {
            api.Logger.Warning(
                "[witchlight] no named block is in the palette, so the map will show codes "
                + "rather than names. The two are keyed differently, which is a bug in this mod.");
        }
    }

    /// <summary>
    /// Says whether block textures are readable at this point. The answer decides
    /// whether a palette can be built server-side at all, so it is logged rather
    /// than assumed.
    /// </summary>
    private static void Report(ICoreAPI api, string stage)
    {
        var blocks = api.World.Blocks;
        api.Logger.Notification(
            "[witchlight] {0}: {1} of {2} blocks have textures in memory",
            stage,
            blocks.Count(block => block?.Textures is { Count: > 0 }),
            blocks.Count);
    }
}
