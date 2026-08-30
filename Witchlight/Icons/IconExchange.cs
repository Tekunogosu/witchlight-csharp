using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Getting the pictures a marker is drawn with.
///
/// Unlike the palette this is not a fallback for a poor result — it is the only
/// way the pictures ever arrive. A dedicated server's install has no SVG in it at
/// all: its `textures` directory is there and empty of them.
///
/// **Anybody is asked, and anybody may add a picture the server does not have;
/// only an admin may replace one it does.** The same rule the palette follows and
/// for the same reason: a server whose operator never joins in game would
/// otherwise draw every marker as a plain diamond forever, and a picture laid
/// where there is none can only improve on nothing.
/// </summary>
public sealed class IconExchange
{
    private readonly ICoreServerAPI _api;
    private readonly string _exports;

    public IconExchange(ICoreServerAPI api, string exports)
    {
        _api = api;
        _exports = exports;
    }

    /// <summary>How many are on disk for the map service to draw with.</summary>
    public int Count => Stored().Count;

    /// <summary>
    /// Asks whoever has just joined for whatever is missing.
    ///
    /// Only what is missing: a mod adding one marker costs one icon, not the whole
    /// set again, and a server that has the lot asks for nothing and is sent
    /// nothing. That is what makes asking everybody cheap — the cost is one small
    /// packet per join once the set is complete.
    /// </summary>
    public void AskForMissing(IServerPlayer player) => Ask(player, Stored());

    /// <summary>
    /// Asks for everything, not only what is missing.
    ///
    /// The way back if an icon on disk is wrong rather than absent.
    /// </summary>
    public void AskForAll(IServerPlayer player) => Ask(player, new List<string>());

    private void Ask(IServerPlayer player, List<string> have)
    {
        _api.Network.GetChannel(Channel.Name)
            .SendPacket(new IconRequest { Have = have }, player);
    }

    /// <summary>
    /// Takes marker pictures from a client and keeps what they add.
    ///
    /// Merged rather than replaced, like the palette: a client only has the art
    /// for the mods it has installed, so two players with different mod sets can
    /// between them cover more than either alone.
    ///
    /// **An admin may overwrite a picture; anybody else may only add one.** A
    /// player's contribution is filtered to names the server does not have, and
    /// bounded in number — the sender no longer has to be an admin, and a client
    /// inventing names is otherwise a client filling a disk.
    /// </summary>
    public void Accept(IServerPlayer player, IconTable table)
    {
        var sent = IconTable.Assemble(new[] { table });
        var taking = player.HasPrivilege(Privilege.controlserver) ? sent : OnlyNew(sent);

        var written = Icons.Accept(taking, _exports);
        if (written > 0)
        {
            _api.Logger.Notification(
                "[witchlight] {0} marker pictures from {1}{2} ({3} in total now)",
                written,
                player.PlayerName,
                player.HasPrivilege(Privilege.controlserver) ? "" : " (not an admin, so only new ones)",
                Count);
        }
    }

    /// <summary>
    /// The pictures a server without them can take from anybody: ones it has no
    /// file for, and no more of them than a map has any use for.
    ///
    /// The count is what makes this safe rather than the names. A name is already
    /// reduced to characters that are safe in a path, but nothing stops a client
    /// inventing an unlimited number of them, and every one is a file.
    /// </summary>
    private List<(string Name, byte[] Svg)> OnlyNew(List<(string Name, byte[] Svg)> sent)
    {
        var have = new HashSet<string>(Stored(), StringComparer.Ordinal);
        var taking = new List<(string, byte[])>();

        foreach (var (name, svg) in sent)
        {
            if (have.Contains(name) || have.Count + taking.Count >= MostIcons)
            {
                continue;
            }
            taking.Add((name, svg));
        }

        return taking;
    }

    /// <summary>
    /// The most marker pictures this will hold from players who are not admins.
    ///
    /// A stock game draws markers with about forty and a heavy mod set with a few
    /// hundred, so this is well past any real set and well short of a disk. An
    /// admin is not held to it: they can write to the map directory anyway, and
    /// the number that would bound them is a guess about somebody else's mods.
    /// </summary>
    private const int MostIcons = 512;

    /// <summary>What the server already has, so a client sends only what is new.</summary>
    private List<string> Stored()
    {
        var dir = Icons.DirectoryIn(_exports);
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.GetFiles(dir, "*.svg")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToList();
    }
}
