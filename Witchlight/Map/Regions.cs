using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// The map on disk: one file per square of chunks, each chunk stored and written
/// on its own.
///
/// The square is sixteen chunks on a side, which at a chunk edge of 32 is 512
/// blocks — one of the renderer's tiles at its finest level, and the same square
/// the game calls a map region. That is the whole reason for the number: a chunk
/// that changes belongs to one file and one tile, so an export writes what moved
/// instead of the entire map, and the renderer redraws one tile instead of all.
///
/// **Each chunk is compressed on its own, behind a directory of fixed size.**
/// Version 4 was one gzip stream over the whole region, which meant a chunk could
/// not be written without rewriting the other two hundred and fifty-five: a
/// single column moving cost a quarter of a megabyte, and a server with people on
/// it wrote half a gigabyte an hour to keep a map that had barely changed. The
/// directory is what buys that back — a chunk's bytes are appended, its entry is
/// rewritten, and nothing else in the file is touched.
///
/// Format (little endian), version 5. Nothing outside a payload is compressed:
///
/// <code>
///   0  magic     "MSQR"
///   4  version   u16 = 5
///   6  edge      u16   columns along a chunk's edge
///   8  regionX   i32
///  12  regionZ   i32
///  16  slots     u16   chunks in a region, which is ChunksPerEdge squared
///  18  reserved  u16
///  20  directory slots entries of 16 bytes, in slot order:
///                  0  offset   u32  from the start of the file; 0 is empty
///                  4  length   u32  bytes of the deflate stream
///                  8  checksum u32  CRC-32 of those bytes
///                 12  season   u8
///                 13  flags    u8   bit 0: a column here is stored as air
///                 14  reserved u16
///      payloads, each a raw deflate stream of edge*edge entries of
///      u16 blockId, i16 surfaceY, u8 temperature, u8 rainfall
/// </code>
///
/// A chunk's slot is its position in the region — <c>dz * ChunksPerEdge + dx</c>
/// — so a record carries no coordinates of its own and cannot disagree with where
/// it is filed.
///
/// **Payloads are appended and never overwritten**, which is what makes a torn
/// write survivable: a run that dies mid-append leaves a directory that still
/// points at the bytes it always pointed at, and the half-written tail is
/// unreferenced. The cost is space, so a file is rewritten whole once the
/// unreferenced bytes outweigh the live ones — see <see cref="Wasteful"/>.
///
/// The checksum is the other half of that. A slot whose bytes do not match it is
/// read as a chunk the map does not have, which <see cref="Repair"/> then asks
/// the server for and writes again. A map damaged by a power cut heals rather
/// than needing to be thrown away.
/// </summary>
public static class Regions
{
    public const int Version = 5;

    /// <summary>
    /// Chunks along a region's edge. At a chunk edge of 32 this is 512 blocks:
    /// one of the map service's tiles at its finest level, and the same square the
    /// game itself calls a map region. Region file, tile and game region are one
    /// thing, which is a whole class of off-by-one that cannot happen.
    /// </summary>
    public const int ChunksPerEdge = 16;

    /// <summary>How many chunks a region holds, and so how many slots it has.</summary>
    public const int Slots = ChunksPerEdge * ChunksPerEdge;

    /// <summary>
    /// Derived rather than written down. An arithmetic shift floors, which is what
    /// negative coordinates need, and deriving it means the edge size cannot be
    /// changed without this following.
    /// </summary>
    private static readonly int Shift = BitOperations.Log2(ChunksPerEdge);

    private const string Magic = "MSQR";

    /// <summary>Magic 4, version 2, edge 2, regionX 4, regionZ 4, slots 2, spare 2.</summary>
    private const int HeaderBytes = 20;

    /// <summary>Offset 4, length 4, checksum 4, season 1, flags 1, spare 2.</summary>
    private const int SlotBytes = 16;

    /// <summary>Where the first payload can start, which is past the directory.</summary>
    private const int PayloadsFrom = HeaderBytes + Slots * SlotBytes;

    /// <summary>Set on a chunk holding a column stored as air, which is a reading
    ///  that failed rather than a fact about the world.</summary>
    private const byte HoledFlag = 1;

    /// <summary>
    /// Block id 2, surface y 2, temperature 1, rainfall 1.
    ///
    /// Public because the side that packs a record is not the side that reads
    /// one, and it was spelled as a bare 6 over there — twice, in the two loops
    /// that have to agree with this or write a file nothing can parse.
    /// </summary>
    public const int EntryBytes = 6;

    /// <summary>Which region a chunk belongs to. Negative coordinates floor, as they must.</summary>
    public static (int, int) Of(int chunkX, int chunkZ)
    {
        return (chunkX >> Shift, chunkZ >> Shift);
    }

    /// <summary>
    /// Which slot in its region a chunk is filed under.
    ///
    /// A mask rather than a modulo, because a modulo of a negative number is
    /// negative and half the world has those.
    /// </summary>
    public static int SlotOf(int chunkX, int chunkZ) =>
        (chunkZ & (ChunksPerEdge - 1)) * ChunksPerEdge + (chunkX & (ChunksPerEdge - 1));

    /// <summary>Which chunk a slot in this region belongs to.</summary>
    public static (int X, int Z) ChunkAt(int regionX, int regionZ, int slot) =>
        (regionX * ChunksPerEdge + slot % ChunksPerEdge,
         regionZ * ChunksPerEdge + slot / ChunksPerEdge);

    /// <summary>Where the map lives, inside the export directory.</summary>
    public static string DirectoryIn(string exports) => Path.Combine(exports, "columns");

    public static string PathOf(string dir, int regionX, int regionZ)
    {
        return Path.Combine(dir, $"r.{regionX}.{regionZ}.msqr");
    }

    /// <summary>One chunk as it is stored: where the year is, and its columns.</summary>
    public readonly struct Stored(byte season, byte[] record)
    {
        public byte Season { get; } = season;
        public byte[] Record { get; } = record;
    }

    /// <summary>
    /// One change to write into a region.
    ///
    /// <see cref="Record"/> is null where only the year moved. A season lives in
    /// the directory rather than in the compressed bytes, so a world turning from
    /// summer to autumn is sixteen bytes per chunk and not a repacking of the
    /// whole map — which is what it cost when the season was stored inside a
    /// record and the record inside one stream per region.
    /// </summary>
    public readonly record struct Update(byte Season, byte[]? Record);

    /// <summary>
    /// One region's directory, in memory: what is in each slot and where.
    ///
    /// Read without touching a payload, which is what lets the start-up survey ask
    /// where the year had reached for every chunk on the map, and which of them
    /// hold a hole, for the price of the header of each file.
    /// </summary>
    private sealed class Directory
    {
        public uint[] Offset { get; } = new uint[Slots];
        public uint[] Length { get; } = new uint[Slots];
        public uint[] Checksum { get; } = new uint[Slots];
        public byte[] Season { get; } = new byte[Slots];
        public byte[] Flags { get; } = new byte[Slots];

        /// <summary>Where the file ends, which is where the next payload goes.</summary>
        public long End { get; set; } = PayloadsFrom;

        public int RegionX { get; init; }
        public int RegionZ { get; init; }
        public int Edge { get; init; }

        public bool Holds(int slot) => Offset[slot] != 0 && Length[slot] != 0;

        /// <summary>Bytes of payload that something still points at.</summary>
        public long Live()
        {
            long live = 0;
            for (var slot = 0; slot < Slots; slot++)
            {
                if (Holds(slot))
                {
                    live += Length[slot];
                }
            }
            return live;
        }

        /// <summary>
        /// Reads one, or nothing where the file is not a region this build can
        /// read — which covers a missing file, a truncated one, an older format,
        /// and one written for a different chunk size.
        /// </summary>
        public static Directory? Of(string path, int edge)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var file = File.OpenRead(path);
                var head = new byte[PayloadsFrom];
                if (file.Read(head, 0, head.Length) != head.Length)
                {
                    return null;
                }

                for (var at = 0; at < Magic.Length; at++)
                {
                    if (head[at] != Magic[at])
                    {
                        return null;
                    }
                }

                if (BitConverter.ToUInt16(head, 4) != Version
                    || BitConverter.ToUInt16(head, 6) != edge
                    || BitConverter.ToUInt16(head, 16) != Slots)
                {
                    return null;
                }

                var directory = new Directory
                {
                    RegionX = BitConverter.ToInt32(head, 8),
                    RegionZ = BitConverter.ToInt32(head, 12),
                    Edge = edge,
                    End = file.Length,
                };

                for (var slot = 0; slot < Slots; slot++)
                {
                    var at = HeaderBytes + slot * SlotBytes;
                    directory.Offset[slot] = BitConverter.ToUInt32(head, at);
                    directory.Length[slot] = BitConverter.ToUInt32(head, at + 4);
                    directory.Checksum[slot] = BitConverter.ToUInt32(head, at + 8);
                    directory.Season[slot] = head[at + 12];
                    directory.Flags[slot] = head[at + 13];
                }

                return directory;
            }
            catch (Exception)
            {
                // One bad region, not a bad map.
                return null;
            }
        }

        /// <summary>A region nothing has been written into yet.</summary>
        public static Directory Fresh(int regionX, int regionZ, int edge) =>
            new() { RegionX = regionX, RegionZ = regionZ, Edge = edge, End = PayloadsFrom };

        /// <summary>The header and the whole directory, as the file opens.</summary>
        public byte[] Head()
        {
            var head = new byte[PayloadsFrom];
            for (var at = 0; at < Magic.Length; at++)
            {
                head[at] = (byte)Magic[at];
            }
            BitConverter.GetBytes((ushort)Version).CopyTo(head, 4);
            BitConverter.GetBytes((ushort)Edge).CopyTo(head, 6);
            BitConverter.GetBytes(RegionX).CopyTo(head, 8);
            BitConverter.GetBytes(RegionZ).CopyTo(head, 12);
            BitConverter.GetBytes((ushort)Slots).CopyTo(head, 16);

            for (var slot = 0; slot < Slots; slot++)
            {
                Entry(slot).CopyTo(head, HeaderBytes + slot * SlotBytes);
            }
            return head;
        }

        /// <summary>One slot's sixteen bytes, which is the whole of what a write
        ///  to an existing file has to put back.</summary>
        public byte[] Entry(int slot)
        {
            var entry = new byte[SlotBytes];
            BitConverter.GetBytes(Offset[slot]).CopyTo(entry, 0);
            BitConverter.GetBytes(Length[slot]).CopyTo(entry, 4);
            BitConverter.GetBytes(Checksum[slot]).CopyTo(entry, 8);
            entry[12] = Season[slot];
            entry[13] = Flags[slot];
            return entry;
        }
    }

    /// <summary>
    /// Reads the chunks named out of one region, or as many of them as it holds.
    ///
    /// Only what is asked for is decompressed. Reading a region to compare three
    /// columns against it used to unpack all two hundred and fifty-six, every
    /// export, which on a busy server was most of what an export cost.
    ///
    /// An unreadable file yields nothing rather than throwing: a region is a
    /// fraction of the map, and losing it costs that square rather than the run.
    /// </summary>
    public static Dictionary<(int, int), Stored> Read(
        string path, int edge, IEnumerable<(int, int)> wanted)
    {
        var chunks = new Dictionary<(int, int), Stored>();
        if (Directory.Of(path, edge) is not { } directory)
        {
            return chunks;
        }

        try
        {
            using var file = File.OpenRead(path);
            foreach (var (cx, cz) in wanted)
            {
                if (Of(cx, cz) != (directory.RegionX, directory.RegionZ))
                {
                    continue;
                }

                var slot = SlotOf(cx, cz);
                if (Payload(file, directory, slot, edge) is { } record)
                {
                    chunks[(cx, cz)] = new Stored(directory.Season[slot], record);
                }
            }
        }
        catch (Exception)
        {
            // One bad region, not a bad map.
        }

        return chunks;
    }

    /// <summary>
    /// One slot's record, unpacked, or null where the slot is empty or its bytes
    /// do not answer to the checksum written beside them.
    ///
    /// A slot that fails its checksum is a chunk this build says the map does not
    /// have. That is the honest answer — the bytes are a half-written payload from
    /// a run that died — and it is also the useful one, because a chunk the map
    /// does not have is one <see cref="Repair"/> already knows how to get back.
    /// </summary>
    private static byte[]? Payload(FileStream file, Directory directory, int slot, int edge)
    {
        if (!directory.Holds(slot))
        {
            return null;
        }

        var packed = new byte[directory.Length[slot]];
        file.Seek(directory.Offset[slot], SeekOrigin.Begin);
        if (file.Read(packed, 0, packed.Length) != packed.Length
            || Crc32.Of(packed) != directory.Checksum[slot])
        {
            return null;
        }

        var record = new byte[edge * edge * EntryBytes];
        using var packing = new MemoryStream(packed);
        using var inflating = new DeflateStream(packing, CompressionMode.Decompress);
        var read = 0;
        while (read < record.Length)
        {
            var got = inflating.Read(record, read, record.Length - read);
            if (got <= 0)
            {
                return null;
            }
            read += got;
        }
        return record;
    }

    /// <summary>
    /// Writes the changes named into one region, and nothing else in it.
    ///
    /// A payload is appended and its entry rewritten, in that order: a run that
    /// dies between them leaves a directory pointing where it always pointed, and
    /// the bytes at the end of the file referenced by nothing. The entries go last
    /// and together for the same reason.
    ///
    /// A file this build cannot read is written again from nothing rather than
    /// patched, which is what a region from an older format, or one truncated to
    /// less than its directory, comes to.
    /// </summary>
    public static long Write(
        string dir, int edge, int regionX, int regionZ,
        IReadOnlyDictionary<(int, int), Update> updates)
    {
        if (updates.Count == 0)
        {
            return 0;
        }

        System.IO.Directory.CreateDirectory(dir);
        var path = PathOf(dir, regionX, regionZ);
        var directory = Directory.Of(path, edge) ?? Directory.Fresh(regionX, regionZ, edge);
        var fresh = !File.Exists(path) || Directory.Of(path, edge) is null;

        using var file = new FileStream(
            path, fresh ? FileMode.Create : FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        var written = 0L;
        if (fresh)
        {
            file.SetLength(0);
            file.Write(directory.Head(), 0, PayloadsFrom);
            directory.End = PayloadsFrom;
            written += PayloadsFrom;
        }

        var touched = new List<int>(updates.Count);
        foreach (var ((cx, cz), update) in updates)
        {
            if (Of(cx, cz) != (regionX, regionZ))
            {
                continue;
            }

            var slot = SlotOf(cx, cz);
            directory.Season[slot] = update.Season;
            touched.Add(slot);

            if (update.Record is not { } record)
            {
                // The year moved and the ground did not. Nothing is appended: the
                // season lives in the entry, which is rewritten below.
                continue;
            }

            var packed = Deflate(record);
            file.Seek(directory.End, SeekOrigin.Begin);
            file.Write(packed, 0, packed.Length);
            directory.Offset[slot] = (uint)directory.End;
            directory.Length[slot] = (uint)packed.Length;
            directory.Checksum[slot] = Crc32.Of(packed);
            directory.Flags[slot] = Holed(record) ? HoledFlag : (byte)0;
            directory.End += packed.Length;
            written += packed.Length;
        }

        // After the payloads, so that a directory on disk never points at bytes
        // that are not there yet.
        file.Flush();
        foreach (var slot in touched)
        {
            file.Seek(HeaderBytes + slot * SlotBytes, SeekOrigin.Begin);
            file.Write(directory.Entry(slot), 0, SlotBytes);
            written += SlotBytes;
        }
        file.Flush();

        if (Wasteful(directory, file.Length))
        {
            var live = directory.Live();
            file.Dispose();
            Compact(path, edge);
            written += live + PayloadsFrom;
        }

        return written;
    }

    /// <summary>
    /// Whether a region file is carrying more bytes nothing points at than bytes
    /// something does.
    ///
    /// Appending rather than overwriting is what makes a half-written payload
    /// harmless, and the price of it is the space the old one still takes. A file
    /// is left to grow to twice what it holds before it is packed down again,
    /// which on a chunk rewritten over and over is one full write per doubling
    /// rather than one per change — and one full write is what every change used
    /// to cost.
    /// </summary>
    private static bool Wasteful(Directory directory, long size) =>
        size - PayloadsFrom > 2 * directory.Live() + Slack;

    /// <summary>
    /// How much unreferenced payload a region is allowed before its size is worth
    /// a pass, whatever the proportion says.
    ///
    /// A region holding one small chunk would otherwise be packed down on almost
    /// every write, which is the cost this whole format exists to avoid.
    /// </summary>
    private const long Slack = 64 * 1024;

    /// <summary>
    /// Writes a region again with only what it still holds, beside itself and then
    /// into place, so a reader sees the old bytes or the new ones and never half
    /// of either.
    /// </summary>
    private static void Compact(string path, int edge)
    {
        if (Directory.Of(path, edge) is not { } directory)
        {
            return;
        }

        var records = new Dictionary<int, byte[]>();
        try
        {
            using var file = File.OpenRead(path);
            for (var slot = 0; slot < Slots; slot++)
            {
                var packed = new byte[directory.Length[slot]];
                if (!directory.Holds(slot))
                {
                    continue;
                }
                file.Seek(directory.Offset[slot], SeekOrigin.Begin);
                if (file.Read(packed, 0, packed.Length) == packed.Length
                    && Crc32.Of(packed) == directory.Checksum[slot])
                {
                    records[slot] = packed;
                }
            }
        }
        catch (Exception)
        {
            // Nothing was moved, so the file is still whatever it was.
            return;
        }

        var packedDown = Directory.Fresh(directory.RegionX, directory.RegionZ, edge);
        Disk.Replace(path, into =>
        {
            using var file = File.Create(into);
            file.Seek(PayloadsFrom, SeekOrigin.Begin);
            foreach (var (slot, packed) in records)
            {
                packedDown.Offset[slot] = (uint)file.Position;
                packedDown.Length[slot] = (uint)packed.Length;
                packedDown.Checksum[slot] = directory.Checksum[slot];
                packedDown.Season[slot] = directory.Season[slot];
                packedDown.Flags[slot] = directory.Flags[slot];
                file.Write(packed, 0, packed.Length);
            }

            file.Seek(0, SeekOrigin.Begin);
            file.Write(packedDown.Head(), 0, PayloadsFrom);
        });
    }

    /// <summary>Whether any column of a record was stored as air, which is a
    ///  reading that failed rather than a fact about the world.</summary>
    private static bool Holed(byte[] record)
    {
        for (var at = 0; at + EntryBytes <= record.Length; at += EntryBytes)
        {
            if (record[at] == 0 && record[at + 1] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static byte[] Deflate(byte[] record)
    {
        using var packed = new MemoryStream();
        using (var packing = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            packing.Write(record, 0, record.Length);
        }
        return packed.ToArray();
    }

    /// <summary>Every region file present, by its coordinates.</summary>
    public static Dictionary<(int, int), string> All(string dir)
    {
        var found = new Dictionary<(int, int), string>();
        if (!System.IO.Directory.Exists(dir))
        {
            return found;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "r.*.msqr"))
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
    /// What one pass over the map on disk found.
    /// </summary>
    /// <param name="Seasons">Where the year had reached for every chunk stored.</param>
    /// <param name="Holed">
    /// The chunks holding a column stored as air — a surface that was read wrong
    /// and is worth reading again.
    /// </param>
    /// <param name="Gaps">
    /// The chunks the map is missing outright, in the middle of what it has. See
    /// <see cref="Enclosed"/>.
    /// </param>
    public sealed record Survey(
        Dictionary<(int, int), byte> Seasons,
        HashSet<(int, int)> Holed,
        HashSet<(int, int)> Gaps);

    /// <summary>
    /// The chunks a map on disk does not hold and is nonetheless surrounded by.
    ///
    /// The map's edge is every chunk nobody has walked to, and it is not missing
    /// anything: asking the server for it would have it generate the world
    /// outward for as long as the map ran. A chunk with mapped terrain on all four
    /// sides is the other thing — somewhere a player has been, that the export
    /// could not read when it was there, and that nothing will read again unless
    /// it is asked for. Enclosure is the whole of the difference, and it is why
    /// this is four lookups per stored chunk rather than a flood fill.
    /// </summary>
    public static HashSet<(int, int)> Enclosed(IReadOnlyCollection<(int, int)> stored)
    {
        var have = stored as HashSet<(int, int)> ?? new HashSet<(int, int)>(stored);
        var gaps = new HashSet<(int, int)>();

        foreach (var (cx, cz) in have)
        {
            // Only the four around each stored chunk are ever candidates: a gap
            // enclosed by stored chunks is, by definition, beside one.
            foreach (var beside in new[] { (cx + 1, cz), (cx - 1, cz), (cx, cz + 1), (cx, cz - 1) })
            {
                if (have.Contains(beside) || gaps.Contains(beside))
                {
                    continue;
                }

                var (gx, gz) = beside;
                if (have.Contains((gx + 1, gz))
                    && have.Contains((gx - 1, gz))
                    && have.Contains((gx, gz + 1))
                    && have.Contains((gx, gz - 1)))
                {
                    gaps.Add(beside);
                }
            }
        }

        return gaps;
    }

    /// <summary>
    /// Walks the map on disk, reading directories and no payloads at all.
    ///
    /// This is what a starting run needs: which chunks are already mapped, whether
    /// the season has moved since, which of them were stored with a hole in their
    /// surface, and which the map is missing from the middle of itself. Every one
    /// of those is answered by the first four kilobytes of each file now — the
    /// season and the hole are in the directory, so a map of ten thousand regions
    /// starts without decompressing a byte of it.
    /// </summary>
    public static Survey Walk(string dir, int edge)
    {
        var seasons = new Dictionary<(int, int), byte>();
        var holed = new HashSet<(int, int)>();

        foreach (var ((rx, rz), path) in All(dir))
        {
            if (Directory.Of(path, edge) is not { } directory)
            {
                continue;
            }

            for (var slot = 0; slot < Slots; slot++)
            {
                if (!directory.Holds(slot))
                {
                    continue;
                }

                var chunk = ChunkAt(rx, rz, slot);
                seasons[chunk] = directory.Season[slot];
                if ((directory.Flags[slot] & HoledFlag) != 0)
                {
                    holed.Add(chunk);
                }
            }
        }

        return new Survey(seasons, holed, Enclosed(seasons.Keys));
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
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
            foreach (var stale in System.IO.Directory.EnumerateFiles(exports, "columns.msq*"))
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
            var version = VersionOf(path);
            if (version != Version)
            {
                return version is null
                    ? $"{Path.GetFileName(path)} could not be read"
                    : $"the map on disk is format version {version}, and this build writes {Version}";
            }
        }

        return null;
    }

    /// <summary>
    /// The format version a file claims, whatever version that is.
    ///
    /// What decides whether a map on disk has to be thrown away, so it
    /// deliberately does not care whether this build could read it.
    /// </summary>
    private static int? VersionOf(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[HeaderBytes];
            if (file.Read(head, 0, head.Length) != head.Length)
            {
                return null;
            }
            for (var at = 0; at < Magic.Length; at++)
            {
                if (head[at] != Magic[at])
                {
                    return null;
                }
            }
            return BitConverter.ToUInt16(head, 4);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// CRC-32, the one deflate itself uses.
///
/// Written out because .NET has no CRC-32 in the box and the map service reads
/// these files with the checksum its compression library already carries. The two
/// have to be the same number, so this is the same polynomial and not a
/// hash of somebody's choosing.
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
