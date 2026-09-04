using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// One shared marker, drawn on the map.
///
/// Drawn exactly as the game draws a waypoint — this is the game's own waypoint
/// component, given a waypoint built from what the server said and the game's
/// waypoint layer to borrow its pictures from — so a marker somebody else made
/// looks like one of the player's own. What differs is what a click does: the
/// game's component opens a window that names the waypoint by its place in the
/// player's list, and this one has no such place, so a click opens this mod's
/// window instead, which names it by key.
/// </summary>
public sealed class SharedMarkerComponent : WaypointMapComponent
{
    /// <summary>How close a click has to land, in the game's own reach.</summary>
    private const float ReachPx = 8f;

    private readonly WaypointMapLayer _game;
    private readonly Waypoint _waypoint;
    private readonly SharedMarker _marker;
    private readonly Action<SharedMarker> _open;

    public SharedMarkerComponent(
        ICoreClientAPI capi, WaypointMapLayer game, SharedMarker marker, Action<SharedMarker> open)
        : this(capi, game, marker, open, Drawn(marker))
    {
    }

    private SharedMarkerComponent(
        ICoreClientAPI capi,
        WaypointMapLayer game,
        SharedMarker marker,
        Action<SharedMarker> open,
        Waypoint waypoint)
        : base(0, waypoint, game, capi)
    {
        _game = game;
        _waypoint = waypoint;
        _marker = marker;
        _open = open;
    }

    /// <summary>
    /// The waypoint the game's drawing reads. Owned by nobody: it is never in
    /// the game's list and nothing may mistake it for one that is.
    /// </summary>
    private static Waypoint Drawn(SharedMarker marker) => new()
    {
        Position = new Vec3d(marker.X, marker.Y, marker.Z),
        Title = Label(marker),
        Icon = marker.Icon,
        Color = marker.Color,
        OwningPlayerUid = null,
        Pinned = marker.Pinned,
        Guid = "witchlight:" + marker.Key,
    };

    /// <summary>
    /// Whose marker this is, said on the marker itself — every death marker is
    /// called "You died here", and on a shared map that needs a name against it.
    /// </summary>
    public static string Label(SharedMarker marker)
    {
        var title = Markers.Title(marker.Title);
        return string.IsNullOrEmpty(marker.Owner) ? title : $"{title} ({marker.Owner})";
    }

    /// <summary>
    /// The game's drawing, once the game has the pictures to draw with. The
    /// waypoint layer loads them when the map opens; before that there is
    /// nothing to draw and the game's own drawing would fall over reaching for
    /// them.
    /// </summary>
    public override void Render(GuiElementMap map, float dt)
    {
        if (_game.texturesByIcon is null || _game.quadModel is null)
        {
            return;
        }
        base.Render(map, dt);
    }

    /// <summary>
    /// The game's own hover — it is what makes the marker swell under the
    /// mouse — with the game's words swapped for this marker's. The game would
    /// say "Waypoint 0", which is the place it has in nobody's list.
    /// </summary>
    public override void OnMouseMove(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        base.OnMouseMove(args, mapElem, new StringBuilder());
        if (Under(args, mapElem))
        {
            hoverText.AppendLine(_waypoint.Title);
        }
    }

    /// <summary>A right click on the marker opens this mod's window on it.</summary>
    public override void OnMouseUpOnElement(MouseEvent args, GuiElementMap mapElem)
    {
        if (args.Button != EnumMouseButton.Right || !Under(args, mapElem))
        {
            return;
        }
        _open(_marker);
        args.Handled = true;
    }

    /// <summary>
    /// Whether the mouse is on the marker: the game's own arithmetic, including
    /// where a pinned one is held against the edge of the map.
    /// </summary>
    private bool Under(MouseEvent args, GuiElementMap mapElem)
    {
        var view = new Vec2f();
        mapElem.TranslateWorldPosToViewPos(_waypoint.Position, ref view);
        var bounds = mapElem.Bounds;
        double x = view.X + bounds.renderX;
        double y = view.Y + bounds.renderY;
        if (_waypoint.Pinned)
        {
            mapElem.ClampButPreserveAngle(ref view, 2);
            x = GameMath.Clamp(view.X + bounds.renderX,
                bounds.renderX + 2, bounds.renderX + bounds.InnerWidth - 2);
            y = GameMath.Clamp(view.Y + bounds.renderY,
                bounds.renderY + 2, bounds.renderY + bounds.InnerHeight - 2);
        }

        var reach = RuntimeEnv.GUIScale * ReachPx;
        return Math.Abs(args.X - x) < reach && Math.Abs(args.Y - y) < reach;
    }
}
