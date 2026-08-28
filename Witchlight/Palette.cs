using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

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
    /// Average colour, or null for a block with nothing to draw — air, an
    /// invisible helper, a shape-only block. Recorded either way so the renderer
    /// can tell "nothing here" from "a block I have never heard of".
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public string? Rgb { get; set; }

    /// <summary>Name of the climate colour map, when the block is climate tinted.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ClimateMap { get; set; }

    /// <summary>Name of the season colour map, when the block is seasonally tinted.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? SeasonMap { get; set; }
}

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
/// </summary>
public static class PaletteBuilder
{
    /// <summary>Textures are shared between block variants, so decode each once.</summary>
    private static readonly Dictionary<string, string?> AverageCache = new();

    /// <summary>Sample of what could not be resolved, for diagnosing gaps.</summary>
    public static readonly List<string> Failures = new();

    public static Palette Build(ICoreAPI api, out int missing)
    {
        AverageCache.Clear();
        Failures.Clear();
        FailureCounts.Clear();
        ShapeTints.Clear();
        var palette = new Palette
        {
            GameVersion = GameVersion.ShortGameVersion,
            Fingerprint = Fingerprint.Of(api),
            ModStamp = Fingerprint.LocalStamp(api),
            Source = api.Side == EnumAppSide.Client ? "client" : "server",
            Textured = api.World.Blocks.Count(block => block?.Textures is { Count: > 0 }),
        };
        missing = 0;

        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is null)
            {
                continue;
            }

            var texture = block.Textures is { Count: > 0 } ? SelectTexture(block) : null;
            var rgb = texture is null
                // Plants and other shape-based blocks declare no textures of
                // their own — theirs live in the shape file they point at.
                ? AverageOfShape(api, block)
                : AverageOf(api, texture);
            if (rgb is null)
            {
                // Still recorded: the renderer needs to know the block exists so
                // it can say "no colour" rather than painting an error.
                missing++;
                Note(block.Code.ToString(), texture is null
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

        return palette;
    }

    /// <summary>
    /// Records why a block got no colour. Invisible helper blocks are the bulk of
    /// the misses and are not interesting, so they are counted rather than listed.
    /// </summary>
    /// <summary>
    /// Which colour map tints this block on a map.
    ///
    /// The map-specific properties are the right answer where they are set, but
    /// some block classes only populate them in OnCollectTextures, which never
    /// runs on a server — BlockLeaves is the one that matters, and without the
    /// fallback every forest renders grey. The plain fields come straight from
    /// the block JSON and are populated on both sides.
    /// </summary>
    private static string? ClimateTintOf(Block block)
    {
        return block.ClimateColorMapForMap ?? block.ClimateColorMap ?? ShapeTintOf(block).Climate;
    }

    private static string? SeasonTintOf(Block block)
    {
        return block.SeasonColorMapForMap ?? block.SeasonColorMap ?? ShapeTintOf(block).Season;
    }

    /// <summary>The tint a block's shape declares, when the block declares none.</summary>
    private static (string? Climate, string? Season) ShapeTintOf(Block block)
    {
        var shape = block.Shape?.Base?.ToString();
        return shape is not null && ShapeTints.TryGetValue(shape, out var tint) ? tint : (null, null);
    }

    private static void Note(string code, string message)
    {
        var group = code.Contains("multiblock") || code.EndsWith(":air") ? "(helper blocks)" : code.Split('-')[0];
        FailureCounts.TryGetValue(group, out var count);
        FailureCounts[group] = count + 1;

        if (group != "(helper blocks)" && Failures.Count < 25)
        {
            Failures.Add(message);
        }
    }

    public static readonly Dictionary<string, int> FailureCounts = new();

    /// <summary>
    /// The texture the game would use for this block's map colour, mirroring
    /// Block.LoadTextureSubIdForBlockColor.
    /// </summary>
    private static CompositeTexture? SelectTexture(Block block)
    {
        var code = block.Attributes?["textureCodeForBlockColor"].AsString(null);
        if (code is not null && block.Textures.TryGetValue(code, out var named) && named?.Base is not null)
        {
            return named;
        }

        // A coverage layer is what is actually on top: grass over soil, snow over
        // rock. The game names it `specialSecondTexture`, and without it every
        // meadow on the map is the colour of the dirt underneath it.
        if (block.Textures.TryGetValue("specialSecondTexture", out var covering) && covering?.Base is not null)
        {
            return covering;
        }

        if (block.Textures.TryGetValue("up", out var up) && up?.Base is not null)
        {
            return up;
        }

        foreach (var texture in block.Textures.Values)
        {
            if (texture?.Base is not null)
            {
                return texture;
            }
        }

        return null;
    }

    /// <summary>
    /// The average colour of every texture a block's shape uses.
    ///
    /// A fern has no `textures` block at all: it is a shape, and the shape file
    /// names the textures. Without this those blocks have no colour, and the map
    /// shows a hole wherever one grows.
    /// </summary>
    private static string? AverageOfShape(ICoreAPI api, Block block)
    {
        var shape = block.Shape?.Base;
        if (shape is null)
        {
            return null;
        }

        var key = "shape:" + shape;
        if (AverageCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = DecodeShape(api, shape);
        AverageCache[key] = result;
        return result;
    }

    private static string? DecodeShape(ICoreAPI api, AssetLocation shape)
    {
        var path = shape.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");

        // Shape paths carry the same wildcards texture paths do.
        var candidates = path.Path.Contains('*')
            ? api.Assets
                .GetLocations(path.Path[..path.Path.IndexOf('*')], path.Domain)
                .Where(location => location.Path.EndsWith(".json"))
                .OrderBy(location => location.Path, StringComparer.Ordinal)
                .ToList()
            : new List<AssetLocation> { path };

        double red = 0, green = 0, blue = 0, weight = 0;

        foreach (var candidate in candidates)
        {
            var asset = api.Assets.TryGet(candidate);
            if (asset is null)
            {
                continue;
            }

            ShapeTextures? textures;
            try
            {
                textures = asset.ToObject<ShapeTextures>();
            }
            catch (Exception)
            {
                continue;
            }

            var tint = FindTint(textures?.Elements);
            if (tint.Climate is not null || tint.Season is not null)
            {
                ShapeTints[shape.ToString()] = tint;
            }

            foreach (var texture in textures?.Textures?.Values ?? Enumerable.Empty<string>())
            {
                // A generic shape can still hold an unresolved variant.
                if (string.IsNullOrEmpty(texture) || texture.Contains('{'))
                {
                    continue;
                }

                foreach (var file in Resolve(api, new AssetLocation(shape.Domain, texture)))
                {
                    Accumulate(file, ref red, ref green, ref blue, ref weight);
                }
            }

            // One variant is enough: they differ in arrangement, not colour.
            if (weight > 0)
            {
                break;
            }
        }

        return weight <= 0 ? null : Hex(red, green, blue, weight);
    }

    /// <summary>
    /// The parts of a shape file this needs: its textures, and the colour maps
    /// its elements are tinted by. A fern is grey without the latter, the same
    /// way leaves are.
    /// </summary>
    private class ShapeTextures
    {
        public Dictionary<string, string>? Textures { get; set; }
        public List<ShapeElement>? Elements { get; set; }
    }

    private class ShapeElement
    {
        public string? ClimateColorMap { get; set; }
        public string? SeasonColorMap { get; set; }
        public List<ShapeElement>? Children { get; set; }
    }

    /// <summary>
    /// The first tint declared anywhere in a shape's element tree. A fern keeps
    /// its colour maps on the child elements that make up each frond, not on the
    /// element at the top.
    /// </summary>
    private static (string? Climate, string? Season) FindTint(List<ShapeElement>? elements, int depth = 0)
    {
        if (elements is null || depth > 8)
        {
            return (null, null);
        }

        foreach (var element in elements)
        {
            if (element.ClimateColorMap is not null || element.SeasonColorMap is not null)
            {
                return (element.ClimateColorMap, element.SeasonColorMap);
            }

            var nested = FindTint(element.Children, depth + 1);
            if (nested.Climate is not null || nested.Season is not null)
            {
                return nested;
            }
        }

        return (null, null);
    }

    /// <summary>Tints found in shape files, by shape path.</summary>
    private static readonly Dictionary<string, (string? Climate, string? Season)> ShapeTints = new();

    /// <summary>
    /// The average colour of a texture, weighted by alpha so that the
    /// transparent parts of a leaf or a grass tuft do not wash it out.
    /// </summary>
    private static string? AverageOf(ICoreAPI api, CompositeTexture texture)
    {
        var key = texture.Base.ToString() + "|" + string.Join(
            ",",
            (texture.BlendedOverlays ?? Array.Empty<BlendedOverlayTexture>())
                .Select(overlay => overlay.Base?.ToString() ?? ""));
        if (AverageCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = Decode(api, texture);
        AverageCache[key] = result;
        return result;
    }

    private static string? Decode(ICoreAPI api, CompositeTexture texture)
    {
        // Grass-covered soil is a dirt texture with a grass overlay on top, and
        // the overlay is the part anyone looking at a map cares about. Averaging
        // the base alone turns every meadow into mud.
        var overlays = (texture.BlendedOverlays ?? Array.Empty<BlendedOverlayTexture>())
            .Where(overlay => overlay?.Base is not null)
            .SelectMany(overlay => Resolve(api, overlay.Base))
            .ToList();

        double red = 0, green = 0, blue = 0, weight = 0;

        if (overlays.Count > 0)
        {
            foreach (var asset in overlays)
            {
                Accumulate(asset, ref red, ref green, ref blue, ref weight);
            }
            if (weight > 0)
            {
                return Hex(red, green, blue, weight);
            }
        }

        foreach (var asset in Resolve(api, texture.Base))
        {
            Accumulate(asset, ref red, ref green, ref blue, ref weight);
        }

        return weight <= 0 ? null : Hex(red, green, blue, weight);
    }

    private static string Hex(double red, double green, double blue, double weight)
    {
        return $"#{(int)Math.Round(red / weight):x2}{(int)Math.Round(green / weight):x2}{(int)Math.Round(blue / weight):x2}";
    }

    /// <summary>
    /// The texture files behind one texture reference.
    ///
    /// A block's texture may be a wildcard — `coral/shelf/blue*` — which the
    /// client expands at bake time into the set of alternates it picks from per
    /// position. There is no atlas here to bake against, so the wildcard is
    /// expanded the same way and every match contributes to the average, which is
    /// the honest colour for a block that varies.
    /// </summary>
    private static IEnumerable<IAsset> Resolve(ICoreAPI api, AssetLocation texture)
    {
        var path = texture.Clone().WithPathPrefixOnce("textures/");

        if (!path.Path.Contains('*'))
        {
            var asset = api.Assets.TryGet(path.WithPathAppendixOnce(".png"));
            return asset is null ? Enumerable.Empty<IAsset>() : new[] { asset };
        }

        var prefix = path.Path[..path.Path.IndexOf('*')];
        return api.Assets
            .GetLocations(prefix, path.Domain)
            .Where(location => location.Path.EndsWith(".png"))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .Select(location => api.Assets.TryGet(location))
            .Where(asset => asset is not null)!;
    }

    /// <summary>
    /// Adds one texture to the running average, weighted by alpha so the
    /// transparent parts of a leaf or a grass tuft do not wash it out.
    /// </summary>
    private static void Accumulate(IAsset asset, ref double red, ref double green, ref double blue, ref double weight)
    {
        using var bitmap = SKBitmap.Decode(asset.Data);
        if (bitmap is null)
        {
            return;
        }

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha == 0)
                {
                    continue;
                }
                var alpha = pixel.Alpha / 255.0;
                red += pixel.Red * alpha;
                green += pixel.Green * alpha;
                blue += pixel.Blue * alpha;
                weight += alpha;
            }
        }
    }

    /// <summary>
    /// Copies the colour map textures next to the palette. Blocks like grass,
    /// leaves and water ship as greyscale masks and get their colour from one of
    /// these, looked up by climate — the renderer needs the actual images to do
    /// that, and mods can add their own.
    /// </summary>
    public static int ExportColorMaps(ICoreAPI api, string directory)
    {
        Directory.CreateDirectory(directory);
        var written = 0;

        foreach (var asset in api.Assets.GetMany("config/colormaps.json"))
        {
            List<ColorMap>? maps;
            try
            {
                maps = asset.ToObject<List<ColorMap>>();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var map in maps ?? new List<ColorMap>())
            {
                if (map?.Code is null || map.Texture?.Base is null)
                {
                    continue;
                }

                var path = map.Texture.Base.Clone()
                    .WithPathPrefixOnce("textures/")
                    .WithPathAppendixOnce(".png");
                var texture = api.Assets.TryGet(path);
                if (texture is null)
                {
                    continue;
                }

                File.WriteAllBytes(Path.Combine(directory, map.Code + ".png"), texture.Data);
                written++;
            }
        }

        return written;
    }

    /// <summary>Reads a palette back, or null when there is not a usable one.</summary>
    public static Palette? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<Palette>(File.ReadAllText(path))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the palette, unless it is already what is on disk.
    ///
    /// Returns whether anything was written.
    ///
    /// The comparison is the point, not the saving of a megabyte. The map service
    /// watches this file and a palette arriving means every colour may have moved,
    /// so it drops every tile and redraws the stored zoom levels — which is
    /// seconds of a blank map. Two admins joining a server used to cost that twice
    /// over for two identical palettes. A rewrite that changes nothing now costs
    /// nothing.
    /// </summary>
    public static bool Write(Palette palette, string path)
    {
        return Disk.Write(path, JsonConvert.SerializeObject(palette, Formatting.Indented));
    }
}
