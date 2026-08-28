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
/// its registry, and asks one admin for the rest. Everything that decides when to
/// ask, whom to ask, and what to do with an answer is here.
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

    private string? _askedUid;
    private DateTime _askedAt = DateTime.MinValue;

    public PaletteExchange(ICoreServerAPI api, string exports, Built built)
    {
        _api = api;
        _exports = exports;
        Fingerprint = built.Fingerprint;
        Wanted = built.Wanted;
    }

    /// <summary>What this server's block registry hashes to.</summary>
    public string Fingerprint { get; }

    /// <summary>Whether the palette in hand is poor enough to be worth asking about.</summary>
    public bool Wanted { get; private set; }

    /// <summary>
    /// Builds what this side can and reconciles it with whatever is on disk.
    ///
    /// Run at asset load, which is the only window: the server frees block
    /// textures once assets are loaded, so a palette not built by then cannot be
    /// built at all — and that is before there is a server API to ask anybody
    /// anything with, which is why this is not a method on an instance.
    /// </summary>
    public static Built Bootstrap(ICoreAPI api, string exports)
    {
        var built = PaletteBuilder.Build(api, out var report);

        var path = Palette.PathIn(exports);
        var existing = Palette.Read(path);

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
    /// Asks one admin for a palette, when the server could not build a usable one.
    ///
    /// Only an admin is asked: a palette decides what every block on the map looks
    /// like, and it arrives from a machine the server does not control. One at a
    /// time, because the table is a few hundred kilobytes and every copy after the
    /// first is thrown away.
    /// </summary>
    public void AskIfNeeded(IServerPlayer player)
    {
        if (!Wanted || !player.HasPrivilege(Privilege.controlserver))
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
        _api.Network.GetChannel(SharedServer.Channel)
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
    /// Takes a palette slice from an admin's client, and keeps whatever the whole
    /// of it adds.
    ///
    /// Merged rather than replaced: a client only has textures for the mods it has
    /// installed, so two admins with different mod sets between them can produce a
    /// complete palette where neither could alone.
    /// </summary>
    public void Accept(IServerPlayer player, PaletteTable table)
    {
        if (!player.HasPrivilege(Privilege.controlserver))
        {
            _api.Logger.Warning(
                "[witchlight] ignored a palette from {0}, who is not an admin", player.PlayerName);
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

        if (table.Parts <= 0 || slices.Count < table.Parts)
        {
            return;
        }

        _incoming.Remove(player.PlayerUID);

        var path = Palette.PathIn(_exports);
        var supplied = PaletteTable.Assemble(slices, id => _api.World.GetBlock(id)?.Code?.ToString());
        var existing = Palette.Read(path);
        var merged = existing is not null && existing.Fingerprint == Fingerprint
            ? Palette.Merge(supplied, existing)
            : supplied;

        var written = merged.Write(path);
        Wanted = merged.Coverage < RequiredCoverage;
        _askedUid = null;

        _api.Logger.Notification(
            "[witchlight] palette from {0}: {1} of {2} blocks coloured ({3:P0}){4}{5}",
            player.PlayerName,
            merged.Coloured,
            merged.Textured,
            merged.Coverage,
            written ? "" : ", unchanged — the map was not redrawn",
            Wanted ? ", still incomplete" : "");
    }

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
        if (Palette.Read(Palette.PathIn(_exports)) is not { } palette)
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
                + "own ({1} colours). The map draws nothing until an admin joins and supplies one.",
                existing.Coloured,
                palette.Coloured);
        }

        if (wanted)
        {
            api.Logger.Notification(
                "[witchlight] only {0:P0} of blocks have a colour — an admin will be asked for a palette on join",
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
