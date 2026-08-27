using System;
using System.IO;
using System.Text;

namespace Mapstique;

/// <summary>
/// The pictures players sent of themselves.
///
/// Kept beside the marker icons, and served the same way: a file per player, named
/// so that a name is only ever a name. A player uid is not — the game's are base64
/// and carry `+` and `/`, which is a path and not a filename — so what is stored is
/// the uid in hex. It is not meant to be read, only to be unambiguous, and both the
/// file and the name handed to the viewer come from here.
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

    /// <summary>The name to hand the viewer, or null where this player has no picture.</summary>
    public static string? StoredNameFor(string exports, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }

        var name = NameFor(uid);
        return File.Exists(Path.Combine(DirectoryIn(exports), name + ".png")) ? name : null;
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

            // Beside itself and then into place, so a reader never sees half of it.
            var path = Path.Combine(into, NameFor(uid) + ".png");
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

    /// <summary>The eight bytes every PNG starts with.</summary>
    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
        && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
}
