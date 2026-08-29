using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
    private readonly ICoreServerAPI _api;
    private readonly string _executable;
    private readonly string _config;

    private Process? _process;
    private StreamWriter? _log;
    private readonly object _writing = new();

    /// <summary>Whether the stop about to happen is one we asked for.</summary>
    private bool _stopping;

    private ServiceProcess(ICoreServerAPI api, string executable, string config)
    {
        _api = api;
        _executable = executable;
        _config = config;
    }

    /// <summary>Everything the service says, on its own so it can be tailed.</summary>
    public static string LogPath => Path.Combine(GamePaths.Logs, "witchlight-service.log");

    /// <summary>Whether a service of ours is running right now.</summary>
    public bool Running => _process is { HasExited: false };

    /// <summary>
    /// Unpacks the bundled service and makes sure there is a configuration file,
    /// without starting anything. Null when this machine has no service to run,
    /// which is said out loud rather than left to be noticed.
    /// </summary>
    public static ServiceProcess? Prepare(ICoreServerAPI api, Mod mod)
    {
        if (BundledService.Unpack(api, mod) is not { } executable)
        {
            return null;
        }

        var config = Settings.EnsureWritten(api, executable);
        return config is null ? null : new ServiceProcess(api, executable, config);
    }

    /// <summary>Whether the settings ask for this to run itself.</summary>
    public bool Wanted => Settings.Autostarts;

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
            // Named outright rather than left to be worked out. Where each world
            // keeps its own map there are several to choose between, and only
            // this half knows which world is running.
            started.ArgumentList.Add("--exports");
            started.ArgumentList.Add(Settings.Exports);
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

                if (Settings.Address() is { } at)
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
                     Path.Combine(Settings.Exports, "service.json"),
                     MapService.ConnectionPath(Settings.Exports),
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
