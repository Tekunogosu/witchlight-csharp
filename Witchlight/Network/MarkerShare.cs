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
}

/// <summary>Everything one player should see from everyone else.</summary>
[ProtoContract]
public class SharedMarkers
{
    [ProtoMember(1)]
    public List<SharedMarker> Markers { get; set; } = new();
}
