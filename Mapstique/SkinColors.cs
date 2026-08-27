using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Common;

namespace Mapstique;

/// <summary>
/// What colour each skin part variant is.
///
/// A player's appearance is a set of variant names — `skin4`, `mossgreen`,
/// `azure` — and the server can read those. What it cannot read is what any of
/// them look like: the definitions give a variant a texture and no colour, and
/// the textures are images a dedicated server does not ship, the same gap that
/// leaves it unable to build a block palette or draw a marker.
///
/// So the colours come from a client, once. They are the same for everybody, so
/// this is one small table rather than something asked per player, and it changes
/// only when a mod adds variants.
/// </summary>
public static class SkinColors
{
    public static string PathIn(string exports) => Path.Combine(exports, "skincolors.json");

    /// <summary>How many colours are stored, for saying so in the status.</summary>
    public static int Count(string exports)
    {
        try
        {
            var text = File.ReadAllText(PathIn(exports));
            var count = 0;
            for (var at = text.IndexOf(':'); at >= 0; at = text.IndexOf(':', at + 1))
            {
                count++;
            }
            return count;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The names already known, so a client sends only what is new.</summary>
    public static List<string> Known(string exports)
    {
        var names = new List<string>();
        try
        {
            var text = File.ReadAllText(PathIn(exports));
            var at = 0;
            while (true)
            {
                var open = text.IndexOf('"', at);
                if (open < 0) break;
                var close = text.IndexOf('"', open + 1);
                if (close < 0) break;
                var name = text[(open + 1)..close];
                // Names and values alternate; a value starts with a hash.
                if (!name.StartsWith('#')) names.Add(name);
                at = close + 1;
            }
        }
        catch (Exception)
        {
            // Nothing stored yet is not a failure.
        }
        return names;
    }

    /// <summary>
    /// Merges what a client sent into what is stored.
    ///
    /// Merged, like palettes and icons: a client only has the art for the mods it
    /// runs, so two admins with different mod sets cover between them more than
    /// either alone. Returns how many were new.
    /// </summary>
    public static int Accept(IReadOnlyList<(string Name, string Hex)> colours, string exports)
    {
        var held = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var name in Known(exports))
            {
                held[name] = "";
            }
        }
        catch (Exception)
        {
            // Start from nothing.
        }

        // Re-read properly: the crude scan above only recovers names.
        held.Clear();
        try
        {
            var text = File.ReadAllText(PathIn(exports));
            foreach (var pair in text.Split(','))
            {
                var bits = pair.Split(':');
                if (bits.Length != 2) continue;
                var name = bits[0].Trim().Trim('{', '}', '"', ' ');
                var hex = bits[1].Trim().Trim('{', '}', '"', ' ');
                if (Valid(name) && IsHex(hex)) held[name] = hex;
            }
        }
        catch (Exception)
        {
            // Nothing stored yet.
        }

        var added = 0;
        foreach (var (name, hex) in colours)
        {
            if (!Valid(name) || !IsHex(hex)) continue;
            if (held.TryGetValue(name, out var was) && was == hex) continue;
            held[name] = hex;
            added++;
        }

        if (added == 0)
        {
            return 0;
        }

        var json = new StringBuilder("{");
        var first = true;
        foreach (var (name, hex) in held)
        {
            if (!first) json.Append(',');
            json.Append('"').Append(name).Append("\":\"").Append(hex).Append('"');
            first = false;
        }
        json.Append('}');

        try
        {
            Directory.CreateDirectory(exports);
            var path = PathIn(exports);
            var temporary = path + ".part";
            File.WriteAllText(temporary, json.ToString());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            return 0;
        }

        return added;
    }

    /// <summary>A variant name becomes part of a JSON key; only plain ones pass.</summary>
    public static bool Valid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
        foreach (var c in name)
        {
            if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsHex(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#') return false;
        for (var at = 1; at < 7; at++)
        {
            if (!Uri.IsHexDigit(hex[at])) return false;
        }
        return true;
    }
}
