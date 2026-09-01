using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace Witchlight;

/// <summary>
/// The extra readings a player's card carries, beside their health and food.
///
/// A mod that gives players a resource — mana, stamina, a level — keeps it on
/// the player's own entity, in the same watched attributes the game keeps health
/// and hunger in. That is server-side and already in front of this mod, so
/// showing one on the map costs a lookup rather than a dependency: nothing here
/// references any mod, compiles against one, or breaks when one is uninstalled.
///
/// What is read is the operator's to say, because guessing would be the one way
/// to get it wrong. Each entry names the attribute holding the value, the one
/// holding its maximum, and what colour to draw it — see `[bars]` in
/// `witchlight.conf`, which ships with what a stock Rustbound Magic uses.
///
/// **A bar is drawn only for a player who has one.** An attribute that is not
/// there, or whose maximum is zero, is a player this does not apply to: somebody
/// who has not taken up magic, a server without the mod, an entry naming
/// something nothing on this server keeps. No bar is the honest picture of every
/// one of those, and it is what makes naming an attribute cost nothing.
/// </summary>
public sealed record PlayerBar(string Name, string Value, string Max, string Colour, string Group)
{
    /// <summary>How many bars one card may carry, past which it is a chart.</summary>
    private const int MostBars = 6;

    /// <summary>What separates the four parts of one entry.</summary>
    private const char Between = '|';

    /// <summary>
    /// What the settings ask for, read once.
    ///
    /// Held rather than re-read, because this is asked of every player on every
    /// live post — twice a second on a busy server — and the answer changes only
    /// when somebody edits the file.
    /// </summary>
    private static IReadOnlyList<PlayerBar>? _wanted;

    /// <summary>
    /// The bars this server has been asked to show.
    ///
    /// The api is wanted only to name the mod behind an attribute, and only for
    /// an entry that did not name one itself — so a caller without one gets the
    /// bars and no groups rather than nothing.
    /// </summary>
    public static IReadOnlyList<PlayerBar> Settled(ICoreAPI? api) => _wanted ??= Read(api);

    /// <summary>The same, for a caller that has already settled them.</summary>
    public static IReadOnlyList<PlayerBar> Wanted => _wanted ??= Read(null);

    /// <summary>Reads them again, for a settings file that has changed.</summary>
    public static void Forget() => _wanted = null;

    /// <summary>
    /// What a settings file that has never heard of bars means.
    ///
    /// The same two the map service writes into a fresh file, because a file
    /// written before this existed has to behave as one written today — an
    /// operator who upgrades and sees nothing has no way to tell a feature that
    /// needs configuring from one that is broken, and this was the second of
    /// those to look at. `[commands]` has followed the same rule since it was
    /// added; a table nobody has written is the defaults, and a table somebody
    /// has emptied is none.
    ///
    /// Kept in step with `Config::default` in the service by hand, the way the
    /// command privileges are, because the service is the half that owns the
    /// format and this is the half that has to work before it has been written.
    /// </summary>
    private static readonly string[] ByDefault =
    {
        "Mana | entitybehavior-resource-currentmana_rm "
        + "| entitybehavior-resource-totalmaxmana_rm | #7c5cff | Rustbound Magic",
        "Magic | entitybehavior-resource-currentexptonextmaxmanalevel_rm "
        + "| entitybehavior-resource-maxexptonextmaxmanalevel_rm | #d8a24a | Rustbound Magic",
    };

    private static IReadOnlyList<PlayerBar> Read(ICoreAPI? api)
    {
        var found = new List<PlayerBar>();
        var written = Settings.HasTable("bars")
            ? Settings.Table("bars")
            : ByDefault.Select(said => ("", said));

        foreach (var (_, said) in written)
        {
            var parts = said.Split(Between);
            if (parts.Length < 3 || found.Count >= MostBars)
            {
                continue;
            }

            var name = parts[0].Trim();
            var value = parts[1].Trim();
            var max = parts[2].Trim();
            var colour = parts.Length > 3 ? parts[3].Trim() : "";
            var group = parts.Length > 4 ? parts[4].Trim() : "";

            if (name.Length > 0 && value.Length > 0 && max.Length > 0)
            {
                found.Add(new PlayerBar(
                    name, value, max, colour,
                    group.Length > 0 ? group : WhoseAttribute(api, value)));
            }
        }

        return found;
    }

    /// <summary>
    /// Which mod an attribute probably belongs to, where its own name says so.
    ///
    /// Asked only where the settings did not say. An attribute carries no record
    /// of what wrote it — the game keeps a tree of names and numbers and nothing
    /// about their author — so this cannot be answered properly, and the one
    /// honest guess available is that a mod naming its attributes after itself
    /// has said so. `xskills:level` finds xskills; Rustbound Magic's
    /// `entitybehavior-resource-currentmana_rm` finds nothing, which is why its
    /// entries name their group outright.
    ///
    /// Nothing is invented where nothing matches. A bar with no group is shown
    /// under a heading for the ones nobody could place, which is a true statement
    /// about it rather than a guess dressed as one.
    /// </summary>
    private static string WhoseAttribute(ICoreAPI? api, string attribute)
    {
        var named = attribute.ToLowerInvariant();
        Mod? best = null;
        foreach (var mod in api?.ModLoader?.Mods ?? Array.Empty<Mod>())
        {
            var id = mod?.Info?.ModID;
            if (string.IsNullOrEmpty(id) || id!.Length < 4 || !named.Contains(id))
            {
                continue;
            }

            // The longest match, so a mod called `magic` does not answer for one
            // called `magicextended`.
            if (id.Length > (best?.Info?.ModID?.Length ?? 0))
            {
                best = mod;
            }
        }

        return best?.Info?.Name ?? best?.Info?.ModID ?? "";
    }

    /// <summary>
    /// What this player's entity says about this bar, or nothing where it says
    /// nothing.
    /// </summary>
    public LiveBar? Of(Entity? entity) => Of(entity?.WatchedAttributes);

    /// <summary>
    /// The same, from the attributes alone.
    ///
    /// Split out because everything this decides is decided from them, and an
    /// entity is a thing a test cannot build: reading a mod's number correctly is
    /// the part worth checking, and it is checkable this way without a world, a
    /// server, or the mod itself.
    /// </summary>
    public LiveBar? Of(ITreeAttribute? watched)
    {
        if (watched is null || Number(watched, Max) is not { } most || most <= 0)
        {
            return null;
        }

        return new LiveBar
        {
            Name = Name,
            Group = Group,
            Colour = Colour,
            Value = Math.Clamp(Number(watched, Value) ?? 0f, 0f, most),
            Max = most,
        };
    }

    /// <summary>
    /// A number out of a player's attributes, whatever kind of number it is.
    ///
    /// The game's own readers answer the default for an attribute of the wrong
    /// kind rather than converting, and a mod is free to keep mana as an int and
    /// the experience toward the next level as a float — as the one this ships
    /// the settings for does. So the kind is read off the attribute rather than
    /// assumed, and anything that is not a number at all is nothing rather than
    /// a zero, which is what tells a bar that does not apply from one at empty.
    /// </summary>
    private static float? Number(ITreeAttribute watched, string key) =>
        watched.TryGetAttribute(key, out var held) ? AsNumber(held) : null;

    private static float? AsNumber(IAttribute held) => held switch
    {
        IntAttribute number => number.value,
        FloatAttribute number => number.value,
        DoubleAttribute number => (float)number.value,
        LongAttribute number => number.value,
        StringAttribute number when float.TryParse(
            number.value, NumberStyles.Float, CultureInfo.InvariantCulture, out var said) => said,
        _ => (float?)null,
    };
}
