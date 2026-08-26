using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Mapstique;

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
    /// <summary>How far below the rain height to look for a real block.</summary>
    private const int MaxDrop = 8;

    /// <summary>
    /// Where a chunk sits in the year, as the game reckons it. Position matters:
    /// the hemispheres are in opposite seasons.
    /// </summary>
    private static byte SeasonAt(ICoreServerAPI api, int chunkX, int chunkZ, int edge, BlockPos scratch)
    {
        scratch.Set(chunkX * edge + edge / 2, 0, chunkZ * edge + edge / 2);
        var season = api.World.Calendar?.GetSeasonRel(scratch) ?? 0f;
        return (byte)Math.Clamp((int)(season * 255f), 0, 255);
    }

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
    /// whatever holds those chunks whether a block moved or not. It advances about
    /// three times an hour on the default calendar, against a tick every thirty
    /// seconds, so it is worth asking rather than assuming.
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
        IReadOnlyCollection<(int, int)> regions)
    {
        var edge = api.WorldManager.ChunkSize;
        var area = edge * edge;

        var blockIds = new ushort[area];
        var heights = new short[area];
        var temperature = new byte[area];
        var rainfall = new byte[area];

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

            Read(api, cx, cz, edge, mapChunk, blockIds, heights, temperature, rainfall);
            var record = Encode(area, blockIds, heights, temperature, rainfall);
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

    /// <summary>Packs one chunk's columns into the record the format stores.</summary>
    private static byte[] Encode(
        int area,
        ushort[] blockIds,
        short[] heights,
        byte[] temperature,
        byte[] rainfall)
    {
        var record = new byte[area * 6];
        for (var i = 0; i < area; i++)
        {
            var at = i * 6;
            record[at] = (byte)(blockIds[i] & 0xff);
            record[at + 1] = (byte)(blockIds[i] >> 8);
            record[at + 2] = (byte)(heights[i] & 0xff);
            record[at + 3] = (byte)((heights[i] >> 8) & 0xff);
            record[at + 4] = temperature[i];
            record[at + 5] = rainfall[i];
        }
        return record;
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
        ushort[] blockIds,
        short[] heights,
        byte[] temperature,
        byte[] rainfall)
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

                var y = (int)mapChunk.RainHeightMap[i];
                var id = 0;

                // The rain height map marks where rain stops, which is commonly
                // the air just above the ground. Step down until something is
                // actually there, rather than mapping the sky.
                for (var drop = 0; drop <= MaxDrop && y - drop >= 0; drop++)
                {
                    position.Set(worldX, y - drop, worldZ);
                    id = accessor.GetBlockId(position);
                    if (id != 0)
                    {
                        y -= drop;
                        break;
                    }
                }

                position.Set(worldX, y, worldZ);
                blockIds[i] = (ushort)id;
                heights[i] = (short)y;

                var climate = accessor.GetClimateAt(position, EnumGetClimateMode.WorldGenValues);
                if (climate is null)
                {
                    temperature[i] = 128;
                    rainfall[i] = 128;
                    continue;
                }

                temperature[i] = (byte)Climate.DescaleTemperature(climate.Temperature);
                rainfall[i] = (byte)Math.Clamp((int)(climate.Rainfall * 255f), 0, 255);
            }
        }
    }
}
