using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Puts everyone else's markers on this player's map.
///
/// They are added as *temporary* waypoints: the game keeps those apart from the
/// player's own saved list, so nothing here can edit, delete or overwrite a
/// marker anyone owns. Log out and they are gone; the server sends them again on
/// the next join.
/// </summary>
public class WitchlightClient : ModSystem
{
    private ICoreClientAPI? _capi;

    /// <summary>The shared markers the server last sent, by key.</summary>
    private readonly Dictionary<string, SharedMarker> _shared = new();

    /// <summary>
    /// What the set looked like when it was last drawn, so an unchanged send is
    /// not a redraw. The server sends every marker every fifteen seconds and
    /// almost none of them has moved.
    /// </summary>
    private string _drawn = "";

    /// <summary>Whether the world map was open when this last looked.</summary>
    private bool _wasOpen;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        // Half a second: the map being shut is something a person did, and the
        // set is back before they could open it again.
        api.Event.RegisterGameTickListener(_ => WatchTheMap(), 500);
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
        _watch = new PortraitWatch(api, () => SendPortrait(byHand: false));

        api.ChatCommands
            .Create(Commands.Name)
            .WithShortName(api)
            .WithDescription("Witchlight map tools")
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

    /// <summary>The server asked for a picture, so one is drawn and sent.</summary>
    private void OnPortraitRequest(PortraitRequest request) => SendPortrait(byHand: false);

    /// <summary>
    /// Draws this player and sends the picture to the server.
    ///
    /// Once every five minutes. Nothing about the map being current rests on
    /// anybody typing this — a picture is asked for on every join and sent again
    /// whenever a character has been left alone for half a minute — so the command
    /// is for wanting one sooner than that, and wanting it twice over is wanting it
    /// once. Refused before anything is drawn, so a player leaning on it costs the
    /// server nothing and their own machine no frames.
    /// </summary>
    private TextCommandResult OnPortrait(TextCommandCallingArgs args)
    {
        if (_capi is null || _portrait is null)
        {
            return TextCommandResult.Error("not connected to a world");
        }

        if (WaitLeft() is { } left)
        {
            return TextCommandResult.Error(
                $"you asked for one already — ask again in {Spoken(left)}. "
                + "The map draws you on every join and whenever you change anyway.");
        }

        SendPortrait(byHand: true);
        return TextCommandResult.Success("drawing your character...");
    }

    /// <summary>
    /// Draws this player and sends the picture.
    ///
    /// The drawing happens in a frame, so the answer arrives after whoever asked
    /// has been answered. By hand means a person typed the command and is waiting
    /// on an answer, which is the whole of what separates the two: they are told in
    /// chat rather than only in the log, and the wait before they may ask again
    /// starts. Nothing unprompted reaches the screen at all — a picture is drawn on
    /// every join and after every change of clothes, and a player who did not ask
    /// for any of that is owed silence about all of it.
    /// </summary>
    private void SendPortrait(bool byHand)
    {
        if (_capi is null || _portrait is null)
        {
            return;
        }

        // Whatever prompted this, the character as it stands has now been drawn,
        // so the wait starts again from here rather than from a change already
        // covered by this picture.
        _watch?.Settled();

        _portrait.Take((png, said) =>
        {
            if (png is null)
            {
                _capi.Logger.Warning("[witchlight] could not draw a portrait: {0}", said);
                if (byHand)
                {
                    _capi.ShowChatMessage($"Witchlight: could not draw your portrait — {said}");
                }
                return;
            }

            // Counted from the picture rather than from the command, so an attempt
            // that drew nothing may be made again instead of costing five minutes
            // for a failure nobody chose.
            if (byHand)
            {
                _askedAt = System.DateTime.UtcNow;
            }

            _capi.Network.GetChannel(SharedServer.Channel).SendPacket(new PlayerPortrait { Png = png });
            _capi.Logger.Notification("[witchlight] sent a {0} byte portrait ({1})", png.Length, said);
            if (byHand)
            {
                // Bytes, not kilobytes. A picture of nothing weighs a hundred and
                // fifty bytes and rounds to "0 KiB", which reads as a unit being
                // silly rather than as an empty render — and it cost an evening.
                _capi.ShowChatMessage($"Witchlight: sent your portrait, {png.Length} bytes — {said}");
            }
        });
    }

    /// <summary>
    /// How long is left before this player may ask for another, or nothing when
    /// they may ask now.
    /// </summary>
    private System.TimeSpan? WaitLeft()
    {
        if (_askedAt is not { } last)
        {
            return null;
        }

        var left = System.TimeSpan.FromMilliseconds(CommandWaitMs) - (System.DateTime.UtcNow - last);
        return left > System.TimeSpan.Zero ? left : null;
    }

    /// <summary>A wait as somebody would say it, rather than as a count of seconds.</summary>
    private static string Spoken(System.TimeSpan left) => left < System.TimeSpan.FromMinutes(1)
        ? $"{left.Seconds + 1}s"
        : $"{(int)left.TotalMinutes}m {left.Seconds}s";

    /// <summary>
    /// How long between pictures a player asks for by hand.
    ///
    /// Generous on purpose. Everything that keeps the map current happens without
    /// the command, so nobody waiting this out is waiting for their own face to
    /// appear — the picture they would have asked for arrives on its own.
    /// </summary>
    private const int CommandWaitMs = 300000;

    /// <summary>When this player last had a picture they asked for by hand.</summary>
    private System.DateTime? _askedAt;

    /// <summary>Draws this player when asked. Only a client can: nobody else has them.</summary>
    private PortraitCapture? _portrait;

    /// <summary>Says when this player has changed and stopped changing.</summary>
    private PortraitWatch? _watch;

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
            "[witchlight] sent {0} marker pictures in {1} packet(s)", icons.Count, slices.Count);
        if (!quiet)
        {
            _capi.ShowChatMessage($"Witchlight: sent {icons.Count} marker pictures");
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
            _capi.ShowChatMessage("[witchlight] the server asked for a block colour palette, building it…");

            var palette = PaletteBuilder.Build(_capi, out _);
            if (palette.Fingerprint != request.Fingerprint)
            {
                // The registry this client holds is not the one the server asked
                // about, so its block ids would be wrong. Said out loud rather
                // than only in a log file, since nothing else would explain the
                // map staying empty.
                var message =
                    $"[witchlight] cannot supply a palette: this client's block registry "
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
                $"[witchlight] sent the server a palette: {palette.Coloured} blocks coloured, "
                + $"in {slices.Count} part(s)");
        }
        catch (System.Exception error)
        {
            _capi.Logger.Error("[witchlight] could not build a palette: {0}", error);
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

        var directory = Path.Combine(GamePaths.DataPath, "witchlight");
        var path = Path.Combine(directory, "palette.json");

        try
        {
            var palette = PaletteBuilder.Build(_capi, out var missing);
            PaletteBuilder.Write(palette, path);
            PaletteBuilder.ExportColorMaps(_capi, Path.Combine(directory, "colormaps"));

            return TextCommandResult.Success(
                $"{palette.Blocks.Count} blocks written to {path}"
                + (missing > 0 ? $" ({missing} with nothing to draw)" : "")
                + ". Copy it to the server's witchlight folder and restart the map service.");
        }
        catch (System.Exception error)
        {
            _capi.Logger.Error("[witchlight] palette build failed: {0}", error);
            return TextCommandResult.Error($"palette build failed: {error.Message}");
        }
    }

    private void OnMarkers(SharedMarkers message)
    {
        _shared.Clear();
        foreach (var marker in message.Markers)
        {
            if (!string.IsNullOrEmpty(marker.Key))
            {
                _shared[marker.Key] = marker;
            }
        }

        Draw(force: false);
    }

    /// <summary>
    /// Puts the shared markers on this client's map, as a set rather than one at
    /// a time.
    ///
    /// They used to be added once each and never touched again, which was wrong
    /// twice. A marker somebody edited kept whatever it said when it first
    /// arrived, because a key already seen was skipped. And the game drops every
    /// temporary waypoint when the map closes, so the whole set vanished the
    /// first time anybody shut their map and never came back.
    ///
    /// Both are the same fix: hold what the server said and lay the set down
    /// again whenever it has changed or the map has been emptied under it. The
    /// layer offers no way to remove one temporary waypoint, but it offers a way
    /// to remove all of them, which is all a set redraw needs.
    /// </summary>
    private void Draw(bool force)
    {
        if (_capi is null)
        {
            return;
        }

        var layer = Markers.Layer(_capi);
        if (layer is null)
        {
            return;
        }

        var shape = Shape();
        if (!force && shape == _drawn)
        {
            return;
        }
        _drawn = shape;

        // Every temporary waypoint, not only this mod's. The game clears them all
        // when the map closes, so anything else adding one already copes with
        // being cleared — and there is no finer instrument than this one.
        layer.OnMapClosedClient();

        foreach (var marker in _shared.Values)
        {
            layer.AddTemporaryWaypoint(new Waypoint
            {
                Position = new Vec3d(marker.X, marker.Y, marker.Z),
                Title = Label(marker),
                Icon = marker.Icon,
                Color = marker.Color,
                OwningPlayerUid = null,
                Pinned = false,
                Guid = "witchlight:" + marker.Key,
            });
        }

        // Rebuilding is only worth anything while there is a map to draw on, and
        // it costs every icon texture. A set laid down while the map is shut is
        // built when the game opens it, which it does through this same call.
        if (MapIsOpen())
        {
            layer.OnMapOpenedClient();
        }

        _capi.Logger.Notification("[witchlight] {0} shared marker(s) on the map", _shared.Count);
    }

    /// <summary>
    /// What the set is, as one string. Sorted, because the order the server
    /// happens to send them in is not a change to anything.
    /// </summary>
    private string Shape()
    {
        var said = _shared.Values
            .Select(marker =>
                $"{marker.Key}|{marker.X}|{marker.Y}|{marker.Z}|{marker.Title}|{marker.Icon}|{marker.Color}|{marker.Owner}")
            .OrderBy(line => line, StringComparer.Ordinal);
        return string.Join("\n", said);
    }

    private bool MapIsOpen() =>
        _capi?.ModLoader.GetModSystem<WorldMapManager>()?.IsOpened ?? false;

    /// <summary>
    /// Watches for the map being shut, and lays the set down again behind it.
    ///
    /// Closing the map empties every temporary waypoint out of the layer. Putting
    /// them back while it is shut costs nothing to draw and means the set is
    /// already there when the game rebuilds on the next opening — which is why
    /// this watches for the closing rather than the opening.
    /// </summary>
    private void WatchTheMap()
    {
        var open = MapIsOpen();
        if (_wasOpen && !open)
        {
            Draw(force: true);
        }
        _wasOpen = open;
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
