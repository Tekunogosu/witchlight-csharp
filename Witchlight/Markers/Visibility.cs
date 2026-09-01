using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Whether a marker is its owner's alone.
///
/// A waypoint has no field for this — the game has never needed one, because in
/// the game a waypoint is only ever its owner's. Sharing is this mod's idea, so
/// the choice is this mod's to keep, and it is kept in the savegame beside the
/// waypoints themselves: see <see cref="Beside{T}"/>, which owns the reading, the
/// writing and the forgetting for every answer of that shape.
///
/// Only a choice somebody actually made is stored. A marker nobody has decided
/// about falls back to <c>markers_public</c>, which is what that setting has
/// always meant, so the store holds one entry per decision rather than one per
/// marker and stays empty on a server where nobody uses the web form.
/// </summary>
public sealed class Visibility
{
    private const string SaveKey = "witchlight:markervisibility";
    private const string Called = "marker visibility";

    private readonly Beside<bool> _chosen;

    private Visibility(Beside<bool> chosen)
    {
        _chosen = chosen;
    }

    /// <summary>A store holding no decisions, which is every marker on the
    ///  operator's default. What the mod holds before the world is up.</summary>
    public static Visibility Empty => new(Beside<bool>.Empty(SaveKey));

    /// <summary>How many markers somebody has decided about. Reported by status.</summary>
    public int Decisions => _chosen.Count;

    /// <summary>What a previous run stored.</summary>
    public static Visibility Read(ICoreServerAPI api) =>
        new(Beside<bool>.Read(api, SaveKey, Called));

    /// <summary>Stores the choices, when they are not the ones already stored.</summary>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive) =>
        _chosen.Write(api, alive, Called);

    /// <summary>
    /// Whether this marker is its owner's alone, given what the operator's setting
    /// says about a marker nobody has decided for.
    /// </summary>
    public bool IsPrivate(Waypoint waypoint, bool byDefault) =>
        _chosen.Knows(waypoint.Guid, out var chosen) ? chosen : byDefault;

    /// <summary>Records what somebody chose for one marker.</summary>
    public void Choose(string guid, bool keepToOwner) => _chosen.Say(guid, keepToOwner);
}
