using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// The regions an export is about to touch, with the columns that moved read in
/// again.
/// </summary>
public sealed class ColumnSet
{
    /// <summary>Every chunk of every region loaded, in the format's record layout.</summary>
    public Dictionary<(int, int), byte[]> Records { get; init; } = new();

    /// <summary>Columns whose surface was read out of the world this time.</summary>
    public int Reread { get; init; }

    /// <summary>Of those, the ones whose surface differs from what was stored.</summary>
    public IReadOnlyCollection<(int, int)> Moved { get; init; } = new List<(int, int)>();

    /// <summary>
    /// Columns wanted but not there to read. Nothing can be read for them now, so
    /// they go to <see cref="Repair"/>, which asks the server for them.
    /// </summary>
    public IReadOnlyCollection<(int, int)> Unloaded { get; init; } = new List<(int, int)>();

    /// <summary>
    /// What moved in the columns that moved, in positions rather than in columns.
    ///
    /// A column counts as changed the moment one of its 1024 positions differs,
    /// so the count of changed columns says how much of the map was rewritten and
    /// nothing at all about why. These say why: a handful of positions with a new
    /// block on them is somebody building, and a thousand positions whose climate
    /// moved is a field this map has no business re-reading drifting under it.
    /// Only columns already on disk are counted — a column being written for the
    /// first time differs everywhere, trivially, and would drown the rest.
    /// </summary>
    public Moves Changes { get; init; }

    /// <summary>
    /// How many of those the server holds nothing at all for, against how many it
    /// holds a map chunk for and no blocks under it.
    ///
    /// Two counts rather than one, because they are two different things going
    /// wrong and only the second is the one this code was written for. A column
    /// the map asked the server to load and that comes back with no map chunk is
    /// a load that did not happen; one that comes back with a map chunk and no
    /// blocks is a load that happened and was undone. They are the same word in
    /// the log otherwise, and telling them apart is the difference between aiming
    /// a fix and guessing at one.
    /// </summary>
    public int Absent { get; init; }
    public int Emptied { get; init; }

    /// <summary>
    /// Columns in memory whose height map is not built yet. Transient, so these
    /// stay dirty and are tried again on the next export.
    /// </summary>
    public IReadOnlyCollection<(int, int)> Unready { get; init; } = new List<(int, int)>();
}

/// <summary>
/// What differed between a column as it was stored and as it reads now, counted
/// in positions.
///
/// Four fields go into a position and any one of them can rewrite a region, so
/// which one it was is the difference between a map following the world and a map
/// chasing its own tail.
/// </summary>
public readonly record struct Moves(int Positions, int Blocks, int Heights, int Climate)
{
    public static Moves operator +(Moves one, Moves two) =>
        new(one.Positions + two.Positions,
            one.Blocks + two.Blocks,
            one.Heights + two.Heights,
            one.Climate + two.Climate);

    /// <summary>What an export says about it, or nothing where nothing moved.</summary>
    public string Said() =>
        Positions == 0
            ? ""
            : $" ({Positions} positions: {Blocks} blocks, {Heights} heights, {Climate} climate)";

    /// <summary>
    /// Where one stored column and the same column read again disagree.
    ///
    /// Walked a position at a time rather than compared as a block of bytes,
    /// because the answer wanted is not whether they differ — the caller has
    /// already asked that — but in what.
    /// </summary>
    public static Moves Between(byte[] was, byte[] now)
    {
        var moves = new Moves();
        if (was.Length != now.Length)
        {
            return moves;
        }

        for (var at = 0; at + Regions.EntryBytes <= now.Length; at += Regions.EntryBytes)
        {
            var block = was[at] != now[at] || was[at + 1] != now[at + 1];
            var height = was[at + 2] != now[at + 2] || was[at + 3] != now[at + 3];
            var climate = was[at + 4] != now[at + 4] || was[at + 5] != now[at + 5];
            if (block || height || climate)
            {
                moves += new Moves(1, block ? 1 : 0, height ? 1 : 0, climate ? 1 : 0);
            }
        }
        return moves;
    }
}

/// <summary>Reads the surface out of loaded chunks.</summary>
public static class ColumnPump
{
    /// <summary>
    /// Where a chunk sits in the year, as the game reckons it, rounded to the
    /// month. Position matters: the hemispheres are in opposite seasons.
    ///
    /// Rounded because this is what decides when the map is redrawn. The year is
    /// stored as one byte, and taken at full precision that byte moves 255 times
    /// a year — every one of which rewrites every region holding a column that
    /// crossed the step, whether or not anybody has been near it. A month is the
    /// coarsest step the eye would not notice and the one the game itself counts
    /// in, and it takes that from 255 redraws a year to twelve.
    ///
    /// The byte still means what it always meant, which is why nothing stored
    /// has to change: it is a position in the year from 0 to 255, and the map
    /// service reads it as the coordinate to sample the season's colours at. Only
    /// the number of distinct values it takes has changed.
    /// </summary>
    private static byte SeasonAt(ICoreServerAPI api, int chunkX, int chunkZ, int edge, BlockPos scratch)
    {
        scratch.Set(chunkX * edge + edge / 2, 0, chunkZ * edge + edge / 2);
        var calendar = api.World.Calendar;
        var season = calendar?.GetSeasonRel(scratch) ?? 0f;
        return InMonths(season, MonthsPerYear(calendar));
    }

    /// <summary>
    /// The middle of the month a point in the year falls in, as the stored byte.
    ///
    /// The middle rather than either edge, so what is drawn is the month's own
    /// colour rather than the colour of the moment it began — the same distance
    /// from wrong at both ends of it.
    /// </summary>
    private static byte InMonths(float season, int months)
    {
        // A year is a circle and `GetSeasonRel` may hand back the point where it
        // closes, which belongs to the last month rather than to a thirteenth.
        var round = Math.Clamp(season, 0f, 0.999999f);
        var month = (int)(round * months);
        var middle = (month + 0.5) / months;
        return (byte)Math.Clamp((int)(middle * 255.0), 0, 255);
    }

    /// <summary>
    /// How many months the world's calendar divides its year into.
    ///
    /// Asked rather than assumed: a world may be configured with a longer month,
    /// and twelve written down here would quietly mean something else on one.
    /// </summary>
    private static int MonthsPerYear(IGameCalendar? calendar)
    {
        if (calendar is null || calendar.DaysPerMonth <= 0 || calendar.DaysPerYear <= 0)
        {
            return DefaultMonthsPerYear;
        }

        var months = (int)Math.Round((double)calendar.DaysPerYear / calendar.DaysPerMonth);
        return Math.Clamp(months, 1, 255);
    }

    /// <summary>What the game's own calendar divides a year into, when it cannot
    /// be asked.</summary>
    private const int DefaultMonthsPerYear = 12;

    /// <summary>
    /// Where the year has reached for each of these columns.
    ///
    /// This is arithmetic over positions the caller already holds, with no world
    /// lookup and no file read, which is what lets an idle server ask whether it
    /// has anything to write without paying for an export to find out.
    /// </summary>
    public static Dictionary<(int, int), byte> Seasons(
        ICoreServerAPI api,
        IEnumerable<(int, int)> columns,
        int edge)
    {
        var scratch = new BlockPos(0);
        var seasons = new Dictionary<(int, int), byte>();
        foreach (var (cx, cz) in columns)
        {
            seasons[(cx, cz)] = SeasonAt(api, cx, cz, edge, scratch);
        }
        return seasons;
    }

    /// <summary>
    /// The columns whose season has moved.
    ///
    /// Columns rather than the regions holding them, which is what they used to
    /// be. A season lives in a region's directory now, so a year advancing costs
    /// sixteen bytes for each chunk that crossed the step — where naming the
    /// region meant repacking every chunk in it, including the great majority
    /// whose season had not moved at all.
    ///
    /// A step is a month, see <see cref="SeasonAt"/>, so this is a dozen redraws
    /// a year rather than one every few minutes.
    /// </summary>
    public static HashSet<(int, int)> SeasonsMoved(
        IReadOnlyDictionary<(int, int), byte> before,
        IReadOnlyDictionary<(int, int), byte> after)
    {
        var moved = new HashSet<(int, int)>();
        foreach (var (column, season) in after)
        {
            if (!before.TryGetValue(column, out var was) || was != season)
            {
                moved.Add(column);
            }
        }
        return moved;
    }

    /// <summary>
    /// Re-reads the surface of the columns asked for, and says which of them
    /// differ from what the map already holds.
    ///
    /// Only those columns are read back off disk — not the regions holding them.
    /// A region is two hundred and fifty-six chunks and an export usually wants
    /// three of them, so unpacking the square to compare a corner of it was most
    /// of what an export cost.
    /// </summary>
    public static ColumnSet Gather(
        ICoreServerAPI api,
        string dir,
        IReadOnlyCollection<(int, int)> wanted,
        System.Func<int, bool> shows,
        Microblocks chiselled)
    {
        var edge = api.WorldManager.ChunkSize;
        var area = edge * edge;

        var surface = new Surface(area);

        var known = new Dictionary<(int, int), byte[]>();
        foreach (var byRegion in Grouped(wanted))
        {
            var (rx, rz) = byRegion.Key;
            foreach (var (column, stored) in
                     Regions.Read(Regions.PathOf(dir, rx, rz), edge, byRegion.Value))
            {
                known[column] = stored.Record;
            }
        }

        var unloaded = new List<(int, int)>();
        var absent = 0;
        var emptied = 0;
        var unready = new List<(int, int)>();
        var moved = new List<(int, int)>();
        var changes = new Moves();
        var reread = 0;

        foreach (var (cx, cz) in wanted)
        {
            var mapChunk = api.WorldManager.GetMapChunk(cx, cz);
            if (mapChunk is null)
            {
                // Marked dirty, then unloaded before the export ran. Holding it
                // dirty would keep the set permanently non-empty for a chunk that
                // cannot be read, and every export would walk the map to find out.
                unloaded.Add((cx, cz));
                absent++;
                continue;
            }

            if (mapChunk.RainHeightMap is null)
            {
                // Loaded but not finished. It will be, so it waits.
                unready.Add((cx, cz));
                continue;
            }

            if (!Readable(api, cx, cz, edge, mapChunk))
            {
                // The heightmap is here and the blocks are not, which reads as a
                // chunk of air rather than as an absence. Held with the ones that
                // were not there at all: both are answered by the server being
                // asked for the column again.
                unloaded.Add((cx, cz));
                emptied++;
                continue;
            }

            Read(api, cx, cz, edge, mapChunk, surface, shows, chiselled);
            var record = surface.Record();
            reread++;

            // Any block moving marks a chunk dirty and most of those are
            // underground, where the surface does not move. Comparing here is what
            // keeps mining out of the map's write path.
            if (!known.TryGetValue((cx, cz), out var stored))
            {
                known[(cx, cz)] = record;
                moved.Add((cx, cz));
            }
            else if (!record.AsSpan().SequenceEqual(stored))
            {
                // Counted only here, where there is something to compare against.
                // A column reaching disk for the first time differs everywhere and
                // would say nothing about why any of them do.
                changes += Moves.Between(stored, record);
                known[(cx, cz)] = record;
                moved.Add((cx, cz));
            }
        }

        return new ColumnSet
        {
            Records = known,
            Reread = reread,
            Moved = moved,
            Changes = changes,
            Unloaded = unloaded,
            Absent = absent,
            Emptied = emptied,
            Unready = unready,
        };
    }

    /// <summary>
    /// Whether the blocks are there to be read, and not only the record of where
    /// their surface is.
    ///
    /// A map chunk is the flat, two-dimensional record a column keeps — its
    /// heightmaps, its climate — and the server holds those long after it has let
    /// go of the blocks underneath them. So <see cref="IWorldManagerAPI.GetMapChunk"/>
    /// answering is not the blocks being loaded, and the block accessor answers
    /// zero for every position in a chunk whose blocks are gone.
    ///
    /// Nothing about that reads as a failure further down. The scan finds nothing
    /// that shows all the way to the bottom of the world, records air at the
    /// height the map chunk claims, and the renderer paints a flat brown square —
    /// one per chunk, chunk-aligned, in the middle of finished terrain, and
    /// stored, so it stays until something marks that chunk dirty again.
    ///
    /// The vertical chunk holding the highest ground in this column is the one
    /// that has to be there: it is where the scan starts and, on ordinary
    /// terrain, where it stops.
    ///
    /// **A rain height above the world is not a height.** The game leaves
    /// `ushort.MaxValue` in that map wherever rain never stopped, and this took
    /// the highest number it found — so one such position spoke for the whole
    /// chunk. It asked for the vertical chunk two thousand layers up, was told
    /// there is none, and set aside a column whose blocks were all in memory as
    /// one whose blocks had gone. Nothing about a column ever changes what its
    /// rain map says, so that column was unreadable for the life of the world: a
    /// chunk-shaped hole in the middle of finished terrain that no export, no
    /// walk back to it and no asking the server to load it could ever fill. It is
    /// what three attempts at filling those holes were actually up against.
    /// </summary>
    private static bool Readable(
        ICoreServerAPI api, int chunkX, int chunkZ, int edge, IMapChunk mapChunk)
    {
        return api.WorldManager.GetChunk(chunkX, Ceiling(api, mapChunk) / edge, chunkZ) is not null;
    }

    /// <summary>
    /// The highest real ground in one chunk, in blocks.
    ///
    /// Real, which is the whole of what this is for: what is read out of the rain
    /// map is a height where rain stopped and `ushort.MaxValue` where it never
    /// did, and only one of those is somewhere the world has a block. Anything at
    /// or above the top of the world is the second kind and is not counted.
    ///
    /// Zero where a chunk holds nothing else, which is a column open to the sky
    /// from top to bottom — the bottom of the world is as good a place as any to
    /// start looking for ground in one, and it is a chunk the column certainly
    /// has.
    /// </summary>
    private static int Ceiling(ICoreServerAPI api, IMapChunk mapChunk)
    {
        var world = api.WorldManager.MapSizeY;
        var top = 0;
        foreach (var height in mapChunk.RainHeightMap)
        {
            if (height > top && height < world)
            {
                top = height;
            }
        }
        return top;
    }

    /// <summary>
    /// Columns filed under the region that holds them.
    ///
    /// Everything that reads or writes the map does so a region at a time, since
    /// a region is a file; everything that decides what to read or write thinks in
    /// columns. This is the one place that turns the second into the first.
    /// </summary>
    private static Dictionary<(int, int), List<(int, int)>> Grouped(
        IEnumerable<(int, int)> columns)
    {
        var byRegion = new Dictionary<(int, int), List<(int, int)>>();
        foreach (var column in columns)
        {
            var region = Regions.Of(column.Item1, column.Item2);
            if (!byRegion.TryGetValue(region, out var held))
            {
                held = byRegion[region] = new List<(int, int)>();
            }
            held.Add(column);
        }
        return byRegion;
    }

    /// <summary>
    /// Writes the changes named, and says how many bytes that took.
    ///
    /// Grouped by region because a region is a file, and handed to that file as
    /// changes rather than as contents: what reaches the disk is the chunks that
    /// moved and the entries naming them, never the two hundred and fifty-odd
    /// chunks in the same square that did not.
    ///
    /// The bytes are counted and reported because they are the point. An export
    /// that says it wrote five regions says nothing about whether that was five
    /// kilobytes or five hundred, and the difference between those two was a
    /// release.
    /// </summary>
    public static long Write(
        string dir, int edge, IReadOnlyDictionary<(int, int), Regions.Update> updates)
    {
        var written = 0L;
        foreach (var (region, columns) in Grouped(updates.Keys))
        {
            var held = new Dictionary<(int, int), Regions.Update>();
            foreach (var column in columns)
            {
                held[column] = updates[column];
            }
            written += Regions.Write(dir, edge, region.Item1, region.Item2, held);
        }
        return written;
    }

    /// <summary>
    /// One chunk's surface, while it is being read and packed.
    ///
    /// Four parallel arrays were allocated by one method and threaded through two
    /// others, which is four chances to hand one a length the others do not have
    /// and no way for a reader to see that they are one thing. They are one
    /// thing: what is on top of each column of one chunk, in the order the format
    /// stores it. Reused across chunks, because an export walks hundreds of them
    /// and the buffer is the same size every time.
    /// </summary>
    private sealed class Surface
    {
        private readonly ushort[] _blocks;
        private readonly short[] _heights;
        private readonly byte[] _temperature;
        private readonly byte[] _rainfall;

        public Surface(int area)
        {
            _blocks = new ushort[area];
            _heights = new short[area];
            _temperature = new byte[area];
            _rainfall = new byte[area];
        }

        /// <summary>What is on top of one column.</summary>
        public void Set(int at, int block, int y, byte temperature, byte rainfall)
        {
            _blocks[at] = (ushort)block;
            _heights[at] = (short)y;
            _temperature[at] = temperature;
            _rainfall[at] = rainfall;
        }

        /// <summary>Packs it into the record the format stores.</summary>
        public byte[] Record()
        {
            var record = new byte[_blocks.Length * Regions.EntryBytes];
            for (var i = 0; i < _blocks.Length; i++)
            {
                var at = i * Regions.EntryBytes;
                record[at] = (byte)(_blocks[i] & 0xff);
                record[at + 1] = (byte)(_blocks[i] >> 8);
                record[at + 2] = (byte)(_heights[i] & 0xff);
                record[at + 3] = (byte)((_heights[i] >> 8) & 0xff);
                record[at + 4] = _temperature[i];
                record[at + 5] = _rainfall[i];
            }
            return record;
        }
    }

    /// <summary>
    /// Fills one chunk's columns. The rain height map already knows where the
    /// surface is, so this is one block lookup and one climate lookup per column
    /// rather than a search down from the sky.
    /// </summary>
    private static void Read(
        ICoreServerAPI api,
        int chunkX,
        int chunkZ,
        int edge,
        IMapChunk mapChunk,
        Surface surface,
        System.Func<int, bool> shows,
        Microblocks chiselled)
    {
        var accessor = api.World.BlockAccessor;
        var position = new BlockPos(0);
        var ceiling = api.WorldManager.MapSizeY - 1;

        for (var dz = 0; dz < edge; dz++)
        {
            for (var dx = 0; dx < edge; dx++)
            {
                var i = dz * edge + dx;
                var worldX = chunkX * edge + dx;
                var worldZ = chunkZ * edge + dz;

                // Held inside the world, for the reason `Ceiling` is: this is
                // where the search downward starts, and a position whose rain
                // never stopped says 65535. Started there, the search walks sixty
                // thousand positions of nothing before it reaches the sky, and
                // what it stores for a column it finds nothing in is that number
                // squeezed into a signed short — which is a surface at -1.
                var top = Math.Min((int)mapChunk.RainHeightMap[i], ceiling);
                var y = top;
                var id = 0;

                // The rain height map marks where rain stops, which is commonly
                // the air just above the ground. Step down until something is
                // actually there, rather than mapping the sky.
                //
                // Something that *shows*, not merely something that is not air.
                // A large structure stands one real block beside a run of
                // invisible placeholders, and a barrel or a door has its own; a
                // search that stopped at the first non-air block recorded one of
                // those and the map drew a speck of nothing in the middle of
                // grass. What counts as showing is the palette's to say — see
                // `PaletteExchange.Shows`.
                //
                // All the way down, rather than a few blocks. A dug shaft is a
                // column of air below where the sky still says the ground is, and
                // a search that gave up after eight of them recorded air — which
                // the map paints as ground nobody has ever explored. So every pit
                // a player dug deeper than eight blocks became a hole on the map
                // that no amount of exporting would fill.
                //
                // The depth is paid by the columns that need it and by no others:
                // ordinary ground answers on the first or second read, and only an
                // open shaft costs its own depth.
                for (; y >= 0; y--)
                {
                    position.Set(worldX, y, worldZ);
                    var here = accessor.GetBlockId(position);
                    if (here == 0)
                    {
                        continue;
                    }

                    // What it is made of, where what it is does not say. A
                    // chiselled block is a shell with its material in the block
                    // entity beside it — see `Microblocks`. Everything else
                    // answers with itself.
                    //
                    // Asked before the palette is, not after. The shell's only
                    // texture is the game's missing-texture checker, so the
                    // palette rightly says it draws nothing; asking that first
                    // would walk past every ruin wall in the world and record the
                    // ground under it. What has to show is the material.
                    var drawn = chiselled.MaterialAt(accessor, position, here, shows);
                    if (shows(drawn))
                    {
                        id = drawn;
                        break;
                    }
                }

                if (id == 0)
                {
                    // Nothing anywhere beneath the sky here, which is no ordinary
                    // column. Recorded where the sky said rather than below the
                    // world, so the height stays a number the map can draw with.
                    y = top;
                }

                position.Set(worldX, y, worldZ);

                // A column whose climate cannot be read is drawn at the middle of
                // both scales rather than left at whatever the last chunk put
                // there — the buffer is reused, so nothing here may be skipped.
                var climate = accessor.GetClimateAt(position, EnumGetClimateMode.WorldGenValues);
                surface.Set(
                    i,
                    id,
                    y,
                    climate is null ? (byte)128 : (byte)Climate.DescaleTemperature(climate.Temperature),
                    climate is null ? (byte)128 : (byte)Math.Clamp((int)(climate.Rainfall * 255f), 0, 255));
            }
        }
    }
}
