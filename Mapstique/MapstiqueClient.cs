using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Mapstique;

/// <summary>
/// Puts everyone else's markers on this player's map.
///
/// They are added as *temporary* waypoints: the game keeps those apart from the
/// player's own saved list, so nothing here can edit, delete or overwrite a
/// marker anyone owns. Log out and they are gone; the server sends them again on
/// the next join.
/// </summary>
public class MapstiqueClient : ModSystem
{
    private ICoreClientAPI? _capi;

    /// <summary>Markers already placed, by the key the server gave them.</summary>
    private readonly HashSet<string> _placed = new();

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        api.Network
            .RegisterChannel(SharedServer.Channel)
            .RegisterMessageType<SharedMarkers>()
            .RegisterMessageType<PaletteRequest>()
            .RegisterMessageType<PaletteTable>()
            .SetMessageHandler<SharedMarkers>(OnMarkers)
            .SetMessageHandler<PaletteRequest>(OnPaletteRequest);

        api.ChatCommands
            .Create("mapstique")
            .WithDescription("Mapstique map tools")
            .BeginSubCommand("palette")
            .WithDescription("Build the block colour palette for the server you are on")
            .HandleWith(OnPalette)
            .EndSubCommand();
    }

    /// <summary>
    /// Builds a palette and sends it, because the server asked.
    ///
    /// The server only asks admins, and only when it could not build a usable one
    /// itself — which is the normal case on a dedicated server, whose install
    /// ships almost no block textures. Nothing is sent unprompted.
    /// </summary>
    private void OnPaletteRequest(PaletteRequest request)
    {
        if (_capi is null)
        {
            return;
        }

        try
        {
            _capi.ShowChatMessage("[mapstique] the server asked for a block colour palette, building it…");

            var palette = PaletteBuilder.Build(_capi, out _);
            if (palette.Fingerprint != request.Fingerprint)
            {
                // The registry this client holds is not the one the server asked
                // about, so its block ids would be wrong. Said out loud rather
                // than only in a log file, since nothing else would explain the
                // map staying empty.
                var message =
                    $"[mapstique] cannot supply a palette: this client's block registry "
                    + $"({palette.Fingerprint}) is not the server's ({request.Fingerprint})";
                _capi.ShowChatMessage(message);
                _capi.Logger.Warning(message);
                return;
            }

            var slices = PaletteTable.Slice(palette);
            var channel = _capi.Network.GetChannel(SharedServer.Channel);
            foreach (var slice in slices)
            {
                channel.SendPacket(slice);
            }

            _capi.ShowChatMessage(
                $"[mapstique] sent the server a palette: {palette.Coloured} blocks coloured, "
                + $"in {slices.Count} part(s)");
        }
        catch (System.Exception error)
        {
            _capi.Logger.Error("[mapstique] could not build a palette: {0}", error);
        }
    }

    /// <summary>
    /// Builds the palette from this client's assets.
    ///
    /// A dedicated server install ships almost no block textures — 46 files
    /// against a full game's 9,587 — so a server can only build a palette when it
    /// happens to sit on a complete install. A client always has every texture,
    /// and while connected it also has the block ids the server assigned, so the
    /// palette it builds lines up with that server's exports.
    /// </summary>
    private TextCommandResult OnPalette(TextCommandCallingArgs args)
    {
        if (_capi is null)
        {
            return TextCommandResult.Error("client not ready");
        }

        var directory = Path.Combine(GamePaths.DataPath, "mapstique");
        var path = Path.Combine(directory, "palette.json");

        try
        {
            var palette = PaletteBuilder.Build(_capi, out var missing);
            PaletteBuilder.Write(palette, path);
            PaletteBuilder.ExportColorMaps(_capi, Path.Combine(directory, "colormaps"));

            return TextCommandResult.Success(
                $"{palette.Blocks.Count} blocks written to {path}"
                + (missing > 0 ? $" ({missing} with nothing to draw)" : "")
                + ". Copy it to the server's mapstique folder and restart the map service.");
        }
        catch (System.Exception error)
        {
            _capi.Logger.Error("[mapstique] palette build failed: {0}", error);
            return TextCommandResult.Error($"palette build failed: {error.Message}");
        }
    }

    private void OnMarkers(SharedMarkers message)
    {
        if (_capi is null)
        {
            return;
        }

        var layer = _capi.ModLoader
            .GetModSystem<WorldMapManager>()?
            .MapLayers?
            .OfType<WaypointMapLayer>()
            .FirstOrDefault();

        if (layer is null)
        {
            return;
        }

        var added = 0;
        foreach (var marker in message.Markers)
        {
            // Only what is new. The server sends the whole set each time, and
            // adding a marker twice would show it twice.
            if (string.IsNullOrEmpty(marker.Key) || !_placed.Add(marker.Key))
            {
                continue;
            }

            layer.AddTemporaryWaypoint(new Waypoint
            {
                Position = new Vec3d(marker.X, marker.Y, marker.Z),
                Title = Label(marker),
                Icon = marker.Icon,
                Color = marker.Color,
                OwningPlayerUid = null,
                Pinned = false,
                Guid = "mapstique:" + marker.Key,
            });
            added++;
        }

        if (added > 0)
        {
            _capi.Logger.Notification("[mapstique] {0} shared marker(s) added to the map", added);
        }
    }

    /// <summary>
    /// Whose marker this is, said on the marker itself — every death marker is
    /// called "You died here", and on a shared map that needs a name against it.
    /// </summary>
    private static string Label(SharedMarker marker)
    {
        var title = string.IsNullOrEmpty(marker.Title) ? "Marker" : marker.Title;
        return string.IsNullOrEmpty(marker.Owner) ? title : $"{title} ({marker.Owner})";
    }
}
