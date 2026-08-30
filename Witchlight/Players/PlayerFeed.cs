using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
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
    /// Which way they are looking, in degrees clockwise from north.
    ///
    /// A compass bearing rather than the game's own angle, because that is what a
    /// north-up map turns a picture of a player by. The game holds a yaw in
    /// radians that starts from south and turns the other way, so the one place
    /// that reads the game's convention is the one that converts it, and nothing
    /// downstream has to know the game had a different idea of an angle.
    /// </summary>
    public float Facing { get; set; }

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

/// <summary>
/// Who is online, arranged by who may see them.
///
/// The same shape the markers travel in, and for the same reason. Whether where
/// somebody is standing may be shown to somebody else depends on a setting and on
/// what groups the game has people in, and this is the half that knows both — so
/// the sorting happens here and the service is left holding lists it hands out
/// without looking into them.
/// </summary>
public class LivePlayers
{
    /// <summary>
    /// How many are on, said to everybody.
    ///
    /// A server that hides where people are standing still says how busy it is:
    /// that is a fact about the server rather than about anybody on it, and a map
    /// that would not say it is a map nobody can tell from a dead one.
    /// </summary>
    public int Online { get; set; }

    /// <summary>Players anyone may see. Everybody, where positions are public.</summary>
    public List<LivePlayer> Public { get; set; } = new();

    /// <summary>
    /// Players one particular person may see beyond that, by their uid. Empty
    /// where positions are public, since then everyone is already in
    /// <see cref="Public"/>.
    /// </summary>
    public Dictionary<string, List<LivePlayer>> Private { get; set; } = new();

    /// <summary>
    /// Who shares a group with one particular person, by their uid, as uids.
    ///
    /// Not about who may be seen — it is what lets the page offer "my group" as a
    /// way of reading a list it has already been given. Somebody always shares a
    /// group with themselves, so a person with no groups still has a list with
    /// themselves in it rather than an empty one meaning nothing.
    /// </summary>
    public Dictionary<string, List<string>> Grouped { get; set; } = new();


}

/// <summary>
/// Who is online, as the map service wants it.
///
/// Its own file rather than a third of the one the markers were in. Where a
/// player is standing and who may see them is a question of its own, and the only
/// thing it shared with a waypoint was travelling on the same beat.
/// </summary>
public static class PlayerFeed
{
    /// <summary>Who is online, sorted by who may see them, as the service wants it.</summary>
    public static string Json(ICoreServerAPI api, string exports)
    {
        return JsonConvert.SerializeObject(Seen(api, exports));
    }

    /// <summary>
    /// Who is online, arranged into what anyone may see and what only a group may.
    ///
    /// Where positions are everybody's, everybody goes in one list and there is
    /// nothing else to work out. Where they are not, each person gets a list of
    /// their own: themselves, and whoever the game has in a group with them.
    ///
    /// Only players who are on can be sorted this way, because a group membership
    /// is read off the player and the game hands that out for the ones it has in
    /// the world. Somebody looking at the map while their own player is offline
    /// therefore sees what everybody sees — which is the same answer the setting
    /// gives anyone the server has not placed in a group.
    /// </summary>
    public static LivePlayers Seen(ICoreServerAPI api, string exports)
    {
        var players = All(api, exports);
        var sorted = new LivePlayers { Online = players.Count };

        // Read each time rather than held, so an operator changing their mind
        // takes effect on the next post rather than the next restart — the same
        // rule the markers follow.
        var everyones = Settings.PlayersPublic;
        if (everyones)
        {
            sorted.Public = players;
        }

        var groups = GroupsOf(api);
        foreach (var player in players)
        {
            if (player.Uid.Length == 0)
            {
                continue;
            }

            var mine = groups.TryGetValue(player.Uid, out var held) ? held : new HashSet<int>();
            var together = players
                .Where(other => other.Uid.Length > 0 && (other.Uid == player.Uid
                    || (groups.TryGetValue(other.Uid, out var theirs) && theirs.Overlaps(mine))))
                .ToList();

            sorted.Grouped[player.Uid] = together.Select(other => other.Uid).ToList();
            if (!everyones)
            {
                sorted.Private[player.Uid] = together;
            }
        }

        return sorted;
    }

    /// <summary>
    /// Which groups the game has each online player in, by uid.
    ///
    /// Worked out once per post rather than per pair: comparing everybody with
    /// everybody is already a square of the players online, and reading the
    /// memberships off the entity inside that loop would make it a cube of them.
    /// </summary>
    private static Dictionary<string, HashSet<int>> GroupsOf(ICoreServerAPI api)
    {
        var groups = new Dictionary<string, HashSet<int>>();
        foreach (var player in api.World.AllOnlinePlayers)
        {
            var uid = player?.PlayerUID;
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }

            var held = new HashSet<int>();
            foreach (var membership in player!.Groups ?? Array.Empty<PlayerGroupMembership>())
            {
                held.Add(membership.GroupUid);
            }
            groups[uid] = held;
        }
        return groups;
    }

    /// <summary>
    /// A game yaw as a compass bearing: degrees clockwise from north, under 360.
    ///
    /// The game's yaw is radians from south, anticlockwise: zero is looking south
    /// and a quarter turn is looking east, which is what
    /// <c>BlockFacing.HorizontalFromYaw</c> reads it as and what the client's own
    /// map turns its player marker by. A bearing is degrees from north, clockwise,
    /// so the half turn and the change of sign are both the difference between the
    /// two and neither is a correction to the other.
    /// </summary>
    private static float Bearing(float yaw) => GameMath.Mod(180f - yaw * GameMath.RAD2DEG, 360f);

    public static List<LivePlayer> All(ICoreServerAPI api, string exports)
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
                X = Blocks.At(position.X),
                Y = Blocks.At(position.Y),
                Z = Blocks.At(position.Z),
                Facing = Bearing(position.Yaw),
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
}
