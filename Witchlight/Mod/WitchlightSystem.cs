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

    /// <summary>
    /// Which markers each player keeps in sight on their own map. Read once the
    /// world is up, because it lives in the savegame, and written back with it —
    /// the same life <see cref="_visibility"/> has, for the same reason.
    /// </summary>
    private Pins _pins = Pins.Empty;

    /// <summary>
    /// What block each marker was put on. Read once the world is up and written
    /// back with it, the same life the two stores above have — and for the same
    /// reason: it is a fact about a waypoint, and a store that outlived the
    /// waypoints would describe markers that are gone.
    /// </summary>
    private Origins _origins = Origins.Empty;

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

    /// <summary>Where the map service may ask this mod for one more column.</summary>
    private ModApi? _modApi;

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
            "[witchlight] {0} ready, exporting every {1}s",
            Mod.Info.Version,
            Settings.ExportIntervalMs / 1000);
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
                Doing("taking a marker from a player", () => Mark(player, ask)))
            // Somebody else's marker, pinned or changed from a player's own map.
            .SetMessageHandler<SharedMarkerChange>((player, change) =>
                Doing("changing a shared marker", () => ChangeShared(player, change)));

        // `PlayerNowPlaying` is the earliest moment a player can be sent
        // anything: their client is loaded and in the world. The map's address is
        // said here, straight away, because a link somebody has to wait for is a
        // link they have already stopped looking for.
        api.Event.PlayerNowPlaying += player => Doing("greeting a player", () =>
        {
            SharedServer.SendTo(api, player, _visibility, _pins);
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

        // A block a player placed or broke says exactly which column moved, so
        // the exporter reads that one column rather than the chunk — see
        // `Exporter.Touched`. Everything the game changes without a player is
        // still caught by the chunk signal above.
        api.Event.DidPlaceBlock += (player, oldBlockId, selection, stack) =>
            Doing("noting a placed block", () => _exporter?.Touched(selection.Position));
        api.Event.DidBreakBlock += (player, oldBlockId, selection) =>
            Doing("noting a broken block", () => _exporter?.Touched(selection.Position));

        api.Event.GameWorldSave += () => Doing("exporting on save", () => Export("world save"));
        api.Event.GameWorldSave += () => Doing("storing marker visibility", StoreVisibility);
        api.Event.GameWorldSave += () => Doing("storing marker pins", StorePins);
        api.Event.GameWorldSave += () =>
            Doing("storing the blocks markers were made on", StoreOrigins);

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
            _exporter = _service is { } service
                ? new Exporter(api, Settings.Exports, service, id => _palettes?.Shows(id) ?? true)
                : null;
            _exporter?.KeepWorldFacts();
            _visibility = Visibility.Read(api);
            _pins = Pins.Read(api);
            _origins = Origins.Read(api);
            _modApi = new ModApi(
                api, api.Logger, Settings.Exports, id => _palettes?.Shows(id) ?? true, Microblocks.In(api.World));
            _modApi.Start();
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
            SharedServer.SendToAll(api, _visibility, _pins);
            _service?.Markers(MarkerFeed.Json(api, _visibility, _pins, _origins));
        }), ShareIntervalMs);

        // The land claims ride the same slow beat, for the same reasons: they
        // change a few times a week, they are the bulk of what there is to send
        // beside the markers, and a post that says nothing new is dropped before
        // it goes anyway. Who may see them travels with them, and that can change
        // between posts — a role granted, a player added — which is the other half
        // of why this repeats rather than being sent once at start.
        api.Event.RegisterGameTickListener(
            Every("sharing claims", () => _service?.Claims(ClaimFeed.Json(api))),
            ShareIntervalMs);

        // The slow lane: where the year has reached, and a whole read of whatever
        // the fast lane answered for by patching. A tick listener rather than a
        // load event: it is guaranteed to fire, and repeating it keeps the map
        // current without anyone typing a command.
        api.Event.RegisterGameTickListener(
            Every("exporting", () => Export("timer")), Settings.ExportIntervalMs);

        // The fast lane: what moved, to the service, within a quarter of a
        // second. Fast enough that a player building sees the map follow their
        // hands, and bounded on the game thread by how much it reads per beat —
        // see `Exporter.Push`.
        api.Event.RegisterGameTickListener(
            Every("pushing changed chunks", () => _exporter?.Push()), PushIntervalMs);

        // Getting the ground back is not writing it, and the two run at their own
        // speeds. A chunk load is work for the server's chunk thread and is paced
        // against that; a write is paced against the disk, which an operator sets
        // with `export_interval_ms`. Tied together, a map rebuilding itself did so
        // at whatever rate somebody had chosen to write at.
        api.Event.RegisterGameTickListener(
            Every("fetching what the map is owed", () => _exporter?.Fetch()),
            Repair.StepIntervalMs);

        // A colour the map has not got is not something a player fixes by
        // rejoining, and on a small server nobody may rejoin for days — so the
        // ask cannot only ride the join. On the export beat because that is the
        // slowest clock here; what keeps it to one ask every couple of minutes,
        // and to one ask per player per palette, is in `AskAround`.
        api.Event.RegisterGameTickListener(
            Every("asking for a palette", () =>
                _palettes?.AskAround(api.World.AllOnlinePlayers.OfType<IServerPlayer>())),
            Settings.ExportIntervalMs);

        // Players move, so this goes far more often than the terrain — and it
        // goes over the socket rather than to a file, because a position is worth
        // nothing by the time a disk has finished with it.
        api.Event.RegisterGameTickListener(Every("posting players", () =>
            _service?.Players(PlayerFeed.Json(api, Settings.Exports))), LiveIntervalMs);

        // On the same beat, and for the same reason: a clock belongs to the
        // server that is running, not to a file beside the map.
        api.Event.RegisterGameTickListener(Every("posting the world's clock", () =>
            _service?.World(WorldClock.Json(api))), LiveIntervalMs);

        // What was asked for on the web rides the same tick. It is rare, the ask
        // is a single request that usually comes back empty, and collecting on the
        // slow marker timer instead would mean watching a form for fifteen seconds
        // to find out whether it worked.
        api.Event.RegisterGameTickListener(
            Every("collecting what the map asked for", CollectAsks), LiveIntervalMs);

    }

    /// <summary>How often changed chunks are pushed. A quarter of a second: fast
    ///  enough that a player building sees the map follow, and still far above
    ///  the cost of one small post.</summary>
    private const int PushIntervalMs = 250;

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
            Repair.StepIntervalMs);
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
    /// A `live.json` from before live data went over the socket is read by
    /// nothing, and leaving it invites a reader to find it and show markers that
    /// are however old it is. The region files an older build wrote are left
    /// where they are: the service reads them once into its database on its
    /// first start and never again, and deleting them is the operator's call.
    /// </summary>
    private static void ClearWhatThisBuildCannotRead(ICoreServerAPI api)
    {
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
    /// Collects what somebody asked for on the web — markers, and land claims —
    /// and does it.
    ///
    /// The asking is a round trip and the doing touches the waypoint list and the
    /// claim list, so the two happen in different places: the request goes off the
    /// game thread, and what comes back is applied on it. Whatever changed goes
    /// straight back out afterwards rather than waiting for the slow timer,
    /// because the browser that asked is watching for its own to appear and
    /// fifteen seconds of nothing reads as a form that failed.
    /// </summary>
    private void CollectAsks()
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
                    var landed = Pending.Apply(api, _visibility, _pins, _origins, held);
                    if (!landed.Anything)
                    {
                        return;
                    }

                    api.Logger.Notification(
                        "[witchlight] web map: {0} marker(s) made, {1} changed, {2} deleted, "
                        + "{3} pinned or unpinned; "
                        + "{4} land claim(s) made, {5} changed, {6} given up",
                        landed.Made, landed.Changed, landed.Removed, landed.Pinned,
                        landed.Claims.Made, landed.Claims.Changed, landed.Claims.Removed);

                    // Only what actually changed. A claim the game has taken tells
                    // every client itself — see `ILandClaimAPI.Add` — so pushing
                    // the markers to everybody because a claim landed would be a
                    // few tens of kilobytes per player to say nothing.
                    if (landed.AnyMarkers)
                    {
                        SharedServer.SendToAll(api, _visibility, _pins);
                        service.Markers(MarkerFeed.Json(api, _visibility, _pins, _origins));
                    }
                    if (landed.Claims.Anything)
                    {
                        service.Claims(ClaimFeed.Json(api));
                    }
                }),
                "witchlight-asks");
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
    /// What a player asked of somebody else's marker from their in-game map.
    ///
    /// The asker is always sent their markers again, because the pin they asked
    /// for is a fact about their map and the window they asked from is waiting
    /// to see it. A marker that itself changed is everybody's to see, and the
    /// map service's to show, the same as one changed from the web.
    /// </summary>
    private void ChangeShared(IServerPlayer player, SharedMarkerChange change)
    {
        if (_sapi is not { } api)
        {
            return;
        }

        var changed = SharedServer.Apply(api, player, change, _visibility, _pins);
        if (changed)
        {
            SharedServer.SendToAll(api, _visibility, _pins);
            _service?.Markers(MarkerFeed.Json(api, _visibility, _pins, _origins));
            return;
        }
        SharedServer.SendTo(api, player, _visibility, _pins);
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

        var reply = Marking.Answer(api, _visibility, _origins, player, ask, person, block);
        api.Network.GetChannel(Channel.Name).SendPacket(reply, player);
        if (!reply.Made)
        {
            return;
        }

        // Straight back out rather than on the slow timer: the person who made it
        // is standing there, and everybody else's map should have it before they
        // walk away from the spot.
        SharedServer.SendToAll(api, _visibility, _pins);
        service.Markers(MarkerFeed.Json(api, _visibility, _pins, _origins));

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

    /// <summary>
    /// Stores which markers each player keeps in sight, beside the waypoints
    /// themselves and under the rule <see cref="StoreVisibility"/> follows: the
    /// live list where there is one, and nothing where the layer cannot be
    /// reached, so a save taken while it is down forgets nobody.
    /// </summary>
    private void StorePins()
    {
        if (_sapi is null)
        {
            return;
        }

        _pins.Write(_sapi, Markers.Layer(_sapi)?.Waypoints);
    }

    /// <summary>Stores what block each marker was put on, under the rule the two
    ///  stores above follow: the live list where there is one, and nothing where
    ///  the layer cannot be reached.</summary>
    private void StoreOrigins()
    {
        if (_sapi is null)
        {
            return;
        }

        _origins.Write(_sapi, Markers.Layer(_sapi)?.Waypoints);
    }

    public override void Dispose()
    {
        _service?.Dispose();
        _service = null;
        _modApi?.Dispose();
        _modApi = null;
        _serviceProcess?.Dispose();
        _serviceProcess = null;
    }

    /// <summary>
    /// How often shared markers are pushed. Markers change rarely, and clients
    /// only act on ones they have not seen, so this is a small packet.
    /// </summary>
    private const int ShareIntervalMs = 15000;

    /// <summary>
    /// How often player positions are posted. A second: the service tells every
    /// browser the moment a post lands, so this is the whole of how often a dot
    /// moves.
    /// </summary>
    private const int LiveIntervalMs = 1000;

    /// <summary>Chunk columns each way from spawn for the initial map.</summary>
    private const int SeedRadius = 8;


}