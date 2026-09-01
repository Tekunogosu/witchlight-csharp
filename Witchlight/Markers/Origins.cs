using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// What block a marker was put on.
///
/// A waypoint is a place and nothing else, which is all the game has ever needed:
/// nothing in it says what was standing there. Presets are this mod's idea and
/// they are keyed on a block, so "what was this marker put on" is a question only
/// this side can answer — and it can only answer it at the moment the marker is
/// made, while the chunk is loaded and the block is still what it was. A marker
/// made on an ore vein that has since been mined out would otherwise report
/// whatever is there now.
///
/// So it is read once and kept, in the savegame beside the waypoints, through
/// <see cref="Beside{T}"/> — the same store <see cref="Visibility"/> uses, because
/// it is the same shape of answer about the same thing.
///
/// What is kept is the code and not the name. A code is the block's identity and
/// is what a preset's pattern is matched against; a name is words in whatever
/// language the reader has, and two servers would disagree about it.
///
/// It can also be a *pattern* rather than a code. Making a screenful of markers
/// look like a preset sets this to that preset's pattern, which is the preset
/// saying what kind of thing those markers are — and "which block this marker is
/// about" is the same question either way, asked of the world or answered
/// outright.
/// </summary>
public sealed class Origins
{
    private const string SaveKey = "witchlight:markerblocks";
    private const string Called = "the blocks markers were made on";

    private readonly Beside<string> _made;

    private Origins(Beside<string> made)
    {
        _made = made;
    }

    /// <summary>A store knowing nothing, which is every marker made before this
    ///  was kept. What the mod holds before the world is up.</summary>
    public static Origins Empty => new(Beside<string>.Empty(SaveKey));

    /// <summary>How many markers this knows the block of. Reported by status.</summary>
    public int Known => _made.Count;

    /// <summary>What a previous run stored.</summary>
    public static Origins Read(ICoreServerAPI api) =>
        new(Beside<string>.Read(api, SaveKey, Called));

    /// <summary>Stores them, when they are not the ones already stored.</summary>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive) =>
        _made.Write(api, alive, Called);

    /// <summary>What this marker was put on, or nothing where it was made before
    ///  the mod kept the answer.</summary>
    public string Of(string? guid) => _made.Knows(guid, out var code) ? code : "";

    /// <summary>
    /// Records which block a marker is about.
    ///
    /// Said again on every change, so a marker that has moved is about whatever
    /// is under it now — unless the ask named a block itself, which is the map
    /// saying that these markers belong to a preset whatever they are standing
    /// on. See <see cref="Pending"/>, which decides between the two.
    /// </summary>
    public void Made(string? guid, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }
        _made.Say(guid, code);
    }
}
