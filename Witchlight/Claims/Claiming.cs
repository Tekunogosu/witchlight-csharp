using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// One land claim somebody drew on the web map.
///
/// Two corners and a description, which is all a map looked at from above can
/// honestly be asked for. The service has squared the rectangle up and taken the
/// owner from the session; everything else about whether this claim may exist is
/// decided here.
/// </summary>
public class WantedClaim
{
    /// <summary>Who asked. The service took this from their session.</summary>
    public string Uid { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>The corners, west and north first.</summary>
    public int X1 { get; set; }
    public int Z1 { get; set; }
    public int X2 { get; set; }
    public int Z2 { get; set; }

    /// <summary>
    /// How far down it starts and how far up it reaches, lower first.
    ///
    /// Asked for rather than assumed, though the map is drawn from above. Depth
    /// is most of what a claim's volume is made of and volume is what a role's
    /// allowance is measured in, so a map that made every claim the full height
    /// of the world would hand a survival player a square thirty-two blocks
    /// across and no way to say they wanted a wider, shallower one.
    /// </summary>
    public int Y1 { get; set; }
    public int Y2 { get; set; }
}

/// <summary>Who a claim is being asked to let in.</summary>
public class WantedGuests
{
    /// <summary>The people named, by the name the game knows them as. Turned
    ///  into uids here, because this is the half that can.</summary>
    public List<string> Names { get; set; } = new();

    /// <summary>The game's own two everybody-permissions.</summary>
    public bool EveryoneUses { get; set; }
    public bool EveryoneWalks { get; set; }
}

/// <summary>
/// A change to a claim that already exists: what it is called, and who it lets
/// in.
///
/// Not the ground. Moving a boundary has to be judged against everybody else's
/// claims and against an allowance, and the map cannot show somebody what they
/// would be giving up — so redrawing is making a new one, which the form already
/// does.
/// </summary>
public class EditedClaim
{
    /// <summary>Which claim, by the name <see cref="ClaimFeed"/> gave it.</summary>
    public string Key { get; set; } = "";

    /// <summary>Who asked. The service took this from their session.</summary>
    public string Uid { get; set; } = "";

    public string Description { get; set; } = "";

    public WantedGuests Allowed { get; set; } = new();
}

/// <summary>One claim somebody asked to give up.</summary>
public class UnwantedClaim
{
    public string Key { get; set; } = "";
    public string Uid { get; set; } = "";
}

/// <summary>Everything about claims the service was holding.</summary>
public class AskedClaims
{
    public List<WantedClaim> Make { get; set; } = new();
    public List<EditedClaim> Change { get; set; } = new();
    public List<UnwantedClaim> Remove { get; set; } = new();
}

/// <summary>How many claims of each kind landed.</summary>
public readonly record struct Claimed(int Made, int Changed, int Removed)
{
    public bool Anything => Made > 0 || Changed > 0 || Removed > 0;
}

/// <summary>
/// Making the land claims somebody asked for on the web map.
///
/// Every rule the game applies to `/land claim` is applied here, and none of them
/// is applied anywhere else. That is the whole point of this file: a web map that
/// could take land the game itself would refuse is a way round the server's own
/// rules, and a second copy of those rules kept in the service would be a second
/// opinion on a question with one right answer.
///
/// So this reads the game's own numbers — the world config, the privilege, the
/// role's allowance and minimum size, how many claims the person already has, and
/// every claim already on the map — and refuses in the same cases and for the same
/// reasons. Nothing about the claim is decided here either: the rectangle and the
/// depth both come from the form, because a claim's volume is mostly its depth and
/// the person taking the land is the one who should be choosing it.
///
/// The map's own permission is asked as well, and never instead. `[claims] create`
/// narrows who may take land through the map; it can only ever take away.
/// </summary>
public static class Claiming
{
    /// <summary>
    /// Makes every claim in the service's reply that may be made, and says how
    /// many landed.
    ///
    /// A claim that is refused is said out loud once, in the log, naming who and
    /// why. Nothing is said back to the person: they are in a browser, and the
    /// page they are looking at is already watching for the claim to appear —
    /// see the viewer, which tells them where the answer is when it does not.
    /// </summary>
    public static Claimed Apply(ICoreServerAPI api, AskedClaims? asked)
    {
        if (asked is null)
        {
            return default;
        }

        var made = 0;
        foreach (var wanted in asked.Make)
        {
            if (wanted is null || string.IsNullOrEmpty(wanted.Uid))
            {
                api.Logger.Warning("[witchlight] the map asked for a land claim with no owner");
                continue;
            }

            if (Make(api, wanted) is { } refusal)
            {
                Refused(api, wanted.Uid, "claim that land", refusal);
                continue;
            }
            made++;
        }

        var changed = 0;
        foreach (var edit in asked.Change)
        {
            if (edit is null || string.IsNullOrEmpty(edit.Uid) || string.IsNullOrEmpty(edit.Key))
            {
                continue;
            }

            if (Change(api, edit) is { } refusal)
            {
                Refused(api, edit.Uid, "change that claim", refusal);
                continue;
            }
            changed++;
        }

        var removed = 0;
        foreach (var gone in asked.Remove)
        {
            if (gone is null || string.IsNullOrEmpty(gone.Uid) || string.IsNullOrEmpty(gone.Key))
            {
                continue;
            }

            if (Give(api, gone) is { } refusal)
            {
                Refused(api, gone.Uid, "give up that claim", refusal);
                continue;
            }
            removed++;
        }

        return new Claimed(made, changed, removed);
    }

    /// <summary>
    /// Says why somebody was refused, once, in the server's log.
    ///
    /// Nothing is said back to them: they are in a browser, and the page they are
    /// looking at is already watching for the claim to change. The server is
    /// where an operator can see who was turned away and why.
    /// </summary>
    private static void Refused(ICoreServerAPI api, string uid, string doing, string why) =>
        api.Logger.Notification("[witchlight] {0} may not {1}: {2}", uid, doing, why);

    /// <summary>
    /// Changes what a claim is called and who it lets in, or says why not.
    ///
    /// The claim is put back rather than edited in place: the game indexes claims
    /// by region and tells every client when one changes, and both of those
    /// happen on `Remove` and `Add` — reaching into a live claim would change
    /// what the server holds and tell nobody. That is what the game's own
    /// `/land claim save` does with an edited copy.
    /// </summary>
    private static string? Change(ICoreServerAPI api, EditedClaim edit)
    {
        if (Held(api, edit.Key) is not { } claim)
        {
            return "there is no claim by that name any more — the map may be out of date";
        }
        if (!Owns(api, claim, edit.Uid))
        {
            return "it is not theirs";
        }

        var changed = claim.Clone();
        changed.Description = edit.Description ?? "";
        changed.AllowUseEveryone = edit.Allowed?.EveryoneUses ?? false;
        changed.AllowTraverseEveryone = edit.Allowed?.EveryoneWalks ?? false;
        Invite(api, changed, edit.Allowed?.Names ?? new List<string>());

        api.World.Claims.Remove(claim);
        api.World.Claims.Add(changed);
        return null;
    }

    /// <summary>Gives a claim up, or says why it cannot be.</summary>
    private static string? Give(ICoreServerAPI api, UnwantedClaim gone)
    {
        if (Held(api, gone.Key) is not { } claim)
        {
            return "there is no claim by that name any more — the map may be out of date";
        }
        if (!Owns(api, claim, gone.Uid))
        {
            return "it is not theirs";
        }

        return api.World.Claims.Remove(claim) ? null : "the game would not release it";
    }

    /// <summary>
    /// The claim the map is naming, or null where none answers to that name.
    ///
    /// A key is worked out from what a claim is, so one that matches nothing is
    /// a claim that has moved since the page was last told about it. Refusing is
    /// the only safe answer: the alternative is editing whichever claim now sits
    /// where the page thinks it is looking.
    /// </summary>
    private static LandClaim? Held(ICoreServerAPI api, string key) =>
        ClaimFeed.ByKey(api, key);

    /// <summary>
    /// Whether this person may change or give up this claim.
    ///
    /// Its owner, or somebody the game trusts with other people's things —
    /// `commandplayer`, which is what vanilla's own `/land adminfree` asks of
    /// whoever deletes a claim that is not theirs. The map asks the same thing
    /// rather than a rule of its own.
    /// </summary>
    private static bool Owns(ICoreServerAPI api, LandClaim claim, string uid) =>
        claim.OwnedByPlayerUid == uid
        || (api.World.PlayerByUid(uid)?.HasPrivilege(Privilege.commandplayer) ?? false);

    /// <summary>
    /// Writes the named people onto a claim, replacing whoever was on it.
    ///
    /// The whole list each time rather than a difference: the form holds all of
    /// it and sends all of it, so working out what "changed" would mean against a
    /// claim somebody else may have edited since is work with a wrong answer.
    ///
    /// A name the server has never seen is dropped rather than refusing the
    /// change. It is a typo in one row of a form, and losing the other rows to it
    /// would be worse than the row quietly not appearing — the form shows what
    /// came back, so a name that did not take is visible on the next post.
    ///
    /// What they are granted is use and traverse and building, which is what the
    /// game's `/land claim grant ... all` gives. The map offers one kind of guest
    /// rather than three, because three checkboxes per person is a permission
    /// system and this is a map.
    /// </summary>
    private static void Invite(ICoreServerAPI api, LandClaim claim, List<string> names)
    {
        claim.PermittedPlayerUids.Clear();
        claim.PermittedPlayerLastKnownPlayerName.Clear();

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var data = api.PlayerData?.GetPlayerDataByLastKnownName(name.Trim());
            if (data is null || string.IsNullOrEmpty(data.PlayerUID))
            {
                api.Logger.Notification(
                    "[witchlight] no player called \"{0}\" on this server, so the claim does not "
                    + "name them", name.Trim());
                continue;
            }

            claim.PermittedPlayerUids[data.PlayerUID] = EnumBlockAccessFlags.BuildOrBreak
                | EnumBlockAccessFlags.Use | EnumBlockAccessFlags.Traverse;
            claim.PermittedPlayerLastKnownPlayerName[data.PlayerUID] =
                data.LastKnownPlayername ?? name.Trim();
        }
    }

    /// <summary>
    /// Makes one claim, or says in one sentence why it cannot be made.
    ///
    /// Null is the claim having been made. The refusals are the game's own, in
    /// the game's own order, so that a claim the map takes is a claim `/land
    /// claim` would have taken from the same player standing in the same place.
    /// </summary>
    private static string? Make(ICoreServerAPI api, WantedClaim wanted)
    {
        // The world's own switch, first, because a world with claiming turned off
        // has no claims to be allowed or refused one of.
        if (!api.World.Config.GetBool("allowLandClaiming", true))
        {
            return "land claiming is off in this world's configuration";
        }

        // The game's privilege and then the map's, in that order: the first is
        // the server's rule and the second only narrows it. Refusing on the game's
        // own first is what makes the message say the true reason.
        if (!Permissions.Holds(api, wanted.Uid, Permissions.ClaimsCreate))
        {
            return $"claiming land on the map is for {Permissions.Who(Permissions.ClaimsCreate)}";
        }

        // Everything below needs the role's numbers, and a role is read off the
        // player. Somebody who has never joined has none, which is the one case
        // this cannot answer for at all.
        var data = api.PlayerData?.GetPlayerDataByUid(wanted.Uid);
        var role = data is null ? null : api.Permissions.GetRole(data.RoleCode);
        if (data is null || role is null)
        {
            return "this server has no record of them, so it has no allowance to check";
        }

        // Read once and refused outright where there is none, rather than reached
        // for twice. Asking `Claims?.All` and then adding through `Claims.Add` is
        // two answers to whether this world has land claims at all, and the
        // second of them would put a claim nowhere and report that it worked.
        if (api.World.Claims is not { } held)
        {
            return "this world has no land claims to add to";
        }

        var everyones = held.All ?? new List<LandClaim>();
        var mine = everyones
            .Where(claim => claim?.OwnedByPlayerUid == wanted.Uid)
            .ToList();

        if (mine.Count >= role.LandClaimMaxAreas + data.ExtraLandClaimAreas)
        {
            return $"they already have {mine.Count} claims, which is all their role allows";
        }

        // Inclusive of every corner: a rectangle drawn from one block to another
        // covers both of them, which is what somebody dragging one out means, and
        // a claim from y=60 to y=80 is meant to include the block at 80.
        //
        // Clamped to the world rather than refused for reaching past it. What
        // arrives is two numbers somebody typed, and the useful answer to "from
        // the bottom of the world" is the bottom of the world.
        var floor = Math.Clamp(Math.Min(wanted.Y1, wanted.Y2), 0, api.WorldManager.MapSizeY);
        var ceiling = Math.Clamp(Math.Max(wanted.Y1, wanted.Y2), 0, api.WorldManager.MapSizeY);
        var ground = new Cuboidi(
            wanted.X1, floor, wanted.Z1,
            wanted.X2 + 1, Math.Min(ceiling + 1, api.WorldManager.MapSizeY), wanted.Z2 + 1);

        var least = role.LandClaimMinSize;
        if (least is not null
            && (ground.SizeX < least.X || ground.SizeY < least.Y || ground.SizeZ < least.Z))
        {
            return $"{ground.SizeX}x{ground.SizeY}x{ground.SizeZ} is under the "
                + $"{least.X}x{least.Y}x{least.Z} their role allows";
        }

        var total = mine.Sum(claim => (long)claim.SizeXYZ) + ground.SizeXYZ;
        var allowed = (long)role.LandClaimAllowance + data.ExtraLandClaimAllowance;
        if (total > allowed)
        {
            return $"that would bring them to {total}m³, past the {allowed}m³ they are allowed";
        }

        // Everybody's claims, including their own. The game checks the whole list
        // rather than other people's, because a claim overlapping one of your own
        // is still two claims on one piece of ground.
        if (everyones.FirstOrDefault(claim => claim?.Intersects(ground) == true) is { } already)
        {
            var whose = string.IsNullOrEmpty(already.LastKnownOwnerName)
                ? "another claim"
                : $"a claim of {already.LastKnownOwnerName}'s";
            return $"it overlaps {whose}";
        }

        // `LastKnownOwnerName` is what the game writes on a claim and what every
        // reader of one shows, so the claim is created from the name rather than
        // from the uid and the uid is written on afterwards — which is what
        // `CreateClaim` does for a player it is handed, and what nothing does for
        // one it is not.
        var claiming = LandClaim.CreateClaim(data.LastKnownPlayername ?? "", role.PrivilegeLevel);
        claiming.OwnedByPlayerUid = wanted.Uid;
        claiming.Description = wanted.Description ?? "";

        var error = claiming.AddArea(ground);
        if (error != EnumClaimError.NoError)
        {
            return error == EnumClaimError.Overlapping
                ? "it overlaps one of their own areas"
                : "it is not next to their other areas";
        }

        // The game's own API, which indexes the claim by region, saves it with the
        // world and tells every client. Nothing here writes any of that down: a
        // second copy of where the claims are is a second thing to be wrong.
        held.Add(claiming);
        return null;
    }
}
