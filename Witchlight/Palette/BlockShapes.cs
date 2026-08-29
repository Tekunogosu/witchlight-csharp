using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// What a block's shape file says about how it looks.
///
/// A fern has no `textures` block at all: it is a shape, and the shape file names
/// the textures and carries the colour maps that tint them. Without reading it
/// those blocks have no colour and no tint, and the map shows a grey hole
/// wherever one grows.
///
/// Two answers come out of one read, so they are asked of one place: the average
/// colour of everything the shape draws with, and the tint its elements declare.
/// Both are kept per shape, because a shape is shared by every variant of a block
/// and a growth stage is not a different plant.
/// </summary>
public static class BlockShapes
{
    /// <summary>Starts a fresh build, keeping nothing from the last one.</summary>
    public static void Forget() => Tints.Clear();

    /// <summary>
    /// The average colour of every texture this block's shape uses, or null where
    /// it has no shape or the shape names nothing that loads.
    /// </summary>
    public static string? AverageColour(ICoreAPI api, Block block)
    {
        var shape = block.Shape?.Base;
        return shape is null
            ? null
            : TextureColours.Once("shape:" + shape, () => Decode(api, shape));
    }

    /// <summary>
    /// The tint a block's shape declares, for a block that declares none itself.
    ///
    /// Only ever known for a shape that has already been decoded, which is the
    /// order the palette builds in: a block with no textures of its own is read
    /// through its shape, and that read is what fills this in.
    /// </summary>
    public static (string? Climate, string? Season) TintOf(Block block)
    {
        var shape = block.Shape?.Base?.ToString();
        return shape is not null && Tints.TryGetValue(shape, out var tint) ? tint : (null, null);
    }

    /// <summary>Tints found in shape files, by shape path.</summary>
    private static readonly Dictionary<string, (string? Climate, string? Season)> Tints = new();

    private static string? Decode(ICoreAPI api, AssetLocation shape)
    {
        var path = shape.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        var average = new TextureColours.Average();

        // Shape paths carry the same wildcards texture paths do.
        foreach (var candidate in TextureColours.Matching(api, path, ".json"))
        {
            var asset = api.Assets.TryGet(candidate);
            if (asset is null)
            {
                continue;
            }

            ShapeFile? read;
            try
            {
                read = asset.ToObject<ShapeFile>();
            }
            catch (Exception)
            {
                continue;
            }

            var tint = FirstTint(read?.Elements);
            if (tint.Climate is not null || tint.Season is not null)
            {
                Tints[shape.ToString()] = tint;
            }

            foreach (var texture in read?.Textures?.Values ?? NoTextures)
            {
                // A generic shape can still hold an unresolved variant.
                if (string.IsNullOrEmpty(texture) || texture.Contains('{'))
                {
                    continue;
                }

                average.AddAll(api, new AssetLocation(shape.Domain, texture));
            }

            // One variant is enough: they differ in arrangement, not colour.
            if (average.Any)
            {
                break;
            }
        }

        return average.Hex;
    }

    /// <summary>A shape file that names none, so the loop below has nothing to walk.</summary>
    private static readonly Dictionary<string, string>.ValueCollection NoTextures =
        new Dictionary<string, string>().Values;

    /// <summary>
    /// The parts of a shape file this needs: its textures, and the colour maps
    /// its elements are tinted by.
    /// </summary>
    private class ShapeFile
    {
        public Dictionary<string, string>? Textures { get; set; }
        public List<Element>? Elements { get; set; }
    }

    private class Element
    {
        public string? ClimateColorMap { get; set; }
        public string? SeasonColorMap { get; set; }
        public List<Element>? Children { get; set; }
    }

    /// <summary>
    /// The first tint declared anywhere in a shape's element tree. A fern keeps
    /// its colour maps on the child elements that make up each frond, not on the
    /// element at the top.
    /// </summary>
    private static (string? Climate, string? Season) FirstTint(List<Element>? elements, int depth = 0)
    {
        if (elements is null || depth > MaxDepth)
        {
            return (null, null);
        }

        foreach (var element in elements)
        {
            if (element.ClimateColorMap is not null || element.SeasonColorMap is not null)
            {
                return (element.ClimateColorMap, element.SeasonColorMap);
            }

            var nested = FirstTint(element.Children, depth + 1);
            if (nested.Climate is not null || nested.Season is not null)
            {
                return nested;
            }
        }

        return (null, null);
    }

    /// <summary>How far down a shape's elements to look before giving up.</summary>
    private const int MaxDepth = 8;
}
