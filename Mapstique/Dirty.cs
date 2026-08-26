using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Mapstique;

/// <summary>
/// Which chunk columns need their surface read again.
///
/// The server raises ChunkDirty for every chunk whose blocks moved, and also for
/// every chunk it loads or generates. Loading is not a change: a chunk coming
/// back into memory looks exactly as it did when it left, so one already in the
/// export is left alone until something actually happens to it. Without that
/// distinction a player walking a circuit would re-read the whole route every
/// time they crossed it.
///
/// The set holds a coordinate pair per changed column, so a quiet server
/// accumulates nothing and the export it triggers is empty.
/// </summary>
public sealed class DirtyColumns
{
    private readonly HashSet<(int, int)> _dirty = new();
    private readonly HashSet<(int, int)> _exported = new();
    private readonly object _gate = new();

    /// <summary>
    /// ChunkDirty is not documented to arrive on the main thread, and the
    /// exporter drains this from a tick listener, so every touch is guarded.
    /// </summary>
    public int Count
    {
        get { lock (_gate) { return _dirty.Count; } }
    }

    /// <summary>
    /// Notes a chunk the server has marked dirty. Vertical position is dropped:
    /// the export is a surface, and a change anywhere in the column can move it.
    /// </summary>
    public void Mark(int chunkX, int chunkZ, EnumChunkDirtyReason reason)
    {
        lock (_gate)
        {
            if (reason == EnumChunkDirtyReason.NewlyLoaded && _exported.Contains((chunkX, chunkZ)))
            {
                return;
            }

            _dirty.Add((chunkX, chunkZ));
        }
    }

    /// <summary>Marks these columns whatever their history, which is what a forced export wants.</summary>
    public void MarkAll(IEnumerable<(int, int)> columns)
    {
        lock (_gate)
        {
            foreach (var column in columns)
            {
                _dirty.Add(column);
            }
        }
    }

    /// <summary>
    /// Records columns that are already in an export on disk, so that loading
    /// them again is not mistaken for a change. Called once at start with what a
    /// previous run left behind.
    /// </summary>
    public void Seed(IEnumerable<(int, int)> exported)
    {
        lock (_gate)
        {
            foreach (var column in exported)
            {
                _exported.Add(column);
            }
        }
    }

    /// <summary>
    /// Hands over the waiting columns and clears them. Anything marked while the
    /// export runs stays for the next one, so a change arriving mid-export is
    /// delayed rather than lost.
    /// </summary>
    public HashSet<(int, int)> Take()
    {
        lock (_gate)
        {
            var taken = new HashSet<(int, int)>(_dirty);
            _dirty.Clear();
            return taken;
        }
    }

    /// <summary>
    /// Puts columns back, for an export that failed or that found a chunk not yet
    /// ready to read.
    /// </summary>
    public void Restore(IEnumerable<(int, int)> columns)
    {
        lock (_gate)
        {
            foreach (var column in columns)
            {
                _dirty.Add(column);
            }
        }
    }

    /// <summary>
    /// Gives up on columns that left memory before they could be read, by
    /// forgetting that they were ever exported.
    ///
    /// They are not held dirty: a chunk that is gone cannot be read, and keeping
    /// it on the pile would leave the set permanently non-empty, which is the one
    /// state that costs an idle server a full pass over the map every tick.
    /// Loading it again raises ChunkDirty, and with no record of having exported
    /// it that counts as a change.
    /// </summary>
    public void Defer(IEnumerable<(int, int)> columns)
    {
        lock (_gate)
        {
            foreach (var column in columns)
            {
                _exported.Remove(column);
            }
        }
    }

    /// <summary>Records that these columns are now in the export on disk.</summary>
    public void Exported(IEnumerable<(int, int)> columns)
    {
        lock (_gate)
        {
            foreach (var column in columns)
            {
                _exported.Add(column);
            }
        }
    }
}
