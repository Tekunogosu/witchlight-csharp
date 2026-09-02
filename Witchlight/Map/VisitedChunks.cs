using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Which chunk columns a player has actually stood in, ever.
///
/// The savegame's own <see cref="IWorldManagerAPI.TestMapChunkExists"/> answers
/// "does the save hold a column here," which is true of everything world
/// generation ever laid down near a player, not only what one of them walked to —
/// so backfilling outward from every mapped column instead of from this store
/// draws the generator's margin as if somebody had explored it, and asks the
/// server for it all on a schedule nobody chose. This is the narrower fact: a
/// column only joins it because a player's feet were in it.
///
/// Kept in the savegame, the same store <see cref="Origins"/> and
/// <see cref="Visibility"/> use for other facts nothing but this mod can answer:
/// small, changes rarely enough that most ticks write nothing, and belongs beside
/// the world it describes rather than in a file that can be lost or copied
/// separately from it.
/// </summary>
public sealed class VisitedChunks
{
    private const string SaveKey = "witchlight:visitedchunks";
    private const string Called = "visited chunks";

    private readonly HashSet<(int, int)> _visited;
    private bool _unsaved;

    private VisitedChunks(HashSet<(int, int)> visited)
    {
        _visited = visited;
    }

    /// <summary>A store holding nowhere, which is every world before anyone has
    ///  walked in it under this version. What the mod holds before the world is
    ///  up, and what a fresh world starts with.</summary>
    public static VisitedChunks Empty => new(new HashSet<(int, int)>());

    /// <summary>How many chunk columns this knows a player has stood in. Reported
    ///  by status.</summary>
    public int Count => _visited.Count;

    /// <summary>Every column recorded, for a restart's backfill to walk out from.</summary>
    public IReadOnlyCollection<(int, int)> All => _visited;

    /// <summary>What a previous run stored. An unreadable store is an empty one:
    ///  the world backfills from nothing rather than from a guess.</summary>
    public static VisitedChunks Read(ICoreServerAPI api)
    {
        try
        {
            var stored = api.WorldManager.SaveGame.GetData(SaveKey);
            if (stored is null || stored.Length == 0)
            {
                return Empty;
            }

            var read = JsonConvert.DeserializeObject<int[][]>(System.Text.Encoding.UTF8.GetString(stored));
            return read is null
                ? Empty
                : new VisitedChunks(read.Select(pair => (pair[0], pair[1])).ToHashSet());
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read {0}: {1}", Called, error.Message);
            return Empty;
        }
    }

    /// <summary>Stores what is held, when it is not what is already stored.</summary>
    public void Write(ICoreServerAPI api)
    {
        if (!_unsaved)
        {
            return;
        }

        try
        {
            var packed = _visited.Select(at => new[] { at.Item1, at.Item2 }).ToArray();
            api.WorldManager.SaveGame.StoreData(
                SaveKey, System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(packed)));
            _unsaved = false;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not store {0}: {1}", Called, error.Message);
        }
    }

    /// <summary>
    /// Records that a player is standing in these columns right now.
    ///
    /// Called on the same fast clock that reads player positions, with wherever
    /// they are this tick. A column already known does not earn a write, so an
    /// idle server with everyone standing still writes nothing.
    /// </summary>
    public void Visit(IEnumerable<(int, int)> at)
    {
        foreach (var column in at)
        {
            if (_visited.Add(column))
            {
                _unsaved = true;
            }
        }
    }
}
