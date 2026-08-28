using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Witchlight;

/// <summary>
/// The two things a client has that a dedicated server does not.
///
/// A dedicated server's install ships 46 block textures against a full game's
/// 9,587, and no marker art at all. So the palette and the marker pictures can
/// only come from a machine with the game's assets on it — and while a client is
/// connected it also holds the block ids that server assigned, so what it builds
/// lines up with that server's exports.
///
/// Nothing here is sent unprompted. The server asks, and only ever asks an admin.
/// </summary>
public partial class WitchlightClient
{
    /// <summary>
    /// Builds a palette and sends it, because the server asked.
    ///
    /// The server only asks admins, and only when it could not build a usable one
    /// itself — which is the normal case on a dedicated server.
    /// </summary>
    private void OnPaletteRequest(PaletteRequest request)
    {
        if (_capi is not { } capi)
        {
            return;
        }

        try
        {
            capi.ShowChatMessage("[witchlight] the server asked for a block colour palette, building it…");

            var palette = PaletteBuilder.Build(capi, out _);
            if (palette.Fingerprint != request.Fingerprint)
            {
                // The registry this client holds is not the one the server asked
                // about, so its block ids would be wrong. Said out loud rather
                // than only in a log file, since nothing else would explain the
                // map staying empty.
                var message =
                    $"[witchlight] cannot supply a palette: this client's block registry "
                    + $"({palette.Fingerprint}) is not the server's ({request.Fingerprint})";
                capi.ShowChatMessage(message);
                capi.Logger.Warning(message);
                return;
            }

            var slices = PaletteTable.Slice(palette);
            var channel = capi.Network.GetChannel(SharedServer.Channel);
            foreach (var slice in slices)
            {
                channel.SendPacket(slice);
            }

            capi.ShowChatMessage(
                $"[witchlight] sent the server a palette: {palette.Coloured} blocks coloured, "
                + $"in {slices.Count} part(s)");
        }
        catch (Exception error)
        {
            capi.Logger.Error("[witchlight] could not build a palette: {0}", error);
        }
    }

    /// <summary>
    /// Builds the palette from this client's assets and writes it beside this
    /// machine's own world data, for an operator moving it across by hand.
    /// </summary>
    private TextCommandResult OnPalette(TextCommandCallingArgs args)
    {
        if (_capi is not { } capi)
        {
            return TextCommandResult.Error("client not ready");
        }

        var directory = Path.Combine(GamePaths.DataPath, "witchlight");
        var path = Palette.PathIn(directory);

        try
        {
            var palette = PaletteBuilder.Build(capi, out var report);
            palette.Write(path);
            ColourMaps.Export(capi, directory);

            return TextCommandResult.Success(
                $"{palette.Blocks.Count} blocks written to {path}"
                + (report.Missing > 0 ? $" ({report.Missing} with nothing to draw)" : "")
                + ". Copy it to the server's witchlight folder and restart the map service.");
        }
        catch (Exception error)
        {
            capi.Logger.Error("[witchlight] palette build failed: {0}", error);
            return TextCommandResult.Error($"palette build failed: {error.Message}");
        }
    }

    /// <summary>
    /// Sends the marker pictures, because the server asked.
    ///
    /// Only what the server says it is missing, so a mod added later costs one
    /// icon rather than the whole set again.
    /// </summary>
    private void OnIconRequest(IconRequest request)
    {
        SendIcons(new HashSet<string>(request.Have ?? new List<string>(), StringComparer.Ordinal), quiet: true);
    }

    private TextCommandResult OnIcons(TextCommandCallingArgs args)
    {
        var sent = SendIcons(new HashSet<string>(StringComparer.Ordinal), quiet: false);
        return sent > 0
            ? TextCommandResult.Success($"sent {sent} marker pictures")
            : TextCommandResult.Error("this client has no marker pictures to send");
    }

    /// <summary>Reads every icon this client can see and sends what the server lacks.</summary>
    private int SendIcons(HashSet<string> already, bool quiet)
    {
        if (_capi is not { } capi)
        {
            return 0;
        }

        var icons = new List<(string Name, byte[] Svg)>();
        foreach (var asset in capi.Assets.GetMany("textures/icons/worldmap", null, true))
        {
            if (asset?.Location is null || asset.Data is null || asset.Data.Length == 0)
            {
                continue;
            }

            var name = Icons.NameOf(asset.Location.GetName());
            if (name is not null && already.Add(name))
            {
                icons.Add((name, asset.Data));
            }
        }

        if (icons.Count == 0)
        {
            return 0;
        }

        var slices = IconTable.Slice(icons);
        foreach (var slice in slices)
        {
            capi.Network.GetChannel(SharedServer.Channel).SendPacket(slice);
        }

        capi.Logger.Notification(
            "[witchlight] sent {0} marker pictures in {1} packet(s)", icons.Count, slices.Count);
        if (!quiet)
        {
            capi.ShowChatMessage($"Witchlight: sent {icons.Count} marker pictures");
        }

        return icons.Count;
    }
}
