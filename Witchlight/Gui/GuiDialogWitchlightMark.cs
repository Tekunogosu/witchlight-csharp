using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
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
///
/// A preset that names some other block can still be the right start: "Presets"
/// opens a list of everything this person has kept beside the window, and
/// choosing one fills the name, the colour, the picture and who may see it, the
/// way pressing the key over that preset's own block would have. The block the
/// marker is made on is not changed by it — that is where they are standing.
/// </summary>
public class GuiDialogWitchlightMark : GuiDialogGeneric
{
    private const string Composed = "witchlight-mark";
    private const string PresetsComposed = "witchlight-mark-presets";
    private const string PresetsScrollbar = "presetsScrollbar";
    private const string PresetsFind = "presetsFind";
    private const string PresetsRows = "presetsRows";
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

    /// <summary>The rows of the preset list, moved as the scrollbar moves.</summary>
    private ElementBounds? _presetRows;

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
        // The switches' own column, down the left edge the pickers above them
        // start at. Its own bounds rather than a copy of the label's, because
        // the two now run the other way round: the box is placed and the words
        // follow it. Where the column starts is settled below, once the picture
        // picker has said how tall it came out.
        var toggle = ElementBounds.Fixed(0, 28, SwitchWidth, SwitchSize);

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
            //
            // The switch is to the left of what it says, in both rows, and the
            // two switches share a column. A control read left to right is found
            // by its box and then read; with the boxes on the right the eye has
            // to cross a line of text of a length it cannot predict to find out
            // whether the answer is yes.
            .AddSwitch(on => _private = on,
                toggle = Under(label), PrivateSwitch, SwitchSize, SwitchPad)
            .AddStaticText("Private", CairoFont.WhiteSmallText(),
                label = Beside(toggle))

            .AddSwitch(on => _preset = on,
                toggle = toggle.BelowCopy(0, 6), PresetSwitch, SwitchSize, SwitchPad)
            // What it does, not what it would be keyed on. The block's code
            // belongs to the pattern the preset carries and not to the sentence
            // on the switch: "keep as what game:rock-granite-* starts as" is a
            // question nobody reads to the end of.
            .AddStaticText("Set as preset", CairoFont.WhiteSmallText(),
                label = Beside(toggle))

            // Level with each other under the last of the switches, one at each
            // end of the row — which is where the game's own window puts them.
            // Hung off the label they had been level with the words rather than
            // with the boxes beside them.
            .AddSmallButton("Cancel", OnCancel,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100),
                EnumButtonStyle.Normal)
            // Between the two ways out: it is neither, being a way to fill the
            // window in rather than to leave it.
            .AddSmallButton("Presets", OnPresets,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100)
                    .WithAlignment(EnumDialogArea.CenterFixed),
                EnumButtonStyle.Normal)
            .AddSmallButton("Save", OnSave,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100)
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

    /// <summary>How tall a switch is drawn, how much of that is its padding, and
    ///  how wide a column one needs. The game's own numbers for a switch, said
    ///  once so the two rows cannot come out at different heights.</summary>
    private const int SwitchSize = 30;
    private const int SwitchPad = 4;
    private const int SwitchWidth = 40;

    /// <summary>
    /// Where the first switch goes: under whatever the picture picker came out
    /// as, by the same arithmetic the row above it used before the switches
    /// changed sides. A picker wraps to as many rows as the mods on the server
    /// give it, so nothing here may assume how tall it is.
    /// </summary>
    private static ElementBounds Under(ElementBounds pictures) =>
        ElementBounds.Fixed(
            0,
            // Four up, which is where the switch sat when it was beside its own
            // words rather than before them: a switch is taller than a line of
            // text and the pair is read as one row.
            pictures.fixedY + pictures.fixedHeight * 2 + 9 - 4,
            SwitchWidth,
            SwitchSize);

    /// <summary>The words that go beside a switch: level with the line of text
    ///  the switch is centred on, and clear of the box by the gap the rest of
    ///  this window is spaced on.</summary>
    private static ElementBounds Beside(ElementBounds toggle) =>
        ElementBounds.Fixed(toggle.fixedX + SwitchWidth + 6, toggle.fixedY + 4, 280, 25);

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

    /// <summary>How the preset list is laid out: one row per preset, so many
    ///  shown at once, how wide the table is, and where the block column starts.</summary>
    private const int RowHeight = 28;
    private const int RowsShown = 8;
    private const int ListWidth = 440;
    private const int NameWidth = 150;
    private const int CellPad = 6;

    /// <summary>What was typed into the search box, lowercased once.</summary>
    private string _finding = "";

    /// <summary>Which column the rows are in order of, and which way.</summary>
    private bool _byBlock;
    private bool _descending;

    /// <summary>Whether the list is on the screen.</summary>
    private bool Listing => Composers[PresetsComposed] is { Enabled: true };

    /// <summary>Shows the list, or puts it away: the one button does both.</summary>
    private bool OnPresets()
    {
        if (Listing)
        {
            HidePresets();
        }
        else
        {
            ShowPresets();
        }
        return true;
    }

    /// <summary>
    /// The presets as the table shows them: those the search matches, in the
    /// order the headers say.
    ///
    /// One function rather than a filter in one place and a sort in another, so
    /// what is on the screen is the answer to one question — and a question
    /// that can be asked without a screen.
    /// </summary>
    public static List<PresetOffer> Shown(
        IEnumerable<PresetOffer> offered, string finding, bool byBlock, bool descending)
    {
        var wanted = finding.Trim().ToLowerInvariant();
        var rows = offered
            .Where(preset => wanted.Length == 0
                || preset.Title.ToLowerInvariant().Contains(wanted)
                || preset.Pattern.ToLowerInvariant().Contains(wanted));
        var ordered = byBlock
            ? rows.OrderBy(preset => preset.Pattern, StringComparer.OrdinalIgnoreCase)
                .ThenBy(preset => preset.Title, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(preset => preset.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(preset => preset.Pattern, StringComparer.OrdinalIgnoreCase);
        return (descending ? ordered.Reverse() : ordered).ToList();
    }

    /// <summary>
    /// Lays the table out beside the window, to its left.
    ///
    /// Its own composer under this dialog rather than a dialog of its own, so it
    /// opens and closes with the window and takes the mouse the same way. Placed
    /// against the window's own edge by measuring the window: the window is as
    /// wide as its pickers came out, which is as many columns as the server has
    /// pictures, so nothing here may assume that width.
    ///
    /// A search box over two headed columns, name and block. The rows are one
    /// container inside a clipped inset, which is the shape the game's own mod
    /// list takes: the container owns every cell's bounds, so moving it is what
    /// scrolling is, and the clip is what stops the rows past the bottom being
    /// drawn over the window. Every other row is shaded, each cell is its own
    /// left-aligned text cut to its column, and a button with no face lies
    /// behind the cells so a press anywhere on the row picks it. Typing or
    /// pressing a header lays the whole table out again: the rows are cheap and
    /// a table that patches itself is one that can disagree with its own headers.
    /// </summary>
    private void ShowPresets()
    {
        Composers[PresetsComposed]?.Dispose();

        var shown = Shown(_offer.Presets, _finding, _byBlock, _descending);
        var find = ElementBounds.Fixed(0, 28, ListWidth, 26);
        var nameHead = ElementBounds.Fixed(0, 62, NameWidth, 24);
        var blockHead = ElementBounds.Fixed(NameWidth + CellPad, 62, ListWidth - NameWidth - CellPad, 24);
        var inset = ElementBounds.Fixed(0, 92, ListWidth, RowsShown * RowHeight + 6);
        var clip = inset.ForkContainingChild(3, 3, 3, 3);
        var rows = clip.ForkContainingChild(0, 0, 0, -3);
        rows.fixedHeight = Math.Max(1, shown.Count) * RowHeight;
        var scroll = ElementStdBounds.VerticalScrollbar(inset);

        var inside = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        inside.BothSizing = ElementSizing.FitToChildren;
        inside.WithChildren(find, nameHead, blockHead, inset, scroll);

        var beside = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.RightMiddle)
            .WithFixedAlignmentOffset(-(GuiStyle.DialogToScreenPadding + WindowWidth() + 8), 0);

        var composer = capi.Gui
            .CreateCompo(PresetsComposed, beside)
            .AddShadedDialogBG(inside, false)
            .AddDialogTitleBar("Presets", HidePresets)
            .BeginChildElements(inside)
            .AddTextInput(find, OnFinding, CairoFont.TextInput(), PresetsFind)
            .AddSmallButton(Headed("Name", byBlock: false), () => Reorder(byBlock: false), nameHead, EnumButtonStyle.Small)
            .AddSmallButton(Headed("Block", byBlock: true), () => Reorder(byBlock: true), blockHead, EnumButtonStyle.Small)
            .AddInset(inset, 3)
            .AddVerticalScrollbar(OnPresetsScrolled, scroll, PresetsScrollbar)
            .BeginClip(clip)
            .AddContainer(rows, PresetsRows)
            .EndClip()
            .EndChildElements();

        var table = composer.GetContainer(PresetsRows);
        var cell = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Left);
        var blockWidth = ListWidth - NameWidth - CellPad * 3 - 6;

        if (shown.Count == 0)
        {
            table.Add(new GuiElementStaticText(capi,
                _offer.Presets.Count == 0
                    ? "Nothing kept yet. Tick \"Set as preset\" here, or make one on the map."
                    : "Nothing matches.",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(CellPad, 4, ListWidth - CellPad * 2, RowHeight * 2),
                cell));
        }

        for (var at = 0; at < shown.Count; at++)
        {
            var preset = shown[at];
            var top = at * RowHeight;
            if (at % 2 == 1)
            {
                table.Add(new GuiElementCustomDraw(capi,
                    ElementBounds.Fixed(0, top, ListWidth, RowHeight), Stripe));
            }
            table.Add(new GuiElementTextButton(capi, "", cell, cell, () => Pick(preset),
                ElementBounds.Fixed(0, top, ListWidth, RowHeight), EnumButtonStyle.None));
            table.Add(new GuiElementStaticText(capi,
                Fitted(cell, preset.Title, NameWidth - CellPad),
                EnumTextOrientation.Left,
                ElementBounds.Fixed(CellPad, top + 5, NameWidth - CellPad, RowHeight - 6),
                cell));
            table.Add(new GuiElementStaticText(capi,
                Fitted(cell, preset.Pattern, blockWidth),
                EnumTextOrientation.Left,
                ElementBounds.Fixed(NameWidth + CellPad, top + 5, blockWidth, RowHeight - 6),
                cell));
        }

        composer.Compose();
        rows.CalcWorldBounds();
        clip.CalcWorldBounds();
        composer.GetScrollbar(PresetsScrollbar)
            .SetHeights((float)clip.fixedHeight, (float)rows.fixedHeight);
        var box = composer.GetTextInput(PresetsFind);
        box.SetPlaceHolderText("find a preset");
        if (_finding.Length > 0)
        {
            box.SetValue(_finding);
        }

        _presetRows = rows;
        Composers[PresetsComposed] = composer;
    }

    /// <summary>The shading every other row wears, so the eye keeps its line.</summary>
    private static void Stripe(Cairo.Context ctx, Cairo.ImageSurface surface, ElementBounds bounds)
    {
        ctx.SetSourceRGBA(1, 1, 1, 0.06);
        ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        ctx.Fill();
    }

    /// <summary>
    /// The text, or as much of it as its column holds with an ellipsis after.
    ///
    /// Measured in the font it is drawn in, against the column's unscaled width
    /// brought up to the screen's scale, so what fits at one interface size
    /// fits at another.
    /// </summary>
    private static string Fitted(CairoFont font, string text, double width)
    {
        var room = width * RuntimeEnv.GUIScale;
        if (font.GetTextExtents(text).Width <= room)
        {
            return text;
        }
        var kept = text;
        while (kept.Length > 1 && font.GetTextExtents(kept + "…").Width > room)
        {
            kept = kept[..^1];
        }
        return kept.TrimEnd() + "…";
    }

    /// <summary>A header's words, with an arrow on the one the rows follow.</summary>
    private string Headed(string name, bool byBlock) =>
        byBlock == _byBlock ? name + (_descending ? " ▼" : " ▲") : name;

    /// <summary>Follows the column pressed, or turns it round when it already is.</summary>
    private bool Reorder(bool byBlock)
    {
        _descending = byBlock == _byBlock && !_descending;
        _byBlock = byBlock;
        ShowPresets();
        return true;
    }

    /// <summary>
    /// Lays the rows out again as the search changes. The box is rebuilt with
    /// the table and given the text back, so typing is not interrupted by it.
    /// </summary>
    private void OnFinding(string typed)
    {
        if (typed == _finding)
        {
            return;
        }
        _finding = typed;
        ShowPresets();
        Composers[PresetsComposed]?.FocusElement(
            Composers[PresetsComposed]!.GetTextInput(PresetsFind).TabIndex);
    }

    /// <summary>How wide the window is on the screen, in the unscaled units a
    ///  fixed offset is given in.</summary>
    private double WindowWidth() =>
        SingleComposer.Bounds.OuterWidth / RuntimeEnv.GUIScale;

    private void OnPresetsScrolled(float value)
    {
        if (_presetRows is null)
        {
            return;
        }
        _presetRows.fixedY = -value;
        _presetRows.CalcWorldBounds();
    }

    private void HidePresets()
    {
        if (Composers[PresetsComposed] is { } list)
        {
            list.Enabled = false;
        }
    }

    /// <summary>
    /// Fills the window from a preset, the way the key would have from its block.
    ///
    /// Every answer the preset gives is taken; one it does not give — a colour
    /// the game no longer offers, a privacy nobody set — leaves what the window
    /// already shows, which is the server's default for this marker.
    /// </summary>
    private bool Pick(PresetOffer preset)
    {
        if (preset.Title.Length > 0)
        {
            SingleComposer.GetTextInput(NameInput).SetValue(preset.Title);
        }
        if (Array.IndexOf(_pictures, preset.Icon) >= 0)
        {
            _picture = preset.Icon;
            SingleComposer.IconListPickerSetValue(PicturePicker, Array.IndexOf(_pictures, _picture));
        }
        if (Markers.Packed(preset.Color) is { } packed && Array.IndexOf(_colours, packed) >= 0)
        {
            _colour = Markers.Hex(packed);
            SingleComposer.ColorListPickerSetValue(ColourPicker, Chosen(_colour));
        }
        if (preset.Private != Mark.Unsaid)
        {
            _private = preset.Private == Mark.Private;
            SingleComposer.GetSwitch(PrivateSwitch).SetValue(_private);
        }
        HidePresets();
        return true;
    }

    /// <summary>The list goes with the window, however the window went.</summary>
    public override void OnGuiClosed()
    {
        Composers[PresetsComposed]?.Dispose();
        _presetRows = null;
        base.OnGuiClosed();
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
