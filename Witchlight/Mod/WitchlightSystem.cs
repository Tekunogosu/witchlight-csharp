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
        // the same reason, and the two are always the same number — they ship as
        // one archive, so two different numbers in one log is a mis-deployment.
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

        // What the assets said comes first, and the exchange is built from what it
        // hands back rather than from a field it fills. The two used to be a field
        // written here and read four lines above: the palette exchange was built
        // against a null every time, so nobody was ever asked for a palette and
        // every server whose own assets could not colour the map drew nothing,
        // for ever, in silence.
        var settled = WriteWhatTheAssetsSaid(api);
        if (settled is { } built)
        {
            _palettes = new PaletteExchange(api, Settings.Exports, built);
        }
        else
        {
            api.Logger.Error(
                "[witchlight] no palette was settled, so nobody can be asked for one and the map "
                + "will draw nothing. This is a bug in this mod.");
        }

        _icons = new IconExchange(api, Settings.Exports);
        _portraits = new PortraitExchange(api, Settings.Exports);
        _service = new MapService(Settings.Exports, api.Logger);
    }

    /// <summary>Running one piece of work, and never letting it out.</summary>
    private void Doing(string what, Action work) => _faults?.Doing(what, work);

    /// <summary>What the game tells this mod, and who it is told to.</summary>
    private void Listen(ICoreServerAPI api)
    {
        // Markers belonging to everyone else, pushed to each player's map, and the
        // three things only a client can supply.
        api.Network.RegisterChannel(Channel.Name)
            .Carrying()
            .SetMessageHandler<PaletteTable>((player, table) =>
                Doing("taking a palette", () => _palettes?.Accept(player, table)))
            .SetMessageHandler<IconTable>((player, table) =>
                Doing("taking marker pictures", () => _icons?.Accept(player, table)))
            .SetMessageHandler<PlayerPortrait>((player, png) =>
                Doing("taking a portrait", () => _portraits?.Accept(player, png)))
            // A marker asked for from in game. Not answered here: what it becomes
            // depends on what the map service holds for that player, which is a
            // round trip and does not belong on the packet thread.
            .SetMessageHandler<MarkAsk>((player, ask) =>
                Doing("taking a marker from a player", () => Mark(player, ask)));

        // `PlayerNowPlaying` is the earliest moment a player can be sent
        // anything: their client is loaded and in the world. The map's address is
        // said here, straight away, because a link somebody has to wait for is a
        // link they have already stopped looking for.
        api.Event.PlayerNowPlaying += player => Doing("greeting a player", () =>
        {
            SharedServer.SendTo(api, player, _visibility);
            // The whole room rather than the player who just joined: an admin
            // arriving is the moment to ask an admin, and an admin already
            // playing is a better answer than the player who walked in.
            _palettes?.AskAround(api.World.AllOnlinePlayers.OfType<IServerPlayer>());
            _icons?.AskForMissing(player);
            _portraits?.AskOnceSettled(player, Doing);
            Greet(player, GreetTries);
        });

        // Said again, but only where the first one cannot have been read.
        //
        // On a first join the character and class screen is up while `NowPlaying`
        // fires, and a line of chat behind it is a line nobody sees; `PlayerReady`
        // is after that screen closes. For everybody else it arrives in the same
        // breath as `NowPlaying`, and `Greet` reads the gap between the two to
        // tell those cases apart rather than guessing which kind of join this is.
        //
        // It cannot be the only teller. A dedicated server has been seen not to
        // raise this event at all, with the address readable and nothing thrown —
        // and when it was the one that spoke, that server said nothing until a
        // twenty second timer gave up on it. Whichever way it behaves, the line
        // above has already been said.
        api.Event.PlayerReady += player =>
            Doing("telling a player where the map is", () => Greet(player, 0));

        api.Event.PlayerDisconnect += player =>
        {
            lock (_greeted)
            {
                _greeted.Remove(player.PlayerUID);
            }

            // A palette arrives in slices and is held until the set is complete,
            // so one that never completes would sit in memory for the life of the
            // server. Anybody can be asked for one now, which makes a half-sent
            // set an ordinary event rather than an admin's misfortune.
            _palettes?.Forget(player.PlayerUID);
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
            // The palette is what says whether a block shows anything, so the
            // exporter reads it through the field rather than being handed a
            // copy: a palette that arrives from a client an hour from now has to
            // correct what gets exported as well as what gets coloured.
            _exporter = new Exporter(api, Settings.Exports, id => _palettes?.Shows(id) ?? true);
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
            _service?.Markers(MarkerFeed.Json(api, _visibility));
        }), ShareIntervalMs);

        // A tick listener rather than a load event: it is guaranteed to fire, and
        // repeating it keeps the map current without anyone typing a command.
        api.Event.RegisterGameTickListener(
            Every("exporting", () => Export("timer")), ExportIntervalMs);

        // A colour the map has not got is not something a player fixes by
        // rejoining, and on a small server nobody may rejoin for days — so the
        // ask cannot only ride the join. On the export beat because that is the
        // slowest clock here; what keeps it to one ask every couple of minutes,
        // and to one ask per player per palette, is in `AskAround`.
        api.Event.RegisterGameTickListener(
            Every("asking for a palette", () =>
                _palettes?.AskAround(api.World.AllOnlinePlayers.OfType<IServerPlayer>())),
            ExportIntervalMs);

        // Players move, so this goes far more often than the terrain — and it
        // goes over the socket rather than to a file, because a position is worth
        // nothing by the time a disk has finished with it.
        api.Event.RegisterGameTickListener(Every("posting players", () =>
            _service?.Players(PlayerFeed.Json(api, Settings.Exports))), LiveIntervalMs);

        // On the same beat, and for the same reason: a clock belongs to the
        // server that is running, not to a file beside the map.
        api.Event.RegisterGameTickListener(Every("posting the world's clock", () =>
            _service?.World(WorldClock.Json(api))), LiveIntervalMs);

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
                    var landed = Pending.Apply(api, _visibility, held);
                    if (!landed.Anything)
                    {
                        return;
                    }

                    api.Logger.Notification(
                        "[witchlight] web map: {0} marker(s) made, {1} changed, {2} deleted",
                        landed.Made, landed.Changed, landed.Removed);
                    SharedServer.SendToAll(api, _visibility);
                    service.Markers(MarkerFeed.Json(api, _visibility));
                }),
                "witchlight-markers");
        });
    }

    /// <summary>
    /// Makes a marker one of this server's players asked for from in game.
    ///
    /// Three things have to happen in three places, which is the whole shape of
    /// this. The block at the spot can only be read on the game thread. What that
    /// player has kept can only be asked of the map service, which is a round
    /// trip. And the waypoint can only be made on the game thread again. So it
    /// goes out and comes back, exactly as collecting the web's markers does.
    ///
    /// Everything the answer contains is decided in <see cref="Marking"/>; what
    /// is here is which thread each part runs on and what else has to be told.
    /// </summary>
    private void Mark(IServerPlayer player, MarkAsk ask)
    {
        if (_sapi is not { } api || _service is not { } service)
        {
            return;
        }

        var block = Marking.BlockAt(api, ask);
        _ = Task.Run(async () =>
        {
            var person = await Presets.Of(service, player.PlayerUID, api.Logger)
                .ConfigureAwait(false);

            api.Event.EnqueueMainThreadTask(
                () => Doing("marking from the game", () => Marked(player, ask, person, block)),
                "witchlight-mark");
        });
    }

    /// <summary>
    /// The half of a mark that has to be on the game thread: the waypoint, the
    /// decision about who sees it, and telling everything that shows markers.
    /// </summary>
    private void Marked(
        IServerPlayer player, MarkAsk ask, Person person, (string Code, string Name) block)
    {
        if (_sapi is not { } api || _service is not { } service)
        {
            return;
        }

        var reply = Marking.Answer(api, _visibility, player, ask, person, block);
        api.Network.GetChannel(Channel.Name).SendPacket(reply, player);
        if (!reply.Made)
        {
            return;
        }

        // Straight back out rather than on the slow timer: the person who made it
        // is standing there, and everybody else's map should have it before they
        // walk away from the spot.
        SharedServer.SendToAll(api, _visibility);
        service.Markers(MarkerFeed.Json(api, _visibility));

        // The preset is the map service's to keep, so it goes off the game thread
        // and nothing waits on it. A marker that landed must not be undone by a
        // service that would not take the preset beside it.
        if (Marking.Keeping(ask, reply, block) is { } preset)
        {
            _ = Task.Run(() => Presets.Keep(service, player.PlayerUID, preset, api.Logger));
        }
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
