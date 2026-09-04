using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Who may do each of the things this mod gates.
///
/// One question with one answer, asked in three places: the privilege a
/// subcommand is registered under, whether a named player may be asked for the
/// thing a command fetches, and who is in the room when the server needs a
/// palette. Those used to be a literal `Privilege.controlserver` at each site and
/// a `bool admin` threaded between them, which is three chances to disagree about
/// what an operator asked for.
///
/// The split the defaults draw is between a command that changes what the server
/// is doing and a command that answers a question about the person typing it.
/// Writing the world, reading the whole of the map's state and starting or
/// stopping the service are an operator's; a link to your own page, a marker
/// where you are standing, and asking a client for the pictures the map draws
/// with are anybody's.
///
/// Loosening one of the last three costs less than it looks. What a client sends
/// back is taken on the same terms whoever asked for it — only an admin's palette
/// or icon may replace one already chosen, and anybody else's fills gaps — so
/// these decide who may ask rather than who may repaint the map. See
/// <see cref="PaletteExchange.Accept"/> and <see cref="IconExchange.Accept"/>.
///
/// Not everything gated is a command. The web map asks two questions of its own —
/// may this person see where the land claims are, and may they draw one — and
/// those are the same question in a different table, so they are answered here
/// rather than by a second class that would have its own idea of what a typo in a
/// privilege name means. What differs between a gate and a gate is the key it is
/// written under in the settings and what it falls back to, which is what
/// <see cref="Gate"/> holds.
/// </summary>
public static class Permissions
{
    /// <summary>Every command this decides for, which is every command with a name.</summary>
    public const string Login = "login";
    public const string Mark = "mark";
    public const string Portrait = "portrait";
    public const string Palette = "palette";
    public const string Icons = "icons";
    public const string Export = "export";
    public const string Status = "status";
    public const string Service = "service";

    /// <summary>Seeing where the land claims are, on the web map.</summary>
    public const string ClaimsView = "claims.view";

    /// <summary>Drawing a new land claim from the web map.</summary>
    public const string ClaimsCreate = "claims.create";

    /// <summary>
    /// One thing that can be asked for, where the operator says who may, and who
    /// may where they have not said.
    ///
    /// The key carries its table, because these no longer all live in one. A
    /// command is under `[commands]` and is named there by the same word the game
    /// registers it under, so the two cannot drift; the claim gates are under
    /// `[claims]`, which is where a settings file already keeps everything else
    /// about them.
    /// </summary>
    private sealed record Gate(string Key, string Fallback);

    /// <summary>What `admin` is short for, in the settings file.</summary>
    private const string AdminWord = "admin";

    /// <summary>What `player` is short for.</summary>
    private const string PlayerWord = "player";

    /// <summary>
    /// What each command asks for where the settings say nothing.
    ///
    /// The same table the map service writes into a fresh settings file, and the
    /// two have to agree: a first run registers its commands before that file
    /// exists, so these are what an operator sees the mod behave as and then
    /// finds written down.
    /// </summary>
    private static readonly Dictionary<string, Gate> ByDefault = new(StringComparer.Ordinal)
    {
        [Login] = new($"{CommandTable}.{Login}", Privilege.chat),
        [Mark] = new($"{CommandTable}.{Mark}", Privilege.chat),
        [Portrait] = new($"{CommandTable}.{Portrait}", Privilege.chat),
        [Palette] = new($"{CommandTable}.{Palette}", Privilege.chat),
        [Icons] = new($"{CommandTable}.{Icons}", Privilege.chat),
        [Export] = new($"{CommandTable}.{Export}", Privilege.controlserver),
        [Status] = new($"{CommandTable}.{Status}", Privilege.controlserver),
        [Service] = new($"{CommandTable}.{Service}", Privilege.controlserver),

        // Where a claim is, is already everybody's: the game sends every claim to
        // every client and draws the borders for anyone holding the right tool.
        // A map that hid them would tell players less than the game does, so this
        // starts as what they already have and the setting is for a server that
        // wants less.
        [ClaimsView] = new(ClaimsView, Privilege.chat),

        // What the game asks of `/land claim`, and for the same reason. The map
        // must not be a way round a rule the server already has, so this starts
        // as that rule rather than as a looser one — and it is checked in
        // addition to the game's own, never instead of it. See
        // <see cref="Claiming.Apply"/>.
        [ClaimsCreate] = new(ClaimsCreate, Privilege.claimland),
    };

    /// <summary>The table the commands are written under, spelled once.</summary>
    private const string CommandTable = "commands";

    /// <summary>
    /// What the settings said, read once.
    ///
    /// Held rather than re-read, because the game bakes a privilege into the
    /// command table at registration: a value read again later could disagree
    /// with the one the command is actually enforcing, and a permission that
    /// reports one thing and enforces another is worse than one that needs a
    /// restart to change.
    /// </summary>
    private static readonly Dictionary<string, string> Settled = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads what the operator asked for, once, at command registration.
    ///
    /// A name the game does not know is refused to everyone but an admin. Failing
    /// closed is the only safe way round: the cost of a typo is a command an
    /// operator has to fix, and the cost of the other way is a command anybody
    /// can run because it was spelled wrong.
    /// </summary>
    public static void Settle(ILogger log)
    {
        Settled.Clear();
        var known = new HashSet<string>(Privilege.AllCodes(), StringComparer.Ordinal);
        var named = Named();

        foreach (var (name, gate) in ByDefault)
        {
            var said = Settings.Value(gate.Key)?.Trim() ?? "";
            Settled[name] = said switch
            {
                "" => gate.Fallback,
                AdminWord => Privilege.controlserver,
                PlayerWord => Privilege.chat,
                _ when known.Contains(said) => said,
                _ when named.TryGetValue(said, out var code) => code,
                _ => Refused(log, gate.Key, said),
            };
        }
    }

    /// <summary>
    /// The game's names for its privileges, by name, where the name and the
    /// code differ.
    ///
    /// The game calls the privilege to claim land <c>claimland</c> and gives it
    /// the code <c>areamodify</c>; the settings file names it the way the game
    /// does, and the file used to be refused for it — which shut claiming to
    /// everybody but admins on every server that never touched the setting.
    /// Read off the game's own table rather than written here, so a privilege
    /// it renames tomorrow is still understood.
    /// </summary>
    private static Dictionary<string, string> Named()
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in typeof(Privilege).GetFields(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType == typeof(string) && field.GetValue(null) is string code && field.Name != code)
            {
                named[field.Name] = code;
            }
        }
        return named;
    }

    /// <summary>
    /// What a privilege name the game does not know comes to.
    ///
    /// Refused to everyone but an admin. Failing closed is the only safe way
    /// round: the cost of a typo is something an operator has to fix, and the
    /// cost of the other way is something anybody can do because it was spelled
    /// wrong. The whole settings key is said, since the reader has to go and find
    /// the line and there are now two tables it could be in.
    /// </summary>
    private static string Refused(ILogger log, string key, string said)
    {
        log.Warning(
            "[witchlight] `{0} = \"{1}\"` in {2} is not a privilege this game has, so only "
            + "admins may. Use `{3}`, `{4}`, or one of: {5}",
            key,
            said,
            Settings.Path,
            AdminWord,
            PlayerWord,
            string.Join(", ", Privilege.AllCodes().Concat(Named().Keys)
                .OrderBy(code => code, StringComparer.Ordinal)));
        return Privilege.controlserver;
    }

    /// <summary>The privilege one gate asks of whoever wants through it.</summary>
    public static string For(string name) =>
        Settled.TryGetValue(name, out var privilege) ? privilege : ByDefault[name].Fallback;

    /// <summary>Whether this player may.</summary>
    public static bool Holds(IPlayer player, string name) => player.HasPrivilege(For(name));

    /// <summary>
    /// Whether somebody may, named by uid rather than handed as a player.
    ///
    /// The web map asks about people who are not standing in the world: whoever
    /// is looking at it may have signed in from a browser and never joined today,
    /// and refusing them for that would make a permission mean "and be online".
    /// So the game's own answer is used where there is an online player to ask,
    /// and where there is not, the same three things the game reads are read off
    /// the stored player data instead — a denial, a permanent grant, and the role
    /// they are in.
    ///
    /// A uid the server has never seen is nobody, and nobody may. That is a
    /// player who has never joined this server, which is exactly the case where
    /// there is nothing to base an answer on.
    /// </summary>
    public static bool Holds(ICoreServerAPI api, string uid, string name)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }

        if (api.World.PlayerByUid(uid) is { } playing)
        {
            return Holds(playing, name);
        }

        var data = api.PlayerData.GetPlayerDataByUid(uid);
        if (data is null)
        {
            return false;
        }

        var privilege = For(name);
        if (data.DeniedPrivileges.Contains(privilege))
        {
            return false;
        }
        if (data.PermaPrivileges.Contains(privilege))
        {
            return true;
        }

        var role = api.Permissions.GetRole(data.RoleCode);
        return role is not null
            && (role.Privileges.Contains(privilege) || role.RuntimePrivileges.Contains(privilege));
    }

    /// <summary>
    /// Who may do what, one line per table, for `witchlight status`.
    ///
    /// Grouped by privilege rather than listed per gate, because an operator
    /// reading this wants the shape of the answer — which of these are locked and
    /// which are not — and eight lines saying `controlserver` five times is a
    /// worse picture of it than one.
    ///
    /// A line per table rather than one over the lot. Which file a reader has to
    /// open to change something is the point of the line, and a single line
    /// naming two tables would leave them to work out which of them held which
    /// name.
    ///
    /// The only place these are visible at all on a server upgrading into this:
    /// a settings file written before a table existed says nothing about it, and
    /// nothing rewrites a file an operator owns just to add a section of defaults
    /// it is already following.
    /// </summary>
    public static IEnumerable<string> Describe() =>
        ByDefault
            .GroupBy(gate => Table(gate.Value.Key), StringComparer.Ordinal)
            .OrderBy(table => table.Key, StringComparer.Ordinal)
            .Select(table => Line(table.Key, table.Select(gate => gate.Key)));

    /// <summary>
    /// One table's gates, grouped by who holds them.
    ///
    /// The name a reader sees is the settings key without its table, since the
    /// line already says which table that is — `commands: an admin may export`
    /// rather than the same with `commands.` in front of every word.
    /// </summary>
    private static string Line(string table, IEnumerable<string> names)
    {
        var groups = names
            .GroupBy(For, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
                $"{Who(group.First())} may "
                + string.Join(", ", group.Select(Bare).OrderBy(name => name, StringComparer.Ordinal)));

        return $"{table}: {string.Join("; ", groups)} — set in [{table}] in {Settings.Path}";
    }

    /// <summary>
    /// Which settings table a key is written under.
    ///
    /// Read off the key rather than listed, so a gate in a table nothing has
    /// mentioned yet still gets a line. A key with no table is a command, which
    /// is the one kind whose name in the file is not prefixed by its section.
    /// </summary>
    private static string Table(string key)
    {
        var dot = key.IndexOf('.');
        return dot < 0 ? CommandTable : key[..dot];
    }

    /// <summary>A gate's name without the table it is written under.</summary>
    private static string Bare(string name)
    {
        var dot = name.IndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>
    /// Who may run it, said the way a line of chat should say it.
    ///
    /// Read off the setting rather than written beside each gate, so a server
    /// that opened `export` to its moderators says so when it refuses somebody
    /// rather than telling them to find an admin.
    /// </summary>
    public static string Who(string name)
    {
        var privilege = For(name);
        if (privilege == Privilege.controlserver)
        {
            return "an admin";
        }
        return privilege == Privilege.chat ? "any player" : $"anybody with `{privilege}`";
    }
}
