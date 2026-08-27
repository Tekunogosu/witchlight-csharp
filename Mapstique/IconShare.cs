using System;
using System.Collections.Generic;
using ProtoBuf;

namespace Mapstique;

/// <summary>Sent to one admin when the server has no marker pictures of its own.</summary>
[ProtoContract]
public class IconRequest
{
    /// <summary>Icons the server already has, so a client sends only what is new.</summary>
    [ProtoMember(1)]
    public List<string> Have { get; set; } = new();
}

/// <summary>
/// Marker pictures on the wire, in slices.
///
/// A dedicated server ships no SVG at all — its `textures` directory is there but
/// empty of them — so the pictures a marker is drawn with can only come from a
/// machine that has the game's art. Same shape as the palette, and for the same
/// reason: the server cannot build this itself and a client can.
///
/// Sliced by measured size rather than by count. An oversized packet does not
/// fail politely, it disconnects the player sending it, and how many icons a mod
/// set adds is not something this end gets to assume.
/// </summary>
[ProtoContract]
public class IconTable
{
    [ProtoMember(1)] public int Part { get; set; }
    [ProtoMember(2)] public int Parts { get; set; }

    /// <summary>Icon names, in step with <see cref="Svgs"/>.</summary>
    [ProtoMember(3)] public List<string> Names { get; set; } = new();

    /// <summary>Each icon's file, in step with <see cref="Names"/>.</summary>
    [ProtoMember(4)] public List<byte[]> Svgs { get; set; } = new();

    /// <summary>
    /// How much one packet may carry. Far below what a server will accept, and
    /// chosen against the bytes rather than a count of icons, because an icon is
    /// a file of unknown size and a mod may ship a large one.
    /// </summary>
    public const int SliceBytes = 400 * 1024;

    /// <summary>The most one icon may be. Beyond this it is not a map marker.</summary>
    public const int LargestIcon = 256 * 1024;

    /// <summary>Splits a set of icons into packets a server will accept.</summary>
    public static List<IconTable> Slice(IReadOnlyList<(string Name, byte[] Svg)> icons)
    {
        var slices = new List<IconTable>();
        var current = new IconTable();
        var carrying = 0;

        foreach (var (name, svg) in icons)
        {
            if (svg is null || svg.Length == 0 || svg.Length > LargestIcon)
            {
                continue;
            }

            if (current.Names.Count > 0 && carrying + svg.Length > SliceBytes)
            {
                slices.Add(current);
                current = new IconTable();
                carrying = 0;
            }

            current.Names.Add(name);
            current.Svgs.Add(svg);
            carrying += svg.Length + name.Length;
        }

        if (current.Names.Count > 0)
        {
            slices.Add(current);
        }

        for (var at = 0; at < slices.Count; at++)
        {
            slices[at].Part = at;
            slices[at].Parts = slices.Count;
        }

        return slices;
    }

    /// <summary>Every name and file across a full set of slices, ready to write.</summary>
    public static List<(string Name, byte[] Svg)> Assemble(IEnumerable<IconTable> slices)
    {
        var all = new List<(string, byte[])>();
        foreach (var slice in slices)
        {
            var count = Math.Min(slice.Names.Count, slice.Svgs.Count);
            for (var at = 0; at < count; at++)
            {
                var name = Icons.NameOf(slice.Names[at]);
                if (name is not null)
                {
                    all.Add((name, slice.Svgs[at]));
                }
            }
        }
        return all;
    }
}
