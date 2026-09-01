using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>One map marker, as its owner placed it.</summary>
public class LiveWaypoint
{
    public string Title { get; set; } = "";
    public string Icon { get; set; } = Markers.PlainIcon;
    public string Color { get; set; } = Markers.WhiteHex;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    /// <summary>Display name of the owner, empty when it cannot be resolved.</summary>
    public string Owner { get; set; } = "";

    /// <summary>
    /// The owning player's uid, exactly as the waypoint stores it. This is the
    /// identity that survives a rename and the one any sharing rule will key on,
    /// so it travels even when the name does not.
    /// </summary>
    public string OwnerUid { get; set; } = "";

    public bool Pinned { get; set; }

    /// <summary>
    /// What names this marker wherever it goes. A browser that asked for one
    /// watches for this to appear, and it is the waypoint's own guid, so the
    /// marker it gets back is the marker it asked for and not one that merely
    /// looks like it.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// Whether this marker is its owner's alone. The service uses it to decide
    /// who is sent it; the page uses it to say so on the marker itself, because a
    /// person who marked something private should be able to see that it took.
    /// </summary>
    public bool Private { get; set; }

}

/// <summary>
/// Every marker, arranged by who may see it.
///
/// The service does not read a waypoint and must not have to. Deciding who sees
/// what needs the owner and the choice, and the half that knows both is this one,
/// so the sorting happens here and the service is left holding two lists it only
/// has to hand out: everybody's, and each person's own.
/// </summary>
public class LiveMarkers
{
    /// <summary>The colours the game offers, so the web form can offer the same.</summary>
    public List<string> Colors { get; set; } = new();

    /// <summary>Markers anyone may see.</summary>
    public List<LiveWaypoint> Public { get; set; } = new();

    /// <summary>Markers only their owner may see, by the uid of that owner.</summary>
    public Dictionary<string, List<LiveWaypoint>> Private { get; set; } = new();


}

/// <summary>
/// Every marker on the server, as the map service wants it.
///
/// Waypoints live server-side in the world map manager, so every marker is
/// readable from here. Which of them reach whom is decided here too: the service
/// does not read a waypoint and must not have to, and the half that knows both
/// the owner and their choice is this one.
/// </summary>
public static class MarkerFeed
{
    /// <summary>Every marker, sorted by who may see it, as the service wants it.</summary>
    public static string Json(ICoreServerAPI api, Visibility visibility)
    {
        return JsonConvert.SerializeObject(Sorted(api, visibility));
    }

    /// <summary>

    /// Every marker, arranged into what anyone may see and what only its owner may.
    ///
    /// The colour list rides along rather than going on a channel of its own. It
    /// is a few hundred bytes against a payload of tens of kilobytes, it changes
    /// only when the mod set does, and sending it with the markers means a service
    /// that restarted has the palette back on the next post instead of needing to
    /// be told separately that it lost it.
    /// </summary>
    public static LiveMarkers Sorted(ICoreServerAPI api, Visibility visibility)
    {
        var sorted = new LiveMarkers { Colors = Markers.Palette(api) };
        foreach (var marker in All(api, visibility))
        {
            if (!marker.Private)
            {
                sorted.Public.Add(marker);
                continue;
            }

            // A private marker whose owner is nobody can be shown to nobody. It
            // should not exist; dropping it is the only honest thing to do with
            // one that does.
            if (marker.OwnerUid.Length == 0)
            {
                continue;
            }

            if (!sorted.Private.TryGetValue(marker.OwnerUid, out var theirs))
            {
                theirs = new List<LiveWaypoint>();
                sorted.Private[marker.OwnerUid] = theirs;
            }
            theirs.Add(marker);
        }

        return sorted;
    }

    /// <summary>

    /// Every marker saved on the server. Public because `/witchlight status`
    /// reports how many there are: an empty map with a working service is either
    /// no markers or no post, and those need telling apart.
    /// </summary>
    public static List<LiveWaypoint> All(ICoreServerAPI api, Visibility visibility)
    {
        var layer = Markers.Layer(api);
        if (layer?.Waypoints is null)
        {
            return new List<LiveWaypoint>();
        }

        // Read each time rather than held, so an operator changing their mind
        // takes effect on the next post instead of the next restart — the same
        // rule the announcement and the in-game share both follow.
        var byDefault = Settings.MarkersPrivateByDefault;

        var waypoints = new List<LiveWaypoint>();
        foreach (var waypoint in layer.Waypoints.ToList())
        {
            if (waypoint?.Position is null)
            {
                continue;
            }

            waypoints.Add(new LiveWaypoint
            {
                Title = waypoint.Title ?? "",
                Icon = Markers.Picture(waypoint.Icon),
                Color = Markers.Hex(waypoint.Color),
                X = Blocks.At(waypoint.Position.X),
                Y = Blocks.At(waypoint.Position.Y),
                Z = Blocks.At(waypoint.Position.Z),
                Owner = Markers.OwnerName(api, waypoint.OwningPlayerUid),
                OwnerUid = waypoint.OwningPlayerUid ?? "",
                Pinned = waypoint.Pinned,
                Key = Markers.Key(waypoint),
                Private = visibility.IsPrivate(waypoint, byDefault),
            });
        }
        return waypoints;
    }
}
