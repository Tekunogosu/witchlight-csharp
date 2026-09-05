using System.Collections.Generic;
using ProtoBuf;

namespace Witchlight;

/// <summary>
/// A marker asked for from in game, and the answer to it.
///
/// The map's own form asks the service and waits for the marker to arrive; a
/// client asks the server directly, because it is already talking to it and the
/// server is where the waypoint lives. What the two have in common is that
/// nobody is trusted to say whose marker it is: the owner is the player the
/// packet arrived from, exactly as the service takes it from a session.
///
/// Which preset applies is decided on the server, not here. It is the side that
/// can read the block at a position and the side that can ask the map service
/// what that player has kept, and a client that worked it out for itself would
/// be a second answer to a question with one right one.
/// </summary>
[ProtoContract]
public class MarkAsk
{
    /// <summary>Where the marker goes, in the world's own numbers.</summary>
    [ProtoMember(1)] public double X { get; set; }
    [ProtoMember(2)] public double Y { get; set; }
    [ProtoMember(3)] public double Z { get; set; }

    /// <summary>
    /// Which block names it: the one being looked at, or the one underfoot.
    ///
    /// Not the same as the position above. Somebody standing on a rock and
    /// pressing the key means a marker where they stand, named after the rock —
    /// and the block at their feet is air. The server reads the block itself
    /// from these three numbers rather than taking a code from a client.
    /// </summary>
    [ProtoMember(4)] public int BlockX { get; set; }
    [ProtoMember(5)] public int BlockY { get; set; }
    [ProtoMember(6)] public int BlockZ { get; set; }

    /// <summary>
    /// Whether a preset should decide what this marker is.
    ///
    /// True from the hotkey and the chat command, which is the whole of what
    /// those two do: mark this the way I have said this kind of thing is marked.
    /// Where no preset names the block, nothing is made and the answer says so,
    /// so that the client can open a window rather than guess.
    ///
    /// False from that window, which has already asked every question a preset
    /// would have answered.
    /// </summary>
    [ProtoMember(7)] public bool UsePreset { get; set; }

    [ProtoMember(8)] public string Title { get; set; } = "";
    [ProtoMember(9)] public string Icon { get; set; } = Markers.PlainIcon;
    [ProtoMember(10)] public string Color { get; set; } = "";

    /// <summary>
    /// Who may see it, as one of <see cref="Mark"/>'s three answers.
    ///
    /// A number rather than a nullable, because "nobody has said" is a real third
    /// answer and every path that flattened it to false made somebody's marker
    /// public on a server whose default is not.
    /// </summary>
    [ProtoMember(11)] public int Private { get; set; } = Mark.Unsaid;

    /// <summary>Whether this is also being kept as what that block starts as.</summary>
    [ProtoMember(12)] public bool KeepPreset { get; set; }

    /// <summary>What it would be kept against. Empty means the block itself.</summary>
    [ProtoMember(13)] public string Pattern { get; set; } = "";
}

/// <summary>
/// What became of an ask, and everything a window would need to finish it.
///
/// One answer for both outcomes rather than two packets. "Made it" and "no
/// preset names that, here is what you would start from" are the same question
/// answered, and the fields a window fills itself in from are the fields the
/// marker would have been made with.
/// </summary>
[ProtoContract]
public class MarkReply
{
    /// <summary>Whether a marker now exists.</summary>
    [ProtoMember(1)] public bool Made { get; set; }

    /// <summary>What to say to the player, in words meant to be read.</summary>
    [ProtoMember(2)] public string Said { get; set; } = "";

    /// <summary>Whether the client should open its window on this.</summary>
    [ProtoMember(3)] public bool Yours { get; set; }

    [ProtoMember(4)] public double X { get; set; }
    [ProtoMember(5)] public double Y { get; set; }
    [ProtoMember(6)] public double Z { get; set; }
    [ProtoMember(7)] public int BlockX { get; set; }
    [ProtoMember(8)] public int BlockY { get; set; }
    [ProtoMember(9)] public int BlockZ { get; set; }

    /// <summary>What the block at that spot is called, and its code.</summary>
    [ProtoMember(10)] public string Block { get; set; } = "";
    [ProtoMember(11)] public string Pattern { get; set; } = "";

    [ProtoMember(12)] public string Title { get; set; } = "";
    [ProtoMember(13)] public string Icon { get; set; } = "";
    [ProtoMember(14)] public string Color { get; set; } = "";

    /// <summary>Who would see it, already decided against this person's own
    /// default and the operator's, so a window has one answer to show and never
    /// <see cref="Mark.Unsaid"/>.</summary>
    [ProtoMember(15)] public int Private { get; set; }

    /// <summary>Whether the window's "keep as preset" starts on, which is what
    /// this person set on the map for themselves.</summary>
    [ProtoMember(16)] public bool KeepPreset { get; set; }

    /// <summary>
    /// Everything this person has kept, for the window to offer as a starting
    /// point. Sent only with an answer that opens the window: a marker already
    /// made has no use for the list, and the list is the bulk of the packet.
    /// </summary>
    [ProtoMember(17)] public List<PresetOffer> Presets { get; set; } = new();
}

/// <summary>
/// One preset as the window is offered it: what a marker made from it would be.
///
/// The service's own record carries the same five things — see
/// <see cref="Preset"/> — and this is that record in the shape that travels,
/// with "nobody said" for its privacy spelled the way <see cref="Mark"/> spells
/// it, so the window can fall back the same way the server does.
/// </summary>
[ProtoContract]
public class PresetOffer
{
    [ProtoMember(1)] public string Pattern { get; set; } = "";
    [ProtoMember(2)] public string Title { get; set; } = "";
    [ProtoMember(3)] public string Icon { get; set; } = "";
    [ProtoMember(4)] public string Color { get; set; } = "";
    [ProtoMember(5)] public int Private { get; set; } = Mark.Unsaid;
}

/// <summary>
/// Asks a player's own client to mark what they are looking at.
///
/// The game keeps client and server commands in separate registries and gives
/// them different prefixes, so `.wl mark` and `/wl mark` are two commands. They
/// are not two behaviours: which block somebody is looking at exists only on
/// their own machine, so the server's copy asks that machine the question rather
/// than answering a poorer version of it from where they happen to be standing.
///
/// Carries nothing. Everything it would say is something the client is better
/// placed to work out.
/// </summary>
[ProtoContract]
public class MarkNudge
{
}

/// <summary>
/// The three answers to who may see a marker, as they travel.
///
/// **Unsaid is zero, and nothing else is.** Protobuf leaves out a field holding
/// its type's default and the far end fills in whatever its own property
/// initializer said, so any meaning given to zero is a meaning that cannot be
/// told from silence. Zero is therefore the one answer that already means
/// silence, and an explicit `public` survives the wire because it is a one.
/// </summary>
public static class Mark
{
    /// <summary>Nobody has said who may see it, so a preset — and failing that
    /// this person's own default — decides. See <see cref="MarkAsk.Private"/>.</summary>
    public const int Unsaid = 0;

    /// <summary>Everybody on the server may see it.</summary>
    public const int Public = 1;

    /// <summary>Only its owner may.</summary>
    public const int Private = 2;

    /// <summary>Whether a number means private, given what decides an unsaid one.</summary>
    public static bool IsPrivate(int said, bool byDefault) =>
        said == Unsaid ? byDefault : said == Private;

    /// <summary>The number that says this, for the side that holds a boolean.</summary>
    public static int Says(bool kept) => kept ? Private : Public;
}
