using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Marking a place from in game, without opening a map first.
///
/// The whole of what this offers is one press: mark what I am looking at, the
/// way I have said this kind of thing is marked. A key and a chat command say
/// the same thing, because a key is unreachable while a chat box is open and a
/// command is what somebody reaches for the first time.
///
/// Nothing here decides what the marker becomes. The server holds the block at
/// the position, and the map service holds what this player has kept, and both
/// of those are on the far end of the ask — see <see cref="Marking"/>. What is
/// left on this side is where the player means and what to do with the answer.
/// </summary>
public partial class WitchlightClient
{
    /// <summary>What the key answers to in the controls settings.</summary>
    private const string MarkHotkey = "witchlightmark";

    /// <summary>The window that opens where no preset had an answer.</summary>
    private GuiDialogWitchlightMark? _marking;

    /// <summary>The key and the command that ask for a marker.</summary>
    private void ListenForMarking(ICoreClientAPI api)
    {
        // `[` because it is unbound in a stock game, is next to the other bracket
        // for a second one to grow into, and is nowhere near the movement keys —
        // marking a place is something done while standing still, but a key that
        // shares a corner with WASD is a key somebody presses by accident while
        // not standing still.
        api.Input.RegisterHotKey(
            MarkHotkey,
            "Create from Witchlight preset",
            GlKeys.BracketLeft,
            HotkeyType.CharacterControls);
        api.Input.SetHotKeyHandler(MarkHotkey, _ => AskToMark());
    }

    /// <summary>
    /// Asks the server to mark where this player means, using a preset.
    ///
    /// True whichever way it goes: the key was handled, and what came of it is
    /// said in chat by the answer rather than by the key not working.
    /// </summary>
    private bool AskToMark()
    {
        if (_capi is not { } api)
        {
            return true;
        }

        // A window already open is the answer to the last press, and a second
        // press behind it would put a second window over the first.
        if (_marking is not null && _marking.IsOpened())
        {
            return true;
        }

        api.Network.GetChannel(Channel.Name).SendPacket(Meaning(api));
        return true;
    }

    /// <summary>
    /// Where this player means, and which block names it.
    ///
    /// Two different questions with two different answers. Somebody looking at a
    /// block means that block, and both are it. Somebody looking at nothing means
    /// where they are standing — and the block there is air, so what names the
    /// marker is the one under their feet.
    /// </summary>
    private static MarkAsk Meaning(ICoreClientAPI api)
    {
        var player = api.World.Player;
        if (player.CurrentBlockSelection is { } aimed)
        {
            var at = aimed.Position;
            return new MarkAsk
            {
                X = at.X + 0.5,
                Y = at.Y,
                Z = at.Z + 0.5,
                BlockX = at.X,
                BlockY = at.Y,
                BlockZ = at.Z,
                UsePreset = true,
            };
        }

        var standing = player.Entity.Pos;
        return new MarkAsk
        {
            X = standing.X,
            Y = standing.Y,
            Z = standing.Z,
            BlockX = Blocks.At(standing.X),
            BlockY = Blocks.At(standing.Y) - 1,
            BlockZ = Blocks.At(standing.Z),
            UsePreset = true,
        };
    }

    /// <summary>
    /// What became of an ask.
    ///
    /// A marker made is a line of chat, because the marker itself is already on
    /// the map and a window over it would be one more thing to close. A preset
    /// that answered nothing opens the window, filled in with everything the
    /// server worked out — which is the whole difference between the two: one
    /// press did it, or one press got as far as it could and handed the rest over.
    /// </summary>
    private void TakeMarkReply(MarkReply reply)
    {
        if (_capi is not { } api)
        {
            return;
        }

        if (!reply.Yours)
        {
            api.ShowChatMessage("[Witchlight] " + reply.Said);
            return;
        }

        if (Markers.Layer(api) is not { } layer)
        {
            api.ShowChatMessage("[Witchlight] The map layer is not up, so there is nothing to mark on.");
            return;
        }

        // The map behind it, so the window opens over a picture of where the
        // marker is going rather than over the world it is being placed in.
        // Toggled, so a map that is already up is left up rather than shut.
        var maps = api.ModLoader.GetModSystem<WorldMapManager>();
        if (maps is { IsOpened: false })
        {
            maps.ToggleMap(EnumDialogType.Dialog);
        }

        _marking?.TryClose();
        _marking = new GuiDialogWitchlightMark(api, layer, reply, Send);
        _marking.TryOpen();
        api.ShowChatMessage("[Witchlight] " + reply.Said);
    }

    /// <summary>What the window sends when it is finished with.</summary>
    private void Send(MarkAsk ask)
    {
        _capi?.Network.GetChannel(Channel.Name).SendPacket(ask);
    }

    /// <summary>
    /// The chat command that does what the key does.
    ///
    /// The same one function behind both, so `/wl mark` and the key cannot come
    /// to mean different things. It is worth having both: a key is the one to
    /// use and a command is the one somebody finds.
    /// </summary>
    private TextCommandResult OnMark(TextCommandCallingArgs args)
    {
        AskToMark();
        return TextCommandResult.Success("Marking…");
    }
}
