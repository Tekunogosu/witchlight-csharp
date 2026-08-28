using System;
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
    private PaletteExchange? _palettes;
    private IconExchange? _icons;
    private PortraitExchange? _portraits;

    /// <summary>Where live data goes, in place of a file rewritten every two seconds.</summary>
    private MapService? _service;

    /// <summary>The map service, when this server is the one running it.</summary>
    private ServiceProcess? _serviceProcess;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    /// <summary>Runs before the server frees texture data, which is the only window.</summary>
    public override double ExecuteOrder() => 1.0;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _sapi = api;
        _faults = new Faults(api.Logger);
        _icons = new IconExchange(api, Settings.Exports);
        if (_built is { } built)
        {
            _palettes = new PaletteExchange(api, Settings.Exports, built);
        }
        _portraits = new PortraitExchange(api, Settings.Exports);
        _service = new MapService(Settings.Exports, api.Logger);

        Listen(api);
        ClearWhatThisBuildCannotRead(api);
        Schedule(api);
        RegisterCommands(api);

        // Version first, and on every start: the quickest way to tell a deployed
        // mod from the one you meant to deploy. The map service prints its own for
        // the same reason, and the two must match on minor version.
        api.Logger.Notification(
            "[witchlight] {0} ready, exporting every {1}s to {2}",
            Mod.Info.Version,
            ExportIntervalMs / 1000,
            Settings.Exports);
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
            SayWhereTheMapIs(player);
        });

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
            _exporter = new Exporter(api, Settings.Exports);
            _exporter.KeepWorldFacts();
            _visibility = Visibility.Read(api);
            StartService();
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

        // Markers asked for on the web ride the same tick. They are rare, the ask
        // is a single request that usually comes back empty, and collecting them
        // on the slow marker timer instead would mean watching a form for fifteen
        // seconds to find out whether it worked.
        api.Event.RegisterGameTickListener(
            Every("collecting markers", CollectMarkers), LiveIntervalMs);

        api.Event.RegisterCallback(
            _ => Doing("seeding the map", () => _exporter?.Seed(SeedRadius)), SeedDelayMs);
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
    private void SayWhereTheMapIs(IServerPlayer player)
    {
        if (_sapi is null || !Settings.Announces || Settings.Announcement() is not { } where)
        {
            return;
        }

        _sapi.SendMessage(
            player, GlobalConstants.GeneralChatGroup, $"The map is at {where}", EnumChatType.Notification);
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

        _serviceProcess = ServiceProcess.Prepare(_sapi, Mod, Settings.Exports);
        if (_serviceProcess is null)
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

        _built = PaletteExchange.Bootstrap(api, Settings.Exports);
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

    /// <summary>Long enough after start that the world is ready to generate.</summary>
    private const int SeedDelayMs = 10000;

    /// <summary>
    /// How often the surface is re-exported. Frequent enough to be useful while
    /// testing; a large server will want this longer, which is what a config
    /// file is for once there is one.
    /// </summary>
    private const int ExportIntervalMs = 30000;
}
