using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// The map service binary that rides along in the mod archive.
///
/// The service is a separate program and stays one: it knows pixels and the mod
/// knows the game, and a map worth keeping outlives any single game server. What
/// it does not need to be is a second thing to install. So the binary travels
/// inside the mod and is written out where it can be run.
///
/// Getting it out of the archive is all this does. Running it is
/// <see cref="ServiceProcess"/>.
/// </summary>
public static class BundledService
{
    /// <summary>Where the binary sits inside the mod archive.</summary>
    private const string BundledAt = "service/linux-x64/witchlight";

    /// <summary>What it is called once unpacked, and in the log.</summary>
    private const string Name = "witchlight";

    /// <summary>
    /// Writes the bundled service out where it can be run, once per version.
    ///
    /// Null where this machine has no service to run — a platform nothing was
    /// bundled for, an archive packaged without one — which is said out loud
    /// rather than left to be noticed.
    ///
    /// The archive is the only copy that matters, so the unpacked one is thrown
    /// away and written again whenever the archive is newer — which is what
    /// upgrading the mod does, and is the difference between running the service
    /// that came with this build and the one that came with the last.
    /// </summary>
    public static string? Unpack(ICoreServerAPI api, Mod mod)
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
                if (archive.GetEntry(BundledAt) is not { } entry)
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
            MakeRunnable(executable);

            api.Logger.Notification("[witchlight] unpacked the map service to {0}", executable);
            return executable;
        }
        catch (Exception error)
        {
            api.Logger.Error("[witchlight] could not unpack the map service: {0}", error);
            return null;
        }
    }

    /// <summary>
    /// Gives the unpacked binary the bits it needs to be executed.
    ///
    /// Guarded rather than assumed, even though only a Linux build is ever
    /// unpacked: the check above is what makes that true, and a platform check
    /// three call frames away is not something the next reader of this line — or
    /// the compiler — can see.
    /// </summary>
    private static void MakeRunnable(string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string? Missing(ICoreServerAPI api, string source)
    {
        api.Logger.Warning(
            "[witchlight] {0} carries no map service at {1} — it was packaged without one. "
            + "The map will export as usual, and `witchlight serve` run yourself will serve it",
            source, BundledAt);
        return null;
    }
}
