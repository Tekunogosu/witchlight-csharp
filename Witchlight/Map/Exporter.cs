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

    /// <summary>
    /// The columns the map is owed, and the getting of them back.
    ///
    /// Handed a way to write the ground it recovers rather than reaching for one,
    /// so that it knows how to ask the server for a column and nothing about what
    /// a map is. What it hands back is read on a beat of its own, a second after
    /// the ground lands, because the export beat is longer than an untouched
    /// column stays in memory — see <see cref="Repair"/>.
    /// </summary>
    private readonly Repair _repair;

    /// <summary>
    /// The ground the savegame holds that the map has never drawn.
    ///
    /// Walks outward from what the map has, asks the game whether the save holds
    /// each column beside it, and hands the ones it does to the repair — which
    /// already knows how to fetch a column and write it. See <see cref="Backfill"/>
    /// for why this asks the game rather than reading the savegame itself.
    /// </summary>
    private readonly Backfill _backfill;

    /// <summary>Where the year had reached for each column when it was last written.</summary>
    private readonly Dictionary<(int, int), byte> _seasons;

    /// <summary>Whether spawn has reached the map service yet.</summary>
    private bool _wroteWorldFacts;

    /// <summary>
    /// Whether a block puts anything where it stands, which is what makes it the
    /// top of a column rather than something to walk past.
    ///
    /// Asked of the palette rather than worked out here, and asked each time
    /// rather than copied, so a better palette from a client corrects what gets
    /// exported from the next beat onward — see <see cref="PaletteExchange.Shows"/>.
    /// </summary>
    private readonly System.Func<int, bool> _shows;

    /// <summary>
    /// The chiselled blocks, whose colour is the colour of what they were cut
    /// from rather than anything the block itself has.
    ///
    /// Read off the block list once. Which ids those are is fixed for the life of
    /// a world — it is decided when the mods finish loading, and this is built
    /// after that — so it is asked once rather than on every column of every
    /// export.
    /// </summary>
    private readonly Microblocks _chiselled;

    public Exporter(ICoreServerAPI api, string exports, System.Func<int, bool> shows)
    {
        _api = api;
        _exports = exports;
        _shows = shows;
        _chiselled = Microblocks.In(api.World);
        // What a recovered column is for: it was asked for so that it could be
        // written, and writing it is this class's. Marked as well as written,
        // because a column the server already held raises no ChunkDirty when it
        // is asked for again — the callback is the only thing that says it is
        // there, so it is also the thing that says it is wanted.
        _repair = new Repair(api, columns =>
        {
            _dirty.MarkAll(columns);
            Write("repair", force: false, asked: 0);
        });
        _backfill = new Backfill(api, column => _repair.Owe(new[] { column }));

        // What a previous run left on disk. Columns already there are not re-read
        // merely for being loaded again, and the seasons stored beside them say
        // whether the year has moved since.
        var survey = Regions.Walk(Regions.DirectoryIn(exports), GlobalConstants.ChunkSize);
        _seasons = survey.Seasons;

        // Except the ones stored with a hole in them. A column recorded as air is
        // a reading that failed, not a fact about the world, and the map paints it
        // as ground nobody has ever explored — so it is worth the one re-read that
        // replaces it with what is actually down there. Left out of what counts as
        // exported, which is the existing way of saying "read this again when it
        // is next in memory": loaded now, and it is marked below; loaded later,
        // and the server's own ChunkDirty says so.
        _dirty.Seed(_seasons.Keys.Where(column => !survey.Holed.Contains(column)));
        if (survey.Holed.Count > 0)
        {
            api.Logger.Notification(
                "[witchlight] {0} chunk(s) on disk hold a column stored as air — they will be "
                + "read again as they load",
                survey.Holed.Count);
        }

        // And what the server loaded before this existed to hear about it, which
        // is the square of chunks around spawn. Those are held for the life of the
        // server, so their one chance to be noticed has already gone by.
        _dirty.MarkUnexported(LoadedColumns(api));

        // Last, the chunks the map is missing from the middle of itself. Marking
        // them is no use — nothing can read a chunk the server does not hold — so
        // they go on the list of columns owed a read, which is where the repair
        // looks for something to ask the server for. This is what carries a hole
        // across a restart: the list itself is memory, and the map on disk is the
        // only thing that still knows the hole is there.
        // And the edges of it, which is where the ground the map has never drawn
        // begins. Offered what the map holds so that the first frontier is the
        // whole of its outline; every column written after this offers its own
        // neighbours as it lands.
        _backfill.Beside(_seasons.Keys, _seasons.Keys);

        _repair.Owe(survey.Gaps);
        if (survey.Gaps.Count > 0)
        {
            api.Logger.Notification(
                "[witchlight] {0} chunk(s) are missing from the middle of the map — "
                + "the server will be asked for them",
                survey.Gaps.Count);
        }
    }

    /// <summary>How many columns are waiting to be re-read.</summary>
    public int Waiting => _dirty.Count;

    /// <summary>How many columns the map wants and cannot read yet.</summary>
    public int Withheld => _repair.Owed;

    /// <summary>What the savegame is still to be asked about, or nothing.</summary>
    public string? Backfilling => _backfill.Describe();

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
    /// One beat of the map: ask the server for some of the ground the map is
    /// owed, and write whatever has moved.
    ///
    /// Two acts rather than one, and the asking comes first — before anything is
    /// marked, because a forced export marks every loaded map chunk and the
    /// columns owed a read are exactly the ones a map chunk alone cannot answer
    /// for. What comes back is not written by this export: it arrives seconds
    /// later and is written by a beat of its own, which is the whole of why the
    /// repair works at all. See <see cref="Repair"/>.
    /// </summary>
    public string? Export(string reason, bool force = false)
    {
        // Before the write, and in this order: the frontier is walked a little
        // ahead of the repair, so that what the repair asks the server for on the
        // next beat is already waiting for it.
        _backfill.Step(force ? Backfill.PerCommand : Backfill.PerExport);
        return Write(reason, force, _repair.Ask(force ? Repair.PerCommand : Repair.PerExport));
    }

    /// <summary>
    /// Writes the regions that have moved. Returns what happened, or null if it
    /// failed — the caller decides how loudly to say so.
    ///
    /// <paramref name="asked"/> is how many columns the repair has just asked the
    /// server for, which is nothing this write does and part of what the beat it
    /// belongs to has to report.
    /// </summary>
    private string? Write(string reason, bool force, int asked)
    {
        KeepWorldFacts();

        var dir = Regions.DirectoryIn(_exports);

        if (force)
        {
            // What an operator typing the command expects: read everything in
            // memory again, whether or not the server thinks it moved.
            _dirty.MarkAll(LoadedColumns(_api));
        }

        var edge = _api.WorldManager.ChunkSize;
        var seasonsNow = ColumnPump.Seasons(_api, _seasons.Keys, edge);
        var aged = ColumnPump.SeasonsMoved(_seasons, seasonsNow);

        if (_dirty.Count == 0 && aged.Count == 0)
        {
            return $"nothing has moved since the last export, on {reason}{Asked(asked)}";
        }

        var wanted = _dirty.Take();

        try
        {
            // Timed because this runs on the server's own tick: whatever it costs
            // is time the server is not doing anything else.
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var set = ColumnPump.Gather(_api, dir, wanted, _shows, _chiselled);

            // Neither of these was read, so neither counts as exported. One waits
            // for the next export; the other cannot be read at all until the
            // server hands its blocks back, so it is both forgotten here and put
            // on the list of what the server has to be asked for.
            _dirty.Restore(set.Unready);
            _dirty.Forget(set.Unloaded);
            _repair.Owe(set.Unloaded);
            wanted.ExceptWith(set.Unready);
            wanted.ExceptWith(set.Unloaded);

            // One entry per chunk that has to be written, and nothing for the
            // chunks beside it. A ground that moved carries its new surface; a
            // year that moved carries only where it reached, which is a season in
            // the region's directory and no repacking at all.
            var updates = new Dictionary<(int, int), Regions.Update>();
            foreach (var (column, season) in ColumnPump.Seasons(_api, set.Moved, edge))
            {
                updates[column] = new Regions.Update(season, set.Records[column]);
            }
            foreach (var column in aged)
            {
                if (!updates.ContainsKey(column))
                {
                    updates[column] = new Regions.Update(seasonsNow[column], null);
                }
            }

            if (updates.Count == 0)
            {
                // Every dirty column looks the same from above. Underground work,
                // almost always, and no reason to touch the map.
                clock.Stop();
                Wrote(wanted);
                return $"{set.Reread} columns re-read, none changed the surface, "
                    + $"in {clock.ElapsedMilliseconds}ms on {reason}{Missing(set)}{Asked(asked)}";
            }

            var written = ColumnPump.Write(dir, edge, updates);
            foreach (var (column, update) in updates)
            {
                _seasons[column] = update.Season;
            }
            clock.Stop();
            Wrote(wanted);

            var message = $"wrote {updates.Count} chunks ({written} bytes) of "
                + $"{Regions.All(dir).Count} regions, "
                + $"{set.Moved.Count} of {set.Reread} columns re-read changed{set.Changes.Said()}"
                + (aged.Count > 0 ? $", season moved in {aged.Count}" : "")
                + $", {_seasons.Count} chunks mapped, in {clock.ElapsedMilliseconds}ms on {reason}"
                + Missing(set)
                + Asked(asked);
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

    /// <summary>
    /// Records that these columns are now on disk.
    ///
    /// Said to both halves that care, from the one place that knows. Reaching
    /// disk is what stops a column being re-read for merely loading again, and it
    /// is also the one thing that settles a column the map was owed — two facts
    /// with one cause, which is why they are not two calls at four sites.
    /// </summary>
    private void Wrote(IReadOnlyCollection<(int, int)> columns)
    {
        _dirty.Exported(columns);
        _repair.Settled(columns);
        // A column drawn for the first time is a new edge of the map, and what
        // lies beside it is ground the savegame may hold and the map has never
        // seen. Offered here because this is where a column becomes something the
        // map has.
        _backfill.Beside(columns, _seasons.Keys);
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
        return $"terrain: {regions.Count} regions, {stored / 1024} KiB, "
            + $"newest written {newest:HH:mm:ss}, {_chiselled.Kinds} chiselled block(s) resolved "
            + "to their material";
    }

    private static long Length(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    /// <summary>
    /// What an export says about the columns it wanted and could not read.
    ///
    /// Which of the two it was, rather than one word for both. A column with no
    /// map chunk is a load the server never did; one with a map chunk and no
    /// blocks under it is a load it did and then undid. The two want different
    /// fixes, and for a while this line said "no longer loaded" to both — which
    /// sent the reading of it the wrong way for a whole session.
    /// </summary>
    private static string Missing(ColumnSet set) =>
        set.Unloaded.Count == 0
            ? ""
            : $", {set.Unloaded.Count} not there to read ({set.Absent} with no map chunk, "
                + $"{set.Emptied} with no blocks under it)";

    /// <summary>
    /// What an export says about the repair it asked for, or nothing.
    ///
    /// Two numbers rather than one out of the other: what was asked for is
    /// counted when the asking happens and what is still owed when the line is
    /// written, and columns settle in between — so "asked for 8 of 4 owed a read"
    /// was a sentence this printed and nobody could read.
    /// </summary>
    private string Asked(int asked) =>
        asked > 0 ? $", asked the server for {asked}, {_repair.Owed} still owed" : "";

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
