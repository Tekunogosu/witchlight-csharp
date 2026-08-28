using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
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
public sealed class SharedMarkerLayer
{
    /// <summary>
    /// Half a second: the map being shut is something a person did, and the set
    /// is back before they could open it again.
    /// </summary>
    private const int WatchMs = 500;

    private readonly ICoreClientAPI _capi;

    /// <summary>The shared markers the server last sent, by key.</summary>
    private readonly Dictionary<string, SharedMarker> _shared = new(StringComparer.Ordinal);

    /// <summary>
    /// What the set looked like when it was last drawn, so an unchanged send is
    /// not a redraw. The server sends every marker every fifteen seconds and
    /// almost none of them has moved.
    /// </summary>
    private string _drawn = "";

    /// <summary>Whether the world map was open when this last looked.</summary>
    private bool _wasOpen;

    public SharedMarkerLayer(ICoreClientAPI capi)
    {
        _capi = capi;
        capi.Event.RegisterGameTickListener(_ => WatchTheMap(), WatchMs);
    }

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
        if (Markers.Layer(_capi) is not { } layer)
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

    private bool MapIsOpen() => _capi.ModLoader.GetModSystem<WorldMapManager>()?.IsOpened ?? false;

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
