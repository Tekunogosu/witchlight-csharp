using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Which markers one player keeps in sight on their own map.
///
/// A pinned waypoint is held against the edge of the map instead of scrolling off
/// it, which is the game's own answer to "I want to see where that is from here".
/// The game keeps the flag on the waypoint, and that works for exactly the
/// markers the game thinks a player has — their own. Everybody else's arrive on a
/// client as temporary waypoints this mod lays down, and a flag on those has
/// nowhere to live: the waypoint is rebuilt from what the server sends every time
/// the map is opened.
///
/// So the answer is in two halves and this owns both of them, because "is this
/// marker pinned for this person" is one question and two places answering it is
/// two answers. A marker somebody owns is answered by the waypoint itself, which
/// is where the game's own map dialog also writes it. Anybody else's is answered
/// from here, kept beside the waypoints in the savegame for the reason
/// <see cref="Visibility"/> is: a world that loses its waypoints should lose what
/// was said about them at the same moment, not keep a store describing markers
/// that no longer exist.
///
/// **A pin is one person's, never everyone's.** Nothing here changes what anybody
/// else sees; pinning somebody's marker puts it on the pinner's map and on no
/// other.
/// </summary>
public sealed class Pins
{
    private const string SaveKey = "witchlight:markerpins";

    /// <summary>Player uid to the keys of other people's markers they keep in
    ///  sight. Their own are not in here: the waypoint holds that.</summary>
    private readonly Dictionary<string, HashSet<string>> _kept;

    /// <summary>Whether anything has changed since the last write. Writes are
    ///  earned: a save that would store what is already stored does not.</summary>
    private bool _unsaved;

    private Pins(Dictionary<string, HashSet<string>> kept)
    {
        _kept = kept;
    }

    /// <summary>
    /// A store holding nothing, which is everybody's map showing what the game
    /// alone put on it. What the mod holds before the world is up: the savegame
    /// cannot be read then, and a null store would put a check at every use.
    /// </summary>
    public static Pins Empty => new(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));

    /// <summary>How many markers of other people's are kept in sight, over
    ///  everybody. Reported by status.</summary>
    public int Decisions => _kept.Values.Sum(kept => kept.Count);

    /// <summary>
    /// What a previous run stored. An unreadable store is an empty one: nothing
    /// is lost that a person cannot put back with one press, and a map that shows
    /// too little is better than one that shows somebody the wrong thing.
    /// </summary>
    public static Pins Read(ICoreServerAPI api)
    {
        try
        {
            var stored = api.WorldManager.SaveGame.GetData(SaveKey);
            if (stored is null || stored.Length == 0)
            {
                return Empty;
            }

            var read = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(
                Encoding.UTF8.GetString(stored));
            if (read is null)
            {
                return Empty;
            }

            var kept = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (uid, keys) in read)
            {
                kept[uid] = new HashSet<string>(keys ?? new List<string>(), StringComparer.Ordinal);
            }
            return new Pins(kept);
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read marker pins: {0}", error.Message);
            return Empty;
        }
    }

    /// <summary>
    /// Stores the pins, when they are not the ones already stored.
    ///
    /// Pins on markers that no longer exist go first, so deleting a marker
    /// eventually takes everyone's pin on it with it rather than leaving the store
    /// growing by one entry for every marker the server has ever had.
    ///
    /// Null is "the waypoints could not be read", which is emphatically not "there
    /// are none": standing an empty list in for it would drop every pin on the
    /// server the first time a save landed while the map layer was not up. Nothing
    /// is forgotten unless the live list is in hand — the rule
    /// <see cref="Visibility"/> follows, for the same reason.
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
            var said = _kept.ToDictionary(
                held => held.Key, held => held.Value.ToList(), StringComparer.Ordinal);
            api.WorldManager.SaveGame.StoreData(
                SaveKey, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(said)));
            _unsaved = false;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not store marker pins: {0}", error.Message);
        }
    }

    /// <summary>
    /// Whether this person keeps this marker in sight.
    ///
    /// Their own marker is answered by the waypoint, because that is the flag the
    /// game's own map dialog sets and reads; anybody else's is answered from the
    /// store. One function, so the two halves cannot come to disagree.
    /// </summary>
    public bool Kept(Waypoint waypoint, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }
        if (waypoint.OwningPlayerUid == uid)
        {
            return waypoint.Pinned;
        }
        return _kept.TryGetValue(uid, out var theirs) && theirs.Contains(Markers.Key(waypoint));
    }

    /// <summary>
    /// Records that this person does, or no longer does, keep this marker in
    /// sight — and puts it on their map if it is theirs to be flagged.
    ///
    /// Their own waypoint is changed and resent, which is what makes the pin show
    /// on the map they have open. Anybody else's is written here and reaches them
    /// with the next share, because a temporary waypoint is laid down again from
    /// what the server sends rather than edited where it lies.
    /// </summary>
    public void Choose(ICoreServerAPI api, Waypoint waypoint, string uid, bool keep)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return;
        }

        if (waypoint.OwningPlayerUid == uid)
        {
            if (waypoint.Pinned == keep)
            {
                return;
            }
            waypoint.Pinned = keep;
            Markers.Resend(api, uid);
            return;
        }

        var key = Markers.Key(waypoint);
        if (!_kept.TryGetValue(uid, out var theirs))
        {
            if (!keep)
            {
                return;
            }
            theirs = new HashSet<string>(StringComparer.Ordinal);
            _kept[uid] = theirs;
        }

        var moved = keep ? theirs.Add(key) : theirs.Remove(key);
        if (!moved)
        {
            return;
        }
        if (theirs.Count == 0)
        {
            _kept.Remove(uid);
        }
        _unsaved = true;
    }

    /// <summary>
    /// Every marker each person keeps in sight, by uid, for the map service to
    /// hand each of them their own.
    ///
    /// Both halves in one answer, because the page asking has one question. A
    /// person's own pinned waypoints are read off the waypoints; the rest come
    /// from the store.
    /// </summary>
    public Dictionary<string, List<string>> Everyones(IEnumerable<Waypoint> alive)
    {
        var said = _kept.ToDictionary(
            held => held.Key, held => held.Value.ToList(), StringComparer.Ordinal);

        foreach (var waypoint in alive)
        {
            var uid = waypoint?.OwningPlayerUid;
            if (waypoint is null || !waypoint.Pinned || string.IsNullOrEmpty(uid))
            {
                continue;
            }

            if (!said.TryGetValue(uid, out var theirs))
            {
                theirs = new List<string>();
                said[uid] = theirs;
            }
            theirs.Add(Markers.Key(waypoint));
        }

        return said;
    }

    /// <summary>Drops pins on markers that are no longer on the map.</summary>
    private void Forget(IEnumerable<Waypoint> alive)
    {
        var live = new HashSet<string>(
            alive.Where(waypoint => waypoint is not null).Select(Markers.Key),
            StringComparer.Ordinal);

        foreach (var (uid, theirs) in _kept.ToList())
        {
            if (theirs.RemoveWhere(key => !live.Contains(key)) == 0)
            {
                continue;
            }
            _unsaved = true;
            if (theirs.Count == 0)
            {
                _kept.Remove(uid);
            }
        }
    }
}
