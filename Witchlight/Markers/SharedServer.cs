using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

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
    public static SharedMarkers For(
        ICoreServerAPI api, IServerPlayer player, Visibility visibility, Pins pins)
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
        var byDefault = Settings.MarkersPrivateByDefault;
        var editable = Settings.PublicMarkersEditable;

        foreach (var waypoint in layer.Waypoints.ToList())
        {
            if (waypoint?.Position is null || waypoint.OwningPlayerUid == player.PlayerUID)
            {
                continue;
            }

            var isPrivate = visibility.IsPrivate(waypoint, byDefault);
            if (isPrivate)
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
                Icon = Markers.Picture(waypoint.Icon),
                Color = waypoint.Color,
                Owner = Markers.OwnerName(api, waypoint.OwningPlayerUid),
                // Whether this one player keeps it in sight. Asked per send
                // rather than per marker, because that is the shape of the
                // question: a pin is one person's and never everyone's.
                Pinned = pins.Kept(waypoint, player.PlayerUID),
                Editable = Markers.MayEdit(waypoint, player.PlayerUID, isPrivate, editable),
            });
        }

        return shared;
    }

    /// <summary>
    /// What one player asked of somebody else's marker from their in-game map.
    ///
    /// Decided here against the waypoint itself, by the rules the web map's asks
    /// are decided by — see <see cref="Pending"/> — because a player's map and a
    /// player's browser are two doors to the one marker. Anybody it is shared
    /// with may keep it in sight; changing it takes what the operator allowed.
    ///
    /// Answers whether the marker itself changed, which is what decides who has
    /// to be told: a pin is one person's map and the marker is everybody's.
    /// </summary>
    public static bool Apply(
        ICoreServerAPI api, IServerPlayer player, SharedMarkerChange change,
        Visibility visibility, Pins pins)
    {
        if (string.IsNullOrEmpty(change.Key))
        {
            return false;
        }

        var waypoint = Markers.ByGuid(api, change.Key);
        if (waypoint is null)
        {
            Tell(api, player, "That marker is not there any more.");
            return false;
        }

        var uid = player.PlayerUID;
        var isPrivate = visibility.IsPrivate(waypoint, Settings.MarkersPrivateByDefault);
        if (waypoint.OwningPlayerUid != uid && isPrivate)
        {
            Tell(api, player, "That marker is not shared with you.");
            return false;
        }

        pins.Choose(api, waypoint, uid, change.Pinned);
        if (!change.Editing)
        {
            return false;
        }

        if (!Markers.MayEdit(waypoint, uid, isPrivate, Settings.PublicMarkersEditable))
        {
            Tell(api, player, "That marker is not yours to change.");
            return false;
        }

        Markers.Change(
            api, waypoint, waypoint.Position, Markers.Title(change.Title),
            Markers.Picture(change.Icon), change.Color);
        return true;
    }

    /// <summary>
    /// The preset one player asked to keep from somebody else's marker, or
    /// nothing where they did not ask or there is no block to key it on.
    ///
    /// Keyed on the block the marker was made on, where the server recorded
    /// one, and otherwise on the block under it — the rule a marker made on the
    /// web follows. The name, picture and colour are the marker's as the asker
    /// saw them, or as they changed them where they may.
    /// </summary>
    public static Preset? Keeping(ICoreServerAPI api, SharedMarkerChange change, Origins origins)
    {
        if (!change.KeepPreset)
        {
            return null;
        }

        var waypoint = Markers.ByGuid(api, change.Key);
        if (waypoint?.Position is null)
        {
            return null;
        }

        var code = origins.Of(waypoint.Guid);
        if (code.Length == 0)
        {
            code = Marking.CodeUnder(
                api, Blocks.At(waypoint.Position.X), Blocks.At(waypoint.Position.Y), Blocks.At(waypoint.Position.Z));
        }
        var pattern = BlockPattern.Widened(code);
        if (pattern.Length == 0)
        {
            return null;
        }

        return new Preset
        {
            Pattern = pattern,
            Title = Markers.Title(change.Editing ? change.Title : waypoint.Title ?? ""),
            Icon = Markers.Picture(change.Editing ? change.Icon : waypoint.Icon),
            Color = Markers.Hex(change.Editing ? change.Color : waypoint.Color),
        };
    }

    /// <summary>
    /// Keeps a preset on the map service and tells the asker how it went, off
    /// the game thread and back on it: nothing waits on the service, and a
    /// preset that quietly failed is a switch pressed again tomorrow.
    /// </summary>
    public static void Keep(ICoreServerAPI api, MapService service, IServerPlayer player, Preset preset)
    {
        _ = Task.Run(async () =>
        {
            var kept = await Presets.Keep(service, player.PlayerUID, preset, api.Logger).ConfigureAwait(false);
            var said = kept is null
                ? $"The map would not keep the preset for {preset.Pattern}."
                : $"Kept as a preset for {preset.Pattern}.";
            api.Event.EnqueueMainThreadTask(() => Tell(api, player, said), "witchlight preset kept");
        });
    }

    private static void Tell(ICoreServerAPI api, IServerPlayer player, string said) =>
        api.SendMessage(player, GlobalConstants.GeneralChatGroup, "[Witchlight] " + said, EnumChatType.Notification);

    public static void SendTo(
        ICoreServerAPI api, IServerPlayer player, Visibility visibility, Pins pins)
    {
        api.Network.GetChannel(Channel.Name)?.SendPacket(For(api, player, visibility, pins), player);
    }

    public static void SendToAll(ICoreServerAPI api, Visibility visibility, Pins pins)
    {
        foreach (var player in api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            SendTo(api, player, visibility, pins);
        }
    }
}
