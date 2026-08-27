using ProtoBuf;

namespace Mapstique;

/// <summary>
/// A player's own likeness, on its way to the server.
///
/// A PNG rather than anything the server could reconstruct. What a seraph looks
/// like is settled on the client — its skin parts are textures a dedicated server
/// does not ship, and its clothes and armour are a rendered scene rather than a
/// list of colours — so the only honest answer is a picture drawn by the machine
/// that can see it.
/// </summary>
/// <summary>Asks a player's client to draw them. Sent by the server.</summary>
[ProtoContract]
public class PortraitRequest
{
}

[ProtoContract]
public class PlayerPortrait
{
    [ProtoMember(1)]
    public byte[]? Png { get; set; }
}
