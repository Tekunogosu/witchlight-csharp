using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// The client half of Witchlight: what runs, and when.
///
/// A client is asked for three things the server cannot produce for itself — a
/// picture of its own player, a block colour palette, and the marker art — and it
/// shows one thing the server cannot show it: everybody else's markers on the
/// in-game map. That is the whole of what this side does.
///
/// Nothing is ever sent unprompted except a portrait, and that only when this
/// player's own character has changed and settled.
/// </summary>
public partial class WitchlightClient : ModSystem
{
    private ICoreClientAPI? _capi;

    /// <summary>Everyone else's markers, on this player's map.</summary>
    private SharedMarkerLayer? _markers;

    /// <summary>Draws this player when asked. Only a client can: nobody else has them.</summary>
    private PortraitCapture? _portrait;

    /// <summary>Says when this player has changed and stopped changing.</summary>
    private PortraitWatch? _watch;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        _markers = new SharedMarkerLayer(api);
        _portrait = new PortraitCapture(api);
        _watch = new PortraitWatch(api, () => SendPortrait(byHand: false));

        api.Network
            .RegisterChannel(Channel.Name)
            .Carrying()
            .SetMessageHandler<SharedMarkers>(message => _markers?.Take(message))
            .SetMessageHandler<PaletteRequest>(OnPaletteRequest)
            .SetMessageHandler<IconRequest>(OnIconRequest)
            .SetMessageHandler<PortraitRequest>(_ => SendPortrait(byHand: false));

        api.ChatCommands
            .Create(Commands.Name)
            .WithShortName(api)
            .WithDescription("Witchlight map tools")
            .BeginSubCommand("portrait")
                .WithDescription("Send the map a picture of your character")
                .HandleWith(OnPortrait)
            .EndSubCommand()
            .BeginSubCommand("palette")
                .WithDescription("Build the block colour palette for the server you are on")
                .HandleWith(OnPalette)
            .EndSubCommand()
            .BeginSubCommand("icons")
                .WithDescription("Send the marker pictures to the server you are on")
                .HandleWith(OnIcons)
            .EndSubCommand();
    }
}
