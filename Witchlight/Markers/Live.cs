using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>Where a player is, right now.</summary>
public class LivePlayer
{
    public string Name { get; set; } = "";
    public string Uid { get; set; } = "";

    /// <summary>
    /// Which stored picture is this player's, or null where there is none.
    ///
    /// A name rather than the address of one, and worked out here rather than by
    /// whoever draws the map: a player uid is base64 and carries characters a path
    /// cannot, so the one place that files these decides what they are called.
    /// </summary>
    public string? Portrait { get; set; }

    /// <summary>
    /// When that picture was drawn, in seconds, or zero where there is none.
    ///
    /// The name alone is the same before and after a player is redrawn, so nothing
    /// downstream could tell that anything had happened: the map went on showing
    /// the picture it already had until somebody reloaded the page. This is the
    /// part that changes, and it travels beside the name rather than inside it
    /// because what is stored really is one file under one name.
    /// </summary>
    public long PortraitAt { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    /// <summary>
    /// Health and food, as the server already knows them.
    ///
    /// Both live in the entity's watched attributes, which is server side, so
    /// nothing has to be asked of anyone to show them. Maximums travel with the
    /// values because a player's can be raised, so a bare number would be a
    /// fraction with a missing denominator.
    /// </summary>
    public float Health { get; set; }
    public float MaxHealth { get; set; }
    public float Saturation { get; set; }
    public float MaxSaturation { get; set; }

}

/// <summary>One map marker, as its owner placed it.</summary>
public class LiveWaypoint
{
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "circle";
    public string Color { get; set; } = "#ffffff";
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
/// The parts of the map that move: who is online and where, and the markers
/// players have saved.
///
/// Waypoints live server-side in the world map manager, so every marker on the
/// server is readable from here. Which of them reach whom is decided here too:
/// see <see cref="LiveMarkers"/>.
/// </summary>
/// <summary>
/// What the world's clock says, in the game's own words.
///
/// Sent rather than filed. A clock is the thing a map has least business writing
/// to a disk: it is stale before the write finishes, and the page asking for it
/// every two seconds is asking a running server, not a file.
/// </summary>
public class LiveWorld
{
    /// <summary>The day and the month, as the game words them — `12. May`.</summary>
    public string Date { get; set; } = "";

    /// <summary>`Year 3`.</summary>
    public string Year { get; set; } = "";

    /// <summary>`14:30`, on the world's own clock.</summary>
    public string Time { get; set; } = "";

    /// <summary>Where spawn is in the year: a season is a fact about a place,
    /// and the hemispheres are in opposite ones.</summary>
    public string Season { get; set; } = "";
}

public static class Live
{
    /// <summary>What the world's clock says, as the service wants it.</summary>
    public static string WorldJson(ICoreServerAPI api)
    {
        return JsonConvert.SerializeObject(WorldNow(api));
    }

    /// <summary>
    /// The date, the time and the season, each in the words the game would use.
    ///
    /// Worded here rather than on the page because the game holds the month names
    /// and the operator's language, and a page that spelled them itself would be
    /// spelling them in English on a server that had chosen otherwise.
    /// </summary>
    public static LiveWorld WorldNow(ICoreServerAPI api)
    {
        var calendar = api.World?.Calendar;
        if (calendar is null)
        {
            return new LiveWorld();
        }

        var perMonth = Math.Max(1, calendar.DaysPerMonth);
        var dayOfYear = Math.Max(0, calendar.DayOfYear - 1);
        var month = dayOfYear / perMonth;
        var day = dayOfYear % perMonth + 1;
        var hour = (int)calendar.HourOfDay;
        var minute = (int)((calendar.HourOfDay - hour) * 60);

        return new LiveWorld
        {
            Date = $"{day}. {MonthName(month)}",
            Year = Lang.Get("Year {0}", calendar.Year),
            Time = $"{hour:00}:{minute:00}",
            Season = SeasonAtSpawn(api),
        };
    }

    /// <summary>
    /// The name of a month, counting from zero.
    ///
    /// The game names twelve and a world may be configured with more; one past
    /// the twelve is said by its number rather than left blank or wrapped round
    /// to January, which would be a lie about which month it is.
    /// </summary>
    private static string MonthName(int month)
    {
        string[] names =
        {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December",
        };
        return month >= 0 && month < names.Length
            ? Lang.Get("month-" + names[month])
            : $"{month + 1}";
    }

    private static string SeasonAtSpawn(ICoreServerAPI api)
    {
        try
        {
            var spawn = WorldFacts.Spawn(api);
            if (spawn is not { } at)
            {
                return "";
            }

            return api.World.Calendar?.GetSeason(new BlockPos(at.X, at.Y, at.Z)).ToString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Who is online and where, as the map service wants it.</summary>
    public static string PlayersJson(ICoreServerAPI api, string exports)
    {
        return JsonConvert.SerializeObject(Players(api, exports));
    }

    /// <summary>Every marker, sorted by who may see it, as the service wants it.</summary>
    public static string WaypointsJson(ICoreServerAPI api, Visibility visibility)
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
        foreach (var marker in Waypoints(api, visibility))
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

    public static List<LivePlayer> Players(ICoreServerAPI api, string exports)
    {
        var players = new List<LivePlayer>();
        foreach (var player in api.World.AllOnlinePlayers)
        {
            if (player?.Entity?.Pos is not { } position)
            {
                continue;
            }

            var watched = player.Entity.WatchedAttributes;
            var health = watched?.GetTreeAttribute("health");
            var hunger = watched?.GetTreeAttribute("hunger");
            var stored = Portraits.StoredFor(exports, player.PlayerUID ?? "");

            players.Add(new LivePlayer
            {
                Name = player.PlayerName ?? "",
                Uid = player.PlayerUID ?? "",
                X = Block(position.X),
                Y = Block(position.Y),
                Z = Block(position.Z),
                Health = health?.GetFloat("currenthealth") ?? 0f,
                MaxHealth = health?.GetFloat("maxhealth") ?? 0f,
                Saturation = hunger?.GetFloat("currentsaturation") ?? 0f,
                MaxSaturation = hunger?.GetFloat("maxsaturation") ?? 0f,
                Portrait = stored?.Name,
                PortraitAt = stored?.At ?? 0,
            });
        }
        return players;
    }

    /// <summary>
    /// Every marker saved on the server. Public because `/witchlight status`
    /// reports how many there are: an empty map with a working service is either
    /// no markers or no post, and those need telling apart.
    /// </summary>
    public static List<LiveWaypoint> Waypoints(ICoreServerAPI api, Visibility visibility)
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
                Icon = string.IsNullOrEmpty(waypoint.Icon) ? "circle" : waypoint.Icon,
                Color = Markers.Hex(waypoint.Color),
                X = Block(waypoint.Position.X),
                Y = Block(waypoint.Position.Y),
                Z = Block(waypoint.Position.Z),
                Owner = Markers.OwnerName(api, waypoint.OwningPlayerUid),
                OwnerUid = waypoint.OwningPlayerUid ?? "",
                Pinned = waypoint.Pinned,
                Key = Markers.Key(waypoint),
                Private = visibility.IsPrivate(waypoint, byDefault),
            });
        }
        return waypoints;
    }

    /// <summary>
    /// Which block a position is in.
    ///
    /// Floor rather than a cast, which rounds toward zero and so names the block
    /// one to the east and south of the one a negative coordinate is actually in.
    /// A Vintage Story world runs from zero, so nothing on a real map reaches
    /// that; it is one operation with one right answer, and both the players and
    /// the markers ask it here rather than each spelling it out.
    /// </summary>
    private static int Block(double at) => (int)Math.Floor(at);

}
