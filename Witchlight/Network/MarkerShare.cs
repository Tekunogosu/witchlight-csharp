using System.Collections.Generic;
using ProtoBuf;

namespace Witchlight;

/// <summary>
/// One player's marker, as it travels to everyone else's client.
///
/// Carries no player uid: the clients only need to know whose marker it is by
/// name, and identity is the server's business.
/// </summary>
[ProtoContract]
public class SharedMarker
{
    /// <summary>Stable identity, so a client can tell a new marker from one it has.</summary>
    [ProtoMember(1)]
    public string Key { get; set; } = "";

    [ProtoMember(2)] public double X { get; set; }
    [ProtoMember(3)] public double Y { get; set; }
    [ProtoMember(4)] public double Z { get; set; }
    [ProtoMember(5)] public string Title { get; set; } = "";
    [ProtoMember(6)] public string Icon { get; set; } = Markers.PlainIcon;
    [ProtoMember(7)] public int Color { get; set; }
    [ProtoMember(8)] public string Owner { get; set; } = "";

    /// <summary>
    /// Whether the player this is being sent to keeps it in sight — the game's
    /// own pin, which holds a waypoint against the edge of the map rather than
    /// letting it scroll off. One person's answer about one marker, which is why
    /// it rides the per-player send rather than the marker itself.
    /// </summary>
    [ProtoMember(9)] public bool Pinned { get; set; }

    /// <summary>
    /// Whether the player this is being sent to may change it. Decided by the
    /// server, which holds the rule, so the map can offer the fields or not
    /// without a client ever guessing at what the server would allow.
    /// </summary>
    [ProtoMember(10)] public bool Editable { get; set; }
}

/// <summary>
/// What a player asks of somebody else's marker from the in-game map.
///
/// A shared marker is drawn by this mod rather than by the game, so the game's
/// own edit window cannot reach it: that window names a waypoint by its place in
/// the asker's own list, and a marker that is not theirs has no such place.
/// This names it by key, which is the one name both halves agree on.
///
/// Every ask carries the pin, and one that is also an edit says so. Whether the
/// asker may do either is the server's to decide against the waypoint itself.
/// </summary>
[ProtoContract]
public class SharedMarkerChange
{
    [ProtoMember(1)] public string Key { get; set; } = "";

    /// <summary>Whether the asker keeps this marker in sight on their own map.</summary>
    [ProtoMember(2)] public bool Pinned { get; set; }

    /// <summary>Whether the fields below are meant, or only the pin.</summary>
    [ProtoMember(3)] public bool Editing { get; set; }

    [ProtoMember(4)] public string Title { get; set; } = "";
    [ProtoMember(5)] public string Icon { get; set; } = Markers.PlainIcon;
    [ProtoMember(6)] public int Color { get; set; }
}

/// <summary>Everything one player should see from everyone else.</summary>
[ProtoContract]
public class SharedMarkers
{
    [ProtoMember(1)]
    public List<SharedMarker> Markers { get; set; } = new();
}
