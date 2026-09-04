using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>Where a player is, right now.</summary>
public class LivePlayer
{
    public string Name { get; set; } = "";
    public string Uid { get; set; } = "";

    /// <summary>
    /// How far the server loads ground around this player, in chunks: their
    /// own view distance as the server granted it, never past the server's
    /// MaxChunkRadius. What standing here adds to their map, and how far beside
    /// them the map service may ask the game for ground.
    /// </summary>
    public int ViewChunks { get; set; }

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

    /// <summary>
    /// Anything else this server has been asked to show for a player — mana, a
    /// level, whatever a mod keeps on the entity.
    ///
    /// Empty on a server that asked for none and on a player who has none, which
    /// are the same thing as far as a card is concerned. See
    /// <see cref="PlayerBar"/> for why the map can read a mod's numbers without
    /// knowing anything about the mod.
    /// </summary>
    public List<LiveBar> Bars { get; set; } = new();
}

/// <summary>One reading, and how much of it there could be.</summary>
public class LiveBar
{
    public string Name { get; set; } = "";
    public float Value { get; set; }
    public float Max { get; set; }

    /// <summary>What colour to draw it, as the settings gave it.</summary>
    public string Colour { get; set; } = "";

    /// <summary>
    /// What to file it under where the map lets a reader switch bars off, or
    /// empty where nobody could say. See <see cref="PlayerBar"/> for why this
    /// cannot simply be worked out.
    /// </summary>
    public string Group { get; set; } = "";
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

    /// <summary>
    /// Every group the server has, by id: what it is called and who is in it,
    /// online or not. What the service shares a map against, so it has to name
    /// members who are not on — a player shares with their group, not with
    /// whoever of it happens to be logged in.
    /// </summary>
    public Dictionary<string, LiveGroup> Groups { get; set; } = new();
}

/// <summary>One group, as the service wants it.</summary>
public class LiveGroup
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
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
        sorted.Groups = AllGroups(api);
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
        foreach (var player in Playing(api))
        {
            var uid = player.PlayerUID;
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }

            var held = new HashSet<int>();
            foreach (var membership in player.Groups ?? Array.Empty<PlayerGroupMembership>())
            {
                if (Joined(api, membership.GroupUid))
                {
                    held.Add(membership.GroupUid);
                }
            }
            groups[uid] = held;
        }
        return groups;
    }

    /// <summary>
    /// Every group the server has and everybody in it, read off the server's
    /// own player data rather than off the players who are on. The game keeps
    /// a group's online members on the group and every player's memberships on
    /// the player, so the full membership is the second read the other way.
    /// </summary>
    private static Dictionary<string, LiveGroup> AllGroups(ICoreServerAPI api)
    {
        var all = new Dictionary<string, LiveGroup>();
        var known = api.Groups?.PlayerGroupsById;
        if (known is null)
        {
            return all;
        }

        foreach (var (id, group) in known)
        {
            if (group is null || !Joined(api, id))
            {
                continue;
            }
            all[id.ToString()] = new LiveGroup { Name = group.Name ?? "" };
        }

        var data = api.PlayerData?.PlayerDataByUid;
        if (data is null)
        {
            return all;
        }

        foreach (var (uid, player) in data)
        {
            foreach (var id in player?.PlayerGroupMemberships?.Keys ?? Enumerable.Empty<int>())
            {
                if (all.TryGetValue(id.ToString(), out var group))
                {
                    group.Members.Add(uid);
                }
            }
        }
        return all;
    }

    /// <summary>
    /// Whether a membership is in a group somebody actually joined.
    ///
    /// A player's memberships are not only the groups they joined. The game puts
    /// every player in its own channels — general chat, server info, the damage
    /// and info logs — and those arrive here as memberships like any other. Every
    /// pair of players on the server therefore shares one, which made "my group"
    /// on the map the whole server: the tab that exists to show a smaller list
    /// showed the same list as the one beside it, on every server that had never
    /// created a group at all.
    ///
    /// So a membership counts when the server can name a player group behind it.
    /// The channels have none, being the game's own and not anybody's. Read from
    /// the server's own list rather than by refusing the uids those channels
    /// happen to use, because a rule about which numbers are real is a rule that
    /// goes wrong quietly the day the game adds a channel.
    ///
    /// A group the game made to carry a private message is a real group and is
    /// still not one of these. It is two people talking, and a map that read it
    /// as a party would put whoever you last messaged on your group list and
    /// leave them there.
    /// </summary>
    private static bool Joined(ICoreServerAPI api, int group)
    {
        var known = api.Groups?.PlayerGroupsById;
        return known is not null
            && known.TryGetValue(group, out var joined)
            && joined is not null
            && !joined.CreatedByPrivateMessage;
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

    /// <summary>
    /// The extra readings this player carries, of the ones the settings ask for.
    ///
    /// A player with none of them gets an empty list rather than a list of empty
    /// bars — see <see cref="PlayerBar.Of"/>, which is where a bar that does not
    /// apply is told from one that is merely at zero.
    /// </summary>
    private static List<LiveBar> Bars(ICoreServerAPI api, Entity entity)
    {
        var found = new List<LiveBar>();
        foreach (var bar in PlayerBar.Settled(api))
        {
            if (bar.Of(entity) is { } reading)
            {
                found.Add(reading);
            }
        }
        return found;
    }

    /// <summary>
    /// The server grants a view distance in blocks and loads whole chunks out to
    /// it, no further than its own MaxChunkRadius.
    /// </summary>
    private static int ViewChunksOf(ICoreServerAPI api, IPlayer player)
    {
        var blocks = player.WorldData?.LastApprovedViewDistance ?? 0;
        var chunks = (int)Math.Ceiling(blocks / (double)Math.Max(1, api.WorldManager.ChunkSize));
        return Math.Max(0, Math.Min(chunks, api.Server.Config.MaxChunkRadius));
    }

    /// <summary>
    /// Everybody who is actually in the world.
    ///
    /// The game's list of online players also holds whoever is still connecting
    /// — its own documentation warns so — and a connecting player already has
    /// an entity standing at their last position for the half minute or more
    /// their client takes to load, or for good where it never finishes. Counted,
    /// that is a map saying somebody is on when nobody is; so every reading of
    /// who is on goes through here, and here means playing.
    /// </summary>
    private static IEnumerable<IServerPlayer> Playing(ICoreServerAPI api)
    {
        return api.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .Where(player => player.ConnectionState == EnumClientState.Playing);
    }

    public static List<LivePlayer> All(ICoreServerAPI api, string exports)
    {
        var players = new List<LivePlayer>();
        foreach (var player in Playing(api))
        {
            if (player.Entity?.Pos is not { } position)
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
                ViewChunks = ViewChunksOf(api, player),
                X = Blocks.At(position.X),
                Y = Blocks.At(position.Y),
                Z = Blocks.At(position.Z),
                Facing = Bearing(position.Yaw),
                Health = health?.GetFloat("currenthealth") ?? 0f,
                MaxHealth = health?.GetFloat("maxhealth") ?? 0f,
                Saturation = hunger?.GetFloat("currentsaturation") ?? 0f,
                MaxSaturation = hunger?.GetFloat("maxsaturation") ?? 0f,
                Bars = Bars(api, player.Entity),
                Portrait = stored?.Name,
                PortraitAt = stored?.At ?? 0,
            });
        }
        return players;
    }
}
