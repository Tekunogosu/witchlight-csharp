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
/// all: its `textures` directory is there and empty of them. Only an admin is
/// asked, for the same reason as the palette: this decides what everyone sees and
/// it comes from a machine the server does not control.
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
    /// Asks an admin for whatever is missing.
    ///
    /// Only what is missing: a mod adding one marker costs one icon, not the whole
    /// set again.
    /// </summary>
    public void AskForMissing(IServerPlayer player)
    {
        if (player.HasPrivilege(Privilege.controlserver))
        {
            Ask(player, Stored());
        }
    }

    /// <summary>
    /// Asks for everything, not only what is missing.
    ///
    /// The way back if an icon on disk is wrong rather than absent.
    /// </summary>
    public void AskForAll(IServerPlayer player) => Ask(player, new List<string>());

    private void Ask(IServerPlayer player, List<string> have)
    {
        _api.Network.GetChannel(SharedServer.Channel)
            .SendPacket(new IconRequest { Have = have }, player);
    }

    /// <summary>
    /// Takes marker pictures from an admin's client and keeps what they add.
    ///
    /// Merged rather than replaced, like the palette: a client only has the art
    /// for the mods it has installed, so two admins with different mod sets can
    /// between them cover more than either alone.
    /// </summary>
    public void Accept(IServerPlayer player, IconTable table)
    {
        if (!player.HasPrivilege(Privilege.controlserver))
        {
            _api.Logger.Warning(
                "[witchlight] ignored marker pictures from {0}, who is not an admin", player.PlayerName);
            return;
        }

        var written = Icons.Accept(IconTable.Assemble(new[] { table }), _exports);
        if (written > 0)
        {
            _api.Logger.Notification(
                "[witchlight] {0} marker pictures from {1} ({2} in total now)",
                written, player.PlayerName, Count);
        }
    }

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
