using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// What this mod's commands are called, and how one is declared.
///
/// A long name that is always there and a short one that is there when nothing
/// else has claimed it. Both sides register the same pair, so what a player types
/// reads the same whether the command runs on their own machine or the server's —
/// only the prefix differs, and that is the game's way of saying which side is
/// answering.
/// </summary>
public static class Commands
{
    /// <summary>The name the command tree is registered under.</summary>
    public const string Name = "witchlight";

    /// <summary>The short name, for anyone typing it more than once.</summary>
    public const string Short = "wl";

    /// <summary>
    /// Gives a command its short name, unless something already answers to it.
    ///
    /// The game's own <c>WithAlias</c> writes straight into the command table
    /// without looking first, so a name another mod holds would be taken from it
    /// silently, leaving a mod broken and nothing anywhere saying where its
    /// command went. Two letters is common ground and worth asking about before
    /// claiming.
    ///
    /// Losing the race costs nothing but keystrokes: the long name is registered
    /// either way, and it is the one every piece of documentation gives.
    ///
    /// Asked of each side separately, because the game keeps a command table per
    /// side — the short name can be free on a server and taken on the client
    /// connecting to it.
    /// </summary>
    public static IChatCommand WithShortName(this IChatCommand command, ICoreAPI api)
    {
        if (api.ChatCommands.Get(Short) is not null)
        {
            api.Logger.Notification(
                "[witchlight] something already answers to {0}, so the commands are only "
                + "under {1}", Short, Name);
            return command;
        }

        return command.WithAlias(Short);
    }

    /// <summary>
    /// Opens a subcommand under the privilege the settings give it.
    ///
    /// The name is what the settings key it by, so it is written once and both
    /// the game's command table and <see cref="Permissions"/> are handed the same
    /// string. Written twice, they drift the moment one of them is renamed — and
    /// a command registered under a permission nobody configured is a command
    /// whose setting silently does nothing.
    /// </summary>
    public static IChatCommand BeginSubCommand(
        this IChatCommand tree, string name, string description) =>
        tree.BeginSubCommand(name)
            .WithDescription(description)
            .RequiresPrivilege(Permissions.For(name));
}
