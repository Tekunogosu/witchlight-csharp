using ProtoBuf;

namespace Witchlight;

/// <summary>Asks a player's client to draw them. Sent by the server.</summary>
[ProtoContract]
public class PortraitRequest
{
}

/// <summary>
/// A player's own likeness, on its way to the server.
///
/// A PNG rather than anything the server could reconstruct. What a seraph looks
/// like is settled on the client — its skin parts are textures a dedicated server
/// does not ship, and its clothes and armour are a rendered scene rather than a
/// list of colours — so the only honest answer is a picture drawn by the machine
/// that can see it.
/// </summary>
[ProtoContract]
public class PlayerPortrait
{
    /// <summary>
    /// The picture. How large one may be is answered where it is filed, and how
    /// often one may arrive is answered where it is taken.
    /// </summary>
    [ProtoMember(1)]
    public byte[]? Png { get; set; }
}
