using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Common;

namespace Mapstique;

/// <summary>
/// The pictures a marker is drawn with.
///
/// Waypoints carry an icon by name — `gravestone`, `home`, `trader` — and the
/// game draws each from an SVG under `textures/icons/worldmap`. They are
/// silhouettes filled with a single near-black, which is why the game can tint
/// one any colour and why the map can too.
///
/// Every domain is asked, not just the game's own, so a mod that adds markers
/// adds its icons here without anything knowing about it in advance.
///
/// The file names carry a sort prefix the game uses to order them in its own
/// menu — `0-circle`, `01-turnip` — and a waypoint refers to the name without
/// it, so the prefix is dropped on the way out.
/// </summary>
public static class Icons
{
    private const string AssetPath = "textures/icons/worldmap";

    /// <summary>Writes every icon the server can see. Returns how many, and from where.</summary>
    public static (int Written, string From) Export(ICoreAPI api, string exports)
    {
        var dir = DirectoryIn(exports);
        var domains = new SortedSet<string>(StringComparer.Ordinal);
        var written = 0;

        try
        {
            Directory.CreateDirectory(dir);
            foreach (var asset in api.Assets.GetMany(AssetPath, null, true))
            {
                if (asset?.Location is null || !asset.Location.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = NameOf(asset.Location.GetName());
                if (name is null)
                {
                    continue;
                }

                var data = asset.Data;
                if (data is null || data.Length == 0)
                {
                    continue;
                }

                File.WriteAllBytes(Path.Combine(dir, name + ".svg"), data);
                domains.Add(asset.Location.Domain ?? "game");
                written++;
            }
        }
        catch (Exception error)
        {
            api.Logger.Warning("[mapstique] could not write marker icons: {0}", error.Message);
        }

        return (written, domains.Count == 0 ? "nowhere" : string.Join(", ", domains));
    }

    /// <summary>
    /// Writes icons a client sent. Returns how many were new or different.
    ///
    /// An icon already on disk and unchanged is left alone, so a client sending
    /// the same set twice does not rewrite files the map service is watching.
    /// </summary>
    public static int Accept(IReadOnlyList<(string Name, byte[] Svg)> icons, string exports)
    {
        var dir = DirectoryIn(exports);
        var written = 0;

        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (name, svg) in icons)
            {
                var safe = NameOf(name);
                if (safe is null || svg is null || svg.Length == 0)
                {
                    continue;
                }

                var path = Path.Combine(dir, safe + ".svg");
                if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(svg))
                {
                    continue;
                }

                File.WriteAllBytes(path, svg);
                written++;
            }
        }
        catch (Exception)
        {
            // A picture that cannot be written is a marker drawn as a plain shape,
            // which is not worth failing a join over.
        }

        return written;
    }

    /// <summary>Where the icons land, beside everything else the map service reads.</summary>
    public static string DirectoryIn(string exports) => Path.Combine(exports, "icons");

    /// <summary>
    /// The name a waypoint would use for this file, or null if it cannot be one.
    ///
    /// The name becomes a file and then part of a URL, and it arrives from
    /// whatever mods are installed, so it is reduced to the characters that are
    /// safe in both rather than trusted.
    /// </summary>
    public static string? NameOf(string fileName)
    {
        var name = fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

        // The sort prefix the game orders its menu by, which a waypoint omits.
        var dash = name.IndexOf('-');
        if (dash > 0 && IsAllDigits(name[..dash]))
        {
            name = name[(dash + 1)..];
        }

        var safe = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            {
                safe.Append(c);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                safe.Append(char.ToLowerInvariant(c));
            }
        }

        return safe.Length == 0 ? null : safe.ToString();
    }

    private static bool IsAllDigits(string text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return text.Length > 0;
    }
}
