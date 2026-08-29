using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Which directory a world's map lives in.
///
/// A dedicated server runs one world out of one data path, so its map has always
/// sat in one folder and there is no reason to move it. A client runs every save
/// it has out of the same data path, and one folder for all of them means the
/// second world writes its terrain into the first world's map at the same region
/// coordinates — a map of two worlds at once, with nothing anywhere saying so.
/// It also means the palette and the block names, which are about the mod set and
/// not the world, are rewritten in full on every switch between a world with no
/// mods and a world with fifty.
///
/// So each world may have a directory of its own, named after it. Nothing is
/// shared between them, including what happens to be identical: a file written
/// once and then left alone costs nothing to keep, while one rewritten on every
/// switch costs a disk.
/// </summary>
public static class MapDirectory
{
    /// <summary>
    /// What this world's map is filed under.
    ///
    /// The world's own name, so a directory listing reads as a list of worlds,
    /// and enough of the savegame's identifier to tell two of them apart. Worlds
    /// called "New World" are not rare, and two of them sharing a directory is
    /// the whole failure this exists to prevent — a readable name is worth
    /// having and is not worth being wrong about.
    /// </summary>
    public static string NameFor(string? worldName, string? savegameId, int seed)
    {
        var named = Sanitised(worldName);
        // The identifier where the savegame has one, and something stable where
        // it does not: it is optional in the format, so a world made by an older
        // build may carry none. A name and a seed together are what that world
        // has instead, and they are as fixed as the world is.
        var unique = string.IsNullOrWhiteSpace(savegameId)
            ? $"{worldName}:{seed}"
            : savegameId!;
        return $"{named}-{Short(unique)}";
    }

    /// <summary>The same, for the world a server is actually running.</summary>
    public static string NameFor(ICoreServerAPI api)
    {
        var save = api.WorldManager.SaveGame;
        return NameFor(save?.WorldName, save?.SavegameIdentifier, save?.Seed ?? 0);
    }

    /// <summary>The world a server is running, as the three facts this needs.</summary>
    public static string Settle(ICoreServerAPI api, string baseDir, bool perWorld)
    {
        var save = api.WorldManager.SaveGame;
        return Settle(
            baseDir, save?.WorldName, save?.SavegameIdentifier, save?.Seed ?? 0, perWorld,
            api.Logger);
    }

    /// <summary>
    /// A name a filesystem will take, out of a name a person typed.
    ///
    /// Every path separator and every character Windows refuses, plus leading and
    /// trailing dots and spaces, which Windows also drops silently — a directory
    /// it renamed under us is a map that goes missing on the next start.
    /// </summary>
    private static string Sanitised(string? name)
    {
        var refused = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':' };
        var kept = new string((name ?? "")
            .Select(letter => refused.Contains(letter) || char.IsControl(letter) ? '_' : letter)
            .ToArray())
            .Trim()
            .Trim('.');

        // A world named entirely in characters a filesystem refuses is still a
        // world, and it still needs somewhere to go.
        return kept.Length == 0 ? "world" : kept[..Math.Min(kept.Length, 64)].TrimEnd();
    }

    /// <summary>Eight hex characters of something that does not change.</summary>
    private static string Short(string of)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(of));
        return Convert.ToHexString(digest)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Where this world's map goes, and moving what is already there if it has to.
    ///
    /// A map found sitting loose in the folder belongs to whichever world last
    /// ran, and turning this setting on must not leave it to be written over by
    /// the next one. It is moved down into a directory of its own before anything
    /// else happens: this world's, where `world.json` says it is this world's,
    /// and one named after whoever it does belong to otherwise. Nothing is
    /// deleted and nothing is merged.
    /// </summary>
    public static string Settle(
        string baseDir, string? worldName, string? savegameId, int seed, bool perWorld, ILogger log)
    {
        if (!perWorld)
        {
            return baseDir;
        }

        var mine = Path.Combine(baseDir, NameFor(worldName, savegameId, seed));
        MoveLooseMapAside(baseDir, mine, worldName, log);
        return mine;
    }

    /// <summary>
    /// Puts a map lying loose in the folder into a directory of its own.
    ///
    /// Only when there is one and only when it has nowhere to go yet. A folder
    /// that already holds this world's directory has been through this, and a
    /// move on top of it would be the merge this is here to prevent.
    /// </summary>
    private static void MoveLooseMapAside(
        string baseDir, string mine, string? worldName, ILogger log)
    {
        var loose = Path.Combine(baseDir, "palette.json");
        if (!File.Exists(loose))
        {
            return;
        }

        var whose = LooseMapBelongsTo(baseDir, mine, worldName);
        if (Directory.Exists(whose))
        {
            log.Warning(
                "[witchlight] there is a map in {0} and a map in {1}. The loose one has been left "
                + "where it is rather than written over the other; move or delete it by hand.",
                baseDir,
                whose);
            return;
        }

        try
        {
            var moved = Directory.CreateDirectory(whose + ".moving");
            foreach (var file in Directory.EnumerateFiles(baseDir))
            {
                File.Move(file, Path.Combine(moved.FullName, Path.GetFileName(file)));
            }
            foreach (var folder in Directory.EnumerateDirectories(baseDir))
            {
                var name = Path.GetFileName(folder);
                // Not the directories this is making, and not another world's.
                if (folder == moved.FullName || Directory.Exists(Path.Combine(folder, "columns")))
                {
                    continue;
                }
                Directory.Move(folder, Path.Combine(moved.FullName, name));
            }
            Directory.Move(moved.FullName, whose);

            log.Notification(
                "[witchlight] each world now keeps its own map, so the one that was in {0} "
                + "has been moved to {1}",
                baseDir,
                whose);
        }
        catch (Exception error)
        {
            log.Error(
                "[witchlight] could not move the map in {0} into {1}, so it has been left alone: "
                + "{2}",
                baseDir,
                whose,
                error);
        }
    }

    /// <summary>
    /// Whose the loose map is.
    ///
    /// `world.json` names the world it was written for. Where that is this world
    /// it goes straight into this world's directory, keys and all. Where it names
    /// another it goes under that name alone — this half cannot work out another
    /// world's identifier, and a map parked under a readable name is a map
    /// somebody can find, which beats one written over.
    /// </summary>
    public static string LooseMapBelongsTo(string baseDir, string mine, string? worldName)
    {
        var named = WorldFacts.NameIn(baseDir);
        return string.IsNullOrWhiteSpace(named) || named == worldName
            ? mine
            : Path.Combine(baseDir, Sanitised(named) + "-unknown");
    }
}
