using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Filling a fresh map without holding up the world.
///
/// A server with nobody on it keeps almost nothing in memory, so an export of
/// what happens to be loaded is a single chunk. Asking for the square around
/// spawn gives a fresh server a map without waiting for someone to walk the
/// world; everything players do explore is picked up by the export timer.
///
/// Asked for a few columns at a time rather than all at once. The rectangle form
/// of <c>LoadChunkColumnPriority</c> is documented as asynchronous and is not:
/// the server puts the rectangle on a queue that its chunk thread drains with
/// <c>loadChunkAreaBlocking</c>, which holds that thread until the whole area is
/// generated or twelve seconds have passed. On a dedicated server starting up
/// that is nobody's problem. In singleplayer the player joins in the same tick
/// with a view distance of 1152 blocks, and the thousands of columns they ask
/// for pile up behind the seed until the server's request queue overflows — at
/// which point it clears the queue out from under its own chunk thread and dies
/// with "In queue but missed from index!".
///
/// One column at a time is one short blocking load each, with the thread free
/// between them. Columns already in memory cost nothing at all: the server
/// answers those without queueing anything, which is most of them in
/// singleplayer, where the player's own view distance covers this square several
/// times over.
/// </summary>
public sealed class Seeding
{
    private readonly ICoreServerAPI _api;
    private readonly Queue<(int X, int Z)> _left;
    private readonly Action<string> _export;
    private readonly int _asked;

    /// <summary>How many columns have been asked for since the last export.</summary>
    private int _since;

    private Seeding(ICoreServerAPI api, Queue<(int X, int Z)> left, Action<string> export)
    {
        _api = api;
        _left = left;
        _export = export;
        _asked = left.Count;
    }

    /// <summary>
    /// A seed of the square around spawn, or nothing where the world has no
    /// spawn point to centre it on.
    /// </summary>
    public static Seeding? Around(ICoreServerAPI api, int radius, Action<string> export)
    {
        if (WorldFacts.Spawn(api) is not { } spawn)
        {
            api.Logger.Warning(
                "[witchlight] the world has no spawn point yet, so the map was not seeded — "
                + "it will fill in as players explore");
            return null;
        }

        var size = api.WorldManager.ChunkSize;
        var columns = new Queue<(int X, int Z)>(Outward(spawn.X / size, spawn.Z / size, radius));
        api.Logger.Notification(
            "[witchlight] seeding the map with {0} chunk columns around spawn, {1} at a time",
            columns.Count,
            Repair.PerStep);
        return new Seeding(api, columns, export);
    }

    /// <summary>
    /// The columns of a square, nearest the middle first.
    ///
    /// Ring by ring rather than row by row, because the map is now filled in over
    /// a span somebody can watch: outward from spawn is a map growing, where row
    /// by row is a band creeping across the screen. It also means a seed cut short
    /// by a shutdown leaves a map centred on spawn rather than half a one.
    /// </summary>
    public static IEnumerable<(int X, int Z)> Outward(int centreX, int centreZ, int radius)
    {
        yield return (centreX, centreZ);
        for (var ring = 1; ring <= radius; ring++)
        {
            for (var x = centreX - ring; x <= centreX + ring; x++)
            {
                yield return (x, centreZ - ring);
                yield return (x, centreZ + ring);
            }
            // The corners belong to the rows above and are not repeated here.
            for (var z = centreZ - ring + 1; z <= centreZ + ring - 1; z++)
            {
                yield return (centreX - ring, z);
                yield return (centreX + ring, z);
            }
        }
    }

    /// <summary>How much of the seed is still to ask for.</summary>
    public string Describe() => $"seeding: {_asked - _left.Count} of {_asked} columns asked for";

    /// <summary>
    /// Asks for the next few columns, and says whether there are any more.
    ///
    /// The map is written as the seed goes rather than only at the end. A column
    /// the server loaded and nobody is standing near is freed again after fifteen
    /// seconds, which is less time than a seed of any size takes — so a single
    /// export at the end would find the earliest columns already gone.
    /// </summary>
    public bool Step()
    {
        for (var taken = 0; taken < Repair.PerStep && _left.Count > 0; taken++)
        {
            var (x, z) = _left.Dequeue();
            _api.WorldManager.LoadChunkColumnPriority(x, z);
            _since++;
        }

        if (_since >= ColumnsPerExport || _left.Count == 0)
        {
            _since = 0;
            _export("seed");
        }

        return _left.Count > 0;
    }


    /// <summary>
    /// How often the map is written while the seed runs.
    ///
    /// Well inside the fifteen seconds an idle column stays in memory, and far
    /// enough apart that a seed is a handful of writes rather than one per column.
    /// </summary>
    private const int ColumnsPerExport = 64;

}
