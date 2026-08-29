using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// The server half of Witchlight: what runs, and when.
///
/// Nothing here decides anything. Every piece of judgement lives in a type of its
/// own — <see cref="Exporter"/> for the map, <see cref="PaletteExchange"/> and
/// <see cref="IconExchange"/> and <see cref="PortraitExchange"/> for what has to
/// come from a client, <see cref="MapService"/> for what goes to the service —
/// and what is left is the wiring: which of them the game's events reach, on what
/// clock, and in what order. The commands are in `ServerCommands.cs`, which is
/// the same class: a command surface is a view of the whole system and splitting
/// it into a type of its own would only mean handing that type every field.
///
/// The one thing this file must get right is timing. The palette has to be built
/// while block textures are still in memory, because the server frees them once
/// assets are loaded (Block.FreeRAMServer), so that hangs off the asset stage
/// rather than off run time.
/// </summary>
public partial class WitchlightSystem : ModSystem
{
    private ICoreServerAPI? _sapi;

    /// <summary>Reports a failure once and then holds quiet about it.</summary>
    private Faults? _faults;

    /// <summary>
    /// Who may see which marker. Read once the world is up, because it lives in
    /// the savegame, and written back with it.
    /// </summary>
    private Visibility _visibility = Visibility.Empty;

    private Exporter? _exporter;

    /// <summary>What the palette build at asset load settled on, for the asking
    /// half that cannot exist until the server is up.</summary>
    private PaletteExchange.Built? _built;

    /// <summary>The palette as the assets gave it, before there was anywhere to
    /// put it. Read while block textures are still in memory; written once the
    /// world has said which directory its map is in.</summary>
    private PaletteExchange.Raw? _raw;
    private PaletteExchange? _palettes;
    private IconExchange? _icons;
    private PortraitExchange? _portraits;

    /// <summary>Where live data goes, in place of a file rewritten every two seconds.</summary>
    private MapService? _service;

    /// <summary>The map service, when this server is the one running it.</summary>
    private ServiceProcess? _serviceProcess;

    /// <summary>The seed still running, and the tick it runs on.</summary>
    private Seeding? _seeding;

    /// <summary>
    /// Who has already been told where the map is.
    ///
    /// Two events race to say it and whichever arrives first wins, so this is
    /// what keeps a player from being told twice. Cleared when they leave, so a
    /// rejoin is greeted again.
    /// </summary>
    private readonly HashSet<string> _greeted = new();

    /// <summary>Whether the reason nobody is being greeted has been said.</summary>
    private bool _saidWhyNotGreeting;
    private long _seedTick = -1;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    /// <summary>Runs before the server frees texture data, which is the only window.</summary>
    public override double ExecuteOrder() => 1.0;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        _faults = new Faults(api.Logger);

        Listen(api);
        Schedule(api);
        RegisterCommands(api);

        // Version first, and on every start: the quickest way to tell a deployed
        // mod from the one you meant to deploy. The map service prints its own for
        // the same reason, and the two must match on minor version.
        api.Logger.Notification(
            "[witchlight] {0} ready, exporting every {1}s", Mod.Info.Version, ExportIntervalMs / 1000);
    }

    /// <summary>
    /// Everything that has to know which world this is.
    ///
    /// The map may be filed per world, and which directory that is cannot be
    /// worked out until the world is up and can be asked its name. So nothing
    /// that writes into the export directory is built or run before this — the
    /// palette is built while the assets are still in memory, because that is
    /// the only window there is for it, and written here with everything else.
    /// </summary>
    private void OpenTheMap(ICoreServerAPI api)
    {
        Settings.ForWorld(api);
        api.Logger.Notification("[witchlight] this world's map is in {0}", Settings.Exports);

        ClearWhatThisBuildCannotRead(api);

        _icons = new IconExchange(api, Settings.Exports);
        if (_built is { } built)
        {
            _palettes = new PaletteExchange(api, Settings.Exports, built);
        }
        _portraits = new PortraitExchange(api, Settings.Exports);
        _service = new MapService(Settings.Exports, api.Logger);

        WriteWhatTheAssetsSaid(api);
    }

    /// <summary>Running one piece of work, and never letting it out.</summary>
    private void Doing(string what, Action work) => _faults?.Doing(what, work);

    /// <summary>What the game tells this mod, and who it is told to.</summary>
    private void Listen(ICoreServerAPI api)
    {
        // Markers belonging to everyone else, pushed to each player's map, and the
        // three things only a client can supply.
        api.Network.RegisterChannel(SharedServer.Channel)
            .RegisterMessageType<SharedMarkers>()
            .RegisterMessageType<PaletteRequest>()
            .RegisterMessageType<PaletteTable>()
            .RegisterMessageType<IconRequest>()
            .RegisterMessageType<IconTable>()
            .RegisterMessageType<PortraitRequest>()
            .RegisterMessageType<PlayerPortrait>()
            .SetMessageHandler<PaletteTable>((player, table) =>
                Doing("taking a palette", () => _palettes?.Accept(player, table)))
            .SetMessageHandler<IconTable>((player, table) =>
                Doing("taking marker pictures", () => _icons?.Accept(player, table)))
            .SetMessageHandler<PlayerPortrait>((player, png) =>
                Doing("taking a portrait", () => _portraits?.Accept(player, png)));

        api.Event.PlayerNowPlaying += player => Doing("greeting a player", () =>
        {
            SharedServer.SendTo(api, player, _visibility);
            _palettes?.AskIfNeeded(player);
            _icons?.AskForMissing(player);
            _portraits?.AskOnceSettled(player, Doing);

            // Long enough that somebody choosing a character for the first time
            // has finished — by which point `PlayerReady` has almost certainly
            // said it already and this finds nothing left to do.
            api.Event.RegisterCallback(
                _ => Doing("telling a player where the map is", () => Greet(player)),
                GreetBackstopMs);
        });

        // Later than the rest of it, and for the one reason the rest does not
        // care about: this one is meant to be read. On a first join `NowPlaying`
        // lands while the character and class screen is still up, and a line of
        // chat behind it is a line nobody sees. `PlayerReady` is after that, and
        // is immediate for anybody who has played before — so it is asked first.
        //
        // It is not asked alone. A dedicated server has been seen to greet
        // nobody at all, with the address readable and nothing thrown, which
        // leaves this event not arriving as the only explanation left. Whatever
        // the cause, a greeting nobody receives is worse than one that lands a
        // few seconds late, so `PlayerNowPlaying` — which does arrive, and is
        // what fetches a portrait moments later — starts a timer behind it.
        // Whichever gets there first says it, and `Greet` sees to it that only
        // one of them does.
        api.Event.PlayerReady += player =>
            Doing("telling a player where the map is", () => Greet(player));

        api.Event.PlayerDisconnect += player =>
        {
            lock (_greeted)
            {
                _greeted.Remove(player.PlayerUID);
            }
        };

        // The server marks a chunk dirty for every block that moves in it and for
        // every chunk it loads, which is the whole signal an export needs. Without
        // it each tick re-reads the surface of every chunk in memory to discover
        // that almost none of it moved.
        api.Event.ChunkDirty += (coord, chunk, reason) =>
            Doing("noting a changed chunk", () => _exporter?.Mark(coord.X, coord.Z, reason));

        api.Event.GameWorldSave += () => Doing("exporting on save", () => Export("world save"));
        api.Event.GameWorldSave += () => Doing("storing marker visibility", StoreVisibility);

        // Everything that needs a world rather than a mod: where the world counts
        // from, who may see which marker, and the service that serves them.
        //
        // Spawn is not known while mods are still starting, and asking for it then
        // threw — which left the map counting from absolute zero, with nothing on
        // either side looking wrong.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => Doing("starting up", () =>
        {
            // Unpacked and asked for its settings first: they are what decides
            // which directory the map goes in, and the service is the half that
            // writes them on a first run.
            _serviceProcess = ServiceProcess.Prepare(api, Mod);
            OpenTheMap(api);
            _exporter = new Exporter(api, Settings.Exports);
            _exporter.KeepWorldFacts();
            _visibility = Visibility.Read(api);
            StartService();
            BeginSeeding(api);
        }));
    }

    /// <summary>The clocks everything runs on.</summary>
    private void Schedule(ICoreServerAPI api)
    {
        // Markers ride the timer that already pushes them to players in game:
        // they change a few times an hour, and a post that says nothing new is
        // dropped before it is sent anyway.
        api.Event.RegisterGameTickListener(Every("sharing markers", () =>
        {
            SharedServer.SendToAll(api, _visibility);
            _service?.Markers(Live.WaypointsJson(api, _visibility));
        }), ShareIntervalMs);

        // A tick listener rather than a load event: it is guaranteed to fire, and
        // repeating it keeps the map current without anyone typing a command.
        api.Event.RegisterGameTickListener(
            Every("exporting", () => Export("timer")), ExportIntervalMs);

        // Players move, so this goes far more often than the terrain — and it
        // goes over the socket rather than to a file, because a position is worth
        // nothing by the time a disk has finished with it.
        api.Event.RegisterGameTickListener(Every("posting players", () =>
            _service?.Players(Live.PlayersJson(api, Settings.Exports))), LiveIntervalMs);

        // On the same beat, and for the same reason: a clock belongs to the
        // server that is running, not to a file beside the map.
        api.Event.RegisterGameTickListener(Every("posting the world's clock", () =>
            _service?.World(Live.WorldJson(api))), LiveIntervalMs);

        // Markers asked for on the web ride the same tick. They are rare, the ask
        // is a single request that usually comes back empty, and collecting them
        // on the slow marker timer instead would mean watching a form for fifteen
        // seconds to find out whether it worked.
        api.Event.RegisterGameTickListener(
            Every("collecting markers", CollectMarkers), LiveIntervalMs);
    }

    /// <summary>
    /// Starts filling a fresh map, a few columns at a time.
    ///
    /// Started where the exporter is made rather than on a clock of its own: a
    /// seed has nowhere to write until the exporter exists, and a timer that beat
    /// it there did nothing at all and said nothing about it.
    ///
    /// The stepping is a tick because the asking has to be spread out — see
    /// <see cref="Seeding"/> for what happens to a server handed the whole square
    /// at once.
    /// </summary>
    private void BeginSeeding(ICoreServerAPI api)
    {
        _seeding = Seeding.Around(api, SeedRadius, reason => Export(reason));
        if (_seeding is null)
        {
            return;
        }

        _seedTick = api.Event.RegisterGameTickListener(
            Every("seeding the map", () =>
            {
                if (_seeding is null || !_seeding.Step())
                {
                    StopSeeding(api);
                }
            }),
            Seeding.StepIntervalMs);
    }

    /// <summary>Takes the seed's tick back once there is nothing left to ask for.</summary>
    private void StopSeeding(ICoreServerAPI api)
    {
        if (_seedTick >= 0)
        {
            api.Event.UnregisterGameTickListener(_seedTick);
            _seedTick = -1;
        }
        _seeding = null;
    }

    /// <summary>One piece of work, shaped to be handed to a tick listener.</summary>
    private Action<float> Every(string what, Action work) => _ => Doing(what, work);

    /// <summary>
    /// Throws away what an older build left that this one cannot use.
    ///
    /// A map this build cannot read is discarded rather than upgraded: the format
    /// still moves, and it rebuilds itself as players explore. A `live.json` from
    /// before live data went over the socket is read by nothing, and leaving it
    /// invites a reader to find it and show markers that are however old it is.
    /// </summary>
    private static void ClearWhatThisBuildCannotRead(ICoreServerAPI api)
    {
        Regions.ResetIfUnreadable(Settings.Exports, GlobalConstants.ChunkSize, api.Logger);

        var stale = Path.Combine(Settings.Exports, "live.json");
        if (!File.Exists(stale))
        {
            return;
        }

        try
        {
            File.Delete(stale);
            api.Logger.Notification("[witchlight] removed live.json; live data goes over the socket");
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not remove live.json: {0}", error.Message);
        }
    }

    /// <summary>The export, wherever it is asked for.</summary>
    private string? Export(string reason, bool force = false) => _exporter?.Export(reason, force);

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
    private void Greet(IServerPlayer player)
    {
        // Still here to read it. The backstop fires on a timer, and a player who
        // joined and left inside it is not owed a greeting.
        if (player.ConnectionState != EnumClientState.Playing)
        {
            return;
        }

        lock (_greeted)
        {
            if (!_greeted.Add(player.PlayerUID))
            {
                return;
            }
        }

        SayWhereTheMapIs(player);
    }

    private void SayWhereTheMapIs(IServerPlayer player)
    {
        if (_sapi is not { } api || !Settings.Announces)
        {
            return;
        }

        if (Settings.Announcement() is not { } where)
        {
            // The settings ask for a greeting and there is no address to put in
            // one. Said, because this used to be the whole of the failure: a
            // player joined, nothing was said, and nothing anywhere recorded
            // that anything had been meant to happen. Once per start, since a
            // server without an address would otherwise repeat it at every join.
            if (!_saidWhyNotGreeting)
            {
                _saidWhyNotGreeting = true;
                api.Logger.Warning(
                    "[witchlight] joining players are being told nothing about the map: "
                    + "`announce` is on, `announce_url` is unset, and the service has not "
                    + "written an address to {0}. Set `announce_url` in {1} to say it anyway.",
                    Path.Combine(Settings.Exports, "service.json"),
                    Settings.Path);
            }

            return;
        }

        // The address on its own is what somebody bookmarks, so it is said either
        // way and said as itself. The link beside it is spent on one press.
        var address = $"The map is at {AsLink(where, where)}";

        // Signing them in is the same thing `/witchlight login` does, so it is
        // offered to exactly whoever could have typed that — and only where there
        // is a service to mint one.
        if (_service is null || !player.HasPrivilege(Privilege.chat))
        {
            api.SendMessage(player, GlobalConstants.GeneralChatGroup, address,
                EnumChatType.Notification);
            return;
        }

        WithLoginLink(player, where, (told, link) => api.SendMessage(
            told,
            GlobalConstants.GeneralChatGroup,
            // Nothing is said about a link that could not be minted. Somebody who
            // typed the command is owed the reason; somebody who merely joined a
            // server never asked, and the address still works.
            link is null
                ? address
                : $"{address}. {AsLink(link, "Open it signed in")} — that link works "
                  + "once, for ten minutes.",
            EnumChatType.Notification));
    }

    /// <summary>
    /// An address as chat should show it: something to press where it is, and
    /// plain words where it is not.
    ///
    /// The game makes a link out of `href` only for an address with a scheme it
    /// knows — it splits on `://` and opens what begins with `http`. An
    /// `announce_url` set to a bare host is a perfectly good thing to tell
    /// somebody and a link that would do nothing at all when pressed, and text
    /// that can be copied beats a press that goes nowhere.
    /// </summary>
    private static string AsLink(string where, string said) =>
        where.StartsWith("http", StringComparison.Ordinal) && where.Contains("://")
            ? $"<a href=\"{where}\">{said}</a>"
            : said;

    /// <summary>
    /// Asks the map for a link that signs one player in, and hands it back on the
    /// game thread.
    ///
    /// A round trip to another program, so the asking happens off the game
    /// thread; everything the server says it says from that thread, so the answer
    /// comes back to it. Null where the map could not be asked, which the two
    /// callers answer differently.
    ///
    /// The player is looked up again rather than held across the wait: minting
    /// takes a moment and somebody can leave inside it.
    /// </summary>
    private void WithLoginLink(IServerPlayer player, string where, Action<IServerPlayer, string?> told)
    {
        if (_sapi is not { } api || _service is not { } service)
        {
            return;
        }

        var uid = player.PlayerUID;
        var name = player.PlayerName ?? "";

        _ = Task.Run(async () =>
        {
            var link = await service.Link(uid, name, where).ConfigureAwait(false);
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (api.World.PlayerByUid(uid) is IServerPlayer still)
                {
                    Doing("handing over a map link", () => told(still, link));
                }
            }, "witchlight-login");
        });
    }

    /// <summary>
    /// Brings up the map service, unless the settings say somebody else will.
    ///
    /// Once, at the point the world is ready: the service reads the palette and
    /// the exported regions at start, and both are on disk by then.
    /// </summary>
    private void StartService()
    {
        if (_sapi is null || _serviceProcess is null || _serviceProcess.Running)
        {
            return;
        }

        if (!_serviceProcess.Wanted)
        {
            _sapi.Logger.Notification(
                "[witchlight] autostart is off in {0}, so the map service was not started — "
                + "run `witchlight serve` yourself to serve the map",
                Settings.Path);
            return;
        }

        _serviceProcess.Start();
    }

    /// <summary>
    /// Collects the markers somebody asked for on the web and puts them on the
    /// map.
    ///
    /// The asking is a round trip and the making touches the waypoint list, so the
    /// two happen in different places: the request goes off the game thread, and
    /// what comes back is applied on it. Markers go straight back out afterwards
    /// rather than waiting for the slow timer, because the browser that asked is
    /// watching for its own marker to appear and fifteen seconds of nothing reads
    /// as a form that failed.
    /// </summary>
    private void CollectMarkers()
    {
        if (_sapi is not { } api || _service is not { } service)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var held = await service.Pending().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(held) || !held.Contains('{'))
            {
                return;
            }

            api.Event.EnqueueMainThreadTask(
                () => Doing("making markers", () =>
                {
                    var (made, changed) = Pending.Apply(api, _visibility, held);
                    if (made == 0 && changed == 0)
                    {
                        return;
                    }

                    api.Logger.Notification(
                        "[witchlight] web map: {0} marker(s) made, {1} changed", made, changed);
                    SharedServer.SendToAll(api, _visibility);
                    service.Markers(Live.WaypointsJson(api, _visibility));
                }),
                "witchlight-markers");
        });
    }

    /// <summary>
    /// Stores who may see which marker, beside the waypoints themselves.
    ///
    /// Given the live list so that decisions about markers somebody has since
    /// deleted go with them, and so the store cannot outlive what it describes.
    /// </summary>
    private void StoreVisibility()
    {
        if (_sapi is null)
        {
            return;
        }

        // The live list where there is one, and nothing where the layer cannot be
        // reached — an empty list there would read as "every marker has been
        // deleted" and take every decision with it.
        _visibility.Write(_sapi, Markers.Layer(_sapi)?.Waypoints);
    }

    public override void AssetsLoaded(ICoreAPI api) => Report(api, "AssetsLoaded");

    /// <summary>
    /// Everything that has to be read while the assets are still in memory.
    ///
    /// The server frees block textures once assets are loaded, so a palette not
    /// built here cannot be built at all — and the icons, colour maps and block
    /// names come out of the same assets, so they are read in the same pass.
    /// </summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        Report(api, "AssetsFinalize");
        _raw = PaletteExchange.BuildFromAssets(api);
    }

    /// <summary>
    /// Writes everything the assets said, once there is a directory to write it
    /// to.
    ///
    /// The palette was built at asset load because the textures behind it are
    /// freed straight afterwards. The rest is read here: the colour maps, the
    /// marker pictures and the block names come out of assets the server keeps,
    /// so the only reason they were up there was that the palette had to be.
    /// </summary>
    private void WriteWhatTheAssetsSaid(ICoreServerAPI api)
    {
        if (_raw is not { } raw)
        {
            api.Logger.Warning(
                "[witchlight] no palette was built while the assets were loading, so the map "
                + "has no colours. This is a bug in this mod.");
            return;
        }

        _built = PaletteExchange.Settle(api, Settings.Exports, raw);
        var palette = _built.Palette;

        var (maps, mapsFound) = ColourMaps.Export(api, Settings.Exports);
        api.Logger.Notification("[witchlight] colour maps: {0} of {1} written", maps, mapsFound);

        var (icons, iconsFound, from) = Icons.Export(api, Settings.Exports);
        api.Logger.Notification(
            "[witchlight] marker icons: {0} of {1} written, from {2}", icons, iconsFound, from);

        var named = BlockNames.Export(api, Settings.Exports, palette);
        api.Logger.Notification(
            "[witchlight] block names: {0} of {1} blocks named, {2} of them in the palette{3}",
            named.Named,
            named.Blocks,
            named.Known,
            named.Written ? "" : " — unchanged, not rewritten");
        if (named.Named > 0 && named.Known == 0)
        {
            api.Logger.Warning(
                "[witchlight] no named block is in the palette, so the map will show codes "
                + "rather than names. The two are keyed differently, which is a bug in this mod.");
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
        api.Logger.Notification(
            "[witchlight] {0}: {1} of {2} blocks have textures in memory",
            stage,
            blocks.Count(block => block?.Textures is { Count: > 0 }),
            blocks.Count);
    }

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

    /// <summary>
    /// How long after a player starts playing to say where the map is, if the
    /// event that is supposed to say it has not.
    ///
    /// Long enough for somebody picking a character and a class for the first
    /// time to be done, since that is exactly the case the timing is about. A
    /// player who has played before is greeted the moment they are ready and
    /// never reaches this.
    /// </summary>
    private const int GreetBackstopMs = 20000;

    /// <summary>
    /// How often the surface is re-exported.
    ///
    /// This is most of the wait between a player walking into ground nobody has
    /// been to and seeing it drawn: everything after it — the service noticing,
    /// the levels above being built, the page asking — adds up to about three
    /// seconds together, against up to thirty spent here.
    ///
    /// Shorter costs the server almost nothing extra, because the work is per
    /// column rather than per export and the same columns are read either way.
    /// It is measurably kinder to the tick: an export is timed, and thirty
    /// seconds of somebody exploring gathered 632 columns into one 380ms pass,
    /// where the same ground taken in ten second pieces is three passes near a
    /// hundred. A tick is 33ms, so the long pass is the one that stutters.
    ///
    /// What it does cost is disk. A region is rewritten whole however few of its
    /// columns moved, so a region under somebody walking through it is written
    /// three times where it used to be written once. That is the trade this
    /// number makes, and the reason it is not lower still.
    /// </summary>
    private const int ExportIntervalMs = 10000;
}
