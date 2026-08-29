using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Writing the surface of the world to disk.
///
/// The whole of the export: what has moved since last time, what to re-read, and
/// which region files that lands in. Kept apart from the mod system because none
/// of it is about the game's lifecycle — it is about the map — and because an
/// export is the one thing here that runs on the server's own tick and so has to
/// be readable enough to be sure it is cheap.
///
/// Two things move a region: a column in it whose surface changed, and the year,
/// which is stored per chunk. When neither has, nothing is read and nothing is
/// written.
/// </summary>
public sealed class Exporter
{
    private readonly ICoreServerAPI _api;
    private readonly string _exports;

    /// <summary>Columns whose surface has moved since the last export.</summary>
    private readonly DirtyColumns _dirty = new();

    /// <summary>Where the year had reached for each column when it was last written.</summary>
    private readonly Dictionary<(int, int), byte> _seasons;

    /// <summary>Whether spawn has reached the map service yet.</summary>
    private bool _wroteWorldFacts;

    public Exporter(ICoreServerAPI api, string exports)
    {
        _api = api;
        _exports = exports;

        // What a previous run left on disk. Columns already there are not re-read
        // merely for being loaded again, and the seasons stored beside them say
        // whether the year has moved since.
        _seasons = Regions.Index(Regions.DirectoryIn(exports), GlobalConstants.ChunkSize);
        _dirty.Seed(_seasons.Keys);

        // And what the server loaded before this existed to hear about it, which
        // is the square of chunks around spawn. Those are held for the life of the
        // server, so their one chance to be noticed has already gone by.
        _dirty.MarkUnexported(LoadedColumns(api));
    }

    /// <summary>How many columns are waiting to be re-read.</summary>
    public int Waiting => _dirty.Count;

    /// <summary>How many chunks the map holds.</summary>
    public int Mapped => _seasons.Count;

    /// <summary>Notes a chunk the server has marked dirty.</summary>
    public void Mark(int chunkX, int chunkZ, EnumChunkDirtyReason reason) =>
        _dirty.Mark(chunkX, chunkZ, reason);

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
    public void KeepWorldFacts()
    {
        _wroteWorldFacts = _wroteWorldFacts || WorldFacts.Write(_api, _exports);
    }

    /// <summary>
    /// Writes the regions that have moved. Returns what happened, or null if it
    /// failed — the caller decides how loudly to say so.
    /// </summary>
    public string? Export(string reason, bool force = false)
    {
        KeepWorldFacts();

        var dir = Regions.DirectoryIn(_exports);
        if (force)
        {
            // What an operator typing the command expects: read everything in
            // memory again, whether or not the server thinks it moved.
            _dirty.MarkAll(LoadedColumns(_api));
        }

        var seasonsNow = ColumnPump.Seasons(_api, _seasons.Keys, _api.WorldManager.ChunkSize);
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

            var set = ColumnPump.Gather(_api, dir, wanted, load);

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

            foreach (var (column, season) in ColumnPump.Write(_api, dir, set, write))
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
            _api.Logger.Notification("[witchlight] {0}", message);
            return message;
        }
        catch (System.Exception error)
        {
            // Nothing was written, so what was taken is still owed.
            _dirty.Restore(wanted);
            _api.Logger.Error("[witchlight] export failed: {0}", error);
            return null;
        }
    }

    /// <summary>What the status command says about the terrain on disk.</summary>
    public string Describe()
    {
        var regions = Regions.All(Regions.DirectoryIn(_exports));
        if (regions.Count == 0)
        {
            return "terrain: nothing exported yet";
        }

        var stored = regions.Values.Sum(Length);
        var newest = regions.Values.Select(path => new FileInfo(path).LastWriteTime).Max();
        return $"terrain: {regions.Count} regions, {stored / 1024} KiB, newest written {newest:HH:mm:ss}";
    }

    private static long Length(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    /// <summary>Every chunk column the server currently holds in memory.</summary>
    private static IEnumerable<(int, int)> LoadedColumns(ICoreServerAPI api)
    {
        foreach (var index in new List<long>(api.WorldManager.AllLoadedMapchunks.Keys))
        {
            var pos = api.WorldManager.MapChunkPosFromChunkIndex2D(index);
            yield return (pos.X, pos.Y);
        }
    }
}
