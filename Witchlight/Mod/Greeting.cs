using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Telling a player where the map is, as they join.
///
/// The same class as the rest of the mod system, for the reason the commands are:
/// this is a view of the whole system rather than a thing of its own, and a type
/// of its own would only mean handing it every field this one already has.
///
/// A map nobody knows the address of is a map nobody looks at, and this is the
/// half that can say so in chat. What is kept apart from it is the wiring —
/// which events call this is decided where every other event is.
/// </summary>
public partial class WitchlightSystem
{
    /// <summary>
    /// When each player was told where the map is, on the machine's own clock.
    ///
    /// Kept as a time rather than as a yes, because whether to say it a second
    /// time depends on how long ago the first one was — see <see cref="Greet"/>.
    /// Cleared when they leave, so a rejoin is greeted again.
    /// </summary>
    private readonly Dictionary<string, long> _greeted = new();

    /// <summary>Whether the reason nobody is being greeted has been said.</summary>
    private bool _saidWhyNotGreeting;

    /// <summary>
    /// How long after a greeting a second one counts as the same greeting.
    ///
    /// `PlayerReady` follows `PlayerNowPlaying` in the same breath for anybody who
    /// has played before, and only after the character and class screen closes on
    /// a first join. So the gap between the two says which kind of join this is
    /// without having to ask: inside this, the player has already read the line;
    /// outside it, the line went behind a screen and is worth saying again.
    ///
    /// Comfortably longer than a client finishing its loading and comfortably
    /// shorter than picking a character, which is the whole range it has to tell
    /// apart. Erring long: a line said twice is untidy, and a line said once
    /// behind a screen nobody was looking at is the bug this is here for.
    /// </summary>
    private const int GreetAgainAfterMs = 20000;

    /// <summary>
    /// How long to wait before looking again for an address to greet with, and
    /// how many times.
    ///
    /// The service publishes where it is listening a moment after the world comes
    /// up, so a player who joins into that gap has nothing to be told yet. Three
    /// seconds apart and ten of them covers a slow start without a player who
    /// joined a server that has no address at all being chased forever.
    /// </summary>
    private const int GreetRetryMs = 3000;
    private const int GreetTries = 10;

    /// <summary>
    /// Tells a player where the map is as they join.
    ///
    /// A map nobody knows the address of is a map nobody looks at, and this is the
    /// half that can say so in chat. The settings are read each time rather than
    /// held, so turning the message off takes effect on the next join instead of
    /// on the next restart.
    ///
    /// Nothing is said when there is no address to give. That covers a service
    /// that is not running, one somebody else runs somewhere this cannot see, and
    /// a server whose real address only its operator knows and has not said.
    /// </summary>
    private void Greet(IServerPlayer player, int tries)
    {
        // Gone is gone. Not *playing* is not gone, and the difference cost this
        // its whole job once already: `EnumClientState.Playing` is set by a packet
        // the client sends after it has finished loading and, on a first join,
        // after the character screen closes — the same signal `PlayerReady` waits
        // on. A player at `PlayerNowPlaying` is `Connected`, which is a player in
        // the world who can be sent a line of chat, and refusing to greet anybody
        // who was not yet `Playing` meant refusing to greet anybody at all.
        if (_sapi is not { } api || player.ConnectionState == EnumClientState.Offline)
        {
            return;
        }

        var now = Environment.TickCount64;
        var owed = false;
        lock (_greeted)
        {
            var told = _greeted.TryGetValue(player.PlayerUID, out var when);
            // Told once already. Told a second time only when the first cannot
            // have been read: a `PlayerReady` this long after the greeting is
            // somebody who spent that time on the character screen, with the line
            // sitting behind it. Anybody who has played before reaches it in a
            // breath and is told once.
            owed = !told || now - when >= GreetAgainAfterMs;
            if (owed)
            {
                _greeted[player.PlayerUID] = now;
            }
        }

        if (owed && SayWhereTheMapIs(player))
        {
            return;
        }

        if (owed)
        {
            // Nothing to say yet rather than nothing to say. The service publishes
            // its address a moment after the world comes up, so a player who
            // joined into that gap is owed another look — and until one is made,
            // this join has not been greeted at all.
            lock (_greeted)
            {
                _greeted.Remove(player.PlayerUID);
            }
        }

        if (tries > 0)
        {
            api.Event.RegisterCallback(
                _ => Doing("telling a player where the map is", () => Greet(player, tries - 1)),
                GreetRetryMs);
        }
    }

    /// <summary>Whether anything was said. Nothing is, when there is no address.</summary>
    private bool SayWhereTheMapIs(IServerPlayer player)
    {
        if (_sapi is not { } api || !Settings.Announces)
        {
            // Nothing to say and nothing waiting to be said: an operator who
            // turned the greeting off is answered, not retried.
            return true;
        }

        if (Settings.Announcement() is not { } where)
        {
            // The settings ask for a greeting and there is no address to put in
            // one. Said, because this used to be the whole of the failure: a
            // player joined, nothing was said, and nothing anywhere recorded
            // that anything had been meant to happen. Once per start, since a
            // server without an address would otherwise repeat it at every join.
            if (!_saidWhyNotGreeting)
            {
                _saidWhyNotGreeting = true;
                api.Logger.Warning(
                    "[witchlight] joining players are being told nothing about the map: "
                    + "`announce` is on, `announce_url` is unset, and the service has not "
                    + "written an address to {0}. Set `announce_url` in {1} to say it anyway.",
                    Settings.AddressPath,
                    Settings.Path);
            }

            return false;
        }

        // The address on its own is what somebody bookmarks, so it is said either
        // way and said as itself. The link beside it is spent on one press.
        var address = $"The map is at {AsLink(where, where)}";

        // Signing them in is the same thing `/witchlight login` does, so it is
        // offered to exactly whoever could have typed that — and only where there
        // is a service to mint one.
        if (_service is null || !player.HasPrivilege(Privilege.chat))
        {
            api.SendMessage(player, GlobalConstants.GeneralChatGroup, address,
                EnumChatType.Notification);
            api.Logger.Notification("[witchlight] told {0} where the map is, without a login link ({1})",
                player.PlayerName, player.ConnectionState);
            return true;
        }

        WithLoginLink(player, where, (told, link) =>
        {
            api.SendMessage(
                told,
                GlobalConstants.GeneralChatGroup,
                // Nothing is said about a link that could not be minted. Somebody
                // who typed the command is owed the reason; somebody who merely
                // joined a server never asked, and the address still works.
                link is null
                    ? address
                    : $"{address}. {AsLink(link, "Open it signed in")} — that link works "
                      + "once, for ten minutes.",
                EnumChatType.Notification);
            // Said here rather than only in the log of a failure. How long a
            // player waits for this line was the thing nothing recorded, and it
            // could not be measured from either side without it. The connection
            // state rides along because the whole of one bug here was a wrong
            // assumption about what it is at the moment somebody is greeted.
            api.Logger.Notification("[witchlight] told {0} where the map is, {1} a login link ({2})",
                told.PlayerName, link is null ? "without" : "with", told.ConnectionState);
        });
        return true;
    }

    /// <summary>
    /// An address as chat should show it: something to press where it is, and
    /// plain words where it is not.
    ///
    /// The game makes a link out of `href` only for an address with a scheme it
    /// knows — it splits on `://` and opens what begins with `http`. An
    /// `announce_url` set to a bare host is a perfectly good thing to tell
    /// somebody and a link that would do nothing at all when pressed, and text
    /// that can be copied beats a press that goes nowhere.
    /// </summary>
    private static string AsLink(string where, string said) =>
        where.StartsWith("http", StringComparison.Ordinal) && where.Contains("://")
            ? $"<a href=\"{where}\">{said}</a>"
            : said;

    /// <summary>
    /// Asks the map for a link that signs one player in, and hands it back on the
    /// game thread.
    ///
    /// A round trip to another program, so the asking happens off the game
    /// thread; everything the server says it says from that thread, so the answer
    /// comes back to it. Null where the map could not be asked, which the two
    /// callers answer differently.
    ///
    /// The player is looked up again rather than held across the wait: minting
    /// takes a moment and somebody can leave inside it.
    /// </summary>
    private void WithLoginLink(IServerPlayer player, string where, Action<IServerPlayer, string?> told)
    {
        if (_sapi is not { } api || _service is not { } service)
        {
            return;
        }

        var uid = player.PlayerUID;
        var name = player.PlayerName ?? "";

        _ = Task.Run(async () =>
        {
            var link = await service.Link(uid, name, where).ConfigureAwait(false);
            api.Event.EnqueueMainThreadTask(() =>
            {
                if (api.World.PlayerByUid(uid) is IServerPlayer still)
                {
                    Doing("handing over a map link", () => told(still, link));
                }
            }, "witchlight-login");
        });
    }

}
