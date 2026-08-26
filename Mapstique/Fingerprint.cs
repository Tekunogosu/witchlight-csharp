using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;

namespace Mapstique;

/// <summary>
/// What a palette depends on, reduced to a string.
///
/// This is the **block registry** and nothing else: game version, then every
/// block's id and code. The server sends its registry to clients on join, so a
/// connected client computes exactly the same value — which is what makes it
/// usable as a shared token between the two.
///
/// Mod lists deliberately play no part. A client has client-side mods the server
/// has never heard of, and the server has server-side ones the client never
/// receives, so a fingerprint covering them could never agree across the wire.
/// <see cref="LocalStamp"/> covers what that would have caught.
/// </summary>
public static class Fingerprint
{
    public static string Of(ICoreAPI api)
    {
        var builder = new StringBuilder();
        builder.Append(GameVersionOf()).Append('|');

        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is not null)
            {
                builder.Append(block.Id).Append('=').Append(block.Code).Append(';');
            }
        }

        return Hash(builder.ToString());
    }

    /// <summary>
    /// The mod set as this machine sees it. Never sent anywhere: the server keeps
    /// it beside its palette so that a mod updating its textures — which changes
    /// colours without moving a single block id — still invalidates the palette.
    /// </summary>
    public static string LocalStamp(ICoreAPI api)
    {
        var builder = new StringBuilder();
        foreach (var mod in api.ModLoader.Mods
                     .Where(mod => mod?.Info?.ModID is not null)
                     .OrderBy(mod => mod.Info.ModID, StringComparer.Ordinal))
        {
            builder.Append(mod.Info.ModID).Append(':').Append(mod.Info.Version).Append(';');
        }
        return Hash(builder.ToString());
    }

    private static string GameVersionOf()
    {
        return Vintagestory.API.Config.GameVersion.ShortGameVersion;
    }

    /// <summary>FNV-1a. Not a security boundary — just a way to notice a change.</summary>
    private static string Hash(string text)
    {
        var value = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            value ^= b;
            value *= 0x100000001b3UL;
        }
        return value.ToString("x16");
    }
}
