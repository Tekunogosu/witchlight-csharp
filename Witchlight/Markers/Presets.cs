using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// What a marker starts as, for the block somebody is standing on.
///
/// A preset is the map service's record, kept against a uid, and it is the map's
/// own form that ordinarily makes one. This is the same record read from the
/// game: the mod asks the service for a player's presets at the moment they mark
/// something, picks the one that names the block, and hands the answer back to
/// their client.
///
/// Nothing is held between asks. A preset made in a browser a minute ago has to
/// apply to the next press of the key, and a cache with a clock on it is a cache
/// that is wrong for exactly as long as that clock says.
/// </summary>
public static class Presets
{
    /// <summary>
    /// What one player has set for themselves, or the empty answer where the map
    /// service is not answering.
    ///
    /// Empty rather than null. Somebody who has never opened the map has set
    /// nothing, and a service that is down has told this side nothing — both are
    /// answered the same way, which is by falling back to what the operator set.
    /// </summary>
    public static async Task<Person> Of(MapService service, string uid, ILogger log)
    {
        var body = await service.Presets(uid).ConfigureAwait(false);
        return Read(body, log) ?? new Person();
    }

    /// <summary>
    /// Keeps one preset, and gives back everything that player has set once it
    /// has landed. Null where the service would not take it.
    ///
    /// Said out loud either way. Nothing waits on this — a marker that landed
    /// must not be undone by a service that would not take the preset beside it —
    /// so the log is the only thing that can report it, and a preset that
    /// silently failed to be kept is a person pressing the same switch again
    /// tomorrow.
    /// </summary>
    public static async Task<Person?> Keep(
        MapService service, string uid, Preset preset, ILogger log)
    {
        var body = await service.KeepPreset(uid, preset).ConfigureAwait(false);
        var kept = Read(body, log);
        if (kept is null)
        {
            log.Warning(
                "[witchlight] the map would not keep the preset for {0}", preset.Pattern);
        }
        else
        {
            log.Notification(
                "[witchlight] kept a preset for {0}; that player now has {1}",
                preset.Pattern, kept.Presets.Count);
        }
        return kept;
    }

    /// <summary>The first preset whose pattern names this block, or nothing.</summary>
    public static Preset? For(Person person, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }
        return person.Presets.FirstOrDefault(preset => BlockPattern.Fits(preset.Pattern, code));
    }

    private static Person? Read(string? body, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<Person>(body);
        }
        catch (Exception error)
        {
            log.Warning("[witchlight] could not read what the map holds for a player: {0}", error.Message);
            return null;
        }
    }
}

/// <summary>
/// One person's choices, in the words the map service keeps them in.
///
/// The same document the map's own settings window reads and writes, so the two
/// sides cannot come to disagree about what somebody set — see `preferences.rs`,
/// which is where it is stored and where the field names are decided.
/// </summary>
public class Person
{
    public List<Preset> Presets { get; set; } = new();

    /// <summary>
    /// Whether a new marker of theirs is private, where they have decided.
    /// Absent means the operator's <c>markers_public</c> decides, which is where
    /// everybody starts.
    /// </summary>
    public bool? PrivateByDefault { get; set; }

    /// <summary>Whether making a marker keeps it as a preset without being asked.</summary>
    public bool PresetsByDefault { get; set; }
}

/// <summary>What a marker starts as, when somebody marks a particular thing.</summary>
public class Preset
{
    /// <summary>The block code this names — <c>*</c> stands for any run of
    /// characters. See <see cref="BlockPattern"/>.</summary>
    public string Pattern { get; set; } = "";

    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";

    /// <summary>Whether markers made from this are their owner's alone. Absent
    /// means that person's own default decides.</summary>
    public bool? Private { get; set; }
}
