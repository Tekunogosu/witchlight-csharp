using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Sends every player the markers belonging to everyone else that they are meant
/// to see, so the in-game map can show more than only your own.
///
/// A player's own markers are left out: they already have those, and sending them
/// back would put a second copy on their map.
///
/// **Whose markers travel is an operator's decision.** A marker a player drops is
/// theirs; `markers_public` in the map's settings is what makes one everybody's.
/// Until a player can mark a single waypoint as shared, that setting is the whole
/// of the answer: off shares nothing, on shares everything, and there is no third
/// state yet for the per-marker choice to live in.
/// </summary>
public static class SharedServer
{
    public const string Channel = "witchlight";

    public static SharedMarkers For(ICoreServerAPI api, IServerPlayer player)
    {
        // Read each time rather than held, so an operator changing their mind
        // takes effect on the next send instead of the next restart — the same
        // rule the announcement follows.
        if (!ServiceProcess.MarkersPublic(ServiceProcess.ConfigPath))
        {
            return new SharedMarkers();
        }

        var layer = api.ModLoader
            .GetModSystem<WorldMapManager>()?
            .MapLayers?
            .OfType<WaypointMapLayer>()
            .FirstOrDefault();

        var shared = new SharedMarkers();
        if (layer?.Waypoints is null)
        {
            return shared;
        }

        foreach (var waypoint in layer.Waypoints.ToList())
        {
            if (waypoint?.Position is null || waypoint.OwningPlayerUid == player.PlayerUID)
            {
                continue;
            }

            shared.Markers.Add(new SharedMarker
            {
                Key = Key(waypoint),
                X = waypoint.Position.X,
                Y = waypoint.Position.Y,
                Z = waypoint.Position.Z,
                Title = waypoint.Title ?? "",
                Icon = string.IsNullOrEmpty(waypoint.Icon) ? "circle" : waypoint.Icon,
                Color = waypoint.Color,
                Owner = OwnerName(api, waypoint.OwningPlayerUid),
            });
        }

        return shared;
    }

    /// <summary>
    /// Identity for a marker as the clients see it. The waypoint's own guid is
    /// used where there is one; position and title stand in where there is not,
    /// which is enough to keep a marker from being added twice.
    /// </summary>
    private static string Key(Waypoint waypoint)
    {
        if (!string.IsNullOrEmpty(waypoint.Guid))
        {
            return waypoint.Guid;
        }

        return $"{(int)waypoint.Position.X}:{(int)waypoint.Position.Y}:{(int)waypoint.Position.Z}:{waypoint.Title}";
    }

    private static string OwnerName(ICoreServerAPI api, string? uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return "";
        }

        var online = api.World.PlayerByUid(uid);
        return online?.PlayerName ?? api.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? "";
    }

    public static void SendTo(ICoreServerAPI api, IServerPlayer player)
    {
        api.Network.GetChannel(Channel)?.SendPacket(For(api, player), player);
    }

    public static void SendToAll(ICoreServerAPI api)
    {
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            SendTo(api, player);
        }
    }
}
