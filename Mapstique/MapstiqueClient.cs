using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
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
            .RegisterMessageType<IconRequest>()
            .RegisterMessageType<IconTable>()
            .RegisterMessageType<PortraitRequest>()
            .RegisterMessageType<PlayerPortrait>()
            .SetMessageHandler<SharedMarkers>(OnMarkers)
            .SetMessageHandler<PaletteRequest>(OnPaletteRequest)
            .SetMessageHandler<IconRequest>(OnIconRequest)
            .SetMessageHandler<PortraitRequest>(OnPortraitRequest);

        _portrait = new PortraitCapture(api);

        api.ChatCommands
            .Create("mapstique")
            .WithDescription("Mapstique map tools")
            .BeginSubCommand("portrait")
            .WithDescription("Send the map a picture of your character")
            .HandleWith(OnPortrait)
            .EndSubCommand()
            .BeginSubCommand("palette")
            .WithDescription("Build the block colour palette for the server you are on")
            .HandleWith(OnPalette)
            .EndSubCommand()
            .BeginSubCommand("icons")
            .WithDescription("Send the marker pictures to the server you are on")
            .HandleWith(OnIcons)
            .EndSubCommand();
    }

    /// <summary>
    /// Sends what each skin part variant looks like, because the server asked.
    ///
    /// The colours are not written down anywhere: the game works each one out by
    /// sampling its texture, on the client, at load. So this reads back what the
    /// game already resolved rather than resolving it again.
    /// </summary>
    /// <summary>The server asked for a picture, so one is drawn and sent.</summary>
    private void OnPortraitRequest(PortraitRequest request) => SendPortrait(quiet: true);

    /// <summary>Draws this player and sends the picture to the server.</summary>
    private TextCommandResult OnPortrait(TextCommandCallingArgs args)
    {
        if (_capi is null || _portrait is null)
        {
            return TextCommandResult.Error("not connected to a world");
        }

        if (NotAnAdmin() is { } refused)
        {
            return refused;
        }

        SendPortrait(quiet: false);
        return TextCommandResult.Success("drawing your character...");
    }

    /// <summary>
    /// Draws this player and sends the picture.
    ///
    /// The drawing happens in a frame, so the answer arrives after whoever asked
    /// has been answered. Said in chat when somebody typed a command, and only to
    /// the log when the server asked unprompted — one is a question waiting for an
    /// answer and the other is not.
    /// </summary>
    private void SendPortrait(bool quiet)
    {
        if (_capi is null || _portrait is null)
        {
            return;
        }

        _portrait.Take((png, said) =>
        {
            if (png is null)
            {
                // Said whoever asked. A server that asked for a picture and hears
                // nothing cannot tell a refusal from a slow answer, and the player
                // is the only one whose screen can explain it.
                _capi.Logger.Warning("[mapstique] could not draw a portrait: {0}", said);
                _capi.ShowChatMessage($"Mapstique: could not draw your portrait — {said}");
                return;
            }

            _capi.Network.GetChannel(SharedServer.Channel).SendPacket(new PlayerPortrait { Png = png });
            _capi.Logger.Notification("[mapstique] sent a {0} byte portrait ({1})", png.Length, said);
            if (!quiet)
            {
                // Bytes, not kilobytes. A picture of nothing weighs a hundred and
                // fifty bytes and rounds to "0 KiB", which reads as a unit being
                // silly rather than as an empty render — and it cost an evening.
                _capi.ShowChatMessage($"Mapstique: sent your portrait, {png.Length} bytes — {said}");
            }
        });
    }

    /// <summary>
    /// Why a player may not do this, or nothing when they may.
    ///
    /// The server takes these from admins only and says so in its own log when it
    /// refuses one. Checked here as well so that somebody who is not an admin is
    /// told, rather than left watching a command succeed and nothing happen.
    /// </summary>
    private TextCommandResult? NotAnAdmin()
    {
        var player = _capi?.World?.Player;
        if (player is null || player.HasPrivilege(Privilege.controlserver))
        {
            return null;
        }

        return TextCommandResult.Error(
            "the map only takes these from admins on this server");
    }

    /// <summary>Draws this player when asked. Only a client can: nobody else has them.</summary>
    private PortraitCapture? _portrait;

    /// <summary>
    /// Sends the marker pictures, because the server asked.
    ///
    /// A dedicated server's install has no SVG in it at all, so the pictures a
    /// marker is drawn with cannot come from there. Only what the server says it
    /// is missing is sent, so a mod added later costs one icon rather than the
    /// whole set again.
    /// </summary>
    private void OnIconRequest(IconRequest request)
    {
        Send(new HashSet<string>(request.Have ?? new List<string>()), quiet: true);
    }

    private TextCommandResult OnIcons(TextCommandCallingArgs args)
    {
        var sent = Send(new HashSet<string>(), quiet: false);
        return sent > 0
            ? TextCommandResult.Success($"sent {sent} marker pictures")
            : TextCommandResult.Error("this client has no marker pictures to send");
    }

    /// <summary>Reads every icon this client can see and sends what the server lacks.</summary>
    private int Send(HashSet<string> already, bool quiet)
    {
        if (_capi is null)
        {
            return 0;
        }

        var icons = new List<(string, byte[])>();
        foreach (var asset in _capi.Assets.GetMany("textures/icons/worldmap", null, true))
        {
            if (asset?.Location is null || asset.Data is null || asset.Data.Length == 0)
            {
                continue;
            }

            var name = Icons.NameOf(asset.Location.GetName());
            if (name is null || already.Contains(name))
            {
                continue;
            }

            icons.Add((name, asset.Data));
        }

        if (icons.Count == 0)
        {
            return 0;
        }

        var slices = IconTable.Slice(icons);
        foreach (var slice in slices)
        {
            _capi.Network.GetChannel(SharedServer.Channel).SendPacket(slice);
        }

        _capi.Logger.Notification(
            "[mapstique] sent {0} marker pictures in {1} packet(s)", icons.Count, slices.Count);
        if (!quiet)
        {
            _capi.ShowChatMessage($"Mapstique: sent {icons.Count} marker pictures");
        }

        return icons.Count;
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
