using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Everyone else's markers, on this player's in-game map, as a layer of this
/// mod's own.
///
/// The game draws a player's own waypoints and nobody else's, and every way it
/// offers to put more on the map is a way of adding to that one list. A marker
/// borrowed into it is addressed the way the list is — by the asker's place in
/// their own waypoints — and a marker that is not theirs has no such place: the
/// game's edit window sends an index one past the end, and the server refuses
/// it. So these are not in that list. They are drawn here, from what the server
/// sent, and a click on one opens this mod's own window, which names the marker
/// by its key and asks the server in this mod's own words.
///
/// The game makes one of these when the world loads, from
/// <see cref="WorldMapManager.RegisterMapLayer{T}"/>, and hands it the map to
/// draw on. What it shows arrives through <see cref="Take"/>; nothing here is
/// ever written to the game's waypoint list.
/// </summary>
public sealed class SharedMarkerMapLayer : MapLayer
{
    /// <summary>The name the layer is registered under.</summary>
    public const string Code = "witchlight-shared";

    /// <summary>
    /// Between the players and the waypoints, so a shared marker is drawn under
    /// the player's own and the player's own are what they click first.
    /// </summary>
    public const double Position = 0.9;

    private readonly ICoreClientAPI? _capi;

    /// <summary>The shared markers the server last sent, by key.</summary>
    private readonly Dictionary<string, SharedMarker> _shared = new(StringComparer.Ordinal);

    /// <summary>One drawn thing per marker, rebuilt when the set changes.</summary>
    private readonly List<SharedMarkerComponent> _drawn = new();

    /// <summary>
    /// What the set looked like when it was last drawn, so an unchanged send is
    /// not a redraw. The server sends every marker every fifteen seconds and
    /// almost none of them has moved.
    /// </summary>
    private string _shape = "";

    /// <summary>The window open on one of these, so a second click does not
    ///  put a second window over the first.</summary>
    private GuiDialogWitchlightShared? _dialog;

    public SharedMarkerMapLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
        _capi = api as ICoreClientAPI;
    }

    public override string Title => "Shared markers";

    /// <summary>
    /// The game's own waypoint group, so the one switch on the map that hides
    /// waypoints hides these with them. A tab of its own would need a name the
    /// game has no words for.
    /// </summary>
    public override string LayerGroupCode => "waypoints";

    /// <summary>Client only: the server talks to this over the mod's own channel.</summary>
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

    /// <summary>A marker is drawn wherever it is, explored or not.</summary>
    public override bool RequireChunkLoaded => false;

    /// <summary>The layer the game made, if it has made one yet.</summary>
    public static SharedMarkerMapLayer? On(ICoreClientAPI api) =>
        api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers?
            .OfType<SharedMarkerMapLayer>().FirstOrDefault();

    /// <summary>Takes what the server sent and lays it down if it has changed.</summary>
    public void Take(SharedMarkers message)
    {
        _shared.Clear();
        foreach (var marker in message.Markers)
        {
            if (!string.IsNullOrEmpty(marker.Key))
            {
                _shared[marker.Key] = marker;
            }
        }

        var shape = Shape();
        if (shape == _shape)
        {
            return;
        }
        _shape = shape;
        Redraw();

        // A window open on a marker that has just changed under it is showing
        // what the marker was. The one thing the server changes on its own is
        // the pin this very window asked for, which is what it is waiting on.
        if (_dialog is { } dialog && dialog.IsOpened()
            && _shared.TryGetValue(dialog.Key, out var shown))
        {
            dialog.Shown(shown);
        }
    }

    /// <summary>
    /// One drawn thing per marker, made again from what the server said.
    ///
    /// The pictures they are drawn with belong to the game's waypoint layer,
    /// which loads them when the map opens; a component made while the map is
    /// shut is told so when it is asked to draw. Nothing is held that has to be
    /// loaded first, which is why this can run whether the map is up or not.
    /// </summary>
    private void Redraw()
    {
        foreach (var drawn in _drawn)
        {
            drawn.Dispose();
        }
        _drawn.Clear();

        if (_capi is null || Markers.Layer(_capi) is not { } game)
        {
            return;
        }

        foreach (var marker in _shared.Values)
        {
            _drawn.Add(new SharedMarkerComponent(_capi, game, marker, Open));
        }

        _capi.Logger.Notification("[witchlight] {0} shared marker(s) on the map", _drawn.Count);
    }

    /// <summary>
    /// What the set is, as one string. Sorted, because the order the server
    /// happens to send them in is not a change to anything.
    /// </summary>
    private string Shape()
    {
        var said = _shared.Values
            .Select(marker =>
                $"{marker.Key}|{marker.X}|{marker.Y}|{marker.Z}|{marker.Title}|{marker.Icon}"
                + $"|{marker.Color}|{marker.Owner}|{marker.Pinned}|{marker.Editable}")
            .OrderBy(line => line, StringComparer.Ordinal);
        return string.Join("\n", said);
    }

    /// <summary>
    /// Opens this mod's window on one marker. The game's own window is not
    /// reached for: it addresses a waypoint by its place in the asker's list,
    /// and a shared marker has none.
    /// </summary>
    private void Open(SharedMarker marker)
    {
        if (_capi is null || Markers.Layer(_capi) is not { } game)
        {
            return;
        }

        _dialog?.TryClose();
        _dialog = new GuiDialogWitchlightShared(_capi, game, marker, Send);
        _dialog.TryOpen();
        // The map behind it takes the mouse back when the window shuts, the way
        // the game's own window hands it back.
        _dialog.OnClosed += () =>
        {
            if (_capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg is { } map)
            {
                _capi.Gui.RequestFocus(map);
            }
        };
    }

    /// <summary>What the window sends when it is finished with.</summary>
    private void Send(SharedMarkerChange change)
    {
        _capi?.Network.GetChannel(Channel.Name)?.SendPacket(change);
    }

    /// <summary>
    /// Made again on opening: the game's waypoint layer reloads its pictures
    /// then, and a component drawn from the old ones would draw nothing.
    /// </summary>
    public override void OnMapOpenedClient() => Redraw();

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active)
        {
            return;
        }
        foreach (var drawn in _drawn)
        {
            drawn.Render(mapElem, dt);
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        if (!Active)
        {
            return;
        }
        foreach (var drawn in _drawn)
        {
            drawn.OnMouseMove(args, mapElem, hoverText);
        }
    }

    public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
    {
        if (!Active)
        {
            return;
        }
        foreach (var drawn in _drawn)
        {
            drawn.OnMouseUpOnElement(args, mapElem);
            if (args.Handled)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        foreach (var drawn in _drawn)
        {
            drawn.Dispose();
        }
        _drawn.Clear();
        _dialog?.TryClose();
        _dialog = null;
    }
}
