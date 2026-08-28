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
/// **Whose markers travel is their owner's decision, and the operator sets the
/// default.** A marker a player drops is theirs; `markers_public` in the map's
/// settings decides only the ones nobody has chosen for. A choice made on the web
/// form overrides it in both directions, which is why the question is asked of
/// <see cref="Visibility"/> rather than of the setting.
/// </summary>
public static class SharedServer
{
    public const string Channel = "witchlight";

    public static SharedMarkers For(ICoreServerAPI api, IServerPlayer player, Visibility visibility)
    {
        var shared = new SharedMarkers();
        var layer = Markers.Layer(api);
        if (layer?.Waypoints is null)
        {
            return shared;
        }

        // Read each time rather than held, so an operator changing their mind
        // takes effect on the next send instead of the next restart — the same
        // rule the announcement follows.
        var byDefault = !ServiceProcess.MarkersPublic(ServiceProcess.ConfigPath);

        foreach (var waypoint in layer.Waypoints.ToList())
        {
            if (waypoint?.Position is null || waypoint.OwningPlayerUid == player.PlayerUID)
            {
                continue;
            }

            if (visibility.IsPrivate(waypoint, byDefault))
            {
                continue;
            }

            shared.Markers.Add(new SharedMarker
            {
                Key = Markers.Key(waypoint),
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

    private static string OwnerName(ICoreServerAPI api, string? uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return "";
        }

        var online = api.World.PlayerByUid(uid);
        return online?.PlayerName ?? api.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername ?? "";
    }

    public static void SendTo(ICoreServerAPI api, IServerPlayer player, Visibility visibility)
    {
        api.Network.GetChannel(Channel)?.SendPacket(For(api, player, visibility), player);
    }

    public static void SendToAll(ICoreServerAPI api, Visibility visibility)
    {
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            SendTo(api, player, visibility);
        }
    }
}
