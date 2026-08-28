using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

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
}

/// <summary>
/// The parts of the map that move: who is online and where, and the markers
/// players have saved.
///
/// Waypoints live server-side in the world map manager, so every marker on the
/// server is readable from here — which also means every marker ends up on the
/// map. That is the intent for now; scoping them to groups is a later decision,
/// and the export is the place it will be made.
/// </summary>
public static class Live
{
    /// <summary>Who is online and where, as the map service wants it.</summary>
    public static string PlayersJson(ICoreServerAPI api, string exports)
    {
        return JsonConvert.SerializeObject(Players(api, exports));
    }

    /// <summary>Every marker, as the map service wants it.</summary>
    public static string WaypointsJson(ICoreServerAPI api)
    {
        return JsonConvert.SerializeObject(Waypoints(api));
    }

    public static List<LivePlayer> Players(ICoreServerAPI api, string exports)
    {
        var players = new List<LivePlayer>();
        foreach (var player in api.World.AllOnlinePlayers)
        {
            var position = player?.Entity?.Pos;
            if (player is null || position is null)
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
                X = (int)position.X,
                Y = (int)position.Y,
                Z = (int)position.Z,
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
    public static List<LiveWaypoint> Waypoints(ICoreServerAPI api)
    {
        var layer = api.ModLoader
            .GetModSystem<WorldMapManager>()?
            .MapLayers?
            .OfType<WaypointMapLayer>()
            .FirstOrDefault();

        if (layer?.Waypoints is null)
        {
            return new List<LiveWaypoint>();
        }

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
                Color = Hex(waypoint.Color),
                X = (int)waypoint.Position.X,
                Y = (int)waypoint.Position.Y,
                Z = (int)waypoint.Position.Z,
                Owner = NameOf(api, waypoint.OwningPlayerUid),
                OwnerUid = waypoint.OwningPlayerUid ?? "",
                Pinned = waypoint.Pinned,
            });
        }
        return waypoints;
    }

    /// <summary>
    /// The colours a player chose for themselves.
    ///
    /// Read from the applied skin parts, which are watched attributes and so are
    /// known here without asking anyone. A part the game does not ship, or a mod
    /// that renames one, simply yields nothing and the face is drawn without it.
    /// </summary>

    /// <summary>
    /// Who owns a marker. Offline owners are looked up in the player data, so a
    /// marker still says whose it is when they are not on.
    /// </summary>
    private static string NameOf(ICoreServerAPI api, string? uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return "";
        }

        var online = api.World.PlayerByUid(uid);
        if (online is not null)
        {
            return online.PlayerName ?? "";
        }

        return api.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? "";
    }

    /// <summary>Waypoint colours are packed ints; the map wants CSS.</summary>
    private static string Hex(int color)
    {
        return $"#{color & 0xff:x2}{(color >> 8) & 0xff:x2}{(color >> 16) & 0xff:x2}";
    }
}
