using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Turning a texture into the one colour that stands for it.
///
/// Nothing here knows what a block is. It knows how to find the PNG behind an
/// asset reference and how to average one, which is a question the palette asks
/// of block textures, of overlay textures and of the textures a shape file names
/// — three callers that were each spelling out the same four accumulator
/// variables.
/// </summary>
public static class TextureColours
{
    /// <summary>
    /// A running average of pixels, weighted by alpha.
    ///
    /// Weighted so that the transparent parts of a leaf or a grass tuft do not
    /// wash it out: a fern is mostly nothing, and counting that nothing as black
    /// gives every fern the colour of a shadow.
    ///
    /// A value that carries its own four numbers, rather than four locals passed
    /// by reference to a function that adds to them. The old shape could not be
    /// read without checking what each `ref double` was.
    /// </summary>
    public struct Average
    {
        private double _red, _green, _blue, _weight;

        /// <summary>Whether anything at all was opaque enough to count.</summary>
        public readonly bool Any => _weight > 0;

        /// <summary>Adds every pixel of one texture file.</summary>
        public void Add(IAsset asset)
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
                    _red += pixel.Red * alpha;
                    _green += pixel.Green * alpha;
                    _blue += pixel.Blue * alpha;
                    _weight += alpha;
                }
            }
        }

        /// <summary>Adds every texture file behind one reference.</summary>
        public void AddAll(ICoreAPI api, AssetLocation texture)
        {
            foreach (var asset in Resolve(api, texture))
            {
                Add(asset);
            }
        }

        /// <summary>The average as CSS, or null where nothing was counted.</summary>
        public readonly string? Hex => _weight <= 0
            ? null
            : $"#{Round(_red):x2}{Round(_green):x2}{Round(_blue):x2}";

        private readonly int Round(double channel) => (int)Math.Round(channel / _weight);
    }

    /// <summary>
    /// The average colour of one of a block's textures, with its overlays.
    ///
    /// Textures are shared between block variants, so each is decoded once and
    /// the answer kept for the rest of the build.
    /// </summary>
    public static string? Of(ICoreAPI api, CompositeTexture texture)
    {
        var overlays = Overlays(texture);
        var key = texture.Base + "|" + string.Join(",", overlays.Select(overlay => overlay.ToString()));
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var decoded = Decode(api, texture, overlays);
        Cache[key] = decoded;
        return decoded;
    }

    /// <summary>Keeps an answer against anything already worked out for it.</summary>
    public static string? Once(string key, Func<string?> work)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var decoded = work();
        Cache[key] = decoded;
        return decoded;
    }

    /// <summary>Starts a fresh build, keeping nothing from the last one.</summary>
    public static void Forget() => Cache.Clear();

    private static readonly Dictionary<string, string?> Cache = new();

    private static string? Decode(ICoreAPI api, CompositeTexture texture, List<AssetLocation> overlays)
    {
        // Grass-covered soil is a dirt texture with a grass overlay on top, and
        // the overlay is the part anyone looking at a map cares about. Averaging
        // the base alone turns every meadow into mud.
        var average = new Average();
        foreach (var overlay in overlays)
        {
            average.AddAll(api, overlay);
        }
        if (average.Any)
        {
            return average.Hex;
        }

        average.AddAll(api, texture.Base);
        return average.Hex;
    }

    private static List<AssetLocation> Overlays(CompositeTexture texture)
    {
        return (texture.BlendedOverlays ?? Array.Empty<BlendedOverlayTexture>())
            .Select(overlay => overlay?.Base)
            .Where(location => location is not null)
            .Select(location => location!)
            .ToList();
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
    public static IEnumerable<IAsset> Resolve(ICoreAPI api, AssetLocation texture)
    {
        return Matching(api, texture.Clone().WithPathPrefixOnce("textures/"), ".png")
            .Select(location => api.Assets.TryGet(location))
            .Where(asset => asset is not null)
            .Select(asset => asset!);
    }

    /// <summary>
    /// Every asset one reference names, expanding a wildcard where there is one.
    ///
    /// Ordered, so that a block whose texture varies gets the same average on
    /// every machine and every run rather than whatever the filesystem listed
    /// first.
    /// </summary>
    public static IEnumerable<AssetLocation> Matching(
        ICoreAPI api,
        AssetLocation path,
        string extension)
    {
        if (!path.Path.Contains('*'))
        {
            return new[] { path.WithPathAppendixOnce(extension) };
        }

        return api.Assets
            .GetLocations(path.Path[..path.Path.IndexOf('*')], path.Domain)
            .Where(location => location.Path.EndsWith(extension, StringComparison.Ordinal))
            .OrderBy(location => location.Path, StringComparer.Ordinal);
    }
}
