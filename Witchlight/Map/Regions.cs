using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// The map on disk: one file per square of chunks, each holding the same records
/// the single-file export held.
///
/// The square is sixteen chunks on a side, which at a chunk edge of 32 is 512
/// blocks — one of the renderer's tiles at its finest level, and the same square
/// the game calls a map region. That is the whole reason for the number: a chunk
/// that changes belongs to one file and one tile, so an export writes what moved
/// instead of the entire map, and the renderer redraws one tile instead of all.
///
/// Each file is a gzip stream. The records are mostly repeated block ids and
/// smoothly varying heights, which deflate is very good at; measured between five
/// and eight times smaller on real exports.
///
/// Format (little endian), version 4, inside the gzip:
///
///   header   "MSQR", u16 version, u16 columns per chunk edge,
///            i32 regionX, i32 regionZ, i32 chunks
///   record   i32 chunkX, i32 chunkZ, u8 season, u8 reserved, then edge*edge
///            entries of u16 blockId, i16 surfaceY, u8 temperature, u8 rainfall
///
/// The record is byte for byte what version 2 wrote, so only the container has
/// changed and a reader keeps its column parsing.
/// </summary>
public static class Regions
{
    public const int Version = 4;

    /// <summary>
    /// Chunks along a region's edge. At a chunk edge of 32 this is 512 blocks:
    /// one of the map service's tiles at its finest level, and the same square the
    /// game itself calls a map region. Region file, tile and game region are one
    /// thing, which is a whole class of off-by-one that cannot happen.
    /// </summary>
    public const int ChunksPerEdge = 16;

    /// <summary>
    /// Derived rather than written down. An arithmetic shift floors, which is what
    /// negative coordinates need, and deriving it means the edge size cannot be
    /// changed without this following.
    /// </summary>
    private static readonly int Shift = BitOperations.Log2(ChunksPerEdge);

    private const string Magic = "MSQR";

    /// <summary>Magic 4, version 2, edge 2, regionX 4, regionZ 4, chunks 4.</summary>
    private const int HeaderBytes = 20;

    /// <summary>Chunk x 4, chunk z 4, season 1, reserved 1.</summary>
    private const int RecordHeaderBytes = 10;

    /// <summary>Block id 2, surface y 2, temperature 1, rainfall 1.</summary>
    private const int EntryBytes = 6;

    /// <summary>Which region a chunk belongs to. Negative coordinates floor, as they must.</summary>
    public static (int, int) Of(int chunkX, int chunkZ)
    {
        return (chunkX >> Shift, chunkZ >> Shift);
    }

    /// <summary>Where the map lives, inside the export directory.</summary>
    public static string DirectoryIn(string exports) => Path.Combine(exports, "columns");

    public static string PathOf(string dir, int regionX, int regionZ)
    {
        return Path.Combine(dir, $"r.{regionX}.{regionZ}.msqr");
    }

    /// <summary>
    /// A region file's header, once it has been checked.
    ///
    /// Three readers were each spelling out the same six comparisons against the
    /// same magic bytes and version — and a fourth would have been a fourth chance
    /// to miss one and read a file this build cannot read. It is one question with
    /// one answer: is this a region file this build understands, and if so where
    /// do its records start.
    /// </summary>
    private readonly struct Header
    {
        private Header(byte[] data, int recordBytes)
        {
            Data = data;
            RecordBytes = recordBytes;
        }

        public byte[] Data { get; }

        /// <summary>One record, header and columns together.</summary>
        public int RecordBytes { get; }

        /// <summary>Where the first record starts.</summary>
        public static int First => HeaderBytes;

        /// <summary>
        /// Reads and checks one, or nothing where the file is not a region this
        /// build can read — which covers a missing file, a truncated one, an older
        /// format, and one written for a different chunk size.
        /// </summary>
        public static Header? Of(string path, int edge)
        {
            var data = Bytes(path);
            if (data is null
                || data.Length < HeaderBytes
                || data[0] != 'M' || data[1] != 'S' || data[2] != 'Q' || data[3] != 'R'
                || BitConverter.ToUInt16(data, 4) != Version
                || BitConverter.ToUInt16(data, 6) != edge)
            {
                return null;
            }

            return new Header(data, RecordHeaderBytes + edge * edge * EntryBytes);
        }

        /// <summary>
        /// The format version a file claims, whatever version that is. What
        /// decides whether a map on disk has to be thrown away, so it deliberately
        /// does not care whether this build could read it.
        /// </summary>
        public static int? VersionOf(string path)
        {
            var data = Bytes(path);
            return data is null
                   || data.Length < HeaderBytes
                   || data[0] != 'M' || data[1] != 'S' || data[2] != 'Q' || data[3] != 'R'
                ? null
                : BitConverter.ToUInt16(data, 4);
        }

        /// <summary>Where each record in this file begins.</summary>
        public IEnumerable<int> Records()
        {
            for (var at = First; at + RecordBytes <= Data.Length; at += RecordBytes)
            {
                yield return at;
            }
        }

        /// <summary>Which chunk one record is for.</summary>
        public (int X, int Z) ChunkAt(int at) =>
            (BitConverter.ToInt32(Data, at), BitConverter.ToInt32(Data, at + 4));

        /// <summary>Where the year had reached for one record's chunk.</summary>
        public byte SeasonAt(int at) => Data[at + 8];

        private static byte[]? Bytes(string path)
        {
            try
            {
                return File.Exists(path) ? Decompress(path) : null;
            }
            catch (Exception)
            {
                // One bad region, not a bad map.
                return null;
            }
        }
    }

    /// <summary>One chunk as it is stored: where the year is, and its columns.</summary>
    public readonly struct Stored
    {
        public Stored(byte season, byte[] record)
        {
            Season = season;
            Record = record;
        }

        public byte Season { get; }
        public byte[] Record { get; }
    }

    /// <summary>
    /// Reads one region. An unreadable file yields nothing rather than throwing:
    /// a region is a fraction of the map, and losing it costs that square rather
    /// than the run.
    /// </summary>
    public static Dictionary<(int, int), Stored> Read(string path, int edge)
    {
        var chunks = new Dictionary<(int, int), Stored>();
        if (Header.Of(path, edge) is not { } header)
        {
            return chunks;
        }

        var size = edge * edge * EntryBytes;
        foreach (var at in header.Records())
        {
            var record = new byte[size];
            Array.Copy(header.Data, at + RecordHeaderBytes, record, 0, size);
            chunks[header.ChunkAt(at)] = new Stored(header.SeasonAt(at), record);
        }

        return chunks;
    }

    /// <summary>
    /// Writes one region, whole, beside itself and then into place so a reader
    /// never sees half of it.
    /// </summary>
    public static void Write(
        string dir,
        int edge,
        int regionX,
        int regionZ,
        IReadOnlyDictionary<(int, int), Stored> chunks)
    {
        Disk.Replace(PathOf(dir, regionX, regionZ), into =>
        {
            using var file = File.Create(into);
            using var gzip = new GZipStream(file, CompressionLevel.Optimal);
            using var writer = new BinaryWriter(gzip);

            foreach (var c in Magic)
            {
                writer.Write((byte)c);
            }
            writer.Write((ushort)Version);
            writer.Write((ushort)edge);
            writer.Write(regionX);
            writer.Write(regionZ);
            writer.Write(chunks.Count);

            foreach (var ((cx, cz), stored) in chunks)
            {
                writer.Write(cx);
                writer.Write(cz);
                writer.Write(stored.Season);
                writer.Write((byte)0);
                writer.Write(stored.Record);
            }
        });
    }

    /// <summary>Every region file present, by its coordinates.</summary>
    public static Dictionary<(int, int), string> All(string dir)
    {
        var found = new Dictionary<(int, int), string>();
        if (!Directory.Exists(dir))
        {
            return found;
        }

        foreach (var path in Directory.EnumerateFiles(dir, "r.*.msqr"))
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('.');
            if (parts.Length == 3
                && int.TryParse(parts[1], out var rx)
                && int.TryParse(parts[2], out var rz))
            {
                found[(rx, rz)] = path;
            }
        }

        return found;
    }

    /// <summary>
    /// Where the year had reached for every chunk on disk, without reading the
    /// columns back. This is what a starting run needs to know which chunks are
    /// already mapped and whether the season has moved since.
    /// </summary>
    public static Dictionary<(int, int), byte> Index(string dir, int edge)
    {
        var index = new Dictionary<(int, int), byte>();

        foreach (var (_, path) in All(dir))
        {
            if (Header.Of(path, edge) is not { } header)
            {
                continue;
            }

            foreach (var at in header.Records())
            {
                index[header.ChunkAt(at)] = header.SeasonAt(at);
            }
        }

        return index;
    }

    /// <summary>
    /// Throws away a map this build cannot read, so that one can be built again.
    ///
    /// Witchlight is alpha and this format still moves. Reading an older one and
    /// upgrading it means carrying a reader for every shape the file has ever
    /// had, permanently, for the sake of maps that are days old. The map rebuilds
    /// itself from the world as players move through it, so starting again costs
    /// a walk and keeps the code to one format.
    ///
    /// Returns true when something was thrown away.
    /// </summary>
    public static bool ResetIfUnreadable(string exports, int edge, ILogger? log = null)
    {
        var dir = DirectoryIn(exports);
        var reason = Unreadable(exports, dir, edge);
        if (reason is null)
        {
            return false;
        }

        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            foreach (var stale in Directory.EnumerateFiles(exports, "columns.msq*"))
            {
                File.Delete(stale);
            }
        }
        catch (Exception error)
        {
            log?.Error("[witchlight] could not clear the old map: {0}", error);
            return false;
        }

        log?.Notification(
            "[witchlight] {0} — the map has been cleared and will rebuild as players explore",
            reason);
        return true;
    }

    /// <summary>Why the map on disk cannot be used, or null when it can.</summary>
    private static string? Unreadable(string exports, string dir, int edge)
    {
        if (File.Exists(Path.Combine(exports, "columns.msqc")))
        {
            return "the map on disk is a single-file export from an older build";
        }

        foreach (var (_, path) in All(dir))
        {
            var version = Header.VersionOf(path);
            if (version != Version)
            {
                return version is null
                    ? $"{Path.GetFileName(path)} could not be read"
                    : $"the map on disk is format version {version}, and this build writes {Version}";
            }
        }

        return null;
    }

    private static byte[] Decompress(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }
}
