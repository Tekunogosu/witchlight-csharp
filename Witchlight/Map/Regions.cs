using System.Numerics;

namespace Witchlight;

/// <summary>
/// The geometry the map is filed by, and the checksum the two halves agree on.
///
/// A region is sixteen chunks on a side, which at a chunk edge of 32 is 512
/// blocks: one of the map service's tiles at its finest level, and the same
/// square the game itself calls a map region. Tile and game region are one
/// thing, which is a whole class of off-by-one that cannot happen.
///
/// This used to be a file format as well — one region file per square, written
/// beside the map for the service to read back. Terrain now goes to the service
/// over its API channel and is kept in the service's own database, so what is
/// left here is the arithmetic both halves still share and nothing about disk.
/// </summary>
public static class Regions
{
    /// <summary>Chunks along a region's edge.</summary>
    public const int ChunksPerEdge = 16;

    /// <summary>
    /// Derived rather than written down. An arithmetic shift floors, which is what
    /// negative coordinates need, and deriving it means the edge size cannot be
    /// changed without this following.
    /// </summary>
    private static readonly int Shift = BitOperations.Log2(ChunksPerEdge);

    /// <summary>
    /// Block id 2, surface y 2, temperature 1, rainfall 1: the six bytes a record
    /// holds for each column of a chunk, in the order the service reads them.
    /// </summary>
    public const int EntryBytes = 6;

    /// <summary>Which region a chunk belongs to. Negative coordinates floor, as they must.</summary>
    public static (int, int) Of(int chunkX, int chunkZ)
    {
        return (chunkX >> Shift, chunkZ >> Shift);
    }
}

/// <summary>
/// CRC-32, the one deflate itself uses.
///
/// Written out because .NET has no CRC-32 in the box and the map service keeps
/// this checksum beside every record with the one its compression library
/// already carries. The two have to be the same number — it is how the mod
/// tells a chunk loading again from one that changed, without holding a copy of
/// the ground — so this is the same polynomial and not a hash of somebody's
/// choosing.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint at = 0; at < 256; at++)
        {
            var value = at;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }
            table[at] = value;
        }
        return table;
    }

    public static uint Of(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var one in bytes)
        {
            crc = Table[(crc ^ one) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
