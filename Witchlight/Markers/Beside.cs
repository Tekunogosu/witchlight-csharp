using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// One thing this mod knows about a waypoint that the game does not, kept beside
/// the waypoints in the savegame.
///
/// A waypoint has fields for what the game has ever needed. Sharing is this mod's
/// idea, and so is the block a marker was put on, so those answers are this mod's
/// to keep — and they are kept in the savegame rather than in files of their own
/// because they are properties of a waypoint, and two stores that can be lost
/// separately are two stores that can disagree. A world that loses its waypoints
/// loses these with them.
///
/// The mechanism is here and the meaning is in whatever holds one: reading a
/// store back, writing it when it has changed, and dropping what it says about
/// waypoints that no longer exist are the same three jobs whatever is being
/// remembered, and they were written out once per subject.
///
/// **Only what somebody actually decided is stored.** A waypoint nothing has been
/// said about is simply absent, and its owner reads that as the operator's
/// default or as nothing known — so a store holds one entry per answer rather
/// than one per marker, and stays empty on a server where nobody uses the map.
/// </summary>
public sealed class Beside<T>
{
    private readonly string _key;

    /// <summary>Waypoint guid to what is known about it.</summary>
    private readonly Dictionary<string, T> _held;

    /// <summary>Whether anything has changed since the last write. Writes are
    ///  earned: a save that would store what is already stored does not.</summary>
    private bool _unsaved;

    private Beside(string key, Dictionary<string, T> held)
    {
        _key = key;
        _held = held;
    }

    /// <summary>
    /// A store holding nothing, which is every waypoint on whatever the absence
    /// means. What the mod holds before the world is up: the savegame cannot be
    /// read then, and a null store would put a check at every use of it.
    /// </summary>
    public static Beside<T> Empty(string key) =>
        new(key, new Dictionary<string, T>(StringComparer.Ordinal));

    /// <summary>How many waypoints this knows something about. Reported by status.</summary>
    public int Count => _held.Count;

    /// <summary>
    /// What a previous run stored. An unreadable store is an empty one: every
    /// waypoint then falls back to whatever the absence of an answer means, which
    /// is the only honest thing to do with bytes that cannot be read.
    /// </summary>
    public static Beside<T> Read(ICoreServerAPI api, string key, string called)
    {
        try
        {
            var stored = api.WorldManager.SaveGame.GetData(key);
            if (stored is null || stored.Length == 0)
            {
                return Empty(key);
            }

            var read = JsonConvert.DeserializeObject<Dictionary<string, T>>(
                Encoding.UTF8.GetString(stored));
            return read is null
                ? Empty(key)
                : new Beside<T>(key, new Dictionary<string, T>(read, StringComparer.Ordinal));
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read {0}: {1}", called, error.Message);
            return Empty(key);
        }
    }

    /// <summary>
    /// Stores what is held, when it is not what is already stored.
    ///
    /// Answers about waypoints that no longer exist go first, so deleting a
    /// marker eventually takes what was said about it too rather than leaving the
    /// store growing by one entry for every marker the server has ever had.
    ///
    /// Null is "the waypoints could not be read", which is emphatically not
    /// "there are none": standing an empty list in for it would forget every
    /// answer on the server the first time a save landed while the map layer was
    /// not up. Nothing is forgotten unless the live list is in hand.
    /// </summary>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive, string called)
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
                _key, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_held)));
            _unsaved = false;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not store {0}: {1}", called, error.Message);
        }
    }

    /// <summary>What is known about this waypoint, or nothing.</summary>
    public bool Knows(string? guid, out T held)
    {
        held = default!;
        return !string.IsNullOrEmpty(guid) && _held.TryGetValue(guid, out held!);
    }

    /// <summary>Records what is known about one waypoint. An answer that is
    ///  already the one held is not a change and does not earn a write.</summary>
    public void Say(string? guid, T said)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return;
        }
        if (_held.TryGetValue(guid, out var known) && EqualityComparer<T>.Default.Equals(known, said))
        {
            return;
        }

        _held[guid] = said;
        _unsaved = true;
    }

    /// <summary>Every waypoint this knows about, by guid, for a feed to hand on.</summary>
    public IReadOnlyDictionary<string, T> All => _held;

    /// <summary>Drops what is said about waypoints that are no longer on the map.</summary>
    private void Forget(IEnumerable<Waypoint> alive)
    {
        var live = new HashSet<string>(
            alive.Select(waypoint => waypoint?.Guid ?? "").Where(guid => guid.Length > 0),
            StringComparer.Ordinal);

        foreach (var gone in _held.Keys.Where(guid => !live.Contains(guid)).ToList())
        {
            _held.Remove(gone);
            _unsaved = true;
        }
    }
}
