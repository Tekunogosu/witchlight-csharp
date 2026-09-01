using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Who may run each of this mod's commands.
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
    private static readonly Dictionary<string, string> ByDefault = new(StringComparer.Ordinal)
    {
        [Login] = Privilege.chat,
        [Mark] = Privilege.chat,
        [Portrait] = Privilege.chat,
        [Palette] = Privilege.chat,
        [Icons] = Privilege.chat,
        [Export] = Privilege.controlserver,
        [Status] = Privilege.controlserver,
        [Service] = Privilege.controlserver,
    };

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

        foreach (var (command, fallback) in ByDefault)
        {
            var said = Settings.Value($"commands.{command}")?.Trim() ?? "";
            Settled[command] = said switch
            {
                "" => fallback,
                AdminWord => Privilege.controlserver,
                PlayerWord => Privilege.chat,
                _ when known.Contains(said) => said,
                _ => Refused(log, command, said),
            };
        }
    }

    private static string Refused(ILogger log, string command, string said)
    {
        log.Warning(
            "[witchlight] `commands.{0} = \"{1}\"` in {2} is not a privilege this game has, so "
            + "only admins may run `{3} {0}`. Use `{4}`, `{5}`, or one of: {6}",
            command,
            said,
            Settings.Path,
            Commands.Short,
            AdminWord,
            PlayerWord,
            string.Join(", ", Privilege.AllCodes().OrderBy(code => code, StringComparer.Ordinal)));
        return Privilege.controlserver;
    }

    /// <summary>The privilege one command asks of whoever types it.</summary>
    public static string For(string command) =>
        Settled.TryGetValue(command, out var privilege) ? privilege : ByDefault[command];

    /// <summary>Whether this player may run it.</summary>
    public static bool Holds(IPlayer player, string command) => player.HasPrivilege(For(command));

    /// <summary>
    /// Who may run what, in one line, for `witchlight status`.
    ///
    /// Grouped by privilege rather than listed per command, because an operator
    /// reading this wants the shape of the answer — which of these are locked and
    /// which are not — and eight lines saying `controlserver` five times is a
    /// worse picture of it than one.
    ///
    /// The only place these are visible at all on a server upgrading into this:
    /// a settings file written before `[commands]` existed says nothing about
    /// them, and nothing rewrites a file an operator owns just to add a section
    /// of defaults it is already following.
    /// </summary>
    public static string Describe()
    {
        var groups = ByDefault.Keys
            .GroupBy(For, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
                $"{Who(group.First())} runs "
                + string.Join(", ", group.OrderBy(name => name, StringComparer.Ordinal)));

        return $"commands: {string.Join("; ", groups)} — set in [commands] in {Settings.Path}";
    }

    /// <summary>
    /// Who may run it, said the way a line of chat should say it.
    ///
    /// Read off the setting rather than written beside each command, so a server
    /// that opened `export` to its moderators says so when it refuses somebody
    /// rather than telling them to find an admin.
    /// </summary>
    public static string Who(string command)
    {
        var privilege = For(command);
        if (privilege == Privilege.controlserver)
        {
            return "an admin";
        }
        return privilege == Privilege.chat ? "any player" : $"anybody with `{privilege}`";
    }
}
