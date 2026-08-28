using System;
using System.IO;

namespace Witchlight;

/// <summary>
/// Writing a file only when it would change.
///
/// Every write costs drive life, and the exports here are mostly the same bytes
/// run after run: a palette rebuilt from unchanged assets, a name table for a
/// block set nobody touched. Asking "is this what is already there" is one
/// question with one right answer, so it has one owner and the write lives
/// behind it rather than beside a condition repeated at each caller.
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
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == body)
            {
                return false;
            }
        }
        catch (Exception)
        {
            // Unreadable is not identical.
        }

        File.WriteAllText(path, body);
        return true;
    }
}
