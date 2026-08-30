using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace Witchlight;

/// <summary>
/// Sent to one player when the server cannot build a usable palette itself.
/// </summary>
[ProtoContract]
public class PaletteRequest
{
    /// <summary>What the server needs a palette for; the client echoes it back.</summary>
    [ProtoMember(1)]
    public string Fingerprint { get; set; } = "";
}

/// <summary>
/// A palette on the wire, in slices.
///
/// Three things keep it small. Block codes are not sent at all — the server has
/// its own registry and can turn an id back into a code, and the codes were by
/// far the largest part of the message. Colour map names repeat across thousands
/// of blocks, so they are interned and referenced by index. And the numbers are
/// packed rather than tagged one by one.
///
/// It is still sent in slices: a server rejects an oversized packet by
/// disconnecting the client, so the size of a mod set must not decide whether
/// this works at all.
/// </summary>
[ProtoContract]
public class PaletteTable
{
    [ProtoMember(1)] public string Fingerprint { get; set; } = "";
    [ProtoMember(2)] public string GameVersion { get; set; } = "";
    [ProtoMember(3)] public int Textured { get; set; }

    /// <summary>Colour map names, referenced by index below.</summary>
    [ProtoMember(4)] public List<string> ColorMaps { get; set; } = new();

    /// <summary>Which slice this is, and how many there are in total.</summary>
    [ProtoMember(5)] public int Part { get; set; }
    [ProtoMember(6)] public int Parts { get; set; }

    [ProtoMember(7, IsPacked = true)] public List<int> Ids { get; set; } = new();

    // Zigzagged, all three: they are mostly -1, and a plain varint spends ten
    // bytes on a negative number.
    /// <summary>Packed 0xRRGGBB, or -1 for a block with nothing to draw.</summary>
    [ProtoMember(8, IsPacked = true, DataFormat = DataFormat.ZigZag)]
    public List<int> Colors { get; set; } = new();

    /// <summary>Index into <see cref="ColorMaps"/>, or -1.</summary>
    [ProtoMember(9, IsPacked = true, DataFormat = DataFormat.ZigZag)]
    public List<int> Climate { get; set; } = new();

    [ProtoMember(10, IsPacked = true, DataFormat = DataFormat.ZigZag)]
    public List<int> Season { get; set; } = new();

    /// <summary>Blocks per slice. Ten bytes each, so this is a small packet.</summary>
    public const int SliceSize = 8000;

    /// <summary>Splits a palette into packets a server will accept.</summary>
    public static List<PaletteTable> Slice(Palette palette)
    {
        var maps = new List<string>();
        var seen = new Dictionary<string, int>();
        int Intern(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }
            if (!seen.TryGetValue(name, out var at))
            {
                at = maps.Count;
                maps.Add(name);
                seen[name] = at;
            }
            return at;
        }

        var entries = palette.Blocks.Values.ToList();
        var parts = (entries.Count + SliceSize - 1) / SliceSize;
        var slices = new List<PaletteTable>();

        for (var part = 0; part < parts; part++)
        {
            var slice = new PaletteTable
            {
                Fingerprint = palette.Fingerprint,
                GameVersion = palette.GameVersion,
                Textured = palette.Textured,
                Part = part,
                Parts = parts,
            };

            foreach (var entry in entries.Skip(part * SliceSize).Take(SliceSize))
            {
                slice.Ids.Add(entry.Id);
                slice.Colors.Add(Pack(entry.Rgb));
                slice.Climate.Add(Intern(entry.ClimateMap));
                slice.Season.Add(Intern(entry.SeasonMap));
            }

            slices.Add(slice);
        }

        // Interning fills as the slices are built, so every slice carries the
        // finished list rather than a prefix of it.
        foreach (var slice in slices)
        {
            slice.ColorMaps = maps;
        }

        return slices;
    }

    /// <summary>
    /// Rebuilds a palette from every slice, using the server's own registry to
    /// turn ids back into the codes the palette is keyed by.
    /// </summary>
    public static Palette Assemble(IEnumerable<PaletteTable> slices, Func<int, string?> codeOf)
    {
        var ordered = slices.OrderBy(slice => slice.Part).ToList();
        var first = ordered.FirstOrDefault() ?? new PaletteTable();

        var palette = new Palette
        {
            GameVersion = first.GameVersion,
            Fingerprint = first.Fingerprint,
            Source = "client",
            Textured = first.Textured,
        };

        foreach (var slice in ordered)
        {
            for (var i = 0; i < slice.Ids.Count; i++)
            {
                var id = slice.Ids[i];
                var code = codeOf(id);
                if (code is null)
                {
                    continue;
                }

                palette.Blocks[code] = new PaletteEntry
                {
                    Id = id,
                    Rgb = Unpack(slice.Colors.ElementAtOrDefault(i)),
                    ClimateMap = slice.NameAt(slice.Climate.ElementAtOrDefault(i)),
                    SeasonMap = slice.NameAt(slice.Season.ElementAtOrDefault(i)),
                };
            }
        }

        return palette;
    }

    private string? NameAt(int index)
    {
        return index >= 0 && index < ColorMaps.Count ? ColorMaps[index] : null;
    }

    private static int Pack(string? rgb)
    {
        if (string.IsNullOrEmpty(rgb))
        {
            return -1;
        }
        return int.TryParse(rgb.TrimStart('#'), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }

    private static string? Unpack(int packed)
    {
        return packed < 0 ? null : $"#{packed & 0xffffff:x6}";
    }
}
