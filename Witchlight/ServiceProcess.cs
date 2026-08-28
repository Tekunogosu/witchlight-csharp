using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// The map service, run as a child of the game server.
///
/// The service is a separate program and stays one: it knows pixels and this
/// knows the game, and a map worth keeping outlives any single game server. What
/// it does not need to be is a second thing to install, configure and remember to
/// start. So the binary rides along in the mod archive and is run from here,
/// while everything that made it a separate program still holds — it can be
/// turned off with one setting and started by hand instead.
///
/// Killing it outright is safe and is what happens at shutdown. Everything it
/// writes goes beside itself and is renamed into place, so there is no half
/// written file for it to be interrupted in the middle of.
/// </summary>
public sealed class ServiceProcess : IDisposable
{
    /// <summary>Where the binary sits inside the mod archive.</summary>
    private const string BundledAt = "service/linux-x64/witchlight";

    /// <summary>What it is called once unpacked, and in the log.</summary>
    private const string Name = "witchlight";

    private readonly ICoreServerAPI _api;
    private readonly string _executable;
    private readonly string _config;
    private readonly string _exports;

    private Process? _process;
    private StreamWriter? _log;
    private readonly object _writing = new();

    /// <summary>Whether the stop about to happen is one we asked for.</summary>
    private bool _stopping;

    private ServiceProcess(ICoreServerAPI api, string executable, string config, string exports)
    {
        _api = api;
        _executable = executable;
        _config = config;
        _exports = exports;
    }

    /// <summary>The settings both halves read, beside the server's other mod settings.</summary>
    public static string ConfigPath => Path.Combine(GamePaths.ModConfig, "witchlight.conf");

    /// <summary>Everything the service says, on its own so it can be tailed.</summary>
    public static string LogPath => Path.Combine(GamePaths.Logs, "witchlight-service.log");

    /// <summary>Whether a service of ours is running right now.</summary>
    public bool Running => _process is { HasExited: false };

    /// <summary>
    /// Unpacks the bundled service and makes sure there is a configuration file,
    /// without starting anything. Null when this machine has no service to run,
    /// which is said out loud rather than left to be noticed.
    /// </summary>
    public static ServiceProcess? Prepare(ICoreServerAPI api, Mod mod, string exports)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            api.Logger.Notification(
                "[witchlight] no bundled map service for {0} {1} — the map will export as usual, "
                + "and `witchlight serve` run yourself will serve it",
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture);
            return null;
        }

        var executable = Unpack(api, mod);
        if (executable is null)
        {
            return null;
        }

        var config = EnsureConfig(api, executable);
        return config is null ? null : new ServiceProcess(api, executable, config, exports);
    }

    /// <summary>
    /// One setting's value, by name, or null where the file does not say.
    ///
    /// Looked for rather than parsed: the format belongs to the service, this
    /// half wants two or three values out of it, and a second reader of a format
    /// is a second thing to keep in step with it. Everything the mod asks about
    /// comes through here, so there is one place that knows how a line is shaped.
    /// </summary>
    public static string? Setting(string config, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(config))
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
                    return Value(text[(at + 1)..]);
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
    private static string Value(string after)
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
    /// A setting that is on unless it says otherwise.
    ///
    /// Anything unreadable is on: these turn the map and the message announcing it
    /// on, and a map that quietly does not appear is the failure worth avoiding.
    /// </summary>
    /// <summary>
    /// A yes-or-no setting, and what an absent one means.
    ///
    /// The default is given at the call rather than baked in here, because these
    /// do not all lean the same way: a map runs and announces itself unless told
    /// not to, and shares nobody's markers unless told to. One reader that
    /// silently assumed the first would have made the third quietly wrong.
    /// </summary>
    private static bool On(string config, string key, bool byDefault)
    {
        var said = Setting(config, key);
        if (string.IsNullOrWhiteSpace(said))
        {
            return byDefault;
        }
        return string.Equals(said.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the settings ask the mod to run the service itself.</summary>
    public static bool Autostarts(string config) => On(config, "autostart", byDefault: true);

    /// <summary>Whether a joining player is told where the map is.</summary>
    public static bool Announces(string config) => On(config, "announce", byDefault: true);

    /// <summary>
    /// Whether a marker nobody has decided about belongs to everybody.
    ///
    /// Off by default: a marker a player drops is theirs until they say
    /// otherwise. An operator running a map the whole server is meant to share
    /// turns it on, and every marker without a decision of its own goes to
    /// everyone — in the game and on the web both, which is why the setting sits
    /// with the map rather than in a file of this mod's own.
    /// </summary>
    public static bool MarkersPublic(string config) => On(config, "markers_public", byDefault: false);

    /// <summary>
    /// Whether a marker anybody can see is a marker anybody can change.
    ///
    /// Off by default: being shown something is not being handed it. An operator
    /// running a map the server keeps together — shared trader routes, a road
    /// nobody owns — turns it on, and then a public marker is everyone's to
    /// correct. A private marker is never anybody's but its owner's, whatever
    /// this says.
    /// </summary>
    public static bool PublicMarkersEditable(string config) =>
        On(config, "markers_public_editable", byDefault: false);

    /// <summary>
    /// The address to give a player, or null when there is none to give.
    ///
    /// What an operator set, if they set anything. A server on the open internet
    /// is reached at a name, through a proxy, on a port the service never sees, so
    /// the address it works out for itself is right only on a machine a player can
    /// reach directly.
    /// </summary>
    public static string? Announcement(string config, string exports)
    {
        var told = Setting(config, "announce_url");
        return string.IsNullOrWhiteSpace(told) ? Address(exports) : told.Trim();
    }

    /// <summary>
    /// Where the map is listening, as the service itself last published it.
    ///
    /// The service works out which addresses its bind address actually answers on
    /// — `0.0.0.0` is not something anyone can type into a browser — and writes
    /// them down in the order worth offering, so the first is the answer.
    /// </summary>
    public static string? Address(string exports)
    {
        try
        {
            var path = Path.Combine(exports, "service.json");
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

    /// <summary>Whether the settings ask for this to run itself.</summary>
    public bool Wanted => Autostarts(_config);

    /// <summary>
    /// Starts it, and says what happened either way.
    ///
    /// Its output goes to a log of its own rather than into the server's, because
    /// it is a second program with its own version, its own errors and its own
    /// pace, and reading either one is easier when they are not interleaved.
    /// </summary>
    public string Start()
    {
        if (Running)
        {
            return $"the map service is already running (pid {_process!.Id})";
        }

        try
        {
            var started = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = GamePaths.DataPath,
            };
            started.ArgumentList.Add("--config");
            started.ArgumentList.Add(_config);
            started.ArgumentList.Add("serve");

            // What a previous run said about where it was listening. Cleared
            // before this one starts so that waiting for it to appear is waiting
            // for this service rather than reading the last one's answer.
            Forget();

            // Truncated on start, and shared so that a tail already watching it
            // keeps working across a restart.
            Directory.CreateDirectory(GamePaths.Logs);
            _log = new StreamWriter(
                new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };

            var process = new Process { StartInfo = started, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, line) => Say(line.Data);
            process.ErrorDataReceived += (_, line) => Say(line.Data);
            process.Exited += (who, _) => Ended(who as Process);

            _stopping = false;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            _api.Logger.Notification(
                "[witchlight] map service started (pid {0}), logging to {1}", process.Id, LogPath);
            SayWhereItIsListening();
            return $"the map service is running (pid {process.Id}), logging to {LogPath}";
        }
        catch (Exception error)
        {
            _api.Logger.Error("[witchlight] could not start the map service: {0}", error);
            return $"could not start the map service: {error.Message}";
        }
    }

    /// <summary>Stops it, and says what happened either way.</summary>
    public string Stop()
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            _process = null;
            CloseLog();
            return "the map service is not running";
        }

        try
        {
            var pid = process.Id;
            _stopping = true;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return $"the map service has been stopped (pid {pid})";
        }
        catch (Exception error)
        {
            return $"could not stop the map service: {error.Message}";
        }
        finally
        {
            _process = null;
            Forget();
            CloseLog();
        }
    }

    /// <summary>One line for `/witchlight status`.</summary>
    public string Describe() => Running
        ? $"service: running (pid {_process!.Id}), log at {LogPath}"
        : Wanted
            ? $"service: not running — see {LogPath}, and `/witchlight service start`"
            : "service: not started, autostart is off in " + _config;

    /// <summary>
    /// Writes the bundled service out where it can be run, once per version.
    ///
    /// The archive is the only copy that matters, so the unpacked one is thrown
    /// away and written again whenever the archive is newer — which is what
    /// upgrading the mod does, and is the difference between running the service
    /// that came with this build and the one that came with the last.
    /// </summary>
    private static string? Unpack(ICoreServerAPI api, Mod mod)
    {
        var into = Path.Combine(GamePaths.Cache, "witchlight");
        var executable = Path.Combine(into, Name);

        try
        {
            // A mod loaded from a folder is how it is developed; from an archive
            // is how it is installed. Either way there is one file it came out of.
            var source = mod.SourcePath;
            var folder = Directory.Exists(source);
            var origin = folder ? Path.Combine(source, BundledAt) : source;

            if (!File.Exists(origin))
            {
                return Missing(api, source);
            }

            var packed = File.GetLastWriteTimeUtc(origin);
            if (File.Exists(executable) && File.GetLastWriteTimeUtc(executable) == packed)
            {
                return executable;
            }

            Directory.CreateDirectory(into);

            if (folder)
            {
                File.Copy(origin, executable, overwrite: true);
            }
            else
            {
                using var archive = ZipFile.OpenRead(origin);
                var entry = archive.GetEntry(BundledAt);
                if (entry is null)
                {
                    return Missing(api, source);
                }
                entry.ExtractToFile(executable, overwrite: true);
            }

            // Stamped with the time of the file it came out of, so that "is this
            // the service that came with this build" is a question the two can
            // answer. Extraction gives the copy the time the *entry* carries,
            // which is when the service was compiled — a different clock from
            // when the archive was made, and comparing the two says nothing. The
            // one comparison that means anything is against the archive itself.
            File.SetLastWriteTimeUtc(executable, packed);

            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            api.Logger.Notification("[witchlight] unpacked the map service to {0}", executable);
            return executable;
        }
        catch (Exception error)
        {
            api.Logger.Error("[witchlight] could not unpack the map service: {0}", error);
            return null;
        }
    }

    private static string? Missing(ICoreServerAPI api, string source)
    {
        api.Logger.Warning(
            "[witchlight] {0} carries no map service at {1} — it was packaged without one. "
            + "The map will export as usual, and `witchlight serve` run yourself will serve it",
            source, BundledAt);
        return null;
    }

    /// <summary>
    /// Makes sure there is a settings file, by asking the service to write one.
    ///
    /// Written by the service and not here: the format is the service's, and a
    /// second program writing a format it does not own is how the two come to
    /// disagree about it. The data path is passed in because that is the one thing
    /// the service cannot work out for itself.
    /// </summary>
    private static string? EnsureConfig(ICoreServerAPI api, string executable)
    {
        var config = ConfigPath;
        if (File.Exists(config))
        {
            return config;
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
            write.ArgumentList.Add(config);
            write.ArgumentList.Add("--vs-data");
            write.ArgumentList.Add(GamePaths.DataPath);
            write.ArgumentList.Add("--save-config");
            write.ArgumentList.Add("--print-config");

            using var writing = Process.Start(write);
            if (writing is null)
            {
                return null;
            }

            writing.StandardOutput.ReadToEnd();
            var complaint = writing.StandardError.ReadToEnd();
            writing.WaitForExit(15000);

            if (!File.Exists(config))
            {
                api.Logger.Warning(
                    "[witchlight] the map service did not write {0}: {1}", config, complaint.Trim());
                return null;
            }

            api.Logger.Notification("[witchlight] wrote default map settings to {0}", config);
            return config;
        }
        catch (Exception error)
        {
            api.Logger.Error("[witchlight] could not write {0}: {1}", config, error);
            return null;
        }
    }

    /// <summary>
    /// Puts the address in the server's own log, once the service has one.
    ///
    /// It takes a moment to bind and say so, and this is wanted in the log a
    /// server operator is already reading rather than only in the service's own.
    /// Waited for rather than worked out here: which addresses a bind of
    /// `0.0.0.0` actually answers on is the service's question and it has already
    /// answered it.
    /// </summary>
    private void SayWhereItIsListening()
    {
        var exports = _exports;
        Task.Run(async () =>
        {
            var giveUpAt = DateTime.UtcNow + WaitForAddress;
            while (DateTime.UtcNow < giveUpAt)
            {
                if (!Running)
                {
                    // It stopped. Ended() has already said so, and twice is noise.
                    return;
                }

                if (Address(exports) is { } at)
                {
                    _api.Logger.Notification("[witchlight] the map is being served at {0}", at);
                    return;
                }

                await Task.Delay(250);
            }

            _api.Logger.Warning(
                "[witchlight] the map service has not said where it is listening. See {0}", LogPath);
        });
    }

    /// <summary>
    /// How long to wait for the service to say where it is.
    ///
    /// Long enough for a cold start on a slow disk, short enough that a service
    /// which will never answer is reported while somebody is still watching.
    /// </summary>
    private static readonly TimeSpan WaitForAddress = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Takes down everything the service published about itself, so that nothing
    /// hands a player the address of a map that is not there — and nothing posts
    /// live positions at whatever took its port next.
    ///
    /// The second matters more than the first. An address that has gone answers
    /// nothing and is merely useless; a port that has been taken over answers
    /// something, and there is no telling what.
    /// </summary>
    private void Forget()
    {
        foreach (var path in new[]
                 {
                     Path.Combine(_exports, "service.json"),
                     MapService.ConnectionPath(_exports),
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A service that cannot be reached says so by not answering.
            }
        }
    }

    private void Say(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_writing)
        {
            _log?.WriteLine(line);
        }
    }

    /// <summary>
    /// Says that it stopped, and how loudly depends on whether anybody meant it.
    ///
    /// Not restarted from here. A service that will not start — a port already
    /// taken, settings it cannot read — fails the same way every time, and a
    /// restart loop turns one legible error into a log nobody can read.
    /// </summary>
    private void Ended(Process? which)
    {
        // Read from the process the event carries. By the time a stop we asked
        // for gets here the field may already be cleared, which is how this came
        // to report an exit code of nothing at all.
        var code = Exited(which);

        if (_stopping)
        {
            Say($"witchlight: stopped (exit {code})");
            return;
        }

        Say($"witchlight: stopped on its own (exit {code})");
        _api.Logger.Warning(
            "[witchlight] the map service stopped on its own (exit {0}). See {1}, and "
            + "`/witchlight service start` to run it again",
            code, LogPath);
    }

    /// <summary>An exit code, or what to print when there is not one to be had.</summary>
    private static string Exited(Process? which)
    {
        try
        {
            return which is null ? "unknown" : which.ExitCode.ToString();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private void CloseLog()
    {
        lock (_writing)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    public void Dispose()
    {
        var process = _process;
        _stopping = true;
        _process = null;

        try
        {
            if (process is { HasExited: false })
            {
                // Safe to end outright: everything it writes is put beside itself
                // and renamed into place, so nothing is caught half written.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            process?.Dispose();
        }
        catch (Exception)
        {
            // Shutting down. A service that has already gone is the outcome wanted.
        }

        Forget();
        CloseLog();
    }
}
