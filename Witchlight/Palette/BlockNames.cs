using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Witchlight;

/// <summary>
/// What the game calls each block.
///
/// The map knows a block by its code — `game:crop-spelt-1` — because that is
/// what a palette is keyed on and what a rendered column reads back as. A person
/// clicking the map to mark something wants the name they would see in their own
/// hands: "Growing spelt", not a path with a number on the end.
///
/// Resolved the way the game resolves it, through the same wildcard lookup that
/// turns `block-crop-spelt-*` into one name for every growth stage. Nothing here
/// invents a name: a block the language files say nothing about is left out, and
/// whatever reads this falls back to the code.
///
/// Written to a file rather than posted, because it is the better part of a
/// megabyte and changes only when the mod set does — the same reasons the palette
/// is a file. Written only when it differs, so a server restarted on unchanged
/// assets touches nothing.
/// </summary>
public static class BlockNames
{
    public static string PathIn(string exports) => Path.Combine(exports, "blocknames.json");

    /// <summary>
    /// Writes the name of every block that has one.
    ///
    /// Returns how many were named, how many of those the palette can look up,
    /// and whether the file was rewritten. The middle number is the one worth
    /// reading: this table is only ever read with a code out of the palette, so
    /// a table keyed any other way is a table that answers nothing — and it does
    /// so silently, which is how one shipped keyed on short codes against a
    /// palette keyed on full ones.
    /// </summary>
    public static (int Named, int Known, int Blocks, bool Written) Export(
        ICoreAPI api,
        string exports,
        Palette palette)
    {
        // Written down here because it is the whole contract with the service:
        // it reads this table with a code out of the palette, and a table keyed
        // any other way answers nothing for every block on the map.

        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var blocks = 0;

        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is null)
            {
                continue;
            }
            blocks++;

            // The same spelling the palette keys on — `game:rock-granite`, domain
            // and all. A short code here would be a table nothing could look a
            // block up in, and nothing on either side would say so.
            var code = block.Code.ToString();
            if (names.ContainsKey(code))
            {
                continue;
            }

            var said = NameOf(block);
            // A name that is only the code again says nothing the reader of this
            // file does not already have, and there are thousands of them.
            if (string.IsNullOrEmpty(said) || said == code)
            {
                continue;
            }

            names[code] = said;
        }

        var known = 0;
        foreach (var code in names.Keys)
        {
            if (palette.Blocks.ContainsKey(code))
            {
                known++;
            }
        }

        var written = Disk.Write(PathIn(exports), JsonConvert.SerializeObject(names));
        return (names.Count, known, blocks, written);
    }

    /// <summary>
    /// One block's name, or null where the language files have none.
    ///
    /// The key is the one the game builds for a held block, so a mod's own block
    /// resolves out of that mod's language file without anything here knowing it
    /// exists.
    /// </summary>
    private static string? NameOf(Block block)
    {
        var code = block.Code;
        if (code is null)
        {
            return null;
        }

        var key = code.Domain + AssetLocation.LocationSeparator + "block-" + code.Path;
        return Lang.GetMatchingIfExists(key)?.Trim();
    }
}
