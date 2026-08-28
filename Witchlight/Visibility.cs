using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Whether a marker is its owner's alone.
///
/// A waypoint has no field for this — the game has never needed one, because in
/// the game a waypoint is only ever its owner's. Sharing is this mod's idea, so
/// the choice is this mod's to keep, and it is kept beside the waypoints in the
/// savegame rather than in a file of its own: it is a property of a waypoint, and
/// two stores that can be lost separately are two stores that can disagree about
/// who may see what. A world that loses its waypoints loses these with them.
///
/// Only a choice somebody actually made is stored. A marker nobody has decided
/// about falls back to <c>markers_public</c>, which is what that setting has
/// always meant, so the store holds one entry per decision rather than one per
/// marker and stays empty on a server where nobody uses the web form.
/// </summary>
public sealed class Visibility
{
    private const string SaveKey = "witchlight:markervisibility";

    /// <summary>Waypoint guid to whether its owner chose to keep it to themselves.</summary>
    private readonly Dictionary<string, bool> _chosen;

    /// <summary>Whether anything has changed since the last write. Writes are
    ///  earned: a save that would store what is already stored does not.</summary>
    private bool _unsaved;

    private Visibility(Dictionary<string, bool> chosen)
    {
        _chosen = chosen;
    }

    /// <summary>
    /// A store holding no decisions, which is every marker on the operator's
    /// default. What the mod holds before the world is up: the savegame cannot be
    /// read then, and a null store would put a check at every use of it.
    /// </summary>
    public static Visibility Empty => new(new Dictionary<string, bool>(StringComparer.Ordinal));

    /// <summary>How many markers somebody has decided about. Reported by status.</summary>
    public int Decisions => _chosen.Count;

    /// <summary>
    /// What a previous run stored. An unreadable store is an empty one: every
    /// marker then falls back to the operator's setting, which is the safe
    /// direction on a server whose default is private and a visible one on a
    /// server whose default is not.
    /// </summary>
    public static Visibility Read(ICoreServerAPI api)
    {
        try
        {
            var stored = api.WorldManager.SaveGame.GetData(SaveKey);
            if (stored is null || stored.Length == 0)
            {
                return Empty;
            }

            var read = JsonConvert.DeserializeObject<Dictionary<string, bool>>(
                Encoding.UTF8.GetString(stored));
            return read is null
                ? Empty
                : new Visibility(new Dictionary<string, bool>(read, StringComparer.Ordinal));
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read marker visibility: {0}", error.Message);
            return Empty;
        }
    }

    /// <summary>
    /// Stores the choices, when they are not the ones already stored.
    ///
    /// Choices about waypoints that no longer exist go first, so deleting a marker
    /// eventually takes its decision with it rather than leaving the store growing
    /// by one entry for every marker the server has ever had.
    ///
    /// Null is "the waypoints could not be read", which is emphatically not "there
    /// are none": standing in an empty list for it would forget every decision on
    /// the server the first time a save landed while the map layer was not up, and
    /// every private marker would quietly become whatever the operator's default
    /// says. Nothing is forgotten unless the live list is in hand.
    /// </summary>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive)
    {
        if (alive is not null)
        {
            Forget(alive);
        }

        if (!_unsaved)
        {
            return;
        }

        try
        {
            api.WorldManager.SaveGame.StoreData(
                SaveKey, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_chosen)));
            _unsaved = false;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not store marker visibility: {0}", error.Message);
        }
    }

    /// <summary>
    /// Whether this marker is its owner's alone, given what the operator's setting
    /// says about a marker nobody has decided for.
    /// </summary>
    public bool IsPrivate(Waypoint waypoint, bool byDefault)
    {
        var guid = waypoint.Guid;
        if (!string.IsNullOrEmpty(guid) && _chosen.TryGetValue(guid, out var chosen))
        {
            return chosen;
        }
        return byDefault;
    }

    /// <summary>Records what somebody chose for one marker.</summary>
    public void Choose(string guid, bool keepToOwner)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return;
        }
        if (_chosen.TryGetValue(guid, out var held) && held == keepToOwner)
        {
            return;
        }

        _chosen[guid] = keepToOwner;
        _unsaved = true;
    }

    /// <summary>Drops decisions about markers that are no longer on the map.</summary>
    private void Forget(IEnumerable<Waypoint> alive)
    {
        var live = new HashSet<string>(
            alive.Select(waypoint => waypoint?.Guid ?? "").Where(guid => guid.Length > 0),
            StringComparer.Ordinal);

        foreach (var gone in _chosen.Keys.Where(guid => !live.Contains(guid)).ToList())
        {
            _chosen.Remove(gone);
            _unsaved = true;
        }
    }
}
