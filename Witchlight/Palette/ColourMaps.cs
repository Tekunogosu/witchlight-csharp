using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// The images that tint a block for where and when it is.
///
/// Grass, leaves and water ship as greyscale masks and take their colour from
/// one of these, looked up by temperature and rainfall. The renderer needs the
/// actual pictures to do that, and mods can add their own — so they are copied
/// out beside the palette rather than named in it and hoped for.
/// </summary>
public static class ColourMaps
{
    /// <summary>Where they land, beside everything else the map service reads.</summary>
    public static string DirectoryIn(string exports) => Path.Combine(exports, "colormaps");

    /// <summary>
    /// Copies every colour map this side can see.
    ///
    /// Says how many were written and how many there are: a restart on unchanged
    /// assets writes none, and "0 written" on its own reads exactly like a server
    /// that found no colour maps at all — which is a forest drawn grey.
    /// </summary>
    public static (int Written, int Found) Export(ICoreAPI api, string exports)
    {
        var directory = DirectoryIn(exports);
        Directory.CreateDirectory(directory);
        var written = 0;
        var found = 0;
        var padding = new Dictionary<string, int>();

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
                if (api.Assets.TryGet(path) is not { } texture)
                {
                    continue;
                }

                found++;
                padding[map.Code] = map.Padding;

                // Written only where it differs: the map service watches this
                // directory, and a colour map arriving means every tinted block
                // may have moved.
                if (Disk.WriteBytes(Path.Combine(directory, map.Code + ".png"), texture.Data))
                {
                    written++;
                }
            }
        }

        // How far into each picture the usable part begins. The climate maps are
        // a 256 square inside a 264 one, and reading the border as though it were
        // the map puts every lookup a few pixels out — worst at the extremes,
        // which is exactly where the hottest and coldest ground is. The number is
        // the asset's own and cannot be told from the picture, so it travels
        // beside it.
        Disk.Write(
            Path.Combine(directory, "padding.json"), JsonConvert.SerializeObject(padding));

        return (written, found);
    }
}
