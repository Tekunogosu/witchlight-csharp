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
    /// Columns wanted but no longer in memory. Nothing can be read for them now,
    /// and they are not owed a retry either: the server raises ChunkDirty again
    /// when it loads one, so the way back is to forget having exported it.
    /// </summary>
    public IReadOnlyCollection<(int, int)> Unloaded { get; init; } = new List<(int, int)>();

    /// <summary>
    /// Columns in memory whose height map is not built yet. Transient, so these
    /// stay dirty and are tried again on the next export.
    /// </summary>
    public IReadOnlyCollection<(int, int)> Unready { get; init; } = new List<(int, int)>();
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
    /// Which regions hold a column whose season has moved.
    ///
    /// The season is stored per chunk, so a year that advances a step rewrites
    /// whatever holds those chunks whether a block moved or not. A step is a
    /// month — see `SeasonAt` — so this is a dozen redraws a year rather than one
    /// every few minutes, and still worth asking about rather than assuming.
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
                moved.Add(Regions.Of(column.Item1, column.Item2));
            }
        }
        return moved;
    }

    /// <summary>
    /// Loads the regions named, and re-reads the surface of the columns asked for
    /// over the top of them.
    ///
    /// Only the regions that are about to be written are read. The rest of the map
    /// is never touched, which is the whole point of splitting it up: a chunk
    /// changing costs the square it sits in rather than everything anyone has ever
    /// explored.
    /// </summary>
    public static ColumnSet Gather(
        ICoreServerAPI api,
        string dir,
        IReadOnlyCollection<(int, int)> wanted,
        IReadOnlyCollection<(int, int)> regions,
        System.Func<int, bool> shows,
        Microblocks chiselled)
    {
        var edge = api.WorldManager.ChunkSize;
        var area = edge * edge;

        var surface = new Surface(area);

        var known = new Dictionary<(int, int), byte[]>();
        foreach (var (rx, rz) in regions)
        {
            foreach (var (column, stored) in Regions.Read(Regions.PathOf(dir, rx, rz), edge))
            {
                known[column] = stored.Record;
            }
        }

        var unloaded = new List<(int, int)>();
        var unready = new List<(int, int)>();
        var moved = new List<(int, int)>();
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
                continue;
            }

            if (mapChunk.RainHeightMap is null)
            {
                // Loaded but not finished. It will be, so it waits.
                unready.Add((cx, cz));
                continue;
            }

            Read(api, cx, cz, edge, mapChunk, surface, shows, chiselled);
            var record = surface.Record();
            reread++;

            // Any block moving marks a chunk dirty and most of those are
            // underground, where the surface does not move. Comparing here is what
            // keeps mining out of the map's write path.
            if (!known.TryGetValue((cx, cz), out var stored) || !record.AsSpan().SequenceEqual(stored))
            {
                known[(cx, cz)] = record;
                moved.Add((cx, cz));
            }
        }

        return new ColumnSet
        {
            Records = known,
            Reread = reread,
            Moved = moved,
            Unloaded = unloaded,
            Unready = unready,
        };
    }

    /// <summary>
    /// Writes the regions named, and returns where the year had reached for every
    /// chunk in them so the next export can tell whether it has moved.
    /// </summary>
    public static Dictionary<(int, int), byte> Write(
        ICoreServerAPI api,
        string dir,
        ColumnSet set,
        IReadOnlyCollection<(int, int)> regions)
    {
        var edge = api.WorldManager.ChunkSize;
        var scratch = new BlockPos(0);
        var wanted = new HashSet<(int, int)>(regions);

        var byRegion = new Dictionary<(int, int), Dictionary<(int, int), Regions.Stored>>();
        var seasons = new Dictionary<(int, int), byte>();

        foreach (var (column, record) in set.Records)
        {
            var region = Regions.Of(column.Item1, column.Item2);
            if (!wanted.Contains(region))
            {
                continue;
            }

            if (!byRegion.TryGetValue(region, out var held))
            {
                held = new Dictionary<(int, int), Regions.Stored>();
                byRegion[region] = held;
            }

            var season = SeasonAt(api, column.Item1, column.Item2, edge, scratch);
            seasons[column] = season;
            held[column] = new Regions.Stored(season, record);
        }

        foreach (var ((rx, rz), held) in byRegion)
        {
            Regions.Write(dir, edge, rx, rz, held);
        }

        return seasons;
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

        for (var dz = 0; dz < edge; dz++)
        {
            for (var dx = 0; dx < edge; dx++)
            {
                var i = dz * edge + dx;
                var worldX = chunkX * edge + dx;
                var worldZ = chunkZ * edge + dz;

                var top = (int)mapChunk.RainHeightMap[i];
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
