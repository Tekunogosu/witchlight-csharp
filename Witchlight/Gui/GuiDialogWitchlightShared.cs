using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// The window that opens on somebody else's marker from the in-game map.
///
/// The game's own edit window cannot open here: it names a waypoint by its place
/// in the player's own list, and a shared marker has none. This names it by key.
///
/// What it offers is what the server said this player may do. Anybody the marker
/// is shared with may keep it in sight on their own map, which is the pin, and
/// changes nothing anybody else sees. Only somebody the server said may change
/// it is shown the name, the colour and the picture to change; everybody else
/// reads the name and decides about the pin.
///
/// The colours and the pictures are the game's own, read off the waypoint layer,
/// so what is offered here is exactly what the game's own window offers.
/// </summary>
public class GuiDialogWitchlightShared : GuiDialogGeneric
{
    private const string Composed = "witchlight-shared";
    private const string NameInput = "nameInput";
    private const string ColourPicker = "colorPicker";
    private const string PicturePicker = "iconPicker";
    private const string PinSwitch = "pinSwitch";

    /// <summary>The game's own numbers for a switch, said once.</summary>
    private const int SwitchSize = 30;
    private const int SwitchPad = 4;
    private const int SwitchWidth = 40;

    private readonly int[] _colours;
    private readonly string[] _pictures;
    private readonly Action<SharedMarkerChange> _send;

    private SharedMarker _marker;
    private string _picture;
    private int _colour;
    private bool _pinned;

    public GuiDialogWitchlightShared(
        ICoreClientAPI capi, WaypointMapLayer layer, SharedMarker marker, Action<SharedMarkerChange> send)
        : base("", capi)
    {
        _colours = layer.WaypointColors.ToArray();
        _pictures = layer.WaypointIcons.Keys.ToArray();
        _send = send;
        _marker = marker;
        _pinned = marker.Pinned;
        _picture = _pictures.Contains(marker.Icon) ? marker.Icon : (_pictures.FirstOrDefault() ?? Markers.PlainIcon);
        // A colour the game does not offer is kept as it is: the picker shows
        // its first swatch, and saving without touching it keeps the colour the
        // marker had rather than the swatch it happened to land on.
        _colour = marker.Color;
    }

    /// <summary>Which marker this window is open on.</summary>
    public string Key => _marker.Key;

    /// <summary>
    /// A dialog rather than a piece of the world map, so it takes the mouse the
    /// way every other window does and closes on the key that closes those.
    /// </summary>
    public override bool PrefersUngrabbedMouse => true;

    public override bool TryOpen()
    {
        Compose();
        return base.TryOpen();
    }

    /// <summary>
    /// The marker as the server now says it is. Only the pin is taken up: that
    /// is what this window asked for and is waiting to see land. Anything
    /// somebody is in the middle of typing stays as they typed it.
    /// </summary>
    public void Shown(SharedMarker marker)
    {
        _marker = marker;
        if (_pinned != marker.Pinned)
        {
            _pinned = marker.Pinned;
            SingleComposer?.GetSwitch(PinSwitch)?.SetValue(_pinned);
        }
    }

    private void Compose()
    {
        var label = ElementBounds.Fixed(0, 28, 100, 25);
        var field = label.RightCopy();
        var row = ElementBounds.Fixed(0, 28, 360, 25);
        var toggle = ElementBounds.Fixed(0, 28, SwitchWidth, SwitchSize);

        var inside = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        inside.BothSizing = ElementSizing.FitToChildren;
        inside.WithChildren(label, field);

        SingleComposer?.Dispose();
        var compo = capi.Gui
            .CreateCompo(Composed, ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0))
            .AddShadedDialogBG(inside, false)
            .AddDialogTitleBar(Heading(), () => TryClose())
            .BeginChildElements(inside)
            .AddStaticText("Name", CairoFont.WhiteSmallText(), label = label.FlatCopy());

        if (_marker.Editable)
        {
            compo = compo
                .AddTextInput(field = field.FlatCopy().WithFixedWidth(220), _ => { },
                    CairoFont.TextInput(), NameInput)
                .AddStaticText("Colour", CairoFont.WhiteSmallText(),
                    label = label.BelowCopy(0, 9))
                .AddColorListPicker(_colours, OnColour,
                    label = label.BelowCopy(0, 5).WithFixedSize(22, 22), 270, ColourPicker)
                .AddStaticText("Picture", CairoFont.WhiteSmallText(),
                    label = label.WithFixedPosition(0, label.fixedY + label.fixedHeight)
                        .WithFixedWidth(200).BelowCopy())
                .AddIconListPicker(_pictures, OnPicture,
                    label = label.BelowCopy(0, 5).WithFixedSize(27, 27), 270, PicturePicker);
            toggle = ElementBounds.Fixed(
                0, label.fixedY + label.fixedHeight * 2 + 9 - 4, SwitchWidth, SwitchSize);
        }
        else
        {
            compo = compo.AddStaticText(Markers.Title(_marker.Title), CairoFont.WhiteSmallText(),
                field = field.FlatCopy().WithFixedWidth(220));
            toggle = ElementBounds.Fixed(0, label.fixedY + label.fixedHeight + 9, SwitchWidth, SwitchSize);
        }

        SingleComposer = compo
            // The one thing anybody may decide about somebody else's marker:
            // whether their own map holds it against the edge rather than
            // letting it scroll off. Theirs alone; nobody else's map moves.
            .AddSwitch(on => _pinned = on, toggle, PinSwitch, SwitchSize, SwitchPad)
            .AddStaticText("Keep in sight", CairoFont.WhiteSmallText(), Beside(toggle))
            .AddSmallButton("Cancel", OnCancel,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100),
                EnumButtonStyle.Normal)
            .AddSmallButton("Save", OnSave,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100)
                    .WithAlignment(EnumDialogArea.RightFixed),
                EnumButtonStyle.Normal)
            .EndChildElements()
            .Compose();

        SingleComposer.GetSwitch(PinSwitch).SetValue(_pinned);
        if (_marker.Editable)
        {
            SingleComposer.GetTextInput(NameInput).SetValue(Markers.Title(_marker.Title));
            SingleComposer.ColorListPickerSetValue(ColourPicker, Math.Max(0, Array.IndexOf(_colours, _colour)));
            SingleComposer.IconListPickerSetValue(PicturePicker, Math.Max(0, Array.IndexOf(_pictures, _picture)));
        }
    }

    /// <summary>Whose marker it is, on the bar, so the name field is the name alone.</summary>
    private string Heading() =>
        string.IsNullOrEmpty(_marker.Owner) ? "Shared marker" : $"{_marker.Owner}'s marker";

    /// <summary>The words that go beside a switch, level with the line of text
    ///  the switch is centred on.</summary>
    private static ElementBounds Beside(ElementBounds toggle) =>
        ElementBounds.Fixed(toggle.fixedX + SwitchWidth + 6, toggle.fixedY + 4, 280, 25);

    private void OnColour(int index)
    {
        _colour = _colours[GuiElements.Within(index, _colours.Length)];
    }

    private void OnPicture(int index)
    {
        _picture = _pictures[GuiElements.Within(index, _pictures.Length)];
    }

    private bool OnCancel()
    {
        TryClose();
        return true;
    }

    /// <summary>
    /// Sends what the window is holding, and gets out of the way. Nothing is
    /// changed here: the marker lives on the server, and what came of the ask
    /// arrives with the next share.
    /// </summary>
    private bool OnSave()
    {
        var change = new SharedMarkerChange
        {
            Key = _marker.Key,
            Pinned = _pinned,
            Editing = _marker.Editable,
            Title = _marker.Title,
            Icon = _marker.Icon,
            Color = _marker.Color,
        };
        if (_marker.Editable)
        {
            change.Title = SingleComposer.GetTextInput(NameInput).GetText();
            change.Icon = _picture;
            change.Color = _colour;
        }
        _send(change);
        TryClose();
        return true;
    }
}
