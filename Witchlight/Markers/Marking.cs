using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// A marker asked for from in game, answered.
///
/// The client sends a place and, sometimes, everything a marker is; this decides
/// what the marker actually becomes and whether it is made at all. All of the
/// judgement is here and none of it is on the client, for the two reasons that
/// matter: this side can read the block at a position, and this side is the one
/// the map service will speak to about what somebody has kept.
///
/// The same three things happen as when a marker is asked for on the web — a
/// waypoint under a guid, a decision recorded about who may see it, and the
/// marker feed sent again — so the making itself goes through <see cref="Markers"/>
/// exactly as <see cref="Pending"/>'s does.
/// </summary>
public static class Marking
{
    /// <summary>
    /// What the block that names this marker is, and what the game calls it.
    ///
    /// Read here rather than taken from the client. The code decides which preset
    /// applies, and a client that could name the block could name any block —
    /// which would be a preset for somebody else's rock applied to somebody's
    /// own marker.
    /// </summary>
    public static (string Code, string Name) BlockAt(ICoreServerAPI api, MarkAsk ask) =>
        BlockAt(api, new BlockPos(ask.BlockX, ask.BlockY, ask.BlockZ));

    /// <summary>What is at one position, and what the game calls it. Air, and a
    ///  chunk nobody has loaded, are both nothing rather than a name.</summary>
    public static (string Code, string Name) BlockAt(ICoreServerAPI api, BlockPos at)
    {
        var block = api.World.BlockAccessor.GetBlock(at);
        if (block is null || block.Code is null || block.BlockId == 0)
        {
            return ("", "");
        }

        return (block.Code.ToString(), block.GetPlacedBlockName(api.World, at) ?? "");
    }

    /// <summary>
    /// The block a marker at this place is about.
    ///
    /// A marker made on the web is put where somebody clicked on a map drawn from
    /// above, and what the map drew is the *surface* — so the place the marker
    /// takes is the standing height and the block it means is under its feet.
    /// Tried in that order rather than assumed either way, since a marker typed
    /// into the form can be anywhere at all, a cave floor included.
    /// </summary>
    public static string CodeUnder(ICoreServerAPI api, int x, int y, int z)
    {
        var here = BlockAt(api, new BlockPos(x, y, z)).Code;
        return here.Length > 0 ? here : BlockAt(api, new BlockPos(x, y - 1, z)).Code;
    }

    /// <summary>
    /// Answers one ask: makes the marker where there is enough to make one, and
    /// otherwise says what a window would need to finish it.
    ///
    /// The one case that makes nothing is a press of the key over a block no
    /// preset names. That is not a failure — it is the question the key asks
    /// coming back unanswered — so what comes back carries the defaults a window
    /// should open on rather than an error.
    /// </summary>
    public static MarkReply Answer(
        ICoreServerAPI api,
        Visibility visibility,
        Origins origins,
        IServerPlayer player,
        MarkAsk ask,
        Person person,
        (string Code, string Name) block)
    {
        var preset = ask.UsePreset ? Presets.For(person, block.Code) : null;
        var reply = Starting(ask, person, block, preset);

        if (ask.UsePreset && preset is null)
        {
            reply.Said = block.Code.Length == 0
                ? "Nothing there to mark. Look at a block, or stand on one."
                : $"No preset for {Named(block)}.";
            reply.Yours = true;
            // Everything they have kept, for the window to start from instead
            // of the block's defaults. In the order the map lists them.
            reply.Presets = person.Presets
                .OrderBy(kept => kept.Title, StringComparer.OrdinalIgnoreCase)
                .Select(kept => new PresetOffer
                {
                    Pattern = kept.Pattern,
                    Title = kept.Title,
                    Icon = kept.Icon,
                    Color = kept.Color,
                    Private = kept.Private is { } said ? Mark.Says(said) : Mark.Unsaid,
                })
                .ToList();
            return reply;
        }

        var waypoint = Markers.Make(
            api,
            Guid.NewGuid().ToString(),
            player.PlayerUID,
            new Vec3d(ask.X, ask.Y, ask.Z),
            reply.Title,
            reply.Icon,
            Markers.Packed(reply.Color) ?? Markers.White,
            pinned: false);

        if (waypoint is null)
        {
            reply.Said = "The server has no waypoint layer, so nothing could be marked.";
            return reply;
        }

        // Recorded whichever way it went, the way the web form's markers are: the
        // operator's setting is the fallback for a marker nobody decided about,
        // and somebody who marked something decided — including when they decided
        // to agree with it.
        visibility.Choose(waypoint.Guid, reply.Private == Mark.Private);
        // The block this was made on, read once while the chunk is in hand. A
        // preset made from this marker later is keyed on it, and by then the ore
        // may be mined out and the block something else.
        origins.Made(waypoint.Guid, block.Code);
        reply.Made = true;
        reply.Said = $"Marked {reply.Title}"
            + (reply.Private == Mark.Private ? " — private." : " — everyone can see it.");
        return reply;
    }

    /// <summary>
    /// The preset this ask should be kept as, or nothing.
    ///
    /// What was typed, or failing that the block itself. Nothing to key it on is
    /// not worth stopping a marker over — the marker is the point and the preset
    /// is the extra — which is the rule the map's own form follows.
    /// </summary>
    public static Preset? Keeping(MarkAsk ask, MarkReply made, (string Code, string Name) block)
    {
        if (!ask.KeepPreset)
        {
            return null;
        }

        var pattern = ask.Pattern.Trim();
        if (pattern.Length == 0)
        {
            pattern = BlockPattern.Widened(block.Code);
        }
        if (pattern.Length == 0)
        {
            return null;
        }

        return new Preset
        {
            Pattern = pattern,
            Title = made.Title,
            Icon = made.Icon,
            Color = made.Color,
            Private = made.Private == Mark.Private,
        };
    }

    /// <summary>
    /// What the marker is before anything is made of it: the preset where one
    /// applies, and what the client typed where it did not.
    ///
    /// Every field is settled here, so what is made and what a window would be
    /// opened on are the same answer read twice rather than two answers.
    /// </summary>
    private static MarkReply Starting(
        MarkAsk ask, Person person, (string Code, string Name) block, Preset? preset)
    {
        // Their own choice over the operator's, which is the order the map's own
        // form reads them in.
        var byDefault = person.PrivateByDefault ?? Settings.MarkersPrivateByDefault;
        var kept = preset is null
            ? Mark.IsPrivate(ask.Private, byDefault)
            : preset.Private ?? byDefault;

        var title = preset?.Title ?? ask.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Named(block);
        }

        return new MarkReply
        {
            X = ask.X,
            Y = ask.Y,
            Z = ask.Z,
            BlockX = ask.BlockX,
            BlockY = ask.BlockY,
            BlockZ = ask.BlockZ,
            Block = block.Name,
            // The block's variant number already widened into a wildcard, so one
            // preset answers for a whole family rather than for the one stage of
            // grass that happened to be underfoot. Somebody who wants the exact
            // block takes the star back out.
            Pattern = BlockPattern.Widened(block.Code),
            Title = Markers.Title(title),
            Icon = Markers.Picture(preset?.Icon ?? ask.Icon),
            Color = Colour(preset?.Color ?? ask.Color),
            Private = Mark.Says(kept),
            // What the window would open with, which is what this person set on
            // the map for themselves. An ask that came from that window has
            // already been answered by whoever was looking at it.
            KeepPreset = ask.UsePreset ? person.PresetsByDefault : ask.KeepPreset,
        };
    }

    /// <summary>What to call a marker nobody named: the block, or the word the
    ///  game itself gives an unnamed waypoint.</summary>
    private static string Named((string Code, string Name) block)
    {
        if (block.Name.Length > 0)
        {
            return block.Name;
        }
        return block.Code.Length > 0 ? block.Code : Markers.Unnamed;
    }

    /// <summary>The colour as the map writes one, or white for anything that is
    ///  not six hex digits behind a hash.</summary>
    private static string Colour(string? color)
    {
        var said = (color ?? "").Trim().ToLowerInvariant();
        return Markers.Packed(said) is null ? Markers.WhiteHex : said;
    }
}
