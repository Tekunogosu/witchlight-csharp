using System;
using System.IO;
using System.Text;

namespace Witchlight;

/// <summary>
/// The pictures players sent of themselves: what one may be, how often one may
/// arrive, and where it is kept.
///
/// Kept beside the marker icons, and served the same way: a file per player, named
/// so that a name is only ever a name. A player uid is not — the game's are base64
/// and carry `+` and `/`, which is a path and not a filename — so what is stored is
/// the uid in hex. It is not meant to be read, only to be unambiguous, and both the
/// file and the name handed to the viewer come from here.
///
/// The two periods below are a pair and are stated together on purpose: one is how
/// fast a client sends and the other is how fast the server will take, and a pair
/// kept in two files drifts apart the first time either is tuned.
/// </summary>
public static class Portraits
{
    /// <summary>
    /// The most a portrait may weigh.
    ///
    /// A 128 pixel PNG is a few kilobytes. This is not a tuning figure but a limit
    /// on what an untrusted client can make the server write.
    /// </summary>
    public const int Limit = 512 * 1024;

    /// <summary>
    /// How long a character must go unchanged before its client sends a picture.
    ///
    /// Long enough that a run of changes is one wait rather than a queue of them,
    /// short enough that somebody who changes their hat and walks off is drawn
    /// wearing it before they forget they did.
    /// </summary>
    public const int QuietMs = 30000;

    /// <summary>
    /// The least time between two pictures a client sends unasked.
    ///
    /// The quiet period is the fastest an honest client sends on its own — a change
    /// restarts it, so two pictures cost two settles — so the floor is derived from
    /// it rather than chosen beside it, and sits just under so that a slow packet
    /// never turns an honest picture into a refusal.
    ///
    /// It bounds only what a client sends of its own accord. What the server asked
    /// for is not counted against it: the server knows how often it asks.
    /// </summary>
    public const int FloorMs = QuietMs - 5000;

    public static string DirectoryIn(string exports) => Path.Combine(exports, "portraits");

    /// <summary>What a player's picture is called, without the extension.</summary>
    public static string NameFor(string uid)
    {
        var hex = new StringBuilder(uid.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(uid))
        {
            hex.Append(b.ToString("x2"));
        }
        return hex.ToString();
    }

    /// <summary>
    /// The stored picture for a player: what it is called and when it was drawn,
    /// or nothing where they have none.
    ///
    /// Both, from the one look at the file. The name says which picture and never
    /// changes — it is derived from the player, and a player who is redrawn keeps
    /// it — so on its own it cannot tell a new picture from the one it replaced.
    /// The time is what makes two pictures under one name distinguishable, which
    /// is what anything holding a copy of the last one needs in order to find out
    /// it is holding the wrong one.
    /// </summary>
    public static (string Name, long At)? StoredFor(string exports, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }

        var name = NameFor(uid);
        var file = new FileInfo(Path.Combine(DirectoryIn(exports), name + ".png"));
        return file.Exists
            ? (name, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds())
            : null;
    }

    /// <summary>
    /// Writes one, and says what happened either way.
    ///
    /// The bytes are checked to be a PNG before any of them are written. They came
    /// off the network, and a server that writes whatever it is handed under a name
    /// it will later serve is a server that hosts whatever it is handed.
    /// </summary>
    public static bool Save(string exports, string uid, byte[]? png, out string said)
    {
        if (string.IsNullOrEmpty(uid))
        {
            said = "there is nobody to file it under";
            return false;
        }

        if (png is null || png.Length == 0)
        {
            said = "the picture was empty";
            return false;
        }

        if (png.Length > Limit)
        {
            said = $"the picture is {png.Length / 1024} KiB, over the {Limit / 1024} KiB limit";
            return false;
        }

        if (!IsPng(png))
        {
            said = "that is not a PNG";
            return false;
        }

        try
        {
            var into = DirectoryIn(exports);
            Directory.CreateDirectory(into);
            var path = Path.Combine(into, NameFor(uid) + ".png");

            // A character can be taken apart and put back exactly as it was, and
            // what comes back is the picture already on disk. The stored one is
            // then correct without being rewritten, which is the difference
            // between a drive that wears out and one that does not.
            if (Same(path, png))
            {
                said = $"{png.Length} bytes, the same as the one stored";
                return true;
            }

            // Beside itself and then into place, so a reader never sees half of it.
            var temporary = path + ".part";
            File.WriteAllBytes(temporary, png);
            File.Move(temporary, path, overwrite: true);

            said = $"{png.Length} bytes";
            return true;
        }
        catch (Exception error)
        {
            said = error.Message;
            return false;
        }
    }

    /// <summary>How many are stored, for saying so in the status.</summary>
    public static int Count(string exports)
    {
        try
        {
            return Directory.GetFiles(DirectoryIn(exports), "*.png").Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Whether what is stored is already exactly this.
    ///
    /// The one place the question "does this need writing" is answered, so the
    /// write sits behind it rather than behind a test repeated at each caller.
    /// Length first: two pictures of the same seraph in different clothes almost
    /// never weigh the same, so the read is usually skipped.
    /// </summary>
    private static bool Same(string path, byte[] png)
    {
        try
        {
            var stored = new FileInfo(path);
            return stored.Exists
                && stored.Length == png.Length
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(png);
        }
        catch (Exception)
        {
            // Unreadable is not the same as identical. Writing over it is the
            // right answer to a file this cannot make sense of.
            return false;
        }
    }

    /// <summary>The eight bytes every PNG starts with.</summary>
    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
        && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
}
