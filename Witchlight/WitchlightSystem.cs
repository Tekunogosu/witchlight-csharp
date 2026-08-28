using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Server-side export for the Witchlight map server.
///
/// The palette has to be built while block textures are still in memory: the
/// server frees them once assets are loaded (Block.FreeRAMServer), so this hooks
/// the asset stage rather than run time.
/// </summary>
public class WitchlightSystem : ModSystem
{
    private const string ExportDirName = "witchlight";

    private ICoreServerAPI? _sapi;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;

        // Markers belonging to everyone else, pushed to each player's map.
        api.Network.RegisterChannel(SharedServer.Channel)
            .RegisterMessageType<SharedMarkers>()
            .RegisterMessageType<PaletteRequest>()
            .RegisterMessageType<PaletteTable>()
            .RegisterMessageType<IconRequest>()
            .RegisterMessageType<IconTable>()
            .RegisterMessageType<PortraitRequest>()
            .RegisterMessageType<PlayerPortrait>()
            .SetMessageHandler<PaletteTable>(OnPaletteFromClient)
            .SetMessageHandler<IconTable>(OnIconsFromClient)
            .SetMessageHandler<PlayerPortrait>(OnPortraitFromClient);
        api.Event.PlayerNowPlaying += player => Safely("greeting a player", () =>
        {
            SharedServer.SendTo(api, player);
            AskForPalette(player);
            AskForIcons(player);
            AskForPortrait(player);
            SayWhereTheMapIs(player);
        });
        _service = new MapService(ExportDir, api.Logger);

        // Where the game counts from, so the map can agree with what a player
        // reads off their own screen. Written when the world is ready rather than
        // here: spawn is not known while mods are still starting, and asking for
        // it then threw — which left the map counting from absolute zero, with
        // nothing on either side looking wrong.
        // Everything that needs a world rather than a mod: where the world counts
        // from, and the service that serves it.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => Safely("starting up", () =>
        {
            KeepWorldFacts();
            StartService();
        }));

        // Markers ride the timer that already pushes them to players in game:
        // they change a few times an hour, and a post that says nothing new is
        // dropped before it is sent anyway.
        api.Event.RegisterGameTickListener(_ => Safely("sharing markers", () =>
        {
            SharedServer.SendToAll(api);
            _service?.Markers(Live.WaypointsJson(api));
        }), ShareIntervalMs);

        // A map this build cannot read is thrown away rather than upgraded: the
        // format still moves, and it rebuilds itself as players explore.
        Regions.ResetIfUnreadable(ExportDir, GlobalConstants.ChunkSize, api.Logger);

        // Live data goes over the socket now. A live.json left by an older build
        // is not read by anything, and leaving it invites a reader to find it and
        // show markers that are however old the file is.
        var stale = Path.Combine(ExportDir, "live.json");
        if (File.Exists(stale))
        {
            try
            {
                File.Delete(stale);
                api.Logger.Notification("[witchlight] removed live.json; live data goes over the socket");
            }
            catch (System.Exception error)
            {
                api.Logger.Warning("[witchlight] could not remove live.json: {0}", error.Message);
            }
        }

        // What a previous run left on disk. Columns already there are not re-read
        // merely for being loaded again, and the seasons stored beside them say
        // whether the year has moved since. Seeded before the event is hooked so
        // that nothing arrives ahead of it.
        _seasons = Regions.Index(Regions.DirectoryIn(ExportDir), GlobalConstants.ChunkSize);
        _dirty.Seed(_seasons.Keys);

        // The server marks a chunk dirty for every block that moves in it and for
        // every chunk it loads, which is the whole signal an export needs. Without
        // it each tick re-reads the surface of every chunk in memory to discover
        // that almost none of it moved.
        api.Event.ChunkDirty += (coord, chunk, reason) =>
            Safely("noting a changed chunk", () => _dirty.Mark(coord.X, coord.Z, reason));

        // A tick listener rather than a load event: it is guaranteed to fire, and
        // repeating it keeps the map current without anyone typing a command.
        api.Event.RegisterGameTickListener(
            _ => Safely("exporting", () => Export("timer")), ExportIntervalMs);

        // Players move, so this goes far more often than the terrain — and it
        // goes over the socket rather than to a file, because a position is worth
        // nothing by the time a disk has finished with it.
        api.Event.RegisterGameTickListener(
            _ => Safely("posting players", PostPlayers), LiveIntervalMs);
        api.Event.RegisterCallback(_ => Safely("seeding the map", () => Seed(SeedRadius)), SeedDelayMs);
        api.Event.GameWorldSave += () => Safely("exporting on save", () => Export("world save"));
        // Version first, and on every start: the quickest way to tell a deployed
        // mod from the one you meant to deploy. The map service prints its own for
        // the same reason, and the two must match on minor version.
        api.Logger.Notification(
            "[witchlight] {0} ready, exporting every {1}s to {2}",
            Mod.Info.Version,
            ExportIntervalMs / 1000,
            ExportDir);

        api.ChatCommands
            .Create(Commands.Name)
            .WithShortName(api)
            .WithDescription("Witchlight map server tools")
            .RequiresPrivilege(Privilege.controlserver)
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
    /// Writes the loaded world's surface. Exporting what is in memory keeps this
    /// a command an operator can run on a live server; chunks nobody has visited
    /// are not in memory and are not in the export.
    /// </summary>
    private TextCommandResult OnExport(TextCommandCallingArgs args)
    {
        var message = Export("command", force: true);
        return message is null
            ? TextCommandResult.Error("export failed, see the server log")
            : TextCommandResult.Success(message);
    }

    /// <summary>
    /// Tells a player where the map is as they join.
    ///
    /// A map nobody knows the address of is a map nobody looks at, and this is the
    /// half that can say so in chat. The settings are read each time rather than
    /// held, so turning the message off takes effect on the next join instead of
    /// on the next restart.
    ///
    /// Nothing is said when there is no address to give. That covers a service
    /// that is not running, one somebody else runs somewhere this cannot see, and
    /// a server whose real address only its operator knows and has not said.
    /// </summary>
    private void SayWhereTheMapIs(IServerPlayer player)
    {
        if (_sapi is null || !ServiceProcess.Announces(ServiceProcess.ConfigPath))
        {
            return;
        }

        if (ServiceProcess.Announcement(ServiceProcess.ConfigPath, ExportDir) is not { } where)
        {
            return;
        }

        _sapi.SendMessage(
            player,
            GlobalConstants.GeneralChatGroup,
            $"The map is at {where}",
            EnumChatType.Notification);
    }

    /// <summary>
    /// Brings up the map service, unless the settings say somebody else will.
    ///
    /// Once, at the point the world is ready: the service reads the palette and
    /// the exported regions at start, and both are on disk by then.
    /// </summary>
    private void StartService()
    {
        if (_sapi is null || _serviceProcess is not null)
        {
            return;
        }

        _serviceProcess = ServiceProcess.Prepare(_sapi, Mod, ExportDir);
        if (_serviceProcess is null)
        {
            return;
        }

        if (!_serviceProcess.Wanted)
        {
            _sapi.Logger.Notification(
                "[witchlight] autostart is off in {0}, so the map service was not started — "
                + "run `witchlight serve` yourself to serve the map",
                ServiceProcess.ConfigPath);
            return;
        }

        _serviceProcess.Start();
    }

    /// <summary>The map service, when this server is the one running it.</summary>
    private ServiceProcess? _serviceProcess;

    /// <summary>
    /// Runs one piece of work, and never lets it out.
    ///
    /// A game tick listener that throws does not merely fail that tick. The
    /// server records a listener as having run only after its handler returns —
    /// the store comes after the call, with nothing to catch in between — so a
    /// handler that throws leaves the listener permanently due and it fires again
    /// on the very next pass of the server loop. That turned one unready entity
    /// into a hundred thousand identical errors in four seconds, which is the
    /// server's own error threshold, and it shut itself down.
    ///
    /// Nothing this mod does is worth a server. Each kind of failure is reported
    /// once and then held quiet: the hundredth copy of a stack trace says nothing
    /// the first did not, and burying the log is its own kind of outage. A
    /// different exception from the same work is a different failure and is said.
    /// </summary>
    private void Safely(string what, System.Action work)
    {
        try
        {
            work();
        }
        catch (System.Exception error)
        {
            if (_reported.Add($"{what}/{error.GetType().Name}"))
            {
                _sapi?.Logger.Error(
                    "[witchlight] {0} failed, and this will not be reported again: {1}", what, error);
            }
        }
    }

    /// <summary>Which failures have already been said, so none is said twice.</summary>
    private readonly HashSet<string> _reported = new();

    /// <summary>
    /// Makes sure where the world counts from has reached the map service, and
    /// keeps trying until it has.
    ///
    /// Attempted when the world is ready and again on every export, because "the
    /// world is ready" is a phase and having a spawn point is a fact, and the two
    /// are not guaranteed to arrive in that order. Cheap to repeat: once it is
    /// written this does nothing, and until it is the map is counting from
    /// somewhere the players are not.
    /// </summary>
    private void KeepWorldFacts()
    {
        if (_wroteWorldFacts || _sapi is null)
        {
            return;
        }

        _wroteWorldFacts = WorldFacts.Write(_sapi, ExportDir);
    }

    /// <summary>Whether spawn has reached the map service yet.</summary>
    private bool _wroteWorldFacts;

    /// <summary>
    /// Writes the regions that have moved. Returns what happened, or null if it
    /// failed — the caller decides how loudly to say so.
    ///
    /// Two things move a region: a column in it whose surface changed, and the
    /// year, which is stored per chunk. When neither has, nothing is read and
    /// nothing is written.
    /// </summary>
    private string? Export(string reason, bool force = false)
    {
        if (_sapi is null)
        {
            return null;
        }

        KeepWorldFacts();

        var dir = Regions.DirectoryIn(ExportDir);
        if (force)
        {
            // What an operator typing the command expects: read everything in
            // memory again, whether or not the server thinks it moved.
            _dirty.MarkAll(LoadedColumns(_sapi));
        }

        var seasonsNow = ColumnPump.Seasons(_sapi, _seasons.Keys, _sapi.WorldManager.ChunkSize);
        var stale = ColumnPump.SeasonsMoved(_seasons, seasonsNow);

        if (_dirty.Count == 0 && stale.Count == 0)
        {
            return $"nothing has moved since the last export, on {reason}";
        }

        var wanted = _dirty.Take();

        try
        {
            // Timed because this runs on the server's own tick: whatever it costs
            // is time the server is not doing anything else.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // Only the regions that might be written are read back. Everything
            // else on disk is left exactly where it is.
            var load = new HashSet<(int, int)>(stale);
            foreach (var (cx, cz) in wanted)
            {
                load.Add(Regions.Of(cx, cz));
            }

            var set = ColumnPump.Gather(_sapi, dir, wanted, load);

            // Neither of these was read, so neither counts as exported. One waits
            // for the next export, the other for the chunk to be loaded again.
            _dirty.Restore(set.Unready);
            _dirty.Defer(set.Unloaded);
            wanted.ExceptWith(set.Unready);
            wanted.ExceptWith(set.Unloaded);

            var write = new HashSet<(int, int)>(stale);
            foreach (var (cx, cz) in set.Moved)
            {
                write.Add(Regions.Of(cx, cz));
            }

            if (write.Count == 0)
            {
                // Every dirty column looks the same from above. Underground work,
                // almost always, and no reason to touch the map.
                clock.Stop();
                _dirty.Exported(wanted);
                return $"{set.Reread} columns re-read, none changed the surface, "
                    + $"in {clock.ElapsedMilliseconds}ms on {reason}";
            }

            foreach (var (column, season) in ColumnPump.Write(_sapi, dir, set, write))
            {
                _seasons[column] = season;
            }
            clock.Stop();
            _dirty.Exported(wanted);

            var written = write.Sum(region => Length(Regions.PathOf(dir, region.Item1, region.Item2)));
            var message = $"wrote {write.Count} of {Regions.All(dir).Count} regions ({written / 1024} KiB), "
                + $"{set.Moved.Count} of {set.Reread} columns re-read changed"
                + (stale.Count > 0 ? $", season moved in {stale.Count}" : "")
                + $", {_seasons.Count} chunks mapped, in {clock.ElapsedMilliseconds}ms on {reason}"
                + (set.Unloaded.Count > 0 ? $", {set.Unloaded.Count} no longer loaded" : "");
            _sapi.Logger.Notification("[witchlight] {0}", message);
            return message;
        }
        catch (System.Exception error)
        {
            // Nothing was written, so what was taken is still owed.
            _dirty.Restore(wanted);
            _sapi.Logger.Error("[witchlight] export failed: {0}", error);
            return null;
        }
    }

    private static long Length(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    /// <summary>How many marker icons are on disk for the map service to draw with.</summary>
    private static int IconCount()
    {
        var dir = Icons.DirectoryIn(ExportDir);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.svg").Length : 0;
    }

    /// <summary>Every chunk column the server currently holds in memory.</summary>
    private static IEnumerable<(int, int)> LoadedColumns(ICoreServerAPI api)
    {
        var loaded = new List<long>(api.WorldManager.AllLoadedMapchunks.Keys);
        foreach (var index in loaded)
        {
            var pos = api.WorldManager.MapChunkPosFromChunkIndex2D(index);
            yield return (pos.X, pos.Y);
        }
    }

    /// <summary>Columns whose surface has moved since the last export.</summary>
    private readonly DirtyColumns _dirty = new();

    /// <summary>Where the year had reached for each column when it was last written.</summary>
    private Dictionary<(int, int), byte> _seasons = new();

    /// <summary>
    /// Loads a square of chunk columns around spawn and exports once they are
    /// ready.
    ///
    /// A server with nobody on it keeps almost nothing in memory, so an export of
    /// what happens to be loaded is a single chunk. Asking for the area around
    /// spawn gives a fresh server a map without waiting for someone to walk the
    /// world; everything players do explore is picked up by the timer.
    /// </summary>
    private void Seed(int radius)
    {
        if (_sapi is null)
        {
            return;
        }

        var size = _sapi.WorldManager.ChunkSize;
        var spawn = WorldFacts.Spawn(_sapi);
        if (spawn is null)
        {
            _sapi.Logger.Warning(
                "[witchlight] the world has no spawn point yet, so the map was not seeded — "
                + "it will fill in as players explore");
            return;
        }

        var centerX = spawn.Value.X / size;
        var centerZ = spawn.Value.Z / size;

        _sapi.Logger.Notification(
            "[witchlight] loading {0}x{0} chunks around spawn to seed the map",
            radius * 2 + 1);

        _sapi.WorldManager.LoadChunkColumnPriority(
            centerX - radius,
            centerZ - radius,
            centerX + radius,
            centerZ + radius,
            new ChunkLoadOptions { OnLoaded = () => Export("seed") });
    }

    /// <summary>
    /// What state the map data is in. The first thing to look at when the map
    /// looks wrong, since it says whether the palette is the server's own poor
    /// one or a good one from a client.
    /// </summary>
    /// <summary>
    /// The map service, from in game: whether it is up, and up or down on demand.
    ///
    /// Starting is deliberate and does not consult `autostart` — that setting says
    /// who starts it unasked, and somebody typing the command has asked.
    /// </summary>
    private TextCommandResult OnService(TextCommandCallingArgs args)
    {
        if (_sapi is null)
        {
            return TextCommandResult.Error("server not ready");
        }

        var action = (args.Parsers[0].GetValue() as string ?? "status").ToLowerInvariant();

        if (_serviceProcess is null && action != "status")
        {
            _serviceProcess = ServiceProcess.Prepare(_sapi, Mod, ExportDir);
        }

        if (_serviceProcess is null)
        {
            return TextCommandResult.Error(
                "there is no bundled map service on this machine — see the server log, and run "
                + "`witchlight serve` yourself to serve the map");
        }

        return action switch
        {
            "start" => TextCommandResult.Success(_serviceProcess.Start()),
            "stop" => TextCommandResult.Success(_serviceProcess.Stop()),
            "status" => TextCommandResult.Success(_serviceProcess.Describe()),
            _ => TextCommandResult.Error($"no such action `{action}` — try status, start or stop"),
        };
    }

    private TextCommandResult OnStatus(TextCommandCallingArgs args)
    {
        if (_sapi is null)
        {
            return TextCommandResult.Error("server not ready");
        }

        var palettePath = Path.Combine(ExportDir, "palette.json");
        var palette = PaletteBuilder.Read(palettePath);
        var dir = Regions.DirectoryIn(ExportDir);
        var regions = Regions.All(dir);
        var stored = regions.Values.Sum(Length);
        var newest = regions.Values.Select(path => new FileInfo(path).LastWriteTime).DefaultIfEmpty().Max();

        var lines = new List<string>
        {
            $"version: {Mod.Info.Version}",
            $"exports: {ExportDir}",
            palette is null
                ? "palette: none written yet"
                : $"palette: from {palette.Source}, {palette.Coloured} of {palette.Blocks.Count} blocks coloured "
                  + $"({palette.Coverage:P0}), fingerprint {palette.Fingerprint}",
            // The one line that would have shown the map counting from the wrong
            // place: both halves have to be right, and neither is visible in game.
            WorldFacts.Spawn(_sapi) is { } spawn
                ? $"counts from: spawn at {spawn.X}, {spawn.Z}"
                  + (File.Exists(Path.Combine(ExportDir, "world.json"))
                      ? ""
                      : "  (world.json not written — the map counts from absolute zero)")
                : "counts from: absolute zero — the world has no readable spawn point",
            _serviceProcess?.Describe() ?? $"service: not run from here; settings at {ServiceProcess.ConfigPath}",
            $"fingerprint now: {_fingerprint}"
                + (palette is not null && palette.Fingerprint != _fingerprint ? "  (palette is stale)" : ""),
            _needsPalette
                ? "wanted: a palette from an admin's client"
                : "wanted: nothing, the palette is good enough",
            regions.Count > 0
                ? $"terrain: {regions.Count} regions, {stored / 1024} KiB, newest written {newest:HH:mm:ss}"
                : "terrain: nothing exported yet",
            $"mapped: {_seasons.Count} chunks",
            $"markers: {Live.Waypoints(_sapi).Count} saved on this server",
            $"icons: {IconCount()} for drawing them",
            $"portraits: {Portraits.Count(ExportDir)} sent by players",
            $"players out: {_service?.PlayersHealth ?? "not started"}",
            $"markers out: {_service?.MarkersHealth ?? "not started"}",
            $"waiting: {_dirty.Count} columns changed since then",
        };

        return TextCommandResult.Success(string.Join("\n", lines));
    }

    /// <summary>
    /// Asks for a palette now rather than waiting for the next admin to join.
    /// Names a player, or asks whoever ran the command.
    /// </summary>
    private TextCommandResult OnPaletteFetch(TextCommandCallingArgs args)
    {
        if (_sapi is null)
        {
            return TextCommandResult.Error("server not ready");
        }

        var target = args.Parsers[0].IsMissing
            ? args.Caller.Player as IServerPlayer
            : _sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(player =>
                    string.Equals(player.PlayerName, args[0] as string, System.StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return TextCommandResult.Error(args.Parsers[0].IsMissing
                ? "no player to ask — name an online admin, or run this in game"
                : $"{args[0]} is not online");
        }

        if (!target.HasPrivilege(Privilege.controlserver))
        {
            return TextCommandResult.Error(
                $"{target.PlayerName} is not an admin; a palette is only taken from admins");
        }

        // Forced: forget that anyone was asked, and ask again regardless of how
        // good the current palette looks.
        _needsPalette = true;
        _askedUid = null;
        _askedAt = System.DateTime.MinValue;
        AskForPalette(target);

        return TextCommandResult.Success($"asked {target.PlayerName} for a palette");
    }

    /// <summary>
    /// Asks one admin for a palette, when the server could not build a usable one.
    ///
    /// Only an admin is asked: a palette decides what every block on the map looks
    /// like, and it arrives from a machine the server does not control. One at a
    /// time, because the table is a few hundred kilobytes and every copy after the
    /// first is thrown away.
    /// </summary>
    private void AskForPalette(IServerPlayer player)
    {
        if (_sapi is null || !_needsPalette)
        {
            return;
        }

        if (!player.HasPrivilege(Privilege.controlserver))
        {
            return;
        }

        var waited = System.DateTime.UtcNow - _askedAt;
        if (_askedUid is not null && waited < System.TimeSpan.FromMinutes(2))
        {
            return;
        }

        _askedUid = player.PlayerUID;
        _askedAt = System.DateTime.UtcNow;
        _sapi.Network.GetChannel(SharedServer.Channel)
            .SendPacket(new PaletteRequest { Fingerprint = _fingerprint }, player);
        _sapi.Logger.Notification(
            "[witchlight] asked {0} for a block colour palette", player.PlayerName);

        // Silence is otherwise indistinguishable from success at a glance.
        var uid = player.PlayerUID;
        var name = player.PlayerName;
        _sapi.Event.RegisterCallback(_ =>
        {
            if (_needsPalette && _askedUid == uid)
            {
                _sapi.Logger.Notification(
                    "[witchlight] no palette came back from {0} — check that they have the mod, "
                    + "and their client log for why it declined",
                    name);
            }
        }, PaletteReplyWaitMs);
    }

    /// <summary>
    /// Asks one admin for the marker pictures.
    ///
    /// A dedicated server's install has no SVG in it, so unlike the palette this
    /// is not a fallback for a poor result — it is the only way the pictures ever
    /// arrive. Only an admin is asked, for the same reason as the palette: this
    /// decides what everyone sees and it comes from a machine the server does not
    /// control.
    /// </summary>
    private void AskForIcons(IServerPlayer player)
    {
        if (_sapi is null || !player.HasPrivilege(Privilege.controlserver))
        {
            return;
        }

        // Only what is missing. A mod adding one marker costs one icon, not the
        // whole set again.
        _sapi.Network.GetChannel(SharedServer.Channel)
            .SendPacket(new IconRequest { Have = IconNames() }, player);
    }

    /// <summary>
    /// Asks a player who has just joined to draw themselves.
    ///
    /// Everyone, not only an admin: a portrait decides what one card looks like and
    /// that card is the sender's own, so there is nothing here to be trusted with.
    /// Asked on every join rather than once ever, because a seraph that changed
    /// while its player was away is a picture that is now wrong, and only the
    /// machine that can see it knows the difference.
    ///
    /// Not at once. A client at the moment the server calls it playing is still
    /// finding its feet — the world is drawn but the seraph may not be — and a
    /// picture taken then is of nothing at all. This is the pause that costs
    /// nobody anything and saves an empty portrait on every join.
    /// </summary>
    private void AskForPortrait(IServerPlayer player)
    {
        if (_sapi is null)
        {
            return;
        }

        _sapi.Event.RegisterCallback(_ => Safely("asking for a portrait", () =>
        {
            // They may well have left in the meantime, and a packet to somebody
            // who is gone is at best wasted.
            if (player.ConnectionState != EnumClientState.Playing)
            {
                return;
            }

            Ask(player);
        }), PortraitAskDelayMs);
    }

    /// <summary>
    /// Asks one player to draw themselves, and remembers having asked.
    ///
    /// The only place an ask is sent, so that a picture arriving afterwards can be
    /// recognised as the answer to one. Nothing else can put a name in that table,
    /// which is what makes it safe to let an answer past the floor.
    /// </summary>
    private void Ask(IServerPlayer player)
    {
        if (_sapi is null)
        {
            return;
        }

        Forget(_portraitAsked, PortraitAnswerWindowMs);
        _portraitAsked[player.PlayerUID] = System.DateTime.UtcNow;
        _sapi.Network.GetChannel(SharedServer.Channel).SendPacket(new PortraitRequest(), player);
    }

    /// <summary>
    /// Whether this picture answers an ask — and takes that ask when it does, so
    /// one ask buys one picture rather than a window of them.
    /// </summary>
    private bool AnswersAnAsk(IServerPlayer player)
    {
        Forget(_portraitAsked, PortraitAnswerWindowMs);
        return _portraitAsked.Remove(player.PlayerUID);
    }

    /// <summary>
    /// Drops what is old enough to no longer decide anything, so a table of times
    /// stays a table of recent ones rather than a name per player the server has
    /// ever seen.
    /// </summary>
    private static void Forget(Dictionary<string, System.DateTime> times, int olderThanMs)
    {
        var cutoff = System.DateTime.UtcNow - System.TimeSpan.FromMilliseconds(olderThanMs);
        foreach (var uid in times.Where(at => at.Value <= cutoff).Select(at => at.Key).ToList())
        {
            times.Remove(uid);
        }
    }

    /// <summary>
    /// How long ago this player's last picture was, when that is too recent to take
    /// another — and nothing when it is not.
    ///
    /// An honest client sends one unasked only once its character has been left
    /// alone for the quiet period, so nothing legitimate arrives this close
    /// together. This is the floor under a client that is not honest: what it can
    /// make the server write is capped in size where a picture is filed, and in
    /// rate here.
    /// </summary>
    private System.TimeSpan? TooSoonFor(IServerPlayer player)
    {
        if (!_portraitAt.TryGetValue(player.PlayerUID, out var last))
        {
            return null;
        }

        var waited = System.DateTime.UtcNow - last;
        return waited < System.TimeSpan.FromMilliseconds(Portraits.FloorMs) ? waited : null;
    }

    /// <summary>
    /// Notes that a picture has been taken from this player, so the next one they
    /// send unasked is measured against this moment.
    /// </summary>
    private void TookOneFrom(IServerPlayer player)
    {
        Forget(_portraitAt, Portraits.FloorMs);
        _portraitAt[player.PlayerUID] = System.DateTime.UtcNow;
    }

    /// <summary>When each player's last unasked-for picture arrived.</summary>
    private readonly Dictionary<string, System.DateTime> _portraitAt = new();

    /// <summary>Who has been asked for a picture and not yet answered.</summary>
    private readonly Dictionary<string, System.DateTime> _portraitAsked = new();

    /// <summary>
    /// Takes a player's picture of themselves.
    ///
    /// Filed under who sent it rather than under anything in the message: a client
    /// says what it looks like, not who it is. Anyone may send one, unlike the
    /// palette and the marker pictures — those decide what everybody sees and this
    /// decides only what its own sender looks like, which is a thing they are
    /// already entitled to be wrong about.
    ///
    /// That does open a write to every client rather than to a handful of trusted
    /// ones, so how often one may arrive is answered here and how large it may be
    /// is answered where it is filed.
    /// </summary>
    private void OnPortraitFromClient(IServerPlayer player, PlayerPortrait portrait)
    {
        if (_sapi is null)
        {
            return;
        }

        // A picture the server asked for is expected, however soon after the last
        // one it lands: a player who joins and then types the command a second
        // later has done nothing wrong, and their second picture must not vanish
        // into a log line while their own screen says it was sent. The floor is
        // for what a client sends of its own accord.
        if (!AnswersAnAsk(player))
        {
            if (TooSoonFor(player) is { } wait)
            {
                _sapi.Logger.Warning(
                    "[witchlight] ignored a portrait from {0}, sent unasked {1:0.#}s after "
                    + "the last one; unasked pictures are taken no closer together than {2}s",
                    player.PlayerName, wait.TotalSeconds, Portraits.FloorMs / 1000);
                return;
            }

            TookOneFrom(player);
        }

        if (Portraits.Save(ExportDir, player.PlayerUID, portrait.Png, out var said))
        {
            _sapi.Logger.Notification("[witchlight] portrait from {0} ({1})", player.PlayerName, said);
        }
        else
        {
            _sapi.Logger.Warning(
                "[witchlight] ignored a portrait from {0}: {1}", player.PlayerName, said);
        }
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
        if (_sapi is null)
        {
            return TextCommandResult.Error("server not ready");
        }

        var target = args.Parsers[0].IsMissing
            ? args.Caller.Player as IServerPlayer
            : _sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(player =>
                    string.Equals(player.PlayerName, args[0] as string, System.StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return TextCommandResult.Error(args.Parsers[0].IsMissing
                ? "no player to ask — name one who is online, or run this in game"
                : $"{args[0]} is not online");
        }

        Ask(target);
        return TextCommandResult.Success($"asked {target.PlayerName} to draw themselves");
    }

    private TextCommandResult OnIconFetch(TextCommandCallingArgs args)
    {
        if (_sapi is null)
        {
            return TextCommandResult.Error("server not ready");
        }

        var target = args.Parsers[0].IsMissing
            ? args.Caller.Player as IServerPlayer
            : _sapi.World.AllOnlinePlayers
                .OfType<IServerPlayer>()
                .FirstOrDefault(player =>
                    string.Equals(player.PlayerName, args[0] as string, System.StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            return TextCommandResult.Error(args.Parsers[0].IsMissing
                ? "no player to ask — name an online admin, or run this in game"
                : $"{args[0]} is not online");
        }

        if (!target.HasPrivilege(Privilege.controlserver))
        {
            return TextCommandResult.Error(
                $"{target.PlayerName} is not an admin; pictures are only taken from admins");
        }

        // Everything, not only what is missing: this is the way back if an icon
        // on disk is wrong rather than absent.
        _sapi.Network.GetChannel(SharedServer.Channel)
            .SendPacket(new IconRequest { Have = new List<string>() }, target);
        return TextCommandResult.Success($"asked {target.PlayerName} for the marker pictures");
    }

    /// <summary>
    /// Takes marker pictures from an admin's client and keeps what they add.
    ///
    /// Merged rather than replaced, like the palette: a client only has the art
    /// for the mods it has installed, so two admins with different mod sets can
    /// between them cover more than either alone.
    /// </summary>
    private void OnIconsFromClient(IServerPlayer player, IconTable table)
    {
        if (_sapi is null)
        {
            return;
        }

        if (!player.HasPrivilege(Privilege.controlserver))
        {
            _sapi.Logger.Warning(
                "[witchlight] ignored marker pictures from {0}, who is not an admin", player.PlayerName);
            return;
        }

        var written = Icons.Accept(IconTable.Assemble(new[] { table }), ExportDir);
        if (written > 0)
        {
            _sapi.Logger.Notification(
                "[witchlight] {0} marker pictures from {1} ({2} in total now)",
                written, player.PlayerName, IconCount());
        }
    }

    /// <summary>What the server already has, so a client sends only what is new.</summary>
    private static List<string> IconNames()
    {
        var dir = Icons.DirectoryIn(ExportDir);
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(dir, "*.svg")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }

    /// <summary>How long to wait before saying an ask went unanswered.</summary>
    private const int PaletteReplyWaitMs = 30000;

    /// <summary>
    /// How long after a join before a player is asked to draw themselves.
    ///
    /// The server calls them playing before their client has finished getting
    /// there, and a seraph that is not loaded yet renders as an empty picture the
    /// client then reports it could not draw. A few seconds is the whole of the fix.
    /// </summary>
    private const int PortraitAskDelayMs = 8000;

    /// <summary>
    /// How long an ask stands before the server stops expecting an answer to it.
    ///
    /// A client draws on its next frame, so an answer is normally back in well
    /// under a second. This is long enough to cover one that is busy and short
    /// enough that an ask nobody answered does not sit there excusing a picture
    /// sent much later for a different reason.
    /// </summary>
    private const int PortraitAnswerWindowMs = 60000;

    /// <summary>
    /// Takes a palette from an admin's client and keeps whatever it adds.
    ///
    /// Merged rather than replaced: a client only has textures for the mods it has
    /// installed, so two admins with different mod sets between them can produce a
    /// complete palette where neither could alone.
    /// </summary>
    private void OnPaletteFromClient(IServerPlayer player, PaletteTable table)
    {
        if (_sapi is null)
        {
            return;
        }

        if (!player.HasPrivilege(Privilege.controlserver))
        {
            _sapi.Logger.Warning(
                "[witchlight] ignored a palette from {0}, who is not an admin", player.PlayerName);
            return;
        }

        if (table.Fingerprint != _fingerprint)
        {
            _sapi.Logger.Warning(
                "[witchlight] ignored a palette from {0}: it was built for a different mod set",
                player.PlayerName);
            return;
        }

        // Slices arrive one packet at a time; nothing is written until the whole
        // palette is here.
        if (!_incoming.TryGetValue(player.PlayerUID, out var slices))
        {
            slices = new List<PaletteTable>();
            _incoming[player.PlayerUID] = slices;
        }

        slices.RemoveAll(existing => existing.Part == table.Part);
        slices.Add(table);

        if (table.Parts <= 0 || slices.Count < table.Parts)
        {
            return;
        }

        _incoming.Remove(player.PlayerUID);

        var path = Path.Combine(ExportDir, "palette.json");
        var supplied = PaletteTable.Assemble(slices, id => _sapi.World.GetBlock(id)?.Code?.ToString());
        var existing = PaletteBuilder.Read(path);
        var merged = existing is not null && existing.Fingerprint == _fingerprint
            ? Palette.Merge(supplied, existing)
            : supplied;

        PaletteBuilder.Write(merged, path);
        _needsPalette = merged.Coverage < RequiredCoverage;
        _askedUid = null;

        _sapi.Logger.Notification(
            "[witchlight] palette from {0}: {1} of {2} blocks coloured ({3:P0}){4}",
            player.PlayerName,
            merged.Coloured,
            merged.Textured,
            merged.Coverage,
            _needsPalette ? ", still incomplete" : "");
    }

    /// <summary>
    /// How much of what has textures must have a colour before the server stops
    /// asking. A dedicated server with no block textures scores near zero; one
    /// running from a full game install scores near one.
    /// </summary>
    private const double RequiredCoverage = 0.9;

    /// <summary>Palette slices in flight, by the player sending them.</summary>
    private readonly Dictionary<string, List<PaletteTable>> _incoming = new();

    private bool _needsPalette;
    private string _fingerprint = "";
    private string? _askedUid;
    private System.DateTime _askedAt = System.DateTime.MinValue;

    /// <summary>Sends who is online. Said once if the service cannot be reached.</summary>
    private void PostPlayers()
    {
        if (_sapi is not null)
        {
            _service?.Players(Live.PlayersJson(_sapi, ExportDir));
        }
    }

    /// <summary>Where live data goes, in place of a file rewritten every two seconds.</summary>
    private MapService? _service;

    public override void Dispose()
    {
        _service?.Dispose();
        _service = null;
        _serviceProcess?.Dispose();
        _serviceProcess = null;
    }

    /// <summary>
    /// How often shared markers are pushed. Markers change rarely, and clients
    /// only act on ones they have not seen, so this is a small packet.
    /// </summary>
    private const int ShareIntervalMs = 15000;

    /// <summary>How often player positions are posted.</summary>
    private const int LiveIntervalMs = 2000;

    /// <summary>Chunk columns each way from spawn for the initial map.</summary>
    private const int SeedRadius = 8;

    /// <summary>Long enough after start that the world is ready to generate.</summary>
    private const int SeedDelayMs = 10000;

    /// <summary>
    /// How often the surface is re-exported. Frequent enough to be useful while
    /// testing; a large server will want this longer, which is what a config
    /// file is for once there is one.
    /// </summary>
    private const int ExportIntervalMs = 30000;

    /// <summary>Where every export lands, next to the world data.</summary>
    private static string ExportDir => Path.Combine(GamePaths.DataPath, ExportDirName);

    /// <summary>Runs before the server frees texture data, which is the only window.</summary>
    public override double ExecuteOrder() => 1.0;

    public override void AssetsLoaded(ICoreAPI api)
    {
        Report(api, "AssetsLoaded");
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        Report(api, "AssetsFinalize");

        var built = PaletteBuilder.Build(api, out var missing);
        _fingerprint = built.Fingerprint;

        // A palette a client supplied for this same mod set is better than
        // anything this server can build, so it is kept and only filled in.
        var path = Path.Combine(ExportDir, "palette.json");
        // A stored palette is only reusable if the registry still matches and the
        // server's own mods have not moved underneath it.
        var existing = PaletteBuilder.Read(path);
        var reusable = existing is not null
            && existing.Fingerprint == built.Fingerprint
            && (existing.ModStamp.Length == 0 || existing.ModStamp == built.ModStamp);
        var palette = reusable ? Palette.Merge(existing!, built) : built;

        PaletteBuilder.Write(palette, path);
        _needsPalette = palette.Coverage < RequiredCoverage;
        if (_needsPalette)
        {
            api.Logger.Notification(
                "[witchlight] only {0:P0} of blocks have a colour — an admin will be asked for a palette on join",
                palette.Coverage);
        }

        var colorMaps = PaletteBuilder.ExportColorMaps(api, Path.Combine(ExportDir, "colormaps"));
        api.Logger.Notification("[witchlight] colour maps: {0} written", colorMaps);

        var (icons, from) = Icons.Export(api, ExportDir);
        api.Logger.Notification("[witchlight] marker icons: {0} written, from {1}", icons, from);

        api.Logger.Notification(
            "[witchlight] palette: {0} blocks written to {1}{2}",
            palette.Blocks.Count,
            path,
            missing > 0 ? $" ({missing} without a readable texture)" : "");

        foreach (var group in PaletteBuilder.FailureCounts.OrderByDescending(entry => entry.Value).Take(12))
        {
            api.Logger.Notification("[witchlight] missing group {0}: {1}", group.Key, group.Value);
        }
        foreach (var failure in PaletteBuilder.Failures)
        {
            api.Logger.Notification("[witchlight] skipped {0}", failure);
        }
    }

    /// <summary>
    /// Says whether block textures are readable at this point. The answer decides
    /// whether a palette can be built server-side at all, so it is logged rather
    /// than assumed.
    /// </summary>
    private static void Report(ICoreAPI api, string stage)
    {
        var blocks = api.World.Blocks;
        var withTextures = blocks.Count(block => block?.Textures is { Count: > 0 });
        api.Logger.Notification(
            "[witchlight] {0}: {1} of {2} blocks have textures in memory",
            stage,
            withTextures,
            blocks.Count);
    }
}
