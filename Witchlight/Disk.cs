using System;
using System.IO;

namespace Witchlight;

/// <summary>
/// Writing a file, and not writing one.
///
/// Two rules, both of which were spelled out at every call site until they were
/// gathered here.
///
/// A write is earned. Every write costs drive life, and the exports here are
/// mostly the same bytes run after run: a palette rebuilt from unchanged assets,
/// a name table for a block set nobody touched, an icon a second admin sent. So
/// "is this already what is there" is one question with one right answer, and the
/// write lives behind it rather than beside a condition repeated at each caller.
///
/// A write is whole. Everything here is read by the map service while the server
/// runs, so a file is written beside itself and renamed into place — a reader
/// sees the old bytes or the new ones and never half of either.
/// </summary>
public static class Disk
{
    /// <summary>
    /// Writes text where it differs from what is on disk. True when it wrote.
    ///
    /// A file that cannot be read is not the same as one that matches, so it is
    /// written: the cost of being wrong that way is one write, and the cost of
    /// being wrong the other way is an export that never lands.
    /// </summary>
    public static bool Write(string path, string body)
    {
        if (Same(path, () => File.ReadAllText(path) == body))
        {
            return false;
        }

        Replace(path, temporary => File.WriteAllText(temporary, body));
        return true;
    }

    /// <summary>Writes bytes where they differ from what is on disk. True when it wrote.</summary>
    public static bool WriteBytes(string path, byte[] body)
    {
        // Length first: two pictures of the same thing almost never weigh the
        // same, so the read is usually skipped.
        if (Same(path, () => new FileInfo(path).Length == body.Length
                             && File.ReadAllBytes(path).AsSpan().SequenceEqual(body)))
        {
            return false;
        }

        Replace(path, temporary => File.WriteAllBytes(temporary, body));
        return true;
    }

    /// <summary>
    /// Writes through a temporary beside the file and renames it into place.
    ///
    /// Public for a caller that produces its bytes as it writes — a compressed
    /// stream, say — and so has nothing to hand over to be compared first.
    /// </summary>
    public static void Replace(string path, Action<string> write)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".part";
        try
        {
            write(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // A write that failed part way leaves the temporary in the way of the
            // next attempt, and it is not the file anybody asked for.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // Nothing further to try, and the original is still intact.
            }
            throw;
        }
    }

    /// <summary>
    /// Whether what is stored is already exactly this.
    ///
    /// Unreadable is not the same as identical: writing over a file this cannot
    /// make sense of is the right answer to it.
    /// </summary>
    private static bool Same(string path, Func<bool> matches)
    {
        try
        {
            return File.Exists(path) && matches();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
