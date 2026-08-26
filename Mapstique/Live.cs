using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Mapstique;

/// <summary>Where a player is, right now.</summary>
public class LivePlayer
{
    public string Name { get; set; } = "";
    public string Uid { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
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
    public static string PlayersJson(ICoreServerAPI api)
    {
        return JsonConvert.SerializeObject(Players(api));
    }

    /// <summary>Every marker, as the map service wants it.</summary>
    public static string WaypointsJson(ICoreServerAPI api)
    {
        return JsonConvert.SerializeObject(Waypoints(api));
    }

    private static List<LivePlayer> Players(ICoreServerAPI api)
    {
        var players = new List<LivePlayer>();
        foreach (var player in api.World.AllOnlinePlayers)
        {
            var position = player?.Entity?.Pos;
            if (player is null || position is null)
            {
                continue;
            }

            players.Add(new LivePlayer
            {
                Name = player.PlayerName ?? "",
                Uid = player.PlayerUID ?? "",
                X = (int)position.X,
                Y = (int)position.Y,
                Z = (int)position.Z,
            });
        }
        return players;
    }

    /// <summary>
    /// Every marker saved on the server. Public because `/mapstique status`
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
