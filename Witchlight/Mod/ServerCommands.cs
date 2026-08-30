using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// What an operator can type at the server, and what happens when they do.
///
/// The same class as the rest of the mod system rather than a type of its own: a
/// command surface is a view of the whole system, and a separate type would only
/// mean handing that type every field this one already has. What it is kept apart
/// from is the wiring — nothing here decides when anything runs.
///
/// Everything but `login` and `mark` is an operator's. Every player has their own
/// markers and their own settings, so every player can ask for the link that
/// reaches them and can mark the place they are standing in.
/// </summary>
public partial class WitchlightSystem
{
    private void RegisterCommands(ICoreServerAPI api)
    {
        api.ChatCommands
            .Create(Commands.Name)
            .WithShortName(api)
            .WithDescription("Witchlight map server tools")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("login")
                .WithDescription("Send yourself a link to your own page of the map")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(OnLogin)
            .EndSubCommand()
            .BeginSubCommand("mark")
                .WithDescription(
                    "Mark where you are looking, using your preset for that block")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(OnMarkHere)
            .EndSubCommand()
            .BeginSubCommand("export")
                .WithDescription("Write the surface of every loaded chunk")
                .HandleWith(OnExport)
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("What has been exported, and where the palette came from")
                .HandleWith(OnStatus)
            .EndSubCommand()
            .BeginSubCommand("service")
                .WithDescription("The map service: `status`, `start` or `stop`")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("action"))
                .HandleWith(OnService)
            .EndSubCommand()
            .BeginSubCommand("portrait")
                .WithDescription("Ask a player's client for a picture of their character")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("player"))
                .HandleWith(OnPortraitFetch)
            .EndSubCommand()
            .BeginSubCommand("icons")
                .WithDescription("Ask an admin's client for the marker pictures")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("player"))
                .HandleWith(OnIconFetch)
            .EndSubCommand()
            .BeginSubCommand("palette")
                .WithDescription("Ask an admin's client for a block colour palette")
                .WithArgs(api.ChatCommands.Parsers.OptionalWord("player"))
                .HandleWith(OnPaletteFetch)
            .EndSubCommand();
    }

    /// <summary>
    /// Sends one player a link that logs their browser in as them.
    ///
    /// The game is the only place identity exists, and this is the only channel
    /// that reaches a particular person — so the link is minted on the service's
    /// private channel, which nothing but this mod can reach, and handed over in
    /// a message only its owner sees.
    ///
    /// The address comes from the same setting that tells joining players where
    /// the map is. A link to an address only this machine can reach is not worth
    /// sending, which is the same reason `announce_url` exists.
    /// </summary>
    private TextCommandResult OnLogin(TextCommandCallingArgs args)
    {
        if (_sapi is not { } api || _service is null)
        {
            return TextCommandResult.Error("The map service is not running.");
        }

        if (args.Caller.Player is not IServerPlayer player)
        {
            return TextCommandResult.Error("Only a player has a page of their own.");
        }

        if (Settings.Announcement() is not { } where)
        {
            return TextCommandResult.Error(
                "The map has no address to hand out. Set `announce_url` in witchlight.conf.");
        }

        // The same round trip the greeting on join makes, so the two cannot come
        // to disagree about what a link is or how one is asked for. What is said
        // about it differs, and only that.
        WithLoginLink(player, where, (told, link) => api.SendMessage(
            told,
            GlobalConstants.GeneralChatGroup,
            link is null
                ? "The map could not be asked for a link. Is the service running?"
                : $"Your map: <a href=\"{link}\">open it</a> — the link is good for ten "
                  + "minutes and works once.",
            EnumChatType.Notification));

        return TextCommandResult.Success("Asking the map for a link…");
    }

    /// <summary>
    /// Marks where a player is looking, from the slash side of the command tree.
    ///
    /// The game keeps client and server commands in separate registries, so
    /// `/witchlight mark` and `.witchlight mark` are two commands — and a slash
    /// is what anybody types first. They are not two behaviours: which block
    /// somebody is looking at exists only on their own machine, so this asks that
    /// machine rather than answering a poorer version from where they stand.
    ///
    /// A player without this mod's client half hears nothing back, which the
    /// server cannot tell from a client that answered. What is said is therefore
    /// what was actually done: the asking.
    /// </summary>
    private TextCommandResult OnMarkHere(TextCommandCallingArgs args)
    {
        if (_sapi is not { } api)
        {
            return TextCommandResult.Error("server not ready");
        }
        if (args.Caller.Player is not IServerPlayer player)
        {
            return TextCommandResult.Error("Only a player is standing anywhere.");
        }

        api.Network.GetChannel(Channel.Name).SendPacket(new MarkNudge(), player);
        return TextCommandResult.Success("Marking…");
    }

    /// <summary>
    /// Writes the loaded world's surface. Exporting what is in memory keeps this
    /// a command an operator can run on a live server; chunks nobody has visited
    /// are not in memory and are not in the export.
    /// </summary>
    private TextCommandResult OnExport(TextCommandCallingArgs args)
    {
        return Export("command", force: true) is { } message
            ? TextCommandResult.Success(message)
            : TextCommandResult.Error("export failed, see the server log");
    }

    /// <summary>
    /// What state the map data is in. The first thing to look at when the map
    /// looks wrong, since it says whether the palette is the server's own poor
    /// one or a good one from a client.
    /// </summary>
    private TextCommandResult OnStatus(TextCommandCallingArgs args)
    {
        if (_sapi is not { } api)
        {
            return TextCommandResult.Error("server not ready");
        }

        var lines = new List<string?>
        {
            $"version: {Mod.Info.Version}",
            $"exports: {Settings.Exports}",
            Settings.PerWorld
                ? $"filed: a directory per world, inside {Settings.MapData}"
                : "filed: one map for this data path (per_world is off)",
            _palettes?.Describe() ?? "palette: not built yet",
            // The one line that would have shown the map counting from the wrong
            // place: both halves have to be right, and neither is visible in game.
            WorldFacts.Describe(api, Settings.Exports),
            _serviceProcess?.Describe() ?? $"service: not run from here; settings at {Settings.Path}",
            _palettes is { Wanted: true }
                ? "wanted: a palette from a player's client"
                : "wanted: nothing, the palette is good enough",
            _exporter?.Describe() ?? "terrain: not exporting yet",
            $"mapped: {_exporter?.Mapped ?? 0} chunks",
            $"markers: {MarkerFeed.All(api, _visibility).Count} saved on this server"
                + $", {_visibility.Decisions} with a chosen visibility",
            $"icons: {_icons?.Count ?? 0} for drawing them",
            $"portraits: {Portraits.Count(Settings.Exports)} sent by players",
            $"players out: {_service?.PlayersHealth ?? "not started"}",
            $"markers out: {_service?.MarkersHealth ?? "not started"}",
            $"waiting: {_exporter?.Waiting ?? 0} columns changed since then",
            // Only while one is running. A seed is a state a server passes through
            // rather than one it sits in, and a line saying it is not seeding is a
            // line every reader has to skip for the rest of the world's life.
            _seeding?.Describe(),
        };

        return TextCommandResult.Success(string.Join("\n", lines.Where(line => line is not null)));
    }

    /// <summary>
    /// The map service, from in game: whether it is up, and up or down on demand.
    ///
    /// Starting is deliberate and does not consult `autostart` — that setting says
    /// who starts it unasked, and somebody typing the command has asked.
    /// </summary>
    private TextCommandResult OnService(TextCommandCallingArgs args)
    {
        if (_sapi is not { } api)
        {
            return TextCommandResult.Error("server not ready");
        }

        var action = (args.Parsers[0].GetValue() as string ?? "status").ToLowerInvariant();

        if (_serviceProcess is null && action != "status")
        {
            _serviceProcess = ServiceProcess.Prepare(api, Mod);
        }

        if (_serviceProcess is not { } service)
        {
            return TextCommandResult.Error(
                "there is no bundled map service on this machine — see the server log, and run "
                + "`witchlight serve` yourself to serve the map");
        }

        return action switch
        {
            "start" => TextCommandResult.Success(service.Start()),
            "stop" => TextCommandResult.Success(service.Stop()),
            "status" => TextCommandResult.Success(service.Describe()),
            _ => TextCommandResult.Error($"no such action `{action}` — try status, start or stop"),
        };
    }

    /// <summary>
    /// Asks a player's client to draw them.
    ///
    /// Only their own machine can: nobody else's has that seraph loaded, which is
    /// why the picture travels rather than a description of it. A player naming
    /// nobody is asking for their own, which is the ordinary case.
    /// </summary>
    private TextCommandResult OnPortraitFetch(TextCommandCallingArgs args)
    {
        return Asking(args, "name one who is online", admin: false, target =>
        {
            _portraits?.Ask(target);
            return $"asked {target.PlayerName} to draw themselves";
        });
    }

    private TextCommandResult OnIconFetch(TextCommandCallingArgs args)
    {
        return Asking(args, "name an online admin", admin: true, target =>
        {
            // Everything, not only what is missing: this is the way back if an
            // icon on disk is wrong rather than absent.
            _icons?.AskForAll(target);
            return $"asked {target.PlayerName} for the marker pictures";
        });
    }

    /// <summary>
    /// Asks for a palette now rather than waiting for the next player to join.
    /// Names a player, or asks whoever ran the command.
    /// </summary>
    private TextCommandResult OnPaletteFetch(TextCommandCallingArgs args)
    {
        return Asking(args, "name an online admin", admin: true, target =>
        {
            _palettes?.AskAnyway(target);
            return $"asked {target.PlayerName} for a palette";
        });
    }

    /// <summary>
    /// The three commands that ask one player's client for something.
    ///
    /// They differ in one line each and were three copies of the same twenty:
    /// find the player named, or the one who typed it; refuse if there is nobody
    /// to ask; refuse if what is being asked for may only be taken from an admin
    /// and this player is not one.
    /// </summary>
    private TextCommandResult Asking(
        TextCommandCallingArgs args,
        string instead,
        bool admin,
        System.Func<IServerPlayer, string> ask)
    {
        if (_sapi is not { } api)
        {
            return TextCommandResult.Error("server not ready");
        }

        var named = args.Parsers[0].IsMissing ? null : args[0] as string;
        var target = named is null
            ? args.Caller.Player as IServerPlayer
            : api.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(player =>
                    string.Equals(player.PlayerName, named, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return TextCommandResult.Error(named is null
                ? $"no player to ask — {instead}, or run this in game"
                : $"{named} is not online");
        }

        if (admin && !target.HasPrivilege(Privilege.controlserver))
        {
            return TextCommandResult.Error(
                $"{target.PlayerName} is not an admin; that is only taken from admins");
        }

        return TextCommandResult.Success(ask(target));
    }
}
