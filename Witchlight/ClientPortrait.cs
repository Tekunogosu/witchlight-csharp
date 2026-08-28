using System;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Drawing this player, and sending the picture.
///
/// Only their own machine can: nobody else's has that seraph loaded, which is why
/// the picture travels rather than a description of it. The drawing itself is
/// <see cref="PortraitCapture"/> and the watching for a change is
/// <see cref="PortraitWatch"/>; what is here is when to do it and what to say.
/// </summary>
public partial class WitchlightClient
{
    /// <summary>
    /// How long between pictures a player asks for by hand.
    ///
    /// Generous on purpose. Everything that keeps the map current happens without
    /// the command, so nobody waiting this out is waiting for their own face to
    /// appear — the picture they would have asked for arrives on its own.
    /// </summary>
    private static readonly TimeSpan CommandWait = TimeSpan.FromMinutes(5);

    /// <summary>When this player last had a picture they asked for by hand.</summary>
    private DateTime? _askedAt;

    /// <summary>
    /// Draws this player and sends the picture to the server.
    ///
    /// Once every five minutes. Nothing about the map being current rests on
    /// anybody typing this — a picture is asked for on every join and sent again
    /// whenever a character has been left alone for half a minute — so the command
    /// is for wanting one sooner than that, and wanting it twice over is wanting it
    /// once. Refused before anything is drawn, so a player leaning on it costs the
    /// server nothing and their own machine no frames.
    /// </summary>
    private TextCommandResult OnPortrait(TextCommandCallingArgs args)
    {
        if (_capi is null || _portrait is null)
        {
            return TextCommandResult.Error("not connected to a world");
        }

        if (WaitLeft() is { } left)
        {
            return TextCommandResult.Error(
                $"you asked for one already — ask again in {Spoken(left)}. "
                + "The map draws you on every join and whenever you change anyway.");
        }

        SendPortrait(byHand: true);
        return TextCommandResult.Success("drawing your character...");
    }

    /// <summary>
    /// Draws this player and sends the picture.
    ///
    /// The drawing happens in a frame, so the answer arrives after whoever asked
    /// has been answered. By hand means a person typed the command and is waiting
    /// on an answer, which is the whole of what separates the two: they are told in
    /// chat rather than only in the log, and the wait before they may ask again
    /// starts. Nothing unprompted reaches the screen at all — a picture is drawn on
    /// every join and after every change of clothes, and a player who did not ask
    /// for any of that is owed silence about all of it.
    /// </summary>
    private void SendPortrait(bool byHand)
    {
        if (_capi is not { } capi || _portrait is not { } portrait)
        {
            return;
        }

        // Whatever prompted this, the character as it stands has now been drawn,
        // so the wait starts again from here rather than from a change already
        // covered by this picture.
        _watch?.Settled();

        portrait.Take((png, said) =>
        {
            if (png is null)
            {
                capi.Logger.Warning("[witchlight] could not draw a portrait: {0}", said);
                if (byHand)
                {
                    capi.ShowChatMessage($"Witchlight: could not draw your portrait — {said}");
                }
                return;
            }

            // Counted from the picture rather than from the command, so an attempt
            // that drew nothing may be made again instead of costing five minutes
            // for a failure nobody chose.
            if (byHand)
            {
                _askedAt = DateTime.UtcNow;
            }

            capi.Network.GetChannel(SharedServer.Channel).SendPacket(new PlayerPortrait { Png = png });
            capi.Logger.Notification("[witchlight] sent a {0} byte portrait ({1})", png.Length, said);
            if (byHand)
            {
                // Bytes, not kilobytes. A picture of nothing weighs a hundred and
                // fifty bytes and rounds to "0 KiB", which reads as a unit being
                // silly rather than as an empty render — and it cost an evening.
                capi.ShowChatMessage($"Witchlight: sent your portrait, {png.Length} bytes — {said}");
            }
        });
    }

    /// <summary>
    /// How long is left before this player may ask for another, or nothing when
    /// they may ask now.
    /// </summary>
    private TimeSpan? WaitLeft()
    {
        if (_askedAt is not { } last)
        {
            return null;
        }

        var left = CommandWait - (DateTime.UtcNow - last);
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>A wait as somebody would say it, rather than as a count of seconds.</summary>
    private static string Spoken(TimeSpan left) => left < TimeSpan.FromMinutes(1)
        ? $"{left.Seconds + 1}s"
        : $"{(int)left.TotalMinutes}m {left.Seconds}s";
}
