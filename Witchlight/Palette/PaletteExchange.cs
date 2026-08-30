using System;
using System.Collections.Generic;
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
    /// What the bootstrap settled on, carried from one lifecycle stage to the next.
    ///
    /// The palette must be built at asset load and can only be asked for once the
    /// server is up, and those are two different calls with two different APIs. So
    /// the build hands its answer over rather than leaving it in a field for the
    /// asking half to find.
    /// </summary>
    public sealed record Built(Palette Palette, string Fingerprint, bool Wanted);

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

    public PaletteExchange(ICoreServerAPI api, string exports, Built built)
    {
        _api = api;
        _exports = exports;
        Fingerprint = built.Fingerprint;
        Wanted = built.Wanted;
        _mostSlices = built.Palette.Blocks.Count / PaletteTable.SliceSize + 2;
    }

    /// <summary>What this server's block registry hashes to.</summary>
    public string Fingerprint { get; }

    /// <summary>Whether the palette in hand is poor enough to be worth asking about.</summary>
    public bool Wanted { get; private set; }

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

        var written = palette.Write(path);

        // Coverage alone. A palette that colours what there is to colour is good,
        // and nothing about a mod's version number says otherwise: witchlight
        // ships no block textures, so its own releases moved the stamp and every
        // admin joining was asked for a palette that was already correct — and
        // every one of those answers blanked the map while it redrew.
        //
        // A texture changing under an unmoved block id is the case this cannot
        // see, and it is the case `/witchlight palette` exists for. Asking on
        // suspicion costs a blank map every time; asking by hand costs a command
        // on the rare occasion it is true.
        var wanted = palette.Coverage < RequiredCoverage;

        Report(api, exports, palette, existing, valid, moved, written, report, wanted);
        return new Built(palette, built.Fingerprint, wanted);
    }

    /// <summary>
    /// Asks a player for a palette, when the server could not build a usable one.
    ///
    /// Anybody is asked, not only an admin. A server whose operator never joins in
    /// game is otherwise a server with no map, and that is the ordinary shape of a
    /// dedicated server: it is run from a console, and the person who runs it may
    /// never have a character on it. What guards the map is not who is asked but
    /// what is done with the answer — see <see cref="Accept"/>.
    ///
    /// One at a time, because the table is a few hundred kilobytes and every copy
    /// after the first is thrown away.
    /// </summary>
    public void AskIfNeeded(IServerPlayer player)
    {
        if (!Wanted)
        {
            return;
        }

        if (_askedUid is not null && DateTime.UtcNow - _askedAt < AskAgainAfter)
        {
            return;
        }

        Ask(player);
    }

    /// <summary>
    /// Asks now, whatever the current palette looks like and whoever was asked
    /// last. What `/witchlight palette` does.
    /// </summary>
    public void AskAnyway(IServerPlayer player)
    {
        Wanted = true;
        _askedUid = null;
        _askedAt = DateTime.MinValue;
        Ask(player);
    }

    private void Ask(IServerPlayer player)
    {
        _askedUid = player.PlayerUID;
        _askedAt = DateTime.UtcNow;
        _api.Network.GetChannel(Channel.Name)
            .SendPacket(new PaletteRequest { Fingerprint = Fingerprint }, player);
        _api.Logger.Notification(
            "[witchlight] asked {0} for a block colour palette", player.PlayerName);

        // Silence is otherwise indistinguishable from success at a glance.
        var uid = player.PlayerUID;
        var name = player.PlayerName;
        _api.Event.RegisterCallback(_ =>
        {
            if (Wanted && _askedUid == uid)
            {
                _api.Logger.Notification(
                    "[witchlight] no palette came back from {0} — check that they have the mod, "
                    + "and their client log for why it declined",
                    name);
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
        var merged = existing is not null && existing.Fingerprint == Fingerprint
            // An admin's colours win; anybody else's only fill the gaps.
            ? (trusted ? Palette.Merge(supplied, existing) : Palette.Merge(existing, supplied))
            : supplied;

        // Said as it is, whichever way round the merge went: what a reader of
        // `status` wants to know is whether these colours came off a client's
        // assets, and after either merge they did.
        merged.Source = supplied.Source;

        var written = merged.Write(path);
        Wanted = merged.Coverage < RequiredCoverage;
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
    }

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

        return $"palette: from {palette.Source}, {palette.Coloured} of {palette.Blocks.Count} blocks "
            + $"coloured ({palette.Coverage:P0}), for registry {palette.Fingerprint}"
            + (palette.Fingerprint == Fingerprint ? "" : $" — STALE, this server is {Fingerprint}");
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
