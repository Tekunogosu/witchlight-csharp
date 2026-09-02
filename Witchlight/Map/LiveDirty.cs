using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Which chunks have changed since the last live push, for the map service to
/// pull immediately rather than wait for the next export.
///
/// The same ChunkDirty signal <see cref="DirtyColumns"/> already reads, kept
/// apart from it rather than shared: the export is paced by disk cost and
/// batches for whole seconds, and tying this to the same accumulator would mean
/// either the fast push clears state the slow export still needs, or the export
/// clears columns before the fast push has sent them. Two readers of one signal
/// wanted two independent tallies of it, not one shared between them.
///
/// Kept small on purpose: a coordinate pair, nothing else. What changed there is
/// answered by the map service pulling the column over the same channel
/// <see cref="ModApi"/> already answers backfill on — this only says where to
/// look, never what is there.
/// </summary>
public sealed class LiveDirty
{
    private readonly HashSet<(int, int)> _pending = new();
    private readonly HashSet<(int, int)> _everSent = new();
    private readonly object _gate = new();

    /// <summary>
    /// Notes a chunk the server has marked dirty. A chunk merely loading again,
    /// having been sent here before, is not a change — the same distinction
    /// <see cref="DirtyColumns.Mark"/> makes, for the same reason: a player
    /// walking a route they have already been on must not resend the whole thing
    /// every lap.
    /// </summary>
    public void Mark(int chunkX, int chunkZ, EnumChunkDirtyReason reason)
    {
        lock (_gate)
        {
            if (reason == EnumChunkDirtyReason.NewlyLoaded && _everSent.Contains((chunkX, chunkZ)))
            {
                return;
            }

            _pending.Add((chunkX, chunkZ));
        }
    }

    /// <summary>
    /// Hands over what is waiting and clears it, or nothing if nothing is. A
    /// caller that gets nothing sends no post — a quiet server pushes nothing
    /// rather than an empty array on every beat.
    /// </summary>
    public List<(int, int)>? Take()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            var taken = new List<(int, int)>(_pending);
            foreach (var column in taken)
            {
                _everSent.Add(column);
            }
            _pending.Clear();
            return taken;
        }
    }
}
