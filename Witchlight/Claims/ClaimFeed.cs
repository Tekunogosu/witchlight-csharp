using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>One rectangle of ground a claim covers, seen from above.</summary>
public class LiveArea
{
    public int X1 { get; set; }
    public int Z1 { get; set; }
    public int X2 { get; set; }
    public int Z2 { get; set; }

    /// <summary>
    /// How far up and down it reaches.
    ///
    /// Carried even though the map is drawn from above, because a claim covering
    /// three blocks of a cellar is not the same fact as one covering the sky over
    /// it, and a reader who opens the boundary is asking exactly that. Nothing
    /// draws with these; the popup says them.
    /// </summary>
    public int Y1 { get; set; }
    public int Y2 { get; set; }
}

/// <summary>
/// One land claim, as the map draws it.
///
/// The areas rather than the claim's bounding box: a claim is built up out of
/// adjacent rectangles and the shape they make is the shape of the boundary. A
/// box drawn around the lot would put a fence round ground nobody has taken.
/// </summary>
/// <summary>One person a claim lets in, and what it lets them do.</summary>
public class LiveGuest
{
    public string Uid { get; set; } = "";

    /// <summary>The name the game last knew them by, which is what a form shows
    ///  and what somebody types to name them.</summary>
    public string Name { get; set; } = "";

    /// <summary>Whether they may build and break, rather than only use and walk.
    ///  The map offers the two the game's own `/land claim grant` offers, and
    ///  this is which of them.</summary>
    public bool Builds { get; set; }
}

public class LiveClaim
{
    /// <summary>
    /// What names this claim wherever it goes.
    ///
    /// A land claim has no id of the game's own — nothing on it survives a
    /// change and nothing is written down anywhere but the savegame — so this is
    /// worked out from what the claim is: its owner and the ground it covers.
    /// It is what a page names when it asks for one to be changed or given up,
    /// which is the whole reason it exists: without it "this claim" is a thing
    /// only a person looking at a screen can mean.
    ///
    /// That it changes when the ground does is a property and not a fault. A
    /// page holding a key for a claim somebody has since redrawn is holding a
    /// name for land that is no longer that shape, and the mod refusing it is
    /// better than the mod editing whatever now sits there.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>Whose it is, as the game last knew them by name.</summary>
    public string Owner { get; set; } = "";

    /// <summary>
    /// Whose it is, by the identity that survives a rename.
    ///
    /// What the page compares against whoever is looking, so that somebody who
    /// has just drawn a claim can see their own appear.
    /// </summary>
    public string OwnerUid { get; set; } = "";

    /// <summary>What its owner called it, or empty where they called it nothing.</summary>
    public string Description { get; set; } = "";

    /// <summary>The ground it covers, in one rectangle or several.</summary>
    public List<LiveArea> Areas { get; set; } = new();

    /// <summary>Who it lets in beyond its owner.</summary>
    public List<LiveGuest> Guests { get; set; } = new();

    /// <summary>Whether anybody at all may use what is on it, or walk across it.
    ///  The game's own two, which are the only permissions it offers everybody
    ///  rather than a named person.</summary>
    public bool EveryoneUses { get; set; }
    public bool EveryoneWalks { get; set; }

    /// <summary>How much land it takes, which is what an allowance is spent on.
    ///  Worked out by the game from the areas, so it is read rather than
    ///  recomputed — the map and the game must agree about what a claim costs.</summary>
    public int Volume { get; set; }
}

/// <summary>
/// What one person is allowed to claim, and what they have already used.
///
/// Sent so the map's own form can say what a rectangle will cost before it is
/// asked for. Without it the form is a shape somebody draws and a refusal they
/// cannot predict: a survival player is allowed a quarter of a million cubic
/// metres, and at the full height of the world that is a square thirty-two
/// blocks across — which is a surprise worth having before the round trip rather
/// than after it.
///
/// Only ever a smaller number than the truth. This is what the role and the
/// player data said at the moment of the post, and the mod checks all of it again
/// against the claim it is handed; a form that agrees is a form that saves a
/// round trip, never one that grants anything.
/// </summary>
public class ClaimAllowance
{
    /// <summary>Cubic metres this person may hold in total.</summary>
    public long Allowance { get; set; }

    /// <summary>Cubic metres their existing claims already come to.</summary>
    public long Used { get; set; }

    /// <summary>How many separate claims they may have, and have.</summary>
    public int MaxAreas { get; set; }
    public int Areas { get; set; }

    /// <summary>The smallest area their role allows, on each axis.</summary>
    public int LeastX { get; set; }
    public int LeastY { get; set; }
    public int LeastZ { get; set; }
}

/// <summary>
/// Every land claim, and who may be told about them.
///
/// Not the shape the markers travel in, and the difference is the question. A
/// marker is private to whoever made it, so who may see one is answered per
/// marker; a claim is either the server's business or it is not, and who may see
/// them is answered per person, by a privilege. So the claims travel once with
/// the names of everyone entitled to them beside — which on a server with fifty
/// players and a hundred claims is one list rather than fifty copies of one.
/// </summary>
public class LiveClaims
{
    /// <summary>
    /// Whether the claims are everybody's, which is what the setting says on a
    /// server that has not narrowed it.
    ///
    /// Read off the privilege rather than assumed: `[claims] view = "player"` is
    /// the one answer that holds for a reader the mod has never heard of, and
    /// anything narrower has to be decided per person.
    /// </summary>
    public bool Everyones { get; set; }

    /// <summary>
    /// How tall this world is.
    ///
    /// A fact about the world rather than about the claims, and it travels with
    /// them because it is the one thing the form needs that a map drawn from
    /// above cannot show: how deep a claim goes is what its volume is mostly made
    /// of, and this is what "the whole height of it" means here.
    /// </summary>
    public int Height { get; set; }

    /// <summary>The claims themselves.</summary>
    public List<LiveClaim> Claims { get; set; } = new();

    /// <summary>Who may be shown them, beyond that, by uid.</summary>
    public List<string> Seen { get; set; } = new();

    /// <summary>
    /// Who may draw a new one, and what each of them is allowed, by uid.
    ///
    /// A separate answer from <see cref="Seen"/>, because they are separate
    /// questions: a server can show every boundary to everybody and still let
    /// nobody but its landholders draw one.
    ///
    /// What each is allowed rides along rather than going on a channel of its
    /// own, for the reason the marker colours ride with the markers: it is a few
    /// dozen bytes per person who may claim at all, it changes when their role or
    /// their claims do, and sending it here means a form always has it.
    /// </summary>
    public Dictionary<string, ClaimAllowance> Making { get; set; } = new();
}

/// <summary>
/// Every land claim on the server, as the map service wants it.
///
/// Claims live in the world manager, so all of them are readable from here. Who
/// may be told about them is decided here too, for the reason the markers and the
/// player positions are: the service does not know what a privilege is and must
/// not have to, and this is the half that does.
///
/// Whom to decide it for is the question this shape answers. A privilege can only
/// be tested against somebody the server knows, and the people looking at the web
/// map are not all standing in the world — so it is asked about everybody the
/// server has ever had player data for rather than about everybody online, and
/// <see cref="Permissions.Holds(ICoreServerAPI, string, string)"/> is what makes
/// that possible.
/// </summary>
public static class ClaimFeed
{
    /// <summary>Every claim, with who may see them, as the service wants it.</summary>
    public static string Json(ICoreServerAPI api) => JsonConvert.SerializeObject(Sorted(api));

    /// <summary>
    /// The claim answering to one of these names, or null where none does.
    ///
    /// Walked rather than kept in a dictionary: a server has tens of claims, this
    /// is asked only when somebody presses something in a browser, and an index
    /// held beside the game's own list is a second copy of where the land is —
    /// which is the thing this file exists not to keep.
    ///
    /// The key is worked out the same way it is written, by the same function, so
    /// the two cannot disagree about what a claim is called.
    /// </summary>
    public static LandClaim? ByKey(ICoreServerAPI api, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (var claim in api.World.Claims?.All ?? new List<LandClaim>())
        {
            if (claim?.Areas is null)
            {
                continue;
            }
            if (Key(claim, Rectangles(claim)) == key)
            {
                return claim;
            }
        }
        return null;
    }

    /// <summary>The ground one claim covers, seen from above.</summary>
    private static List<LiveArea> Rectangles(LandClaim claim) =>
        claim.Areas
            .Where(area => area is not null)
            .Select(area => new LiveArea
            {
                X1 = area.MinX,
                Z1 = area.MinZ,
                X2 = area.MaxX,
                Z2 = area.MaxZ,
                Y1 = area.MinY,
                Y2 = area.MaxY,
            })
            .ToList();

    /// <summary>How many there are, for `witchlight status`.</summary>
    public static int Count(ICoreServerAPI api) => api.World.Claims?.All?.Count ?? 0;

    /// <summary>
    /// Every claim, and the two lists of who may do what with them.
    ///
    /// Where seeing them is open to any player there is nothing to work out and
    /// <see cref="LiveClaims.Everyones"/> says so; the service then sends them to
    /// anybody, signed in or not, which is what the game already does. Where it is
    /// not, each person the server knows is asked about in turn.
    /// </summary>
    public static LiveClaims Sorted(ICoreServerAPI api)
    {
        var sorted = new LiveClaims
        {
            Claims = All(api),
            Height = api.WorldManager.MapSizeY,
            // Open to everybody is the one answer that can be given without a
            // person to give it about, so it is the one thing worth asking first.
            Everyones = Permissions.For(Permissions.ClaimsView) == Privilege.chat,
        };

        // A world with claiming switched off has nobody who may draw one,
        // whatever any role says. Asked once rather than per person: it is a fact
        // about the world, and the mod refuses on it first for the same reason.
        var claiming = api.World.Config.GetBool("allowLandClaiming", true);

        // Whose claims are whose, worked out once. Asked per person instead, this
        // walks every claim on the server for every player it has ever had —
        // which on a settled server is a square of two numbers that both only
        // grow, on the game thread, every share interval.
        var byOwner = new Dictionary<string, List<LandClaim>>(StringComparer.Ordinal);
        foreach (var claim in api.World.Claims?.All ?? new List<LandClaim>())
        {
            var owner = claim?.OwnedByPlayerUid;
            if (string.IsNullOrEmpty(owner))
            {
                continue;
            }
            if (!byOwner.TryGetValue(owner, out var theirs))
            {
                theirs = byOwner[owner] = new List<LandClaim>();
            }
            theirs.Add(claim!);
        }

        foreach (var uid in Known(api))
        {
            if (!sorted.Everyones && Permissions.Holds(api, uid, Permissions.ClaimsView))
            {
                sorted.Seen.Add(uid);
            }
            if (claiming
                && Permissions.Holds(api, uid, Permissions.ClaimsCreate)
                && Allowed(api, uid, byOwner) is { } allowance)
            {
                sorted.Making[uid] = allowance;
            }
        }

        return sorted;
    }

    /// <summary>
    /// What one person may claim, or null where the server has no record to say.
    ///
    /// The allowance is the role's plus whatever an operator granted them by
    /// hand, which is exactly the sum `/land claim add` checks against — read
    /// here rather than restated, so the number the form shows and the number the
    /// game enforces cannot be two numbers.
    /// </summary>
    private static ClaimAllowance? Allowed(
        ICoreServerAPI api, string uid, Dictionary<string, List<LandClaim>> byOwner)
    {
        var data = api.PlayerData?.GetPlayerDataByUid(uid);
        var role = data is null ? null : api.Permissions.GetRole(data.RoleCode);
        if (data is null || role is null)
        {
            return null;
        }

        var mine = byOwner.TryGetValue(uid, out var theirs) ? theirs : new List<LandClaim>();
        var least = role.LandClaimMinSize ?? new Vec3i(1, 1, 1);
        return new ClaimAllowance
        {
            Allowance = (long)role.LandClaimAllowance + data.ExtraLandClaimAllowance,
            Used = mine.Sum(claim => (long)claim.SizeXYZ),
            MaxAreas = role.LandClaimMaxAreas + data.ExtraLandClaimAreas,
            Areas = mine.Count,
            LeastX = least.X,
            LeastY = least.Y,
            LeastZ = least.Z,
        };
    }

    /// <summary>
    /// Everybody this server could be asked about, by uid.
    ///
    /// The stored player data rather than who is online: somebody reading the map
    /// may have signed in from a browser and not joined today, and a permission
    /// that also meant "and be online" would take the map away from them the
    /// moment they logged out of the game.
    ///
    /// Online players are folded in because a first join writes player data at a
    /// moment of the server's choosing, and somebody in the world who is not yet
    /// in that table would be nobody for as long as that took.
    /// </summary>
    private static IEnumerable<string> Known(ICoreServerAPI api)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uid in api.PlayerData?.PlayerDataByUid?.Keys ?? Enumerable.Empty<string>())
        {
            uids.Add(uid);
        }
        foreach (var player in api.World.AllOnlinePlayers)
        {
            if (!string.IsNullOrEmpty(player?.PlayerUID))
            {
                uids.Add(player!.PlayerUID);
            }
        }
        return uids;
    }

    /// <summary>
    /// Who one claim lets in, as the map shows them.
    ///
    /// The game keeps three dictionaries keyed on uid — what each person may do,
    /// and what they were last called — and a form needs one row per person. The
    /// name is what somebody reads and types; the uid is what survives a rename
    /// and what the claim actually stores.
    /// </summary>
    private static List<LiveGuest> Guests(LandClaim claim)
    {
        var guests = new List<LiveGuest>();
        foreach (var (uid, may) in claim.PermittedPlayerUids ?? new Dictionary<string, EnumBlockAccessFlags>())
        {
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }
            claim.PermittedPlayerLastKnownPlayerName.TryGetValue(uid, out var name);
            guests.Add(new LiveGuest
            {
                Uid = uid,
                Name = string.IsNullOrEmpty(name) ? uid : name,
                Builds = (may & EnumBlockAccessFlags.BuildOrBreak) != 0,
            });
        }
        return guests;
    }

    /// <summary>
    /// A name for a claim, made out of what the claim is.
    ///
    /// The game gives a claim nothing to be known by, so this is the owner and
    /// every corner run through a hash. Two properties are what it is for: the
    /// same claim gives the same name for as long as nobody moves it, which is
    /// what lets a page name one it is looking at; and a claim whose ground has
    /// changed gives a different one, so a page holding a stale key is refused
    /// rather than quietly editing land it was not looking at.
    ///
    /// Not a secret and not a checksum. FNV-1a because it is four lines and
    /// spreads well enough that two claims on one server will not collide.
    /// </summary>
    private static string Key(LandClaim claim, List<LiveArea> areas)
    {
        var hash = 0xcbf29ce484222325UL;
        void Eat(string text)
        {
            foreach (var letter in text)
            {
                hash ^= letter;
                hash *= 0x100000001b3UL;
            }
        }

        Eat(claim.OwnedByPlayerUid ?? "");
        foreach (var area in areas)
        {
            Eat($"|{area.X1},{area.Y1},{area.Z1},{area.X2},{area.Y2},{area.Z2}");
        }
        return hash.ToString("x16");
    }

    /// <summary>Every claim saved on the server, as the map draws one.</summary>
    public static List<LiveClaim> All(ICoreServerAPI api)
    {
        var drawn = new List<LiveClaim>();
        foreach (var claim in api.World.Claims?.All ?? new List<LandClaim>())
        {
            if (claim?.Areas is null)
            {
                continue;
            }

            var areas = Rectangles(claim);
            if (areas.Count == 0)
            {
                continue;
            }

            drawn.Add(new LiveClaim
            {
                Key = Key(claim, areas),
                Owner = claim.LastKnownOwnerName ?? "",
                OwnerUid = claim.OwnedByPlayerUid ?? "",
                Description = claim.Description ?? "",
                Areas = areas,
                Guests = Guests(claim),
                EveryoneUses = claim.AllowUseEveryone,
                EveryoneWalks = claim.AllowTraverseEveryone,
                Volume = claim.SizeXYZ,
            });
        }

        return drawn;
    }

}

