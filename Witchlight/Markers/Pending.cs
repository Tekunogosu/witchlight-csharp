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
    public string Icon { get; set; } = "circle";
    public string Color { get; set; } = "#ffffff";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    /// <summary>Whether its owner asked to keep it to themselves.</summary>
    public bool Private { get; set; }

    /// <summary>Where the game should put it, in the middle of the block named.</summary>
    public Vec3d Position => new(X + 0.5, Y, Z + 0.5);

    /// <summary>The colour as a waypoint stores it, or a fallback for one that
    /// is not six hex digits behind a hash.</summary>
    public int Packed(int fallback) => Markers.Packed(Color) ?? fallback;

    /// <summary>The picture to draw it with, or the game's own default.</summary>
    public string Picture => string.IsNullOrEmpty(Icon) ? "circle" : Icon;

    /// <summary>What the marker is called, or something rather than nothing.</summary>
    public string Named
    {
        get
        {
            var said = (Title ?? "").Trim();
            if (said.Length == 0)
            {
                return "Marker";
            }
            return said.Length > LongestTitle ? said[..LongestTitle] : said;
        }
    }

    /// <summary>The most a marker's name may be. Longer is a paragraph, not a name.</summary>
    private const int LongestTitle = 128;
}

/// <summary>Everything the service was holding when the mod last asked.</summary>
public class Asked
{
    public List<Wanted> Make { get; set; } = new();
    public List<Wanted> Change { get; set; } = new();
}

/// <summary>
/// Markers asked for on the web, and the making of them.
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
    /// Makes and changes every marker in the service's reply, and records what
    /// each owner chose about who may see it. Gives back how many of each landed.
    /// </summary>
    public static (int Made, int Changed) Apply(ICoreServerAPI api, Visibility visibility, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (0, 0);
        }

        Asked? asked;
        try
        {
            asked = JsonConvert.DeserializeObject<Asked>(json);
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read the markers the map is holding: {0}", error.Message);
            return (0, 0);
        }

        if (asked is null)
        {
            return (0, 0);
        }

        return (Made(api, visibility, asked.Make), Changed(api, visibility, asked.Change));
    }

    /// <summary>New markers, each owned by whoever the service says asked for it.</summary>
    private static int Made(ICoreServerAPI api, Visibility visibility, List<Wanted> asked)
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
                wanted.Packed(White),
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
    private static int Changed(ICoreServerAPI api, Visibility visibility, List<Wanted> asked)
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

    /// <summary>A marker whose colour did not survive the trip is drawn white.</summary>
    private static readonly int White = Markers.Packed("#ffffff")!.Value;
}
