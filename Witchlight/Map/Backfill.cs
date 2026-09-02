using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Drawing the ground the game already has and the map has never seen.
///
/// A map only knows what it has been told: a column reaches it when somebody
/// walks there while the mod is running. Every world has more than that — terrain
/// explored before witchlight was installed, and the margin the generator lays
/// down around wherever anybody has been. On a world of eight thousand saved
/// columns the map held six, and the missing quarter was not a fault anywhere,
/// only ground nobody had walked back over.
///
/// The savegame is what knows. Rather than reading it — a private, versioned
/// format this mod would have to reimplement and keep in step with the game
/// forever — this asks the game the one question it needs: **is there a column
/// saved here?** <see cref="IWorldManagerAPI.TestMapChunkExists"/> answers out of
/// the same database the server loads from, and a column it says yes to is one
/// <see cref="Repair"/> already knows how to fetch and write.
///
/// Outward from what the map holds, never outward from nothing. A column is only
/// ever considered because a mapped column sits beside it, and it is only asked
/// for when the save already holds it — so this walks to the edge of what has
/// been explored and stops. It cannot make the server generate world, which is
/// the one thing a repair reaching outward must never do.
///
/// Everything here happens on the server's main thread: the export drives the
/// stepping from a tick listener, and the game answers an existence test as a
/// main thread task.
/// </summary>
public sealed class Backfill
{
    private readonly ICoreServerAPI _api;

    /// <summary>What to do with a column the save turns out to hold.</summary>
    private readonly Action<(int, int)> _owe;

    /// <summary>
    /// Columns beside something the map holds, waiting to be asked about.
    ///
    /// A queue because the order is the order they were found in, which is
    /// outward from what the map already had — so a map fills from its own edges
    /// rather than from a corner of the world.
    /// </summary>
    private readonly Queue<(int, int)> _asking = new();

    /// <summary>
    /// Every column already asked about, however it answered.
    ///
    /// The whole of what stops this walking in circles: a column that does not
    /// exist is asked about once, and a column that does is handed over once. It
    /// grows to the size of the frontier this has ever considered, which is the
    /// explored world and a ring around it.
    /// </summary>
    private readonly HashSet<(int, int)> _tried = new();

    /// <summary>How many the save turned out to hold, for `witchlight status`.</summary>
    private int _found;

    public Backfill(ICoreServerAPI api, Action<(int, int)> owe)
    {
        _api = api;
        _owe = owe;
    }

    /// <summary>How many columns are still to be asked about.</summary>
    public int Waiting => _asking.Count;

    /// <summary>
    /// Notes columns the map now holds, and considers what lies beside them.
    ///
    /// Called with what a start-up survey found and again with every column an
    /// export writes, which is what makes the frontier move: a column drawn for
    /// the first time offers its four neighbours, and the ones the save holds are
    /// drawn in their turn.
    ///
    /// Cheap enough to call on every export. It is four lookups per column
    /// written, against the whole map walked per beat had the frontier been
    /// worked out from scratch each time.
    /// </summary>
    public void Beside(IEnumerable<(int, int)> mapped, ICollection<(int, int)> held)
    {
        foreach (var (cx, cz) in mapped)
        {
            foreach (var beside in new[] { (cx + 1, cz), (cx - 1, cz), (cx, cz + 1), (cx, cz - 1) })
            {
                if (!held.Contains(beside) && _tried.Add(beside))
                {
                    _asking.Enqueue(beside);
                }
            }
        }
    }

    /// <summary>
    /// Asks the save about the next few, and says how many were asked.
    ///
    /// The answer arrives later and on this thread, and a column the save holds
    /// goes straight to the repair — which loads it, reads it and writes it, at
    /// its own pace and no faster. So the number here is how fast the frontier is
    /// explored rather than how fast the map grows.
    ///
    /// A few at a time because there is no hurry: a map filling itself in over an
    /// evening is a map nobody had to do anything about, and a map that stalls a
    /// server to do it is worse than a map with a gap.
    /// </summary>
    public int Step(int most)
    {
        var asked = 0;
        while (asked < most && _asking.Count > 0)
        {
            var (cx, cz) = _asking.Dequeue();
            asked++;
            _api.WorldManager.TestMapChunkExists(cx, cz, saved =>
            {
                if (!saved)
                {
                    return;
                }

                _found++;
                _owe((cx, cz));
            });
        }
        return asked;
    }

    /// <summary>What the status command says about it, or nothing once it is done.</summary>
    public string? Describe() =>
        _asking.Count == 0 && _found == 0
            ? null
            : $"backfill: {_asking.Count} columns to ask the savegame about, "
                + $"{_found} found so far that the map had never drawn";

    /// <summary>
    /// How many columns one step asks the savegame about.
    ///
    /// An existence test is a queued read of one row, so this is cheaper than the
    /// loading that follows it and is deliberately set above it: the frontier
    /// wants to be a little ahead of the repair, or the repair runs dry between
    /// steps and the map fills more slowly than the server could manage.
    /// </summary>
    public const int PerStep = 2 * Repair.PerStep;

    /// <summary>
    /// And how many a forced export asks about, which is an operator who wants
    /// the map filled in and is willing to wait for it.
    ///
    /// Bounded all the same: what this finds still goes through the repair, and
    /// the repair loads at its own pace whatever it is handed.
    /// </summary>
    public const int PerCommand = 512;
}
