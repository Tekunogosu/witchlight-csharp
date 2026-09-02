using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Filling the chunk-shaped holes a map is left with.
///
/// A map chunk outlives the blocks under it, so a column marked dirty and reached
/// after its blocks have gone cannot be read. Nothing brings one back on its own:
/// the server loads a column when somebody walks to it, and a column at the
/// trailing edge of a path nobody retraces is never loaded again — so it stays a
/// single chunk of nothing in the middle of finished terrain, permanently.
///
/// So the server is asked for it. That much was true before this was a class of
/// its own; what was not was the timing. Asking at the head of an export and
/// reading on the next one loses a race the map cannot win: an untouched column
/// is aged out of memory in under ten seconds and the export beat is ten, so the
/// column arrived, sat there, and was gone again before anything looked at it.
/// The count of columns owed a read stood still for hours while every beat
/// faithfully asked for them.
///
/// So a column is read when the game says it has arrived rather than on the next
/// beat, which is a second later instead of ten.
///
/// It is also how the map grows into ground nobody has walked back over: the
/// columns <see cref="Backfill"/> finds in the savegame arrive here, because
/// "get this column from the server and write it" is one job whoever wanted it.
///
/// Most of what this was built to fix turned out not to be this. The holes in the
/// middle of a map were columns whose blocks had never left memory at all: a
/// sentinel in the rain heightmap made the export believe they had, permanently —
/// see <see cref="ColumnPump"/>. What is left here is the real, ordinary case a
/// column can still fall into, which is small and slow-moving, and the export line
/// now says which of the two ways a column was unreadable so that the next person
/// reading it is not sent the way this one was.
///
/// Everything here happens on the server's main thread: the export drives the
/// asking from a tick listener, and the game runs a load's callback as a main
/// thread task. Nothing is guarded, because there is nothing to guard it from —
/// unlike <see cref="DirtyColumns"/>, which the server's own chunk events reach
/// from wherever it raises them.
/// </summary>
public sealed class Repair
{
    private readonly ICoreServerAPI _api;

    /// <summary>
    /// What to do with the ground once it is there: mark it changed and write it.
    ///
    /// Handed in rather than reached for, so that this knows how to get a column
    /// back and nothing about what a map is. It is the shape <see cref="Seeding"/>
    /// already uses, and for the same reason — the exporter owns the writing, and
    /// two classes calling into each other is one dependency too many.
    /// </summary>
    private readonly Action<IReadOnlyCollection<(int, int)>> _read;

    /// <summary>
    /// The columns the map wants and cannot read, in order.
    ///
    /// Walked from where the last pass stopped, because only a few are asked for
    /// at a time and a long list has to be reached the end of rather than the same
    /// short prefix of it asked for forever. A list rather than a set because it
    /// holds only what failed, which is tens of columns and not thousands, and one
    /// structure that is both the membership and the order is one that cannot
    /// disagree with itself.
    /// </summary>
    private readonly List<(int, int)> _owed = new();

    /// <summary>Where the last pass over <see cref="_owed"/> stopped.</summary>
    private int _asked;

    /// <summary>
    /// Columns the server has handed back and that have not been read yet.
    ///
    /// A set, because a column asked for twice before either answer arrives is
    /// still one column to read.
    /// </summary>
    private readonly HashSet<(int, int)> _landed = new();

    /// <summary>Whether a read of what has landed is already coming.</summary>
    private bool _reading;

    public Repair(ICoreServerAPI api, Action<IReadOnlyCollection<(int, int)>> read)
    {
        _api = api;
        _read = read;
    }

    /// <summary>How many columns the map wants and cannot read yet.</summary>
    public int Owed => _owed.Count;

    /// <summary>
    /// Notes columns that left memory before they could be read.
    ///
    /// A column leaves this list when it reaches disk and not before, because
    /// until then it is still owed. Asking for one that is already on the list
    /// costs nothing and changes nothing, which is what makes an export free to
    /// say so on every beat that fails to read it.
    /// </summary>
    public void Owe(IEnumerable<(int, int)> columns)
    {
        foreach (var column in columns)
        {
            if (!_owed.Contains(column))
            {
                _owed.Add(column);
            }
        }
    }

    /// <summary>
    /// Notes columns that have reached disk, which is the one thing that settles
    /// a column that was owed a read.
    /// </summary>
    public void Settled(IEnumerable<(int, int)> columns)
    {
        foreach (var column in columns)
        {
            var at = _owed.IndexOf(column);
            if (at < 0)
            {
                continue;
            }

            _owed.RemoveAt(at);
            if (_asked > at)
            {
                _asked--;
            }
        }
    }

    /// <summary>
    /// Asks the server to load some of the columns the map is owed, and says how
    /// many it asked for.
    ///
    /// Each is asked for with a callback of its own, which is what makes the
    /// reading find anything: the game says when the ground is there, and a read
    /// a second later is well inside the life of a column nobody is standing near,
    /// where a read on the next export beat is not — the beat is ten seconds and
    /// an untouched column is aged out in under that.
    ///
    /// Nothing is asked for twice in a row — the list is walked from where the
    /// last pass stopped.
    ///
    /// A few at a time on the beat, and as many as an operator's patience on the
    /// command, because a chunk load is real work for the server and healing the
    /// map is not worth a stutter that everybody feels.
    /// </summary>
    public int Ask(int most)
    {
        var take = Math.Min(most, _owed.Count);
        for (var i = 0; i < take; i++)
        {
            var column = _owed[(_asked + i) % _owed.Count];
            _api.WorldManager.LoadChunkColumnPriority(
                column.Item1,
                column.Item2,
                new ChunkLoadOptions { OnLoaded = () => Landed(column) });
        }

        _asked = _owed.Count == 0 ? 0 : (_asked + take) % _owed.Count;
        return take;
    }

    /// <summary>
    /// One column has arrived, and the read of everything that has is arranged.
    ///
    /// Not read here. A handful are asked for together and their callbacks land in
    /// the same tick or the next few, and a read apiece would be a full export
    /// apiece — so the first one to land books the read and the rest join it.
    /// </summary>
    private void Landed((int, int) column)
    {
        _landed.Add(column);
        if (_reading)
        {
            return;
        }

        _reading = true;
        _api.Event.RegisterCallback(_ => ReadWhatLanded(), SettleMs);
    }

    /// <summary>Reads the ground that has arrived, and lets go of it.</summary>
    private void ReadWhatLanded()
    {
        _reading = false;
        if (_landed.Count == 0)
        {
            return;
        }

        var landed = new List<(int, int)>(_landed);
        _landed.Clear();
        _read(landed);
    }

    /// <summary>
    /// How long the read waits after the first column of a batch lands.
    ///
    /// Long enough for the rest of the batch to arrive behind it, and short enough
    /// that the first one is still in memory when the last one gets there — an
    /// untouched column is aged out in something under ten seconds, and this is
    /// one.
    /// </summary>
    private const int SettleMs = 1000;

    /// <summary>
    /// How many owed columns one beat of the export asks the server to load.
    ///
    /// Small, because this runs forever on a live server and the map heals in the
    /// background rather than in a hurry: a handful every export is a few hundred
    /// an hour, which is more than a session loses.
    /// </summary>
    public const int PerExport = 8;

    /// <summary>
    /// And how many a forced export asks for, which is an operator waiting for an
    /// answer and willing to pay for it.
    ///
    /// Bounded all the same. A map that has lost thousands of columns is a bug to
    /// fix rather than a queue to flood, and the next command asks for the next
    /// few hundred.
    /// </summary>
    public const int PerCommand = 256;
}
