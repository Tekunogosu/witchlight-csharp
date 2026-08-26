using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Mapstique;

/// <summary>
/// Server-side export for the Mapstique map server.
///
/// The palette has to be built while block textures are still in memory: the
/// server frees them once assets are loaded (Block.FreeRAMServer), so this hooks
/// the asset stage rather than run time.
/// </summary>
public class MapstiqueSystem : ModSystem
{
    private const string ExportDirName = "mapstique";

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
            .SetMessageHandler<PaletteTable>(OnPaletteFromClient);
        api.Event.PlayerNowPlaying += player =>
        {
            SharedServer.SendTo(api, player);
            AskForPalette(player);
        };
        _service = new MapService(ExportDir, api.Logger);

        // Markers ride the timer that already pushes them to players in game:
        // they change a few times an hour, and a post that says nothing new is
        // dropped before it is sent anyway.
        api.Event.RegisterGameTickListener(_ =>
        {
            SharedServer.SendToAll(api);
            _service?.Markers(Live.WaypointsJson(api));
        }, ShareIntervalMs);

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
                api.Logger.Notification("[mapstique] removed live.json; live data goes over the socket");
            }
            catch (System.Exception error)
            {
                api.Logger.Warning("[mapstique] could not remove live.json: {0}", error.Message);
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
        api.Event.ChunkDirty += (coord, chunk, reason) => _dirty.Mark(coord.X, coord.Z, reason);

        // A tick listener rather than a load event: it is guaranteed to fire, and
        // repeating it keeps the map current without anyone typing a command.
        api.Event.RegisterGameTickListener(_ => Export("timer"), ExportIntervalMs);

        // Players move, so this goes far more often than the terrain — and it
        // goes over the socket rather than to a file, because a position is worth
        // nothing by the time a disk has finished with it.
        api.Event.RegisterGameTickListener(_ => PostPlayers(), LiveIntervalMs);
        api.Event.RegisterCallback(_ => Seed(SeedRadius), SeedDelayMs);
        api.Event.GameWorldSave += () => Export("world save");
        // Version first, and on every start: the quickest way to tell a deployed
        // mod from the one you meant to deploy. The map service prints its own for
        // the same reason, and the two must match on minor version.
        api.Logger.Notification(
            "[mapstique] {0} ready, exporting every {1}s to {2}",
            Mod.Info.Version,
            ExportIntervalMs / 1000,
            ExportDir);

        api.ChatCommands
            .Create("mapstique")
            .WithDescription("Mapstique map server tools")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("export")
                .WithDescription("Write the surface of every loaded chunk")
                .HandleWith(OnExport)
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("What has been exported, and where the palette came from")
                .HandleWith(OnStatus)
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
            _sapi.Logger.Notification("[mapstique] {0}", message);
            return message;
        }
        catch (System.Exception error)
        {
            // Nothing was written, so what was taken is still owed.
            _dirty.Restore(wanted);
            _sapi.Logger.Error("[mapstique] export failed: {0}", error);
            return null;
        }
    }

    private static long Length(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
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
        var spawn = _sapi.World.DefaultSpawnPosition?.AsBlockPos;
        var centerX = (spawn?.X ?? 0) / size;
        var centerZ = (spawn?.Z ?? 0) / size;

        _sapi.Logger.Notification(
            "[mapstique] loading {0}x{0} chunks around spawn to seed the map",
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
            "[mapstique] asked {0} for a block colour palette", player.PlayerName);

        // Silence is otherwise indistinguishable from success at a glance.
        var uid = player.PlayerUID;
        var name = player.PlayerName;
        _sapi.Event.RegisterCallback(_ =>
        {
            if (_needsPalette && _askedUid == uid)
            {
                _sapi.Logger.Notification(
                    "[mapstique] no palette came back from {0} — check that they have the mod, "
                    + "and their client log for why it declined",
                    name);
            }
        }, PaletteReplyWaitMs);
    }

    /// <summary>How long to wait before saying an ask went unanswered.</summary>
    private const int PaletteReplyWaitMs = 30000;

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
                "[mapstique] ignored a palette from {0}, who is not an admin", player.PlayerName);
            return;
        }

        if (table.Fingerprint != _fingerprint)
        {
            _sapi.Logger.Warning(
                "[mapstique] ignored a palette from {0}: it was built for a different mod set",
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
            "[mapstique] palette from {0}: {1} of {2} blocks coloured ({3:P0}){4}",
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
            _service?.Players(Live.PlayersJson(_sapi));
        }
    }

    /// <summary>Where live data goes, in place of a file rewritten every two seconds.</summary>
    private MapService? _service;

    public override void Dispose()
    {
        _service?.Dispose();
        _service = null;
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
                "[mapstique] only {0:P0} of blocks have a colour — an admin will be asked for a palette on join",
                palette.Coverage);
        }

        var colorMaps = PaletteBuilder.ExportColorMaps(api, Path.Combine(ExportDir, "colormaps"));
        api.Logger.Notification("[mapstique] colour maps: {0} written", colorMaps);

        api.Logger.Notification(
            "[mapstique] palette: {0} blocks written to {1}{2}",
            palette.Blocks.Count,
            path,
            missing > 0 ? $" ({missing} without a readable texture)" : "");

        foreach (var group in PaletteBuilder.FailureCounts.OrderByDescending(entry => entry.Value).Take(12))
        {
            api.Logger.Notification("[mapstique] missing group {0}: {1}", group.Key, group.Value);
        }
        foreach (var failure in PaletteBuilder.Failures)
        {
            api.Logger.Notification("[mapstique] skipped {0}", failure);
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
            "[mapstique] {0}: {1} of {2} blocks have textures in memory",
            stage,
            withTextures,
            blocks.Count);
    }
}
