using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Witchlight;

/// <summary>
/// The base game's colours, recorded once and carried inside this assembly.
///
/// A dedicated server's install ships almost no block textures — 46 files against
/// a full game's 9,587 — so the palette it builds for itself colours nearly
/// nothing, and every such server drew a flat map from the moment it came up
/// until a player happened to join with this mod on. That wait is the whole
/// reason <see cref="PaletteExchange"/> exists, and for a server running the base
/// game it was a wait for information nobody needed to send: the base game's
/// blocks look the same on every server there is.
///
/// So they are recorded. The map is fully coloured before the first player
/// arrives, the asking is left for what is genuinely unknowable from here — a
/// mod's blocks, and a texture pack an admin has chosen — and a fresh server's
/// first render is the finished picture rather than the outline of one.
///
/// **Keyed by code and nothing else.** A block id is assigned per world and says
/// nothing on another server, so the recording carries none and every entry is
/// given this world's id as it is read. A code this server has never heard of is
/// dropped; a colour it already has is left alone. See
/// <see cref="Palette.Merge"/> for the second rule, which is the general one.
///
/// Refreshed by `bake-palette.py` in this repository, which records a real
/// palette.json rather than deriving one: working out a block's colour takes the
/// game's own assets, and a second implementation of that rule here would drift
/// from <see cref="PaletteBuilder"/> the week it was written.
/// </summary>
public static class Vanilla
{
    /// <summary>
    /// The recording, as the compiler names it: the root namespace, then the
    /// path to the file with its separators turned into dots.
    /// </summary>
    private const string Resource = "Witchlight.Palette.vanilla.json.gz";

    /// <summary>What was read out of the assembly, and whether reading was tried.</summary>
    private static Palette? _recorded;
    private static bool _read;

    /// <summary>
    /// Fills what this palette has no colour for, from the recording.
    /// Gives back how many colours it supplied.
    ///
    /// The palette is changed in place rather than a merged copy being handed
    /// back, because the count is the interesting part and a caller that has to
    /// both take a new palette and be told a number will sooner or later take one
    /// and ignore the other.
    /// </summary>
    public static int Fill(ICoreAPI api, Palette palette)
    {
        if (Read(api.Logger) is not { } recorded)
        {
            return 0;
        }

        var filled = 0;
        foreach (var (code, entry) in recorded.Blocks)
        {
            // Only what this palette is actually missing. A colour built from
            // this server's own assets is this server's own answer and is not
            // overwritten by a recording of somebody else's.
            //
            // What is *not* honoured is this server calling a block invisible.
            // That judgement is `no textures to try, and the shape gave nothing
            // either`, which is only trustworthy where the textures were there
            // to try: on a dedicated server they are not, and 1,202 blocks that
            // plainly draw — beds, banners, bamboo, barrel cactus — come out of
            // its own build labelled as drawing nothing. Honouring that label
            // costs the recording exactly the blocks it exists to supply. The
            // one block whose invisibility is known for certain is settled
            // before any of this, from what the game says it draws — see
            // `PaletteBuilder` and `EnumDrawType.Empty`.
            if (palette.Blocks.TryGetValue(code, out var held) && held.Rgb is not null)
            {
                continue;
            }

            // The recording carries no ids, so a block this server does not have
            // is one this cannot say anything about and is not invented.
            if (api.World.GetBlock(new AssetLocation(code)) is not { } block)
            {
                continue;
            }

            palette.Blocks[code] = new PaletteEntry
            {
                Id = block.Id,
                Rgb = entry.Rgb,
                Invisible = entry.Invisible,
                ClimateMap = entry.ClimateMap,
                SeasonMap = entry.SeasonMap,
            };

            if (entry.Rgb is not null)
            {
                filled++;
            }
        }

        return filled;
    }

    /// <summary>Which game version the recording was made against.</summary>
    public static string RecordedFor(ILogger log) => Read(log)?.GameVersion ?? "";

    /// <summary>
    /// Says what the recording did, and where it is worth doubting.
    ///
    /// Its own line because a server whose map is coloured wants to know which
    /// of the two ways that happened, and because a recording made against
    /// another game version is the one case where these colours can be wrong in
    /// a way nothing else here would notice: block codes outlive releases and
    /// the textures under them do not.
    /// </summary>
    public static void Report(ICoreAPI api, int filled)
    {
        if (filled == 0)
        {
            return;
        }

        var recorded = RecordedFor(api.Logger);
        var running = GameVersion.ShortGameVersion;

        api.Logger.Notification(
            "[witchlight] {0} colours came from the base game palette shipped with this mod{1}",
            filled,
            recorded == running || recorded.Length == 0
                ? ""
                : $", which was recorded against {recorded} and this is {running} — run "
                  + $"`{Commands.Short} {Permissions.Palette}` if the map's colours look wrong");
    }

    /// <summary>
    /// Reads the recording out of this assembly, once.
    ///
    /// A recording that will not read is a mod that was packaged wrong, and it
    /// costs a flat map on a dedicated server rather than anything worse — so it
    /// is said out loud and everything carries on without it.
    /// </summary>
    private static Palette? Read(ILogger log)
    {
        if (_read)
        {
            return _recorded;
        }

        _read = true;
        try
        {
            using var packed = typeof(Vanilla).Assembly.GetManifestResourceStream(Resource);
            if (packed is null)
            {
                log.Warning(
                    "[witchlight] this build carries no base game palette ({0} is not in the "
                    + "assembly), so a server without block textures starts with a flat map",
                    Resource);
                return null;
            }

            using var plain = new GZipStream(packed, CompressionMode.Decompress);
            using var text = new StreamReader(plain);
            using var json = new JsonTextReader(text);
            _recorded = new JsonSerializer().Deserialize<Palette>(json);
        }
        catch (Exception error)
        {
            log.Warning(
                "[witchlight] the base game palette shipped with this mod could not be read "
                + "({0}), so a server without block textures starts with a flat map",
                error.Message);
            _recorded = null;
        }

        return _recorded;
    }
}
