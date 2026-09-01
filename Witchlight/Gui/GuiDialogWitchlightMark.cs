using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// The window a marker is made in from in game.
///
/// The game has one of its own, reached by opening the map and right clicking,
/// and this is not a replacement for it: that one makes a waypoint and this one
/// makes a Witchlight marker, which is the same waypoint plus the two things the
/// game has no idea about — who may see it, and whether this is what that kind of
/// block starts as from now on.
///
/// It is opened by the one thing that could not be answered without asking: a
/// press of the key over a block no preset names. Everything on it arrives filled
/// in from the server, so the ordinary case is reading a name and pressing Save.
///
/// The colours and the pictures are the game's own, read off the waypoint layer,
/// so what is offered here is exactly what the game's own window offers and a mod
/// that adds one adds it here.
/// </summary>
public class GuiDialogWitchlightMark : GuiDialogGeneric
{
    private const string Composed = "witchlight-mark";
    private const string NameInput = "nameInput";
    private const string ColourPicker = "colorPicker";
    private const string PicturePicker = "iconPicker";
    private const string PrivateSwitch = "privateSwitch";
    private const string PresetSwitch = "presetSwitch";

    private readonly int[] _colours;
    private readonly string[] _pictures;
    private readonly MarkReply _offer;
    private readonly Action<MarkAsk> _send;

    private string _picture;
    private string _colour;
    private bool _private;
    private bool _preset;

    public GuiDialogWitchlightMark(
        ICoreClientAPI capi, WaypointMapLayer layer, MarkReply offer, Action<MarkAsk> send)
        : base("", capi)
    {
        _colours = layer.WaypointColors.ToArray();
        _pictures = layer.WaypointIcons.Keys.ToArray();
        _offer = offer;
        _send = send;

        _private = offer.Private == Mark.Private;
        _preset = offer.KeepPreset;
        _picture = Offered(_pictures, offer.Icon, Markers.PlainIcon);
        // The colour of the swatch that will be selected, rather than the one
        // that was offered. A colour the game does not have leaves the picker on
        // its first swatch, and a window showing one colour and holding another
        // makes a marker nobody chose.
        _colour = _colours.Length > 0 ? Markers.Hex(_colours[Chosen(offer.Color)]) : offer.Color;
    }

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
    /// What the window is, laid out in one pass.
    ///
    /// A name, the two pickers the game itself offers, and then the two answers
    /// that make this a Witchlight marker rather than a waypoint. The two ways
    /// out are last and at opposite ends of their row, which is where the game's
    /// own window puts them.
    /// </summary>
    private void Compose()
    {
        var label = ElementBounds.Fixed(0, 28, 100, 25);
        var field = label.RightCopy();
        var row = ElementBounds.Fixed(0, 28, 360, 25);

        var inside = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        inside.BothSizing = ElementSizing.FitToChildren;
        inside.WithChildren(label, field);

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui
            .CreateCompo(Composed, ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0))
            .AddShadedDialogBG(inside, false)
            .AddDialogTitleBar($"Witchlight marker — {Where()}", () => TryClose())
            .BeginChildElements(inside)

            .AddStaticText("Name", CairoFont.WhiteSmallText(), label = label.FlatCopy())
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
                label = label.BelowCopy(0, 5).WithFixedSize(27, 27), 270, PicturePicker)

            // The two answers the game's own window has no idea about. Said in
            // words rather than as a mark, because a switch in a game dialog is
            // read beside its label and there is nowhere here for an emblem to
            // mean anything.
            .AddStaticText("Private", CairoFont.WhiteSmallText(),
                label = label.WithFixedPosition(0, label.fixedY + label.fixedHeight)
                    .WithFixedWidth(140).BelowCopy(0, 9))
            .AddSwitch(on => _private = on,
                label.RightCopy(20, -4).WithFixedWidth(40), PrivateSwitch, 30, 4)

            .AddStaticText(PresetWords(), CairoFont.WhiteSmallText(),
                label = label.BelowCopy(0, 9).WithFixedWidth(280))
            .AddSwitch(on => _preset = on,
                label.BelowCopy(0, 5).WithFixedWidth(40), PresetSwitch, 30, 4)

            .AddSmallButton("Cancel", OnCancel,
                row.FlatCopy().FixedUnder(label, 34).WithFixedWidth(100),
                EnumButtonStyle.Normal)
            .AddSmallButton("Save", OnSave,
                row.FlatCopy().FixedUnder(label, 34).WithFixedWidth(100)
                    .WithAlignment(EnumDialogArea.RightFixed),
                EnumButtonStyle.Normal)
            .EndChildElements()
            .Compose();

        SingleComposer.GetTextInput(NameInput).SetValue(_offer.Title);
        SingleComposer.GetSwitch(PrivateSwitch).SetValue(_private);
        SingleComposer.GetSwitch(PresetSwitch).SetValue(_preset);
        SingleComposer.ColorListPickerSetValue(ColourPicker, Chosen(_colour));
        SingleComposer.IconListPickerSetValue(PicturePicker, Array.IndexOf(_pictures, _picture));
    }

    /// <summary>What keeping this as a preset would key it on, said out loud —
    ///  the whole of what a reader needs to decide whether they want it.</summary>
    private string PresetWords()
    {
        var what = _offer.Block.Length > 0 ? _offer.Block
            : _offer.Pattern.Length > 0 ? _offer.Pattern
            : "this block";
        return $"Keep as what {what} starts as";
    }

    /// <summary>Where the marker goes, in the world's own numbers.</summary>
    private string Where() =>
        $"{Blocks.At(_offer.X)}, {Blocks.At(_offer.Y)}, {Blocks.At(_offer.Z)}";

    /// <summary>Which swatch is the colour the server offered, or the first.</summary>
    private int Chosen(string colour)
    {
        var packed = Markers.Packed(colour);
        if (packed is null)
        {
            return 0;
        }
        var at = Array.IndexOf(_colours, packed.Value);
        return at < 0 ? 0 : at;
    }

    /// <summary>The picture the server offered, where the game has one by that
    ///  name. A preset naming a picture from a mod since removed is not worth a
    ///  window that will not open.</summary>
    private static string Offered(string[] offered, string wanted, string instead)
    {
        if (offered.Length == 0)
        {
            return instead;
        }
        return Array.IndexOf(offered, wanted) >= 0 ? wanted
            : Array.IndexOf(offered, instead) >= 0 ? instead
            : offered[0];
    }

    private void OnColour(int index)
    {
        _colour = Markers.Hex(_colours[GuiElements.Within(index, _colours.Length)]);
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
    /// Sends what the window is holding, and gets out of the way.
    ///
    /// Nothing is made here and the window does not pretend otherwise: the
    /// waypoint lives on the server and the preset lives on the map service, so
    /// this asks and closes, and what came of it arrives as a line of chat.
    /// </summary>
    private bool OnSave()
    {
        _send(new MarkAsk
        {
            X = _offer.X,
            Y = _offer.Y,
            Z = _offer.Z,
            BlockX = _offer.BlockX,
            BlockY = _offer.BlockY,
            BlockZ = _offer.BlockZ,
            // Every question a preset would have answered has been asked on this
            // window, so a preset must not answer them again over the top.
            UsePreset = false,
            Title = SingleComposer.GetTextInput(NameInput).GetText(),
            Icon = _picture,
            Color = _colour,
            Private = Mark.Says(_private),
            KeepPreset = _preset,
            Pattern = _offer.Pattern,
        });
        TryClose();
        return true;
    }
}

/// <summary>
/// The one thing a list picker's index needs before it is used.
///
/// The game hands back whatever was clicked, and a picker whose list changed
/// under it can hand back an index past the end. Its own function because both
/// pickers ask it and the answer is not obviously the same one twice.
/// </summary>
public static class GuiElements
{
    public static int Within(int index, int many) => index < 0 || index >= many ? 0 : index;
}
