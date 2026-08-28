using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Witchlight;

/// <summary>
/// What a build of the palette found, beside the palette itself.
///
/// Reported rather than left in static fields the caller reads afterwards: a
/// build's findings belong to that build, and two builds in one process — which
/// a client running the command after joining does — must not read each other's.
/// </summary>
public sealed class PaletteReport
{
    /// <summary>Blocks that ended with no colour at all.</summary>
    public int Missing { get; init; }

    /// <summary>How many were missed per group of block, for the shape of it.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>A sample of what could not be resolved, for diagnosing gaps.</summary>
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Builds the block colour palette from the assets the server already has.
///
/// The established map mods build this on a client, using the client's texture
/// atlas. That is convenient rather than necessary: the textures ship with the
/// server too, the asset category is universal, and the rule the game uses to
/// pick a block's map colour is simple enough to follow here — the texture named
/// by the <c>textureCodeForBlockColor</c> attribute, else <c>up</c>, else the
/// first one. Doing it server-side means no admin has to run a client command.
///
/// Reading a texture is <see cref="TextureColours"/> and reading a shape file is
/// <see cref="BlockShapes"/>. What is left here is the choosing: which texture
/// stands for a block, and which colour map tints it.
/// </summary>
public static class PaletteBuilder
{
    public static Palette Build(ICoreAPI api, out PaletteReport report)
    {
        TextureColours.Forget();
        BlockShapes.Forget();

        var palette = new Palette
        {
            GameVersion = GameVersion.ShortGameVersion,
            Fingerprint = Fingerprint.Of(api),
            ModStamp = Fingerprint.LocalStamp(api),
            Source = api.Side == EnumAppSide.Client ? "client" : "server",
            Textured = api.World.Blocks.Count(block => block?.Textures is { Count: > 0 }),
        };

        var missed = new Misses();

        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is null)
            {
                continue;
            }

            var texture = block.Textures is { Count: > 0 } ? MapColourTexture(block) : null;
            var rgb = texture is null
                // Plants and other shape-based blocks declare no textures of
                // their own — theirs live in the shape file they point at.
                ? BlockShapes.AverageColour(api, block)
                : TextureColours.Of(api, texture);

            if (rgb is null)
            {
                // Still recorded: the renderer needs to know the block exists so
                // it can say "no colour" rather than painting an error.
                missed.Note(block.Code.ToString(), texture is null
                    ? $"{block.Code}: no texture, and shape {block.Shape?.Base} gave none either"
                    : $"{block.Code}: texture {texture.Base} did not load");
                palette.Blocks[block.Code.ToString()] = new PaletteEntry { Id = block.Id, Rgb = null };
                continue;
            }

            palette.Blocks[block.Code.ToString()] = new PaletteEntry
            {
                Id = block.Id,
                Rgb = rgb,
                ClimateMap = ClimateTintOf(block),
                SeasonMap = SeasonTintOf(block),
            };
        }

        report = missed.Report();
        return palette;
    }

    /// <summary>
    /// Which colour map tints this block on a map.
    ///
    /// The map-specific properties are the right answer where they are set, but
    /// some block classes only populate them in OnCollectTextures, which never
    /// runs on a server — BlockLeaves is the one that matters, and without the
    /// fallback every forest renders grey. The plain fields come straight from
    /// the block JSON and are populated on both sides.
    /// </summary>
    private static string? ClimateTintOf(Block block) =>
        block.ClimateColorMapForMap ?? block.ClimateColorMap ?? BlockShapes.TintOf(block).Climate;

    private static string? SeasonTintOf(Block block) =>
        block.SeasonColorMapForMap ?? block.SeasonColorMap ?? BlockShapes.TintOf(block).Season;

    /// <summary>
    /// The texture the game would use for this block's map colour, mirroring
    /// Block.LoadTextureSubIdForBlockColor.
    ///
    /// In the game's own order of preference: what the block says to use, then
    /// the coverage layer, then the top face, then whatever there is. A coverage
    /// layer is what is actually on top — grass over soil, snow over rock — and
    /// without it every meadow on the map is the colour of the dirt underneath.
    /// </summary>
    private static CompositeTexture? MapColourTexture(Block block)
    {
        var named = block.Attributes?["textureCodeForBlockColor"].AsString(null);

        foreach (var code in new[] { named, "specialSecondTexture", "up" })
        {
            if (code is not null
                && block.Textures.TryGetValue(code, out var texture)
                && texture?.Base is not null)
            {
                return texture;
            }
        }

        return block.Textures.Values.FirstOrDefault(texture => texture?.Base is not null);
    }

    /// <summary>
    /// Why blocks got no colour, as one build found it.
    ///
    /// Invisible helper blocks are the bulk of the misses and are not interesting,
    /// so they are counted rather than listed — a log of ten thousand of them is
    /// a log nobody reads to the end of.
    /// </summary>
    private sealed class Misses
    {
        private const int MostListed = 25;

        private readonly Dictionary<string, int> _counts = new();
        private readonly List<string> _failures = new();
        private int _missing;

        public void Note(string code, string message)
        {
            _missing++;

            var group = code.Contains("multiblock") || code.EndsWith(":air")
                ? Helpers
                : code.Split('-')[0];
            _counts.TryGetValue(group, out var count);
            _counts[group] = count + 1;

            if (group != Helpers && _failures.Count < MostListed)
            {
                _failures.Add(message);
            }
        }

        public PaletteReport Report() =>
            new() { Missing = _missing, Counts = _counts, Failures = _failures };

        private const string Helpers = "(helper blocks)";
    }
}
