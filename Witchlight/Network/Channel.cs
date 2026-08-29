using System;
using Vintagestory.API.Client;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// The channel the two halves talk over, and everything it carries.
///
/// Both sides must register the same message types in the same order: the game
/// numbers them by the order they arrive in and matches a packet to a reader by
/// that number, so a list that differs by one entry is every message after it
/// being read as the wrong thing. It was written out twice — once on the client
/// and once on the server — which is two places to add to and one of them to
/// forget, with nothing anywhere saying so.
///
/// One list, registered by type rather than by generic argument so that the two
/// sides can share it despite their channels being different types. What each
/// side does with what arrives is genuinely different and stays where it is.
/// </summary>
public static class Channel
{
    /// <summary>What the channel is called, on both sides.</summary>
    public const string Name = "witchlight";

    /// <summary>
    /// Everything the channel carries, in the order both sides number them.
    ///
    /// Adding to the end is safe between builds of the same minor; inserting into
    /// the middle renumbers everything after it and is a protocol change.
    /// </summary>
    private static readonly Type[] Carries =
    {
        typeof(SharedMarkers),
        typeof(PaletteRequest),
        typeof(PaletteTable),
        typeof(IconRequest),
        typeof(IconTable),
        typeof(PortraitRequest),
        typeof(PlayerPortrait),
    };

    /// <summary>Registers everything the channel carries, on the server.</summary>
    public static IServerNetworkChannel Carrying(this IServerNetworkChannel channel)
    {
        foreach (var carried in Carries)
        {
            channel.RegisterMessageType(carried);
        }
        return channel;
    }

    /// <summary>The same list, on the client.</summary>
    public static IClientNetworkChannel Carrying(this IClientNetworkChannel channel)
    {
        foreach (var carried in Carries)
        {
            channel.RegisterMessageType(carried);
        }
        return channel;
    }
}
