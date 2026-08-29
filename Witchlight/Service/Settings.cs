using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// The settings both halves read.
///
/// The file belongs to the map service — it writes it, it owns the format — and
/// this half wants four or five values out of it. So the values are looked for
/// rather than parsed: a second reader of a whole format is a second thing to
/// keep in step with it, while a reader of one line is a reader of one line.
///
/// Every setting the mod asks about comes through here. That is what makes "what
/// does the operator want" one question with one answer, rather than a path
/// threaded through six call sites and a default written down beside each.
/// </summary>
public static class Settings
{
    /// <summary>Beside the server's other mod settings, where the service writes it.</summary>
    public static string Path => System.IO.Path.Combine(GamePaths.ModConfig, "witchlight.conf");

    /// <summary>
    /// Where map data is kept, before any per-world directory inside it.
    ///
    /// The `witchlight` folder beside the world data unless the settings name
    /// somewhere else — a larger disk, a directory a web server already serves.
    /// </summary>
    public static string MapData
    {
        get
        {
            var told = Value("map_data");
            return string.IsNullOrWhiteSpace(told)
                ? System.IO.Path.Combine(GamePaths.DataPath, ExportDirName)
                : told.Trim();
        }
    }

    /// <summary>
    /// Whether each world's map goes in a directory of its own.
    ///
    /// Settled with the rest of it once the world is up, because what an absent
    /// setting means depends on which side is asking.
    /// </summary>
    public static bool PerWorld { get; private set; }

    /// <summary>
    /// Where every export lands.
    ///
    /// The one directory both halves agree on, so it is named once here rather
    /// than rebuilt from `GamePaths` wherever somebody needs it. Which world's
    /// map it is is not known until the world is up, so this is the folder they
    /// all sit in until <see cref="ForWorld"/> has been told.
    /// </summary>
    public static string Exports => _exports ?? MapData;

    /// <summary>Which world's map this is, once a world has said.</summary>
    private static string? _exports;

    /// <summary>
    /// Settles which directory this world's map goes in.
    ///
    /// Called once the world is up, because until then there is no world to name
    /// one after. Everything written before that point — the palette, the block
    /// names, the icons — has to wait for it, which is why they are exported here
    /// rather than when the assets finish loading.
    /// </summary>
    public static void ForWorld(ICoreServerAPI api)
    {
        // An absent setting means the answer for this side rather than a fixed
        // one. A dedicated server runs one world out of one data path and its map
        // has always been where it is; every singleplayer save shares one data
        // path and would otherwise write into the last world's map. A settings
        // file written before this setting existed says nothing, and reading that
        // silence as "off" would leave singleplayer with the fault this fixes.
        PerWorld = On("per_world", byDefault: !api.Server.IsDedicated);
        _exports = MapDirectory.Settle(api, MapData, PerWorld);
        Directory.CreateDirectory(_exports);
    }

    private const string ExportDirName = "witchlight";

    /// <summary>Whether the settings ask the mod to run the service itself.</summary>
    public static bool Autostarts => On("autostart", byDefault: true);

    /// <summary>Whether a joining player is told where the map is.</summary>
    public static bool Announces => On("announce", byDefault: true);

    /// <summary>
    /// Whether a marker nobody has decided about belongs to everybody.
    ///
    /// Off by default: a marker a player drops is theirs until they say
    /// otherwise. An operator running a map the whole server is meant to share
    /// turns it on, and every marker without a decision of its own goes to
    /// everyone — in the game and on the web both, which is why the setting sits
    /// with the map rather than in a file of this mod's own.
    /// </summary>
    public static bool MarkersPublic => On("markers_public", byDefault: false);

    /// <summary>
    /// What a marker nobody has chosen for is, said the way the code asks it.
    ///
    /// Three places were each writing the negation of the setting above, which is
    /// three chances to get the polarity backwards and show somebody's markers to
    /// a server. It is one question — is an undecided marker private — so it has
    /// one answer here and every caller is a thin read of it.
    /// </summary>
    public static bool MarkersPrivateByDefault => !MarkersPublic;

    /// <summary>
    /// Whether a marker anybody can see is a marker anybody can change.
    ///
    /// Off by default: being shown something is not being handed it. An operator
    /// running a map the server keeps together — shared trader routes, a road
    /// nobody owns — turns it on, and then a public marker is everyone's to
    /// correct. A private marker is never anybody's but its owner's, whatever
    /// this says.
    /// </summary>
    public static bool PublicMarkersEditable => On("markers_public_editable", byDefault: false);

    /// <summary>
    /// Whether where a player is standing is everybody's to see.
    ///
    /// On by default, which is what a map of a server people play on together is
    /// for. An operator running a server where being findable is not part of the
    /// deal turns it off, and then a player shows on the map to their own group
    /// and to nobody else. How many are online is still said to everyone: that is
    /// a fact about the server rather than about anybody on it.
    ///
    /// Vintage Story has no setting of its own to follow. Its server config says
    /// nothing about who may see whom, and the nearest thing in the world config,
    /// `allowMap`, decides whether there is a map at all — a different question.
    /// So this is witchlight's own, and it sits with the map settings because it
    /// is the map it is about.
    ///
    /// Enforced here rather than by the service, for the reason the markers are:
    /// this is the half that knows what groups the game has people in, and a
    /// service holding positions it must not send is one bug from sending them.
    /// </summary>
    public static bool PlayersPublic => On("players_public", byDefault: true);

    /// <summary>
    /// The address to give a player, or null when there is none to give.
    ///
    /// What an operator set, if they set anything. A server on the open internet
    /// is reached at a name, through a proxy, on a port the service never sees, so
    /// the address it works out for itself is right only on a machine a player can
    /// reach directly.
    /// </summary>
    public static string? Announcement()
    {
        var told = Value("announce_url");
        return string.IsNullOrWhiteSpace(told) ? Address() : told.Trim();
    }

    /// <summary>
    /// Where the service writes the addresses it answers on.
    ///
    /// Named here because this is the half that reads it, and named once because
    /// two other places quote it in what they tell an operator to go and look at.
    /// </summary>
    public static string AddressPath => System.IO.Path.Combine(Exports, "service.json");

    /// <summary>
    /// Where the map is listening, as the service itself last published it.
    ///
    /// The service works out which addresses its bind address actually answers on
    /// — `0.0.0.0` is not something anyone can type into a browser — and writes
    /// them down in the order worth offering, so the first is the answer.
    /// </summary>
    public static string? Address()
    {
        try
        {
            var path = AddressPath;
            if (!File.Exists(path))
            {
                return null;
            }

            return JObject.Parse(File.ReadAllText(path))["Urls"] is JArray { Count: > 0 } urls
                ? urls[0].ToString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure there is a settings file, by asking the service to write one.
    /// Gives back where it is, or null when it could not be written.
    ///
    /// Written by the service and not here: the format is the service's, and a
    /// second program writing a format it does not own is how the two come to
    /// disagree about it. The data path is passed in because that is the one thing
    /// the service cannot work out for itself.
    /// </summary>
    public static string? EnsureWritten(ICoreServerAPI api, string executable)
    {
        if (File.Exists(Path))
        {
            return Path;
        }

        try
        {
            Directory.CreateDirectory(GamePaths.ModConfig);

            var write = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            write.ArgumentList.Add("--config");
            write.ArgumentList.Add(Path);
            write.ArgumentList.Add("--vs-data");
            write.ArgumentList.Add(GamePaths.DataPath);
            // The default for wherever this file is being created, because the
            // service cannot tell singleplayer from a dedicated server and this
            // half can. Written once; after that it is the operator's to change.
            write.ArgumentList.Add("--per-world");
            write.ArgumentList.Add(api.Server.IsDedicated ? "false" : "true");
            write.ArgumentList.Add("--save-config");
            write.ArgumentList.Add("--print-config");

            using var writing = Process.Start(write);
            if (writing is null)
            {
                return null;
            }

            writing.StandardOutput.ReadToEnd();
            var complaint = writing.StandardError.ReadToEnd();
            writing.WaitForExit(WriteConfigMs);

            if (!File.Exists(Path))
            {
                api.Logger.Warning(
                    "[witchlight] the map service did not write {0}: {1}", Path, complaint.Trim());
                return null;
            }

            api.Logger.Notification("[witchlight] wrote default map settings to {0}", Path);
            return Path;
        }
        catch (Exception error)
        {
            api.Logger.Error("[witchlight] could not write {0}: {1}", Path, error);
            return null;
        }
    }

    /// <summary>Long enough for a cold start on a slow disk to write one file.</summary>
    private const int WriteConfigMs = 15000;

    /// <summary>
    /// One setting's value, by name, or null where the file does not say.
    ///
    /// The one place that knows how a line is shaped.
    /// </summary>
    public static string? Value(string key)
    {
        try
        {
            foreach (var line in File.ReadLines(Path))
            {
                var text = line.Trim();
                var at = text.IndexOf('=');
                if (text.StartsWith('#') || at < 0)
                {
                    continue;
                }

                // The whole key, not a prefix of one: a setting named
                // `announce_url` must not answer for `announce`.
                if (text[..at].Trim().Equals(key, StringComparison.Ordinal))
                {
                    return Said(text[(at + 1)..]);
                }
            }
        }
        catch (Exception)
        {
            // Unreadable settings are the service's to complain about, not a
            // reason for this half to change what it does.
        }

        return null;
    }

    /// <summary>
    /// What is on the right of the equals sign.
    ///
    /// A quoted value ends at its closing quote and an unquoted one ends at a
    /// comment, so that `announce = false # off` is off rather than a value that
    /// merely is not the word false.
    /// </summary>
    private static string Said(string after)
    {
        var text = after.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? text[1..] : text[1..close];
        }

        var comment = text.IndexOf('#');
        return (comment < 0 ? text : text[..comment]).Trim();
    }

    /// <summary>
    /// A yes-or-no setting, and what an absent one means.
    ///
    /// The default is given at the call rather than baked in here, because these
    /// do not all lean the same way: a map runs and announces itself unless told
    /// not to, and shares nobody's markers unless told to. One reader that
    /// silently assumed the first would have made the third quietly wrong.
    /// </summary>
    private static bool On(string key, bool byDefault)
    {
        var said = Value(key);
        return string.IsNullOrWhiteSpace(said)
            ? byDefault
            : string.Equals(said.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
