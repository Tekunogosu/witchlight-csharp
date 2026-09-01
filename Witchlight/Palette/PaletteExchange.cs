using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Getting a usable block colour palette, wherever it has to come from.
///
/// A dedicated server's install ships almost no block textures — 46 files
/// against a full game's 9,587 — so the palette it builds for itself colours
/// nearly nothing. A connected client has every texture *and* the block ids the
/// server assigned, so the palette it builds lines up with that server's exports.
///
/// So this is a negotiation rather than a build: the server builds what it can at
/// asset load, keeps whatever a previous run or a client left that still matches
/// its registry, and asks a player for the rest. Everything that decides when to
/// ask, whom to ask, and what to do with an answer is here.
///
/// **Anybody may fill in what is missing; only an admin may replace what is
/// there.** It used to be admins alone, which is the right instinct — a palette
/// decides what every block on the map looks like and arrives from a machine the
/// server does not control — and the wrong rule, because a server whose operator
/// never joins in game has no map at all. What settles it is that the two cases
/// are not the same risk: a colour laid over a block that has none can only
/// improve on nothing, and one laid over a colour somebody chose is a change to
/// what is already right. So a player's palette is merged as filler, an admin's
/// is preferred, and `/witchlight palette` is the way back either way.
///
/// Two separate things are therefore worth asking for, and they are asked for on
/// different terms. **A missing colour** is a hole in the map and anybody who can
/// see it can fill it, so the room is asked — an admin first, since theirs is the
/// answer the map should keep, and otherwise whoever is about. **An unconfirmed
/// tileset** is not a hole at all: the base game's colours ship with this mod and
/// a server running them is fully drawn from the moment it starts, but nobody has
/// yet said those are the colours this server should look like. A texture pack
/// changes every one of them without changing a block code, so each admin is
/// asked once, and their answer either matches what is stored — in which case
/// nothing is written and nothing is redrawn — or replaces it.
/// </summary>
public sealed class PaletteExchange
{
    /// <summary>
    /// How much of what there is must have a colour before the server stops
    /// asking. A dedicated server with no block textures scores near zero; one
    /// running from a full game install scores near one.
    /// </summary>
    private const double RequiredCoverage = 0.9;

    /// <summary>How long to wait before saying an ask went unanswered.</summary>
    private const int ReplyWaitMs = 30000;

    /// <summary>How long one ask stands before another player may be asked.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Why somebody is being asked, in the two forms the log needs it.
    ///
    /// Both are said about the same packet and they are not the same event. A
    /// silence after one is a map with holes still in it; a silence after the
    /// other costs nothing at all, and reporting the two the same way is how an
    /// operator learns to skip the line that matters.
    /// </summary>
    private sealed record Reason(string Asked, string Missed);

    /// <summary>The map cannot draw something and a client's assets could.</summary>
    private static readonly Reason ForColours = new(
        "for a block colour palette",
        "so the map still has no colour for the blocks it is missing");

    /// <summary>
    /// The map is drawn and nobody who could decide has said it is drawn right.
    /// </summary>
    private static readonly Reason ToConfirm = new(
        "to confirm the map is showing this server's own colours",
        "so the map keeps the colours it has, which is no worse than before");

    /// <summary>
    /// What the bootstrap settled on, carried from one lifecycle stage to the next.
    ///
    /// The palette must be built at asset load and can only be asked for once the
    /// server is up, and those are two different calls with two different APIs. So
    /// the build hands its answer over rather than leaving it in a field for the
    /// asking half to find.
    /// </summary>
    public sealed record Built(Palette Palette, string Fingerprint);

    private readonly ICoreServerAPI _api;
    private readonly string _exports;

    /// <summary>Palette slices in flight, by the player sending them.</summary>
    private readonly Dictionary<string, List<PaletteTable>> _incoming = new(StringComparer.Ordinal);

    /// <summary>
    /// The most slices one palette can honestly be on this server.
    ///
    /// A slice carries a fixed number of blocks and this server has only so many,
    /// so anything past this is not a palette. It matters because the sender no
    /// longer has to be an admin: a client claiming a million parts and streaming
    /// them would otherwise grow this process until it died, and "only admins can
    /// reach it" was the whole of what stopped that before.
    /// </summary>
    private readonly int _mostSlices;

    private string? _askedUid;
    private DateTime _askedAt = DateTime.MinValue;

    /// <summary>Whether the palette in hand colours too little of the registry.</summary>
    private bool _poor;

    /// <summary>Whether an admin's own assets have settled the colours in hand.</summary>
    private bool _fromAdmin;

    /// <summary>Block ids the palette in hand says put nothing where they stand.</summary>
    private HashSet<int> _hidden = new();

    /// <summary>
    /// The blocks the palette in hand should be able to draw and cannot.
    ///
    /// The other half of why a palette is worth asking for, and the half that
    /// matters on a server whose palette is otherwise fine: a map may be 98%
    /// coloured and still have no colour for bare soil, which is the block a
    /// player uncovers every time they dig. Coverage cannot see that — it is one
    /// number over fourteen thousand blocks — so the gap itself is what is
    /// watched.
    /// </summary>
    private IReadOnlyList<string> _gaps = Array.Empty<string>();

    /// <summary>
    /// Who has been asked for a palette this run.
    ///
    /// One ask per player, because one is all a player has to give: a client sends
    /// the whole of what its assets can colour, and everything it could fill is
    /// filled by the merge. Asking again is a packet nobody needed, whether the
    /// first answer improved the palette or never arrived at all. Somebody who
    /// joins later has not been asked and may have the mod set that answers.
    ///
    /// Emptied only by `/witchlight palette`, which is an admin saying that the
    /// colours themselves have moved under the same blocks — the one case none of
    /// this can see.
    /// </summary>
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    public PaletteExchange(ICoreServerAPI api, string exports, Built built)
    {
        _api = api;
        _exports = exports;
        Fingerprint = built.Fingerprint;
        _mostSlices = built.Palette.Blocks.Count / PaletteTable.SliceSize + 2;
        Settled(built.Palette);
    }

    /// <summary>What this server's block registry hashes to.</summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Whether a palette from a client would improve on the one in hand.
    ///
    /// Two reasons, and either is enough: the palette colours too little of the
    /// registry to draw a map at all, or it has a specific block it cannot draw
    /// that it ought to be able to. The second is the one that keeps a map
    /// correct after it is already working.
    /// </summary>
    public bool Wanted => _poor || _gaps.Count > 0;

    /// <summary>What this palette cannot draw and should be able to.</summary>
    public IReadOnlyList<string> Gaps => _gaps;

    /// <summary>
    /// Whether nobody who could decide what this server looks like has decided.
    ///
    /// A different question from <see cref="Wanted"/> and it outlives it: a map
    /// drawn entirely from the colours this mod ships is complete, and still
    /// nobody has said those are this server's colours rather than the base
    /// game's. One ask of one admin settles it either way.
    /// </summary>
    public bool Unconfirmed => !_fromAdmin;

    /// <summary>
    /// Takes the palette now in hand, which is what decides whether another is
    /// worth asking for.
    /// </summary>
    private void Settled(Palette palette)
    {
        _poor = palette.Coverage < RequiredCoverage;
        _gaps = palette.Uncoloured;
        _fromAdmin = palette.FromAdmin;
        _hidden = palette.Blocks.Values
            .Where(entry => entry.Invisible == true)
            .Select(entry => entry.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Whether this block puts anything where it stands.
    ///
    /// Asked by the exporter, which walks down a column looking for its surface
    /// and used to stop at the first thing that was not air. That is a different
    /// question from "is this air": a large structure stands its real block
    /// beside a run of invisible placeholders, so a column that stopped on one
    /// recorded a block with nothing to draw where there was grass, and the map
    /// painted it as ground nobody had ever explored — a handful of black specks
    /// scattered through a finished map.
    ///
    /// The palette is asked rather than the block itself, because this is the
    /// question the palette was built to answer and answering it a second way is
    /// two answers waiting to disagree. It moves when the palette moves, so a
    /// better palette from a client corrects what gets exported as well as what
    /// gets coloured.
    ///
    /// A palette that says nothing about a block is taken at its word: unknown
    /// means it shows, which is what the exporter did before any of this.
    /// </summary>
    public bool Shows(int id) => !_hidden.Contains(id);

    /// <summary>
    /// What this side could build, before it has anywhere to put it.
    ///
    /// Built at asset load, which is the only window: the server frees block
    /// textures once assets are loaded, so a palette not built by then cannot be
    /// built at all. Where it goes is another question and is answered later —
    /// the map may be filed per world, and which world is running is not known
    /// this early.
    /// </summary>
    public sealed record Raw(Palette Palette, PaletteReport Report);

    /// <summary>Reads the blocks while their textures are still in memory.</summary>
    public static Raw BuildFromAssets(ICoreAPI api)
    {
        var built = PaletteBuilder.Build(api, out var report);
        return new Raw(built, report);
    }

    /// <summary>
    /// Reconciles what was built with whatever is already on disk, and writes it.
    ///
    /// Run once the world is up and the map's directory is settled. Split from
    /// the building above because the two answer to different clocks: one has to
    /// happen while the textures are there, the other cannot happen until there
    /// is somewhere to write.
    /// </summary>
    public static Built Settle(ICoreAPI api, string exports, Raw raw)
    {
        var (built, report) = (raw.Palette, raw.Report);

        var path = Palette.PathIn(exports);
        var existing = Palette.Read(path, api.Logger);

        // A stored colour is keyed on a block id, so the fingerprint — which is
        // the block registry and nothing else — is the whole of whether those
        // colours still mean anything. Nothing else may discard them.
        var valid = existing is not null && existing.Fingerprint == built.Fingerprint;

        // A moved mod stamp means some mod's textures may have changed under
        // colours that are still keyed correctly: stale, not wrong. This once
        // threw the palette away for that, and a dedicated server builds itself
        // one with no colours at all — so a map that had every colour lost them
        // on the next restart, and drew nothing above its stored zoom levels
        // until an admin happened to join. Stale colours beat none. They are
        // kept, and an admin is asked for a fresh set instead.
        var moved = valid && existing!.ModStamp.Length > 0 && existing.ModStamp != built.ModStamp;

        var palette = valid ? Palette.Merge(existing!, built) : built;
        if (valid)
        {
            // Reconciled against the mod set as it stands, so one move of it
            // costs one ask rather than an ask on every start thereafter.
            palette.ModStamp = built.ModStamp;
        }

        // The base game's colours, for whatever is still without one. Last,
        // because a colour this server or a player worked out from real assets
        // beats a recording of somebody else's, and only where nothing else
        // answered — which on a dedicated server running the base game is very
        // nearly everything, and on a full install is very nearly nothing.
        var seeded = Vanilla.Fill(api, palette);

        var written = palette.Write(path);

        // Coverage, and nothing about a mod's version number: witchlight ships no
        // block textures, so its own releases moved the stamp and every admin
        // joining was asked for a palette that was already correct — and every one
        // of those answers blanked the map while it redrew.
        //
        // Coverage is not the whole of whether a palette is wanted; a specific
        // block it cannot draw is the other half, and that is watched from the
        // palette itself rather than settled once here — see `Settled`.
        //
        // A texture changing under an unmoved block id is the case neither can
        // see, and it is the case `/witchlight palette` exists for.
        //
        // Read after the seed above, not before it: a dedicated server running
        // the base game is now fully coloured by the time this is asked, and a
        // line telling its operator that the next player will be asked for a
        // palette would be describing a server other than theirs.
        var poor = palette.Coverage < RequiredCoverage;

        Report(api, exports, palette, existing, valid, moved, written, report, poor);
        Vanilla.Report(api, seeded);
        return new Built(palette, built.Fingerprint);
    }

    /// <summary>
    /// Asks whoever in the room is best placed to answer, if anything is worth
    /// asking for.
    ///
    /// The one way an ask is ever made unprompted, and the one place the two
    /// reasons for making one are weighed. It runs on the export beat and again
    /// as each player joins, which is what makes a joining admin the person asked
    /// rather than whoever happened to be standing about.
    ///
    /// **An admin is asked first, always.** Theirs is the tileset the map should
    /// look like, so where one is in the room there is no reason to ask anybody
    /// else and every reason not to: a colour taken from a player is a colour an
    /// admin's answer will only have to replace. Where there is no admin, one of
    /// the others is picked at random rather than by connection order, so a
    /// server does not put the same person's client to work every time it comes
    /// up.
    ///
    /// **Nobody outside the setting is asked at all.** `commands.palette` says
    /// who may be asked for one by hand, and a server that has narrowed that has
    /// said something about whose assets it trusts — asking round the room past
    /// it would be the mod overruling its own operator.
    ///
    /// One at a time, because the table is a few hundred kilobytes and every copy
    /// after the first is thrown away.
    /// </summary>
    public void AskAround(IEnumerable<IServerPlayer> playing)
    {
        if (!Wanted && !Unconfirmed)
        {
            return;
        }

        if (_askedUid is not null && DateTime.UtcNow - _askedAt < AskAgainAfter)
        {
            return;
        }

        var room = playing
            .Where(player => !_asked.Contains(player.PlayerUID))
            .Where(player => Permissions.Holds(player, Permissions.Palette))
            .ToList();
        var admins = room.Where(player => player.HasPrivilege(Privilege.controlserver)).ToList();

        if (OneOf(admins) is { } admin)
        {
            Ask(admin, Wanted ? ForColours : ToConfirm);
            return;
        }

        // Only a hole in the map is worth troubling a player who cannot settle
        // what the map should look like. An unconfirmed tileset is a question
        // only an admin can answer, so it waits for one however long that takes.
        if (Wanted && OneOf(room) is { } anybody)
        {
            Ask(anybody, ForColours);
        }
    }

    /// <summary>
    /// One of them, chosen at random, or nothing where there are none.
    ///
    /// At random rather than first: the room is handed over in whatever order the
    /// server holds its players, which is stable across a restart, so taking the
    /// front of it means one player's client answers for the whole server for as
    /// long as they keep playing there.
    /// </summary>
    private IServerPlayer? OneOf(IReadOnlyList<IServerPlayer> among) =>
        among.Count == 0 ? null : among[_api.World.Rand.Next(among.Count)];

    /// <summary>
    /// Asks now, whatever the current palette looks like and whoever was asked
    /// last. What `/witchlight palette` does.
    /// </summary>
    public void AskAnyway(IServerPlayer player)
    {
        _asked.Clear();
        _askedUid = null;
        _askedAt = DateTime.MinValue;
        Ask(player, ForColours);
    }

    private void Ask(IServerPlayer player, Reason why)
    {
        _askedUid = player.PlayerUID;
        _askedAt = DateTime.UtcNow;
        _asked.Add(player.PlayerUID);
        _api.Network.GetChannel(Channel.Name)
            .SendPacket(new PaletteRequest { Fingerprint = Fingerprint }, player);
        _api.Logger.Notification("[witchlight] asked {0} {1}", player.PlayerName, why.Asked);

        // Silence is otherwise indistinguishable from success at a glance.
        var uid = player.PlayerUID;
        var name = player.PlayerName;
        _api.Event.RegisterCallback(_ =>
        {
            if (_askedUid == uid)
            {
                _api.Logger.Notification(
                    "[witchlight] no palette came back from {0}, who was asked {1} — {2}. Check "
                    + "that they have the mod, and their client log for why it declined",
                    name, why.Asked, why.Missed);
            }
        }, ReplyWaitMs);
    }

    /// <summary>
    /// Takes a palette slice from a client, and keeps whatever the whole of it
    /// adds.
    ///
    /// Merged rather than replaced: a client only has textures for the mods it has
    /// installed, so two players with different mod sets between them can produce
    /// a complete palette where neither could alone.
    ///
    /// **Whose colours win depends on who sent them.** An admin's palette is
    /// preferred over what is stored, because an admin correcting the map is the
    /// whole reason `/witchlight palette` exists. Anybody else's is filler: it
    /// colours blocks that have no colour and leaves every colour already chosen
    /// alone, so a player cannot repaint a map somebody set up.
    /// </summary>
    public void Accept(IServerPlayer player, PaletteTable table)
    {
        if (!Sane(player, table))
        {
            return;
        }

        if (table.Fingerprint != Fingerprint)
        {
            _api.Logger.Warning(
                "[witchlight] ignored a palette from {0}: it was built for a different mod set",
                player.PlayerName);
            return;
        }

        // Slices arrive one packet at a time; nothing is written until the whole
        // palette is here.
        if (!_incoming.TryGetValue(player.PlayerUID, out var slices))
        {
            slices = new List<PaletteTable>();
            _incoming[player.PlayerUID] = slices;
        }

        slices.RemoveAll(existing => existing.Part == table.Part);
        slices.Add(table);

        if (slices.Count < table.Parts)
        {
            return;
        }

        _incoming.Remove(player.PlayerUID);

        var trusted = player.HasPrivilege(Privilege.controlserver);
        var path = Palette.PathIn(_exports);
        var supplied = PaletteTable.Assemble(slices, id => _api.World.GetBlock(id)?.Code?.ToString());
        var existing = Palette.Read(path, _api.Logger);

        // An admin sending one is the map being told what it should look like,
        // and that is remembered whether or not a single colour moves: it is why
        // the next admin to join is not asked the same question again.
        supplied.FromAdmin = trusted;

        var merged = existing is not null && existing.Fingerprint == Fingerprint
            // An admin's colours win; anybody else's only fill the gaps.
            ? (trusted ? Palette.Merge(supplied, existing) : Palette.Merge(existing, supplied))
            : supplied;

        // Said as it is, whichever way round the merge went: what a reader of
        // `status` wants to know is whether these colours came off a client's
        // assets, and after either merge they did.
        merged.Source = supplied.Source;

        var written = merged.Write(path);
        var before = _gaps;
        var confirmed = trusted && Unconfirmed;
        Settled(merged);
        _askedUid = null;

        _api.Logger.Notification(
            "[witchlight] palette from {0}{1}: {2} of {3} blocks coloured ({4:P0}){5}{6}",
            player.PlayerName,
            trusted ? "" : " (not an admin, so it only filled gaps)",
            merged.Coloured,
            merged.Textured,
            merged.Coverage,
            written ? "" : ", unchanged — the map was not redrawn",
            Wanted ? ", still incomplete" : "");

        // The answer to the question an admin was asked, said plainly, because
        // the two outcomes look identical from the line above and mean opposite
        // things about whether anybody needs to look at the map.
        if (confirmed)
        {
            _api.Logger.Notification(
                written
                    ? "[witchlight] {0} is an admin, so these are now the colours this server "
                      + "shows — the map is being redrawn with them"
                    : "[witchlight] {0} is an admin and their colours are the ones already "
                      + "stored, so nothing changed and the map was not redrawn",
                player.PlayerName);
        }

        ReportGaps(before);
    }

    /// <summary>
    /// Says what the map still cannot draw, once somebody has answered.
    ///
    /// Named out loud rather than counted, because the codes are the whole of what
    /// an operator can act on: a block nobody's client can colour is a block whose
    /// mod ships no texture for it, and that is a report to make to that mod
    /// rather than a command to run again here. Said once per answer, and the
    /// asking runs out on its own once everybody has been asked — see
    /// <see cref="_asked"/> — so a gap nobody can fill is reported rather than
    /// chased for ever.
    /// </summary>
    private void ReportGaps(IReadOnlyList<string> before)
    {
        if (_gaps.Count == 0)
        {
            if (before.Count > 0)
            {
                _api.Logger.Notification(
                    "[witchlight] every block the map can be asked to draw now has a colour");
            }
            return;
        }

        _api.Logger.Notification(
            "[witchlight] {0} block(s) still have no colour and will draw as bare ground: {1}{2}",
            _gaps.Count,
            string.Join(", ", _gaps.Take(MostNamed)),
            _gaps.Count > MostNamed ? $", and {_gaps.Count - MostNamed} more" : "");
    }

    /// <summary>How many missing blocks to name before the line stops being read.</summary>
    private const int MostNamed = 12;

    /// <summary>
    /// Whether this slice could be part of a palette for this server at all.
    ///
    /// Its own check because the sender no longer has to be an admin. A part
    /// outside its own total, a total larger than this server's registry could
    /// produce, or more slices held than that total allows are each a client
    /// growing this process rather than sending a palette, and each ends the
    /// whole attempt rather than only that packet.
    /// </summary>
    private bool Sane(IServerPlayer player, PaletteTable table)
    {
        var held = _incoming.TryGetValue(player.PlayerUID, out var slices) ? slices.Count : 0;
        if (table.Parts > 0
            && table.Parts <= _mostSlices
            && table.Part >= 0
            && table.Part < table.Parts
            && held < _mostSlices)
        {
            return true;
        }

        _incoming.Remove(player.PlayerUID);
        _api.Logger.Warning(
            "[witchlight] dropped a palette from {0}: part {1} of {2} is not one this server "
            + "could have asked for (at most {3} parts)",
            player.PlayerName, table.Part, table.Parts, _mostSlices);
        return false;
    }

    /// <summary>
    /// Forgets a half-sent palette, because the player sending it has gone.
    ///
    /// Slices are held until the set is complete, and a set that never completes
    /// would otherwise sit in memory for the life of the server.
    /// </summary>
    public void Forget(string uid) => _incoming.Remove(uid);

    /// <summary>
    /// One line for `/witchlight status`.
    ///
    /// Both fingerprints, when they disagree. The palette's own says what it was
    /// built for and the server's says what it would have to be built for now, and
    /// a line carrying only one of them cannot show the difference — which is the
    /// whole of why a map draws nothing.
    /// </summary>
    public string Describe()
    {
        if (Palette.Read(Palette.PathIn(_exports), _api.Logger) is not { } palette)
        {
            return $"palette: none written yet, for registry {Fingerprint}";
        }

        var gaps = palette.Uncoloured;
        return $"palette: from {palette.Source}, {palette.Coloured} of {palette.Blocks.Count} blocks "
            + $"coloured ({palette.Coverage:P0}), for registry {palette.Fingerprint}"
            + (palette.FromAdmin
                ? ", settled by an admin"
                : ", not yet confirmed by an admin — the next one to join is asked once")
            + (palette.Fingerprint == Fingerprint ? "" : $" — STALE, this server is {Fingerprint}")
            + (gaps.Count == 0
                ? ""
                : $"\n  no colour for {gaps.Count} block(s) that draw, so they map as bare ground: "
                  + string.Join(", ", gaps.Take(MostNamed))
                  + (gaps.Count > MostNamed ? $", and {gaps.Count - MostNamed} more" : ""));
    }

    /// <summary>
    /// Says what the bootstrap found, loudly where colours were genuinely lost.
    /// </summary>
    private static void Report(
        ICoreAPI api,
        string exports,
        Palette palette,
        Palette? existing,
        bool valid,
        bool moved,
        bool written,
        PaletteReport report,
        bool wanted)
    {
        if (moved)
        {
            api.Logger.Notification(
                "[witchlight] a mod moved since these {0} colours were built — keeping them. "
                + "Run `/witchlight palette` if a mod changed its block textures.",
                palette.Coloured);
        }

        // Said out loud, because it is the one case where colours are genuinely
        // lost and the map goes flat until somebody joins to fix it.
        if (!valid && existing is not null && existing.Coloured > palette.Coloured)
        {
            api.Logger.Warning(
                "[witchlight] the block registry changed, so the stored palette's {0} colours no "
                + "longer match this world's block ids and have been replaced by this server's "
                + "own ({1} colours). The map draws nothing until a player joins and supplies one.",
                existing.Coloured,
                palette.Coloured);
        }

        if (wanted)
        {
            api.Logger.Notification(
                "[witchlight] only {0:P0} of blocks have a colour — the next player to join will be asked for a palette",
                palette.Coverage);
        }

        api.Logger.Notification(
            written
                ? "[witchlight] palette: {0} blocks written to {1}{2}"
                : "[witchlight] palette: {0} blocks, unchanged — {1} not rewritten{2}",
            palette.Blocks.Count,
            Palette.PathIn(exports),
            report.Missing > 0 ? $" ({report.Missing} without a readable texture)" : "");

        foreach (var group in Order(report.Counts))
        {
            api.Logger.Notification("[witchlight] missing group {0}: {1}", group.Key, group.Value);
        }
        foreach (var failure in report.Failures)
        {
            api.Logger.Notification("[witchlight] skipped {0}", failure);
        }
    }

    /// <summary>The dozen groups that lost the most, which is where to start looking.</summary>
    private static IEnumerable<KeyValuePair<string, int>> Order(IReadOnlyDictionary<string, int> counts)
    {
        var ordered = new List<KeyValuePair<string, int>>(counts);
        ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
        return ordered.GetRange(0, Math.Min(12, ordered.Count));
    }
}
