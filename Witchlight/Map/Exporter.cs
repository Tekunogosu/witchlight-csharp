using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Sending the surface of the world to the map service.
///
/// The whole of the export: what has moved since last time, what to re-read, and
/// the sending of it. Kept apart from the mod system because none of it is about
/// the game's lifecycle — it is about the map — and because this runs on the
/// server's own tick and so has to be readable enough to be sure it is cheap.
///
/// Terrain used to go to disk, one region file per square, for the service to
/// notice and read back. It goes over the service's API channel now and into
/// the service's own database, which makes that the one place the map is kept.
/// What this holds of the ground is a checksum per chunk — enough to tell a
/// chunk loading again from one that changed — and, for chunks somebody is
/// building in, the record itself, so that a block placed or broken costs one
/// column read and a small post rather than a thousand reads.
///
/// Two lanes, on two clocks:
///
/// The fast lane runs every quarter second. A block a player places or breaks
/// patches one column of its chunk's record in memory and the chunk goes on the
/// next post — nothing is re-read. Chunks the server marked dirty for any other
/// reason are re-read whole, a bounded few per beat, so that the game thread
/// pays a slice of each tick and never all of it.
///
/// The slow lane runs on `export_interval_ms`. It asks where the year has
/// reached for every chunk the map holds and sends the season of any that
/// crossed a month, and it re-reads whole every chunk the fast lane patched
/// rather than read, which is the catch-all for anything a block event did not
/// describe — water, growth, a fire, a tree felled.
/// </summary>
public sealed class Exporter
{
    private readonly ICoreServerAPI _api;
    private readonly string _exports;
    private readonly MapService _service;

    /// <summary>Chunks whose blocks moved and whose surface is to be read again.</summary>
    private readonly DirtyColumns _dirty = new();

    /// <summary>
    /// Chunks whose blocks moved since the slow lane last looked, whether or not
    /// the fast lane has answered for them since. The catch-all's list.
    /// </summary>
    private readonly DirtyColumns _touched = new();

    /// <summary>
    /// The columns the map is owed, and the getting of them back. See
    /// <see cref="Repair"/>.
    /// </summary>
    private readonly Repair _repair;

    /// <summary>
    /// What the service holds for each chunk: the checksum of the record it has
    /// and the season it was sent with. Seeded from the service at start and
    /// moved with every post, so a chunk loading again is known from one that
    /// changed without a copy of the ground on this side.
    /// </summary>
    private readonly Dictionary<(int, int), Known> _known = new();

    /// <summary>
    /// The records of the chunks most recently read, so that a block event can
    /// patch one column rather than re-read a thousand. Bounded, because a
    /// record is six kilobytes and a long-running server loads every chunk in
    /// the world sooner or later.
    /// </summary>
    private readonly Recent _recent = new(RecentChunks);

    /// <summary>Chunks whose held record was patched by a block event and is ready to send.</summary>
    private readonly HashSet<(int, int)> _patched = new();

    /// <summary>Chunks whose season the slow lane found had turned, waiting for the next post.</summary>
    private readonly Dictionary<(int, int), byte> _turned = new();

    /// <summary>The buffer one chunk is read into. Reused: every chunk is the same size.</summary>
    private readonly ColumnPump.Surface _surface;

    /// <summary>Whether what the service holds has been asked for and answered.</summary>
    private bool _synced;
    private bool _syncing;

    /// <summary>Whether spawn has reached the map service yet.</summary>
    private bool _wroteWorldFacts;

    private readonly System.Func<int, bool> _shows;
    private readonly Microblocks _chiselled;

    private readonly record struct Known(uint Crc, byte Season);

    public Exporter(ICoreServerAPI api, string exports, MapService service, System.Func<int, bool> shows)
    {
        _api = api;
        _exports = exports;
        _service = service;
        _shows = shows;
        _chiselled = Microblocks.In(api.World);
        var edge = api.WorldManager.ChunkSize;
        _surface = new ColumnPump.Surface(edge * edge);

        // What a recovered column is for: it was asked for so that it could be
        // sent, and sending is this class's. Marked as well, because a column the
        // server already held raises no ChunkDirty when it is asked for again —
        // the callback is the only thing that says it is there.
        _repair = new Repair(api, columns => _dirty.MarkAll(columns));

        // What the server loaded before this existed to hear about it, which is
        // the square of chunks around spawn. Those are held for the life of the
        // server, so their one chance to be noticed has already gone by.
        _dirty.MarkUnexported(LoadedColumns(api));
    }

    /// <summary>How many chunks the fast lane may re-read whole in one beat.</summary>
    private const int ReadsPerBeat = 8;

    /// <summary>How many chunks' records are held for patching. Twelve megabytes at most.</summary>
    private const int RecentChunks = 2048;

    /// <summary>How many columns are waiting to be re-read.</summary>
    public int Waiting => _dirty.Count;

    /// <summary>How many columns the map wants and cannot read yet.</summary>
    public int Withheld => _repair.Owed;

    /// <summary>How many chunks the map holds, as far as this side knows.</summary>
    public int Mapped => _known.Count;

    /// <summary>Notes a chunk the server has marked dirty.</summary>
    public void Mark(int chunkX, int chunkZ, EnumChunkDirtyReason reason)
    {
        _dirty.Mark(chunkX, chunkZ, reason);
        _touched.Mark(chunkX, chunkZ, reason);
    }

    /// <summary>
    /// Notes a block a player placed or broke. Where the chunk's record is held,
    /// one column is read again and the record patched; otherwise the chunk is
    /// marked for a whole read like any other change.
    ///
    /// Main thread only: the game raises these there, and so is everything this
    /// touches.
    /// </summary>
    public void Touched(BlockPos at)
    {
        var edge = _api.WorldManager.ChunkSize;
        var chunk = ColumnPump.ChunkOf(at.X, at.Z, edge);

        if (!_recent.TryGet(chunk, out var record))
        {
            _dirty.Mark(chunk.X, chunk.Z, EnumChunkDirtyReason.MarkedDirty);
            return;
        }

        var entry = ColumnPump.ReadColumn(_api, at.X, at.Z, _shows, _chiselled);
        if (entry is null)
        {
            _dirty.Mark(chunk.X, chunk.Z, EnumChunkDirtyReason.MarkedDirty);
            return;
        }

        var offset = ColumnPump.OffsetOf(at.X, at.Z, edge);
        if (record.AsSpan(offset, Regions.EntryBytes).SequenceEqual(entry))
        {
            // Underground, almost always: the surface did not move, and there is
            // nothing to send. This is what keeps mining off the wire.
            return;
        }

        entry.CopyTo(record, offset);
        _patched.Add(chunk);
    }

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
    /// The fast lane: one beat, on the game thread.
    ///
    /// Everything that has to reach the service now — patched chunks, a few
    /// re-read chunks, seasons that turned — in one post. Nothing is taken from
    /// the pile while a post is still on its way: the service takes one post of
    /// this kind at a time, and a beat that arrives while one is in flight
    /// leaves everything where it is for the next.
    /// </summary>
    public void Push()
    {
        if (!_synced)
        {
            Sync();
        }

        if (_service.TerrainBusy)
        {
            return;
        }

        var edge = _api.WorldManager.ChunkSize;
        var entries = new List<Entry>();
        var sent = new List<(int, int)>();

        // Patched first: these cost nothing to read and are exactly what a
        // player is standing over.
        foreach (var chunk in _patched)
        {
            if (_recent.TryGet(chunk, out var record))
            {
                entries.Add(Entry.Of(chunk, record, SeasonOf(chunk, edge)));
                sent.Add(chunk);
            }
        }
        _dirty.Drop(_patched);
        _patched.Clear();

        // Then a bounded few re-read whole.
        var wanted = _dirty.TakeUpTo(ReadsPerBeat);
        var unloaded = new List<(int, int)>();
        foreach (var chunk in wanted)
        {
            switch (ColumnPump.TryRead(_api, chunk.Item1, chunk.Item2, _shows, _chiselled, _surface, out var record))
            {
                case Readiness.Unready:
                    // Loaded but not finished. It will be, so it waits.
                    _dirty.Restore(new[] { chunk });
                    continue;
                case Readiness.Unloaded:
                    unloaded.Add(chunk);
                    continue;
            }

            _recent.Keep(chunk, record!);
            _touched.Drop(new[] { chunk });

            if (_known.TryGetValue(chunk, out var known) && known.Crc == Crc32.Of(record!))
            {
                // Any block moving marks a chunk dirty and most of those are
                // underground, where the surface does not move. Comparing here
                // is what keeps mining off the wire.
                Settled(new[] { chunk });
                continue;
            }

            entries.Add(Entry.Of(chunk, record!, SeasonOf(chunk, edge)));
            sent.Add(chunk);
        }

        // A chunk that cannot be read at all is forgotten here and put on the
        // list of what the server has to be asked for.
        _dirty.Forget(unloaded);
        _repair.Owe(unloaded);

        foreach (var (chunk, season) in _turned)
        {
            entries.Add(Entry.Season(chunk, season));
        }
        var turned = new Dictionary<(int, int), byte>(_turned);
        _turned.Clear();

        if (entries.Count == 0)
        {
            return;
        }

        // Recorded as sent now rather than when the post lands: the next beat
        // reads this to know whether a chunk has changed, and a post that fails
        // puts everything back — see `Restore` below.
        foreach (var entry in entries)
        {
            if (entry.Record is not null)
            {
                _known[entry.Chunk] = new Known(Crc32.Of(entry.Record), entry.SeasonByte);
            }
            else if (_known.TryGetValue(entry.Chunk, out var known))
            {
                _known[entry.Chunk] = known with { Season = entry.SeasonByte };
            }
        }
        Settled(sent);

        _service.Terrain(Json(edge, entries), failed: () =>
        {
            // Back on the game thread, because everything here lives there. The
            // ground goes back on the pile to be read and sent again; the
            // seasons go back to be sent again.
            _api.Event.EnqueueMainThreadTask(() =>
            {
                foreach (var chunk in sent)
                {
                    _known.Remove(chunk);
                }
                _dirty.Restore(sent);
                foreach (var (chunk, season) in turned)
                {
                    _turned[chunk] = season;
                }
            }, "witchlight-terrain-restore");
        });
    }

    /// <summary>
    /// The slow lane: one beat, on the game thread.
    ///
    /// Where the year has reached for every chunk the map holds, and a whole
    /// re-read of every chunk the fast lane answered for by patching rather than
    /// reading. Returns what happened, for the command that asks.
    /// </summary>
    public string Export(string reason, bool force = false)
    {
        KeepWorldFacts();

        if (force)
        {
            // What an operator typing the command expects: read everything in
            // memory again, whether or not the server thinks it moved.
            _dirty.MarkAll(LoadedColumns(_api));
            _repair.Ask(Repair.PerCommand);
        }

        // Everything the server marked dirty since last time that the fast lane
        // did not read whole: read whole now, in case the change was one no
        // block event described.
        var catchAll = _touched.Take();
        _dirty.Restore(catchAll);

        var edge = _api.WorldManager.ChunkSize;
        var seasonsNow = ColumnPump.Seasons(_api, _known.Keys, edge);
        var aged = 0;
        foreach (var (chunk, season) in seasonsNow)
        {
            if (_known[chunk].Season != season)
            {
                _turned[chunk] = season;
                aged++;
            }
        }

        var message = $"{catchAll.Count} chunks queued for a whole read"
            + (aged > 0 ? $", the season turned in {aged}" : "")
            + $", {_known.Count} chunks mapped, {_dirty.Count} waiting, on {reason}{Owing()}";
        if (force || aged > 0)
        {
            _api.Logger.Notification("[witchlight] {0}", message);
        }
        return message;
    }

    /// <summary>
    /// Asks the server for a few of the columns the map wants back. On a clock of
    /// its own — see <see cref="Repair"/>.
    /// </summary>
    public void Fetch() => _repair.Ask(Repair.PerStep);

    /// <summary>
    /// Asks the service what it already holds, once, and takes that as what has
    /// been sent.
    ///
    /// Until the answer lands everything is treated as never sent, which costs a
    /// read and a post per chunk loaded in the meantime and nothing else: the
    /// service already holds those and stores nothing for a record it already
    /// has. What arrives is merged under what this side has since learned, since
    /// a chunk sent a moment ago is newer than the service's answer about it.
    /// </summary>
    private void Sync()
    {
        if (_syncing)
        {
            return;
        }
        _syncing = true;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var body = await _service.Held().ConfigureAwait(false);
            _api.Event.EnqueueMainThreadTask(() =>
            {
                _syncing = false;
                if (body is null)
                {
                    return;
                }

                try
                {
                    var read = JObject.Parse(body);
                    var held = new List<(int, int)>();
                    foreach (var one in read["Chunks"] as JArray ?? new JArray())
                    {
                        var chunk = ((int)one[0]!, (int)one[1]!);
                        if (!_known.ContainsKey(chunk))
                        {
                            _known[chunk] = new Known((uint)one[2]!, (byte)one[3]!);
                        }
                        held.Add(chunk);
                    }
                    _dirty.Seed(held);
                    _touched.Seed(held);
                    _synced = true;
                    _api.Logger.Notification("[witchlight] the map service holds {0} chunks", held.Count);
                }
                catch (Exception error)
                {
                    _api.Logger.Warning("[witchlight] could not read what the map holds: {0}", error.Message);
                }
            }, "witchlight-terrain-sync");
        });
    }

    /// <summary>
    /// Records that these columns have reached the service. Reaching it is what
    /// stops a column being re-read for merely loading again, and what settles a
    /// column the map was owed — two facts with one cause.
    /// </summary>
    private void Settled(IReadOnlyCollection<(int, int)> columns)
    {
        _dirty.Exported(columns);
        _touched.Exported(columns);
        _repair.Settled(columns);
    }

    /// <summary>The season a chunk is sent with: the one the year says, freshly.</summary>
    private byte SeasonOf((int, int) chunk, int edge) =>
        ColumnPump.Seasons(_api, new[] { chunk }, edge)[chunk];

    /// <summary>What the status command says about the terrain.</summary>
    public string Describe() =>
        $"terrain: {_known.Count} chunks on the map, {_recent.Count} held for quick reads, "
        + $"{_chiselled.Kinds} chiselled block(s) resolved to their material"
        + (_synced ? "" : ", not yet told what the map holds");

    /// <summary>What an export says about the columns still to come, or nothing.</summary>
    private string Owing() => _repair.Owed > 0 ? $", {_repair.Owed} columns still owed" : "";

    /// <summary>Every chunk column the server currently holds in memory.</summary>
    private static IEnumerable<(int, int)> LoadedColumns(ICoreServerAPI api)
    {
        foreach (var index in new List<long>(api.WorldManager.AllLoadedMapchunks.Keys))
        {
            var pos = api.WorldManager.MapChunkPosFromChunkIndex2D(index);
            yield return (pos.X, pos.Y);
        }
    }

    /// <summary>One chunk on the wire: ground that moved, or a season that turned.</summary>
    private sealed record Entry((int, int) Chunk, byte[]? Record, byte SeasonByte)
    {
        public static Entry Of((int, int) chunk, byte[] record, byte season) =>
            new(chunk, (byte[])record.Clone(), season);

        public static Entry Season((int, int) chunk, byte season) => new(chunk, null, season);
    }

    /// <summary>
    /// The post the service reads: the chunk edge, then one entry per chunk. A
    /// record travels deflated and as base64, the same bytes a region file used
    /// to hold for the chunk.
    /// </summary>
    private static string Json(int edge, IReadOnlyList<Entry> entries)
    {
        var chunks = new List<object>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Record is null)
            {
                chunks.Add(new { X = entry.Chunk.Item1, Z = entry.Chunk.Item2, Season = entry.SeasonByte });
            }
            else
            {
                chunks.Add(new
                {
                    X = entry.Chunk.Item1,
                    Z = entry.Chunk.Item2,
                    Season = entry.SeasonByte,
                    Record = Convert.ToBase64String(Packed(entry.Record)),
                });
            }
        }
        return JsonConvert.SerializeObject(new { Edge = edge, Chunks = chunks });
    }

    /// <summary>A record deflated, raw, the way the service inflates one.</summary>
    private static byte[] Packed(byte[] record)
    {
        using var packed = new MemoryStream();
        using (var packing = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            packing.Write(record, 0, record.Length);
        }
        return packed.ToArray();
    }

    /// <summary>
    /// The records most recently read, most recently used last out. A dictionary
    /// and a list rather than a library: it is thirty lines, and the one
    /// operation that matters — is this chunk's record here — is the dictionary's.
    /// </summary>
    private sealed class Recent
    {
        private readonly int _most;
        private readonly Dictionary<(int, int), LinkedListNode<((int, int) Chunk, byte[] Record)>> _held = new();
        private readonly LinkedList<((int, int) Chunk, byte[] Record)> _order = new();

        public Recent(int most) => _most = most;

        public int Count => _held.Count;

        public bool TryGet((int, int) chunk, out byte[] record)
        {
            if (_held.TryGetValue(chunk, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                record = node.Value.Record;
                return true;
            }
            record = Array.Empty<byte>();
            return false;
        }

        public void Keep((int, int) chunk, byte[] record)
        {
            if (_held.TryGetValue(chunk, out var node))
            {
                _order.Remove(node);
            }
            var kept = new LinkedListNode<((int, int), byte[])>((chunk, (byte[])record.Clone()));
            _order.AddLast(kept);
            _held[chunk] = kept;

            while (_held.Count > _most && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _held.Remove(oldest.Value.Chunk);
            }
        }
    }
}
