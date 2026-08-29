using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// What a waypoint is, as the rest of this mod needs it.
///
/// Four things were being worked out separately in as many files: where the
/// waypoint layer is, what identifies one waypoint, what its packed colour says
/// in CSS, and how to put a new one on the map. Each of those has one right
/// answer and no reason to have two, so each has one function here and every
/// caller is a thin call against it.
/// </summary>
public static class Markers
{
    /// <summary>
    /// The layer every waypoint lives on, or null on a server whose map manager
    /// is not up. Asked for rather than held: mods load in an order this does not
    /// choose, and a null answer on one tick is answered by the next.
    /// </summary>
    public static WaypointMapLayer? Layer(ICoreAPI api)
    {
        return api.ModLoader
            .GetModSystem<WorldMapManager>()?
            .MapLayers?
            .OfType<WaypointMapLayer>()
            .FirstOrDefault();
    }

    /// <summary>
    /// Identity for a marker as anything outside the game sees it.
    ///
    /// The waypoint's own guid where there is one; position and title stand in
    /// where there is not, which is enough to keep one marker from reading as two.
    /// </summary>
    public static string Key(Waypoint waypoint)
    {
        if (!string.IsNullOrEmpty(waypoint.Guid))
        {
            return waypoint.Guid;
        }

        return $"{(int)waypoint.Position.X}:{(int)waypoint.Position.Y}:{(int)waypoint.Position.Z}:{waypoint.Title}";
    }

    /// <summary>
    /// A waypoint's packed colour as CSS.
    ///
    /// The game packs one as ARGB — red in the high bytes — because every path
    /// that makes a waypoint ends in <c>Color.ToArgb</c> or
    /// <c>ColorUtil.Hex2Int</c>, and it draws one by reading red back out of bit
    /// 16. Reading the low byte as red instead swaps red and blue, which leaves
    /// grey and green looking right and everything else wrong.
    /// </summary>
    public static string Hex(int color)
    {
        return $"#{ColorUtil.ColorR(color):x2}{ColorUtil.ColorG(color):x2}{ColorUtil.ColorB(color):x2}";
    }

    /// <summary>
    /// The inverse: CSS back to what a waypoint stores. Opaque, because a
    /// waypoint with no alpha is a waypoint drawn as nothing.
    ///
    /// Null for anything that is not six hex digits behind a hash. What arrives
    /// here came from a browser, and a browser is not a thing whose word is taken.
    /// </summary>
    public static int? Packed(string? css)
    {
        if (css is null || css.Length != 7 || css[0] != '#')
        {
            return null;
        }

        if (!int.TryParse(css.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return null;
        }

        return rgb | unchecked((int)0xff000000);
    }

    /// <summary>
    /// The colours the game offers for a waypoint, in the order its own picker
    /// shows them.
    ///
    /// Read off the layer rather than written down here, so a mod that adds a
    /// colour adds it to the web map's picker as well without anything knowing
    /// about it in advance. The game ships three entries missing their hash,
    /// which <c>Hex2Int</c> parses to some other colour entirely; they come back
    /// as whatever the game itself would draw, because a picker that disagrees
    /// with the game is worse than one that repeats its mistake.
    /// </summary>
    public static List<string> Palette(ICoreAPI api)
    {
        var colors = Layer(api)?.WaypointColors;
        if (colors is null)
        {
            return new List<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var palette = new List<string>();
        foreach (var color in colors)
        {
            var hex = Hex(color);
            if (seen.Add(hex))
            {
                palette.Add(hex);
            }
        }
        return palette;
    }

    /// <summary>
    /// Whether this person may change this marker.
    ///
    /// Its owner always may. Anybody else may only where the operator has said
    /// public markers are everyone's to correct, and only for a marker that is
    /// in fact public — being shown something is not being handed it, and a
    /// private marker is never anybody's but its owner's whatever the setting
    /// says.
    ///
    /// The page works the same question out for itself, to decide whether to
    /// offer an edit at all. That answer is an affordance and this one is the
    /// gate: what the page believes about who owns what came from a post that
    /// may be a few seconds old, so the decision is taken again here against the
    /// waypoint itself.
    /// </summary>
    public static bool MayEdit(Waypoint waypoint, string uid, bool isPrivate, bool publicEditable)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }
        if (waypoint.OwningPlayerUid == uid)
        {
            return true;
        }
        return publicEditable && !isPrivate;
    }

    /// <summary>
    /// Who owns a marker, by name.
    ///
    /// Offline owners are looked up in the player data, so a marker still says
    /// whose it is when they are not on. Two places worked this out separately —
    /// the web feed and the in-game share — and a name is a name whichever asks.
    /// </summary>
    public static string OwnerName(ICoreServerAPI api, string? uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return "";
        }

        return api.World.PlayerByUid(uid)?.PlayerName
            ?? api.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername
            ?? "";
    }

    /// <summary>The waypoint with this guid, or null where there is none.</summary>
    public static Waypoint? ByGuid(ICoreAPI api, string guid)
    {
        var waypoints = Layer(api)?.Waypoints;
        if (waypoints is null || string.IsNullOrEmpty(guid))
        {
            return null;
        }

        foreach (var waypoint in waypoints.ToList())
        {
            if (waypoint?.Guid == guid)
            {
                return waypoint;
            }
        }
        return null;
    }

    /// <summary>
    /// Changes a waypoint that already exists, and makes sure its owner sees it.
    ///
    /// The layer resends a player's whole set rather than one waypoint, and the
    /// only way to ask it to is to add one — so an owner who is online is given
    /// the same waypoint back out of the list and in again. The guid does not
    /// move, so nothing that knows this marker loses track of it.
    /// </summary>
    public static void Change(
        ICoreServerAPI api,
        Waypoint waypoint,
        Vec3d position,
        string title,
        string icon,
        int color)
    {
        waypoint.Position = position;
        waypoint.Title = title;
        waypoint.Icon = icon;
        waypoint.Color = color;

        var layer = Layer(api);
        if (layer?.Waypoints is null)
        {
            return;
        }

        if (api.World.PlayerByUid(waypoint.OwningPlayerUid ?? "") is IServerPlayer owner)
        {
            layer.Waypoints.Remove(waypoint);
            layer.AddWaypoint(waypoint, owner);
        }
    }

    /// <summary>
    /// Puts a new waypoint on the server's map and gives back what was made.
    ///
    /// An owner who is online is told through the layer's own add, which resends
    /// their set so it appears on the map they have open. One who is not is added
    /// to the list directly: the layer resends a player's waypoints whenever their
    /// map view changes, so it reaches them when they next look, and it is written
    /// to the savegame with the rest either way.
    ///
    /// The guid is handed in rather than minted here. A marker asked for on the
    /// web is named by the service before the game has heard of it, and the
    /// browser watching for it to appear has only that name to match on — so the
    /// name it was promised is the name it is made under.
    /// </summary>
    public static Waypoint? Make(
        ICoreServerAPI api,
        string guid,
        string ownerUid,
        Vec3d position,
        string title,
        string icon,
        int color,
        bool pinned)
    {
        var layer = Layer(api);
        if (layer?.Waypoints is null)
        {
            return null;
        }

        var waypoint = new Waypoint
        {
            Position = position,
            Title = title,
            Icon = icon,
            Color = color,
            OwningPlayerUid = ownerUid,
            Pinned = pinned,
            Guid = guid,
        };

        if (api.World.PlayerByUid(ownerUid) is IServerPlayer online)
        {
            layer.AddWaypoint(waypoint, online);
        }
        else
        {
            layer.Waypoints.Add(waypoint);
        }

        return waypoint;
    }
}
