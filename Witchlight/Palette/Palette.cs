using Vintagestory.API.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Witchlight;

/// <summary>
/// One block's map appearance: the colour its top face averages to, and the
/// colour maps the game would tint it with.
/// </summary>
public class PaletteEntry
{
    /// <summary>
    /// The block id in this world. Ids are assigned per world and shift with the
    /// mod set, which is why the palette is keyed by code — but the exported
    /// columns carry ids, so the mapping has to travel with them.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Average colour, or null for a block this palette cannot draw. Recorded
    /// either way so the renderer can tell "nothing here" from "a block I have
    /// never heard of".
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public string? Rgb { get; set; }

    /// <summary>
    /// Which kind of colourless this is: true where the block genuinely draws
    /// nothing — air, an invisible helper — and false where it draws something
    /// the builder could not work out a colour for.
    ///
    /// Null on an entry that has a colour, and on every entry of a palette
    /// written before this existed, which is why it is nullable rather than a
    /// plain bool: a reader must be able to tell "this palette says nothing about
    /// it" from "this palette says it draws".
    ///
    /// The two used to be one state, and the renderer painted both of them the
    /// same near-black it paints ground nobody has explored. So a block whose
    /// colour was missed — bare soil, forest floor, the ground under grass — read
    /// on the map as a hole in the world exactly where somebody had been digging.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Invisible { get; set; }

    /// <summary>Name of the climate colour map, when the block is climate tinted.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ClimateMap { get; set; }

    /// <summary>Name of the season colour map, when the block is seasonally tinted.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? SeasonMap { get; set; }
}

/// <summary>
/// What every block on the map looks like.
///
/// The document itself — what it is, what it was built for, and how to keep it.
/// Building one is <see cref="PaletteBuilder"/>; putting one on the wire is
/// <see cref="PaletteTable"/>.
/// </summary>
public class Palette
{
    public int Version { get; set; } = 1;

    /// <summary>Game version the palette was built against, since assets change.</summary>
    public string GameVersion { get; set; } = "";

    /// <summary>
    /// What this palette was built for: the block registry and mod set. A palette
    /// whose fingerprint no longer matches the server is stale.
    /// </summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>`server` or `client`, which is what says whether to ask for a better one.</summary>
    public string Source { get; set; } = "server";

    /// <summary>
    /// Whether an admin's own assets have settled these colours.
    ///
    /// A palette is a claim about what the world looks like, and two players can
    /// make different ones honestly: a texture pack changes every colour on the
    /// map without changing a single block code, so a palette built from one is
    /// correct for the person who sent it and wrong for the server. The colours
    /// this mod ships and the ones an ordinary player fills a gap with are both
    /// good enough to draw with and neither is anybody's decision.
    ///
    /// So the map takes an admin's as the one it should look like, and this is
    /// how it remembers whether it has one yet. False here is what makes the
    /// server ask the next admin to join, once, whatever the coverage says —
    /// see <see cref="PaletteExchange"/>.
    /// </summary>
    public bool FromAdmin { get; set; }

    /// <summary>
    /// The server's own mod set when this was written. Not part of the shared
    /// fingerprint — a client could never match it — but a change here means the
    /// textures may have moved under the same block ids.
    /// </summary>
    public string ModStamp { get; set; } = "";

    /// <summary>How many blocks had textures to read when this was built.</summary>
    public int Textured { get; set; }

    /// <summary>Block code to appearance. Codes, not ids: ids are per world.</summary>
    public Dictionary<string, PaletteEntry> Blocks { get; set; } = new();

    /// <summary>Blocks that came out with a colour.</summary>
    public int Coloured => Blocks.Values.Count(entry => entry.Rgb is not null);

    /// <summary>
    /// The blocks this palette cannot draw but should be able to: they have
    /// something to show and no colour was worked out for them.
    ///
    /// Sorted, because this set is compared against itself between one palette
    /// and the next to decide whether asking again would achieve anything, and a
    /// comparison of two dictionary orderings is a comparison of nothing.
    ///
    /// A palette written before <see cref="PaletteEntry.Invisible"/> existed says
    /// nothing about which of its colourless blocks draw, so it reports none —
    /// the old reading, and the one that cannot invent work for a server whose
    /// palette is fine.
    /// </summary>
    public IReadOnlyList<string> Uncoloured => Blocks
        .Where(pair => pair.Value.Rgb is null && pair.Value.Invisible == false)
        .Select(pair => pair.Key)
        .OrderBy(code => code, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// How much of the block registry came out with a colour. A dedicated server,
    /// whose install ships almost no block textures, scores near zero however many
    /// blocks it lists — which is the signal that a client must supply one.
    ///
    /// Measured against every block rather than only those with a textures entry,
    /// because a shape file can give a colour to a block that has neither.
    /// </summary>
    public double Coverage => Blocks.Count == 0 ? 0 : (double)Coloured / Blocks.Count;

    /// <summary>Fills this palette's gaps from another. Neither is modified.</summary>
    public static Palette Merge(Palette preferred, Palette filler)
    {
        var merged = new Palette
        {
            Version = preferred.Version,
            GameVersion = preferred.GameVersion,
            Fingerprint = preferred.Fingerprint,
            ModStamp = preferred.ModStamp.Length > 0 ? preferred.ModStamp : filler.ModStamp,
            Source = preferred.Source,
            // Either side having come from an admin is enough: the question this
            // answers is whether an admin has ever settled these colours, and a
            // gap filled from elsewhere afterwards does not unsettle them.
            FromAdmin = preferred.FromAdmin || filler.FromAdmin,
            Textured = Math.Max(preferred.Textured, filler.Textured),
            Blocks = new Dictionary<string, PaletteEntry>(preferred.Blocks),
        };

        foreach (var (code, entry) in filler.Blocks)
        {
            if (!merged.Blocks.TryGetValue(code, out var existing) || existing.Rgb is null)
            {
                merged.Blocks[code] = entry;
            }
        }

        return merged;
    }

    /// <summary>Where the palette lives inside the export directory.</summary>
    public static string PathIn(string exports) => Path.Combine(exports, "palette.json");

    /// <summary>Reads a palette back, or null when there is not a usable one.</summary>
    public static Palette? Read(string path, ILogger? log = null)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<Palette>(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            // Said out loud, because the caller cannot tell this from "there is
            // no palette yet" and the two have opposite consequences: one is a
            // fresh map, the other is a map's colours being thrown away. A
            // palette that will not parse is the only way a good one is lost
            // without the block registry having moved, and it used to be silent.
            log?.Warning(
                "[witchlight] {0} exists but could not be read ({1}) — treating it as no palette "
                + "at all, so any colours in it are lost",
                path,
                error.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes the palette, unless it is already what is on disk. Returns whether
    /// anything was written.
    ///
    /// The comparison is the point, not the saving of a megabyte. The map service
    /// watches this file and a palette arriving means every colour may have moved,
    /// so it drops every tile and redraws the stored zoom levels — which is
    /// seconds of a blank map. Two admins joining a server used to cost that twice
    /// over for two identical palettes. A rewrite that changes nothing now costs
    /// nothing.
    /// </summary>
    public bool Write(string path) =>
        Disk.Write(path, JsonConvert.SerializeObject(this, Formatting.Indented));
}
