using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace Witchlight;

/// <summary>
/// Watches what this player looks like and asks for a new picture once it settles.
///
/// A portrait is worth redrawing when a seraph changes, and a seraph changes in
/// bursts: somebody trying on a hat moves it between two slots half a dozen times
/// in as many seconds, and every one of those is a picture rendered, a packet sent
/// and a file written for a state nobody stayed in. So a change is not a signal to
/// send — it is a signal to start waiting again, and the picture is drawn once the
/// waiting runs out. A whole afternoon at the dressing table costs one portrait.
///
/// What counts as a change is the two things that decide the face: the character
/// inventory, which holds every piece of clothing and armour worn, and the skin
/// configuration, which is who they chose to be. Neither the hotbar nor the
/// backpack is watched — a portrait is cut to the head and shoulders, so what is
/// carried never appears in it.
/// </summary>
public sealed class PortraitWatch
{
    /// <summary>
    /// How often the wait is measured and the subscriptions renewed.
    ///
    /// Both on the same beat, because both questions are "is this still the
    /// character I was watching, and has it sat still long enough". Renewing on a
    /// tick rather than on a join event means a relog, a respawn or an inventory
    /// arriving late all mend themselves without any of them being predicted.
    /// </summary>
    private const int CheckMs = 1000;

    private readonly ICoreClientAPI _capi;
    private readonly Action _send;

    private IInventory? _wearing;
    private SyncedTreeAttribute? _skin;

    /// <summary>When this character last changed, or null when it is settled.</summary>
    private DateTime? _changedAt;

    public PortraitWatch(ICoreClientAPI capi, Action send)
    {
        _capi = capi;
        _send = send;
        capi.Event.RegisterGameTickListener(_ => Tick(), CheckMs);
    }

    /// <summary>
    /// Notes a change without sending anything.
    ///
    /// Called from wherever a picture has just been sent by other means, so that a
    /// change arriving moments later is still waited out from now rather than from
    /// whenever the burst began.
    /// </summary>
    public void Settled() => _changedAt = null;

    private void Tick()
    {
        Follow();

        if (_changedAt is not { } since
            || DateTime.UtcNow - since < TimeSpan.FromMilliseconds(Portraits.QuietMs))
        {
            return;
        }

        _changedAt = null;
        _send();
    }

    /// <summary>
    /// Subscribes to this player's character, and unsubscribes from the last one.
    ///
    /// Idempotent, and cheap when nothing moved: the common tick compares two
    /// references and stops. A character that is not this one is not a change to
    /// this one, so swapping resets the wait rather than starting it.
    /// </summary>
    private void Follow()
    {
        var wearing = _capi.World?.Player?.InventoryManager?
            .GetOwnInventory(GlobalConstants.characterInvClassName);
        if (!ReferenceEquals(wearing, _wearing))
        {
            if (_wearing is not null)
            {
                _wearing.SlotModified -= OnSlotModified;
            }

            _wearing = wearing;
            if (_wearing is not null)
            {
                _wearing.SlotModified += OnSlotModified;
            }

            _changedAt = null;
        }

        var skin = _capi.World?.Player?.Entity?.WatchedAttributes;
        if (!ReferenceEquals(skin, _skin))
        {
            _skin?.UnregisterListener(OnSkinChanged);
            _skin = skin;
            _skin?.RegisterModifiedListener("skinConfig", OnSkinChanged);
            _changedAt = null;
        }
    }

    private void OnSlotModified(int slot) => _changedAt = DateTime.UtcNow;

    private void OnSkinChanged() => _changedAt = DateTime.UtcNow;
}
