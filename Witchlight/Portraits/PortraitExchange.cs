using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Asking players to draw themselves, and taking what comes back.
///
/// Only a player's own client can draw them: nobody else's machine has that
/// seraph loaded, which is why a picture travels rather than a description of one.
///
/// Everyone is asked, and everyone may send. A palette and the marker pictures
/// decide what everybody sees, so anybody may fill a gap in one but only an admin
/// may change what is already there; a portrait decides what one card looks like
/// and that card is the sender's own, which is a thing they are already entitled
/// to be wrong about.
///
/// That does open a write to every client rather than to a handful of trusted
/// ones, so how often one may arrive is answered here and how large it may be is
/// answered where it is filed.
/// </summary>
public sealed class PortraitExchange(ICoreServerAPI api, string exports)
{
    /// <summary>
    /// How long after a join before a player is asked to draw themselves.
    ///
    /// The server calls them playing before their client has finished getting
    /// there, and a seraph that is not loaded yet renders as an empty picture the
    /// client then reports it could not draw. A few seconds is the whole of the fix.
    /// </summary>
    private const int AskDelayMs = 8000;

    /// <summary>
    /// How long an ask stands before the server stops expecting an answer to it.
    ///
    /// A client draws on its next frame, so an answer is normally back in well
    /// under a second. This is long enough to cover one that is busy and short
    /// enough that an ask nobody answered does not sit there excusing a picture
    /// sent much later for a different reason.
    /// </summary>
    private const int AnswerWindowMs = 60000;

    private readonly ICoreServerAPI _api = api;
    private readonly string _exports = exports;

    /// <summary>Who has been asked for a picture and not yet answered.</summary>
    private readonly Recent _asked = new(AnswerWindowMs);

    /// <summary>When each player's last unasked-for picture arrived.</summary>
    private readonly Recent _taken = new(Portraits.FloorMs);

    /// <summary>
    /// Asks a player who has just joined to draw themselves, after a pause.
    ///
    /// Asked on every join rather than once ever, because a seraph that changed
    /// while its player was away is a picture that is now wrong, and only the
    /// machine that can see it knows the difference. The pause costs nobody
    /// anything and saves an empty portrait on every join.
    /// </summary>
    public void AskOnceSettled(IServerPlayer player, Action<string, Action> safely)
    {
        _api.Event.RegisterCallback(_ => safely("asking for a portrait", () =>
        {
            // They may well have left in the meantime, and a packet to somebody
            // who is gone is at best wasted.
            if (player.ConnectionState == EnumClientState.Playing)
            {
                Ask(player);
            }
        }), AskDelayMs);
    }

    /// <summary>
    /// Asks one player to draw themselves, and remembers having asked.
    ///
    /// The only place an ask is sent, so that a picture arriving afterwards can be
    /// recognised as the answer to one. Nothing else can put a name in that table,
    /// which is what makes it safe to let an answer past the floor.
    /// </summary>
    public void Ask(IServerPlayer player)
    {
        _asked.Note(player.PlayerUID);
        _api.Network.GetChannel(Channel.Name).SendPacket(new PortraitRequest(), player);
    }

    /// <summary>
    /// Takes a player's picture of themselves.
    ///
    /// Filed under who sent it rather than under anything in the message: a client
    /// says what it looks like, not who it is.
    /// </summary>
    public void Accept(IServerPlayer player, PlayerPortrait portrait)
    {
        // A picture the server asked for is expected, however soon after the last
        // one it lands: a player who joins and then types the command a second
        // later has done nothing wrong, and their second picture must not vanish
        // into a log line while their own screen says it was sent. The floor is
        // for what a client sends of its own accord.
        if (!_asked.Take(player.PlayerUID))
        {
            if (_taken.Since(player.PlayerUID) is { } wait)
            {
                _api.Logger.Warning(
                    "[witchlight] ignored a portrait from {0}, sent unasked {1:0.#}s after "
                    + "the last one; unasked pictures are taken no closer together than {2}s",
                    player.PlayerName, wait.TotalSeconds, Portraits.FloorMs / 1000);
                return;
            }

            _taken.Note(player.PlayerUID);
        }

        if (Portraits.Save(_exports, player.PlayerUID, portrait.Png, out var said))
        {
            _api.Logger.Notification("[witchlight] portrait from {0} ({1})", player.PlayerName, said);
        }
        else
        {
            _api.Logger.Warning(
                "[witchlight] ignored a portrait from {0}: {1}", player.PlayerName, said);
        }
    }
}

/// <summary>
/// When something last happened, for a bounded while.
///
/// Two questions were being answered by two hand-rolled dictionaries of times and
/// a shared function to prune them: has this player been asked, and did this
/// player send one too recently. Both are "note it, then ask whether it was
/// recent", so both are this.
///
/// Anything older than the window is dropped whenever the table is touched, so it
/// stays a table of recent events rather than one entry per player the server has
/// ever seen.
/// </summary>
public sealed class Recent
{
    private readonly Dictionary<string, DateTime> _at = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public Recent(int windowMs)
    {
        _window = TimeSpan.FromMilliseconds(windowMs);
    }

    /// <summary>Records that it has just happened for this key.</summary>
    public void Note(string key)
    {
        Forget();
        _at[key] = DateTime.UtcNow;
    }

    /// <summary>
    /// How long ago it happened, when that is still inside the window — and
    /// nothing when it is not.
    /// </summary>
    public TimeSpan? Since(string key)
    {
        Forget();
        return _at.TryGetValue(key, out var when) ? DateTime.UtcNow - when : null;
    }

    /// <summary>
    /// Whether it happened inside the window, and takes the record if it did — so
    /// one note is worth one taking rather than a window of them.
    /// </summary>
    public bool Take(string key)
    {
        Forget();
        return _at.Remove(key);
    }

    private void Forget()
    {
        var cutoff = DateTime.UtcNow - _window;
        foreach (var stale in _at.Where(at => at.Value <= cutoff).Select(at => at.Key).ToList())
        {
            _at.Remove(stale);
        }
    }
}
