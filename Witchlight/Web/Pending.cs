using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// One marker somebody asked for on the web map, made or changed.
///
/// The same nine fields whichever it is: the form the person filled in holds all
/// of a marker either way, and a patch of only what differs would need this end
/// to work out what "differs" meant against a marker somebody else may have moved
/// since. Two classes said this twice and would have drifted apart the first time
/// a field was added to one of them.
///
/// The key is minted by the service before the game has heard of the marker: the
/// browser that asked has to recognise its own marker arriving among everyone
/// else's, and a name agreed at the moment of asking is the only thing both ends
/// can match on beforehand. The game makes the waypoint under that same name.
/// </summary>
public class Wanted
{
    /// <summary>The guid the waypoint is, or will be, made under.</summary>
    public string Key { get; set; } = "";

    /// <summary>Whose marker it is. The service took this from their session.</summary>
    public string Uid { get; set; } = "";

    public string Title { get; set; } = "";
    public string Icon { get; set; } = Markers.PlainIcon;
    public string Color { get; set; } = Markers.WhiteHex;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    /// <summary>Whether its owner asked to keep it to themselves.</summary>
    public bool Private { get; set; }

    /// <summary>
    /// Which block this marker is about, where the map is saying so: the code of
    /// the block it was put on, or the pattern of a preset it has been made to
    /// look like. Empty is the ordinary case and means this side reads the world
    /// under it instead. See <see cref="Origins"/>.
    /// </summary>
    public string Block { get; set; } = "";

    /// <summary>What the marker is about, or nothing said. Trimmed here so that
    ///  whitespace and silence are the same answer.</summary>
    public string About => Block.Trim();

    /// <summary>Where the game should put it, in the middle of the block named.</summary>
    public Vec3d Position => new(X + 0.5, Y, Z + 0.5);

    /// <summary>The colour as a waypoint stores it, or a fallback for one that
    /// is not six hex digits behind a hash.</summary>
    public int Packed(int fallback) => Markers.Packed(Color) ?? fallback;

    /// <summary>The picture to draw it with, or the game's own default.</summary>
    public string Picture => Markers.Picture(Icon);

    /// <summary>What the marker is called, or something rather than nothing.</summary>
    public string Named => Markers.Title(Title);
}

/// <summary>
/// One marker somebody asked to be taken away.
///
/// A key and whose ask it was, and nothing else: a removal names a waypoint
/// rather than describing one, and the waypoint itself is what this side reads
/// before removing anything.
/// </summary>
public class Unwanted
{
    /// <summary>The guid of the waypoint to take away.</summary>
    public string Key { get; set; } = "";

    /// <summary>Who asked. The service took this from their session.</summary>
    public string Uid { get; set; } = "";
}

/// <summary>
/// One marker somebody asked to keep in sight, or to stop keeping.
///
/// A key, whose ask it was, and which way. Nothing about the marker itself: a pin
/// names a waypoint rather than describing one, and it changes nothing about the
/// marker — it changes what one person's own map shows. See <see cref="Pins"/>.
/// </summary>
public class Pinning
{
    /// <summary>The guid of the waypoint to keep in sight.</summary>
    public string Key { get; set; } = "";

    /// <summary>Whose map it is for. The service took this from their session.</summary>
    public string Uid { get; set; } = "";

    /// <summary>Whether they are keeping it in sight or no longer are.</summary>
    public bool On { get; set; }
}

/// <summary>Everything about markers the service was holding.</summary>
public class AskedMarkers
{
    public List<Wanted> Make { get; set; } = new();
    public List<Wanted> Change { get; set; } = new();
    public List<Unwanted> Remove { get; set; } = new();
    public List<Pinning> Pin { get; set; } = new();

    public bool Anything =>
        Make.Count > 0 || Change.Count > 0 || Remove.Count > 0 || Pin.Count > 0;
}

/// <summary>
/// Everything the service was holding when the mod last asked.
///
/// Two kinds of thing in one envelope because they share the one thing that
/// empties it: the mod collects on the tick that already posts positions, and a
/// second queue would be a second round trip every two seconds to find out that
/// nothing was in it.
///
/// One group per kind, because the two kinds answer the same three verbs. Flat,
/// this had grown a `Claims` beside a `Make` that only meant markers, which is
/// the shape a third kind of thing would have made worse.
/// </summary>
public class Asked
{
    public AskedMarkers Markers { get; set; } = new();
    public AskedClaims Claims { get; set; } = new();
}

/// <summary>How much of each kind of ask landed. Its own type rather than loose
///  numbers, because the caller says all of them out loud.</summary>
public readonly record struct Landed(
    int Made, int Changed, int Removed, int Pinned, Claimed Claims)
{
    /// <summary>Whether anything happened at all.</summary>
    public bool Anything => AnyMarkers || Claims.Anything;

    /// <summary>Whether any of it was a marker, which is what has to be shared
    ///  again. A claim the game has taken tells every client itself.</summary>
    public bool AnyMarkers => Made > 0 || Changed > 0 || Removed > 0 || Pinned > 0;
}

/// <summary>
/// What was asked for on the web, and the doing of it.
///
/// The service cannot reach the game — the channel between the two halves only
/// runs one way, from the mod that started the service to the service it started.
/// So a marker somebody types into the web form is not sent to the game; it waits
/// in the service until the mod asks, which it does on the tick that already
/// posts positions. What arrives here has already been checked by the service and
/// is checked again, because a waypoint that will not draw is worse on the game's
/// map than a form that refused.
/// </summary>
public static class Pending
{
    /// <summary>
    /// Does everything in the service's reply — the markers made, changed and
    /// removed, and the land claims drawn — and gives back how many of each
    /// landed.
    ///
    /// The claims are handed straight to <see cref="Claiming"/>, which owns every
    /// rule about whether one may exist. Nothing about a claim is decided here;
    /// this is the envelope and the counting.
    /// </summary>
    public static Landed Apply(
        ICoreServerAPI api, Visibility visibility, Pins pins, Origins origins, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        Asked? asked;
        try
        {
            asked = JsonConvert.DeserializeObject<Asked>(json);
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read what the map is holding: {0}", error.Message);
            return default;
        }

        if (asked is null)
        {
            return default;
        }

        return new Landed(
            Made(api, visibility, origins, asked.Markers.Make),
            Changed(api, visibility, origins, asked.Markers.Change),
            Removed(api, asked.Markers.Remove),
            Kept(api, visibility, pins, asked.Markers.Pin),
            Claiming.Apply(api, asked.Claims));
    }

    /// <summary>
    /// Markers somebody asked to keep in sight on their own map, and ones they
    /// asked to stop keeping.
    ///
    /// Whether they may is whether they may *see* it, which is a lower bar than
    /// changing one and deliberately so: a pin puts a marker on the pinner's map
    /// and on nobody else's, so anybody the marker is shared with may keep it in
    /// sight. Their own is always theirs. Decided here against the waypoint
    /// itself, because what the service believes about who owns what came from a
    /// post that is seconds old.
    /// </summary>
    private static int Kept(
        ICoreServerAPI api, Visibility visibility, Pins pins, List<Pinning> asked)
    {
        var byDefault = Settings.MarkersPrivateByDefault;

        var kept = 0;
        foreach (var pin in asked)
        {
            if (pin is null || string.IsNullOrEmpty(pin.Key) || string.IsNullOrEmpty(pin.Uid))
            {
                continue;
            }

            var waypoint = Markers.ByGuid(api, pin.Key);
            if (waypoint is null)
            {
                api.Logger.Notification(
                    "[witchlight] the map asked to pin a marker that is not there any more");
                continue;
            }

            if (waypoint.OwningPlayerUid != pin.Uid && visibility.IsPrivate(waypoint, byDefault))
            {
                api.Logger.Notification(
                    "[witchlight] {0} may not pin the marker \"{1}\"", pin.Uid, waypoint.Title);
                continue;
            }

            pins.Choose(api, waypoint, pin.Uid, pin.On);
            kept++;
        }

        return kept;
    }

    /// <summary>
    /// Markers somebody asked to be taken away.
    ///
    /// Whether they may is decided here and only here, against the waypoint
    /// itself, and only its owner ever may — see <see cref="Markers.Remove"/>.
    /// A marker that is already gone is not a failure worth a line in the log:
    /// two browsers open on one marker is two asks for the same removal.
    /// </summary>
    private static int Removed(ICoreServerAPI api, List<Unwanted> asked)
    {
        var removed = 0;
        foreach (var gone in asked)
        {
            if (gone is null || string.IsNullOrEmpty(gone.Key) || string.IsNullOrEmpty(gone.Uid))
            {
                continue;
            }

            var waypoint = Markers.ByGuid(api, gone.Key);
            if (waypoint is null)
            {
                continue;
            }

            if (!Markers.Remove(api, waypoint, gone.Uid))
            {
                api.Logger.Notification(
                    "[witchlight] {0} may not delete the marker \"{1}\"", gone.Uid, waypoint.Title);
                continue;
            }
            removed++;
        }

        return removed;
    }

    /// <summary>New markers, each owned by whoever the service says asked for it.</summary>
    private static int Made(
        ICoreServerAPI api, Visibility visibility, Origins origins, List<Wanted> asked)
    {
        var made = 0;
        foreach (var wanted in asked)
        {
            if (wanted is null || !Sound(api, wanted))
            {
                continue;
            }

            var waypoint = Markers.Make(
                api,
                wanted.Key,
                wanted.Uid,
                wanted.Position,
                wanted.Named,
                wanted.Picture,
                wanted.Packed(Markers.White),
                pinned: false);

            if (waypoint is null)
            {
                api.Logger.Warning("[witchlight] no waypoint layer, so a marker the map asked for was dropped");
                continue;
            }

            // Recorded whichever way it went. The operator's setting is the
            // fallback for a marker nobody decided about, and somebody filling in
            // this form decided — including when they decided to agree with it.
            visibility.Choose(waypoint.Guid, wanted.Private);
            origins.Made(waypoint.Guid, About(api, wanted));
            made++;
        }

        return made;
    }

    /// <summary>
    /// Changes to markers that already exist.
    ///
    /// Whether somebody may is decided here rather than taken from the service.
    /// The service knows who owns what only from the mod's last post, which is
    /// seconds old and says nothing about a marker made or given away since; the
    /// waypoint itself is the only thing that knows now.
    /// </summary>
    private static int Changed(
        ICoreServerAPI api, Visibility visibility, Origins origins, List<Wanted> asked)
    {
        var byDefault = Settings.MarkersPrivateByDefault;
        var editable = Settings.PublicMarkersEditable;

        var changed = 0;
        foreach (var edit in asked)
        {
            if (edit is null || string.IsNullOrEmpty(edit.Key) || string.IsNullOrEmpty(edit.Uid))
            {
                continue;
            }

            var waypoint = Markers.ByGuid(api, edit.Key);
            if (waypoint is null)
            {
                api.Logger.Notification(
                    "[witchlight] the map asked to change a marker that is not there any more");
                continue;
            }

            if (!Markers.MayEdit(waypoint, edit.Uid, visibility.IsPrivate(waypoint, byDefault), editable))
            {
                api.Logger.Notification(
                    "[witchlight] {0} may not change the marker \"{1}\"", edit.Uid, waypoint.Title);
                continue;
            }

            Markers.Change(
                api, waypoint, edit.Position, edit.Named, edit.Picture, edit.Packed(waypoint.Color));

            origins.Made(waypoint.Guid, About(api, edit));

            // Somebody who may change a marker may change who sees it. On a
            // marker they do not own that is only ever public to public, since
            // making it private would be taking it away from its owner's map.
            if (waypoint.OwningPlayerUid == edit.Uid)
            {
                visibility.Choose(waypoint.Guid, edit.Private);
            }
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Which block a marker is about: what the ask says, or failing that what is
    /// actually there.
    ///
    /// The map says so when it knows — the block under a right click, or the
    /// pattern of a preset a screenful of markers has just been made to look
    /// like, which is a thing only that side can know. It is silent otherwise,
    /// and then this side reads the world, which is the only half that can.
    ///
    /// Nothing rests on the answer but which preset the map offers for this
    /// marker, so a page saying something odd costs a page its own presets. What
    /// it must not do is *lose* an answer: silence means read, not forget.
    /// </summary>
    private static string About(ICoreServerAPI api, Wanted wanted)
    {
        var said = wanted.About;
        return said.Length > 0 ? said : Marking.CodeUnder(api, wanted.X, wanted.Y, wanted.Z);
    }

    /// <summary>
    /// Whether this is a marker the game can be asked to make. A key and an owner
    /// are the two things nothing downstream can invent.
    /// </summary>
    private static bool Sound(ICoreServerAPI api, Wanted wanted)
    {
        if (string.IsNullOrEmpty(wanted.Key) || string.IsNullOrEmpty(wanted.Uid))
        {
            api.Logger.Warning("[witchlight] the map asked for a marker with no name or no owner");
            return false;
        }
        return true;
    }

}
