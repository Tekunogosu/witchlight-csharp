# Witchlight (server mod)

The version tracks the [map service](../rust/witchlight), and the two **carry the
same version at all times** — they ship as one archive and are one release, so a
change to either half moves both. `package.sh` reads the version out of the
service binary and refuses to package a pair that disagrees, which is where that
rule is kept rather than in anybody's memory.

A version that moved for the other half says so and lists nothing, which is not an
omission: it is what "one release" looks like from the side that did not change.

While Witchlight is alpha, a format change **clears the map** on start rather than
upgrading it. It rebuilds as players explore. Read the release note before
upgrading a server whose map you would rather keep.

## 0.47.3

**Deploy note:** both halves, upgraded together; nothing is cleared.

- The preset table is laid out the way the game's own lists are, so it stays
  inside its window and the scrollbar scrolls it. Every other row is shaded,
  the panel is wider, and a name or block too long for its column is cut with
  an ellipsis rather than running off the edge.

## 0.47.2

**Deploy note:** both halves, upgraded together; nothing is cleared.

- The in-game preset panel is a table: a search box over Name and Block
  columns, both left-aligned. A header press sorts by that column and a
  second press turns it round; the search matches either column.

## 0.47.1

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service: markers, preferences and visited chunks live in its database
— see its changelog for what to carry across.

## 0.47.0

**Deploy note:** both halves, upgraded together; nothing is cleared.

- The in-game marker window has a "Presets" button between Cancel and Save. It
  opens a list of everything you have kept beside the window; choosing one
  fills the name, colour, picture and privacy the way the key would have over
  that preset's own block. The marker stays on the block you are looking at.

## 0.46.8

**Deploy note:** both halves, upgraded together; nothing is cleared. The palette
is asked for once more on this start if what is stored did not come from a
client. `commands.palette` now defaults to `admin`.

- Who may see whom is worked out for everybody in a group, on or not, so a
  player signed in to the map with their own player offline still sees their
  group.
- A portrait is asked for on join only where the map has none. The client
  sends a new one when what is worn or the skin actually changes, not on every
  slot event — armour wearing down sent the same picture every few minutes.
- A palette is asked for until a client has supplied one for this mod set, and
  then never on the server's own initiative: a mod changing or an admin running
  `/witchlight palette` is what asks again. Coverage and gaps are reported and
  no longer nag.
- `/witchlight palette` is an operator's command by default. Anybody with the
  mod may still be asked, and a palette from a client that was not asked is
  refused. The commands that ask a client for something no longer refuse a
  target who lacks the command's own privilege.

## 0.46.7

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service: the profile window's layout and its colour palette — see its
changelog.

## 0.46.6

**Deploy note:** both halves, upgraded together; nothing is cleared. A settings
file already written keeps whatever `per_world` it says.

- A fresh settings file is written with `per_world = true` whether the server is
  dedicated or singleplayer, and a file that says nothing about it reads as on.
  Moved with the map service, where the claim being drawn now stays on the map
  while its window is open — see its changelog.

## 0.46.5

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service: a player icon size, and the colour choice as a button — see
its changelog.

## 0.46.4

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service: claims say what you may do on them, open on a right click,
and players choose their colour — see its changelog.

## 0.46.3

**Deploy note:** both halves, upgraded together; nothing is cleared. The game
server restarts before the new map service is live.

- Claiming land from the map works for players again. The settings file names
  the privilege the way the game does, `claimland`, and the mod checked the
  word against the game's privilege *codes*, where it is `areamodify` — so the
  default was refused and claiming fell back to admins only, with a warning in
  the log on every start. The game's names are now understood beside its codes,
  in every `commands` and `claims` setting, and no settings file needs editing.
- The window on somebody else's marker draws over the world map rather than
  under its right edge, at the place the game's own waypoint window takes.
- Its switch reads "Pin marker", and a second switch, "Save as preset", keeps
  the marker's name, picture and colour as a preset of your own for the block it
  was made on. A chat line says it was kept, and under which block pattern.

## 0.46.2

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service, which lays its accessibility window out afresh — see its
changelog.

## 0.46.1

**Deploy note:** both halves, upgraded together; nothing is cleared. The game
server restarts before the new map service is live.

- How many are on, and where they stand, counts only players whose client has
  finished joining. The game's list of online players also holds whoever is
  still connecting — its own documentation warns so — and a connecting player
  already stands in the world at their last position for as long as their
  client takes to load, or for good where it never finishes. That read as one
  player on a server nobody was on.
- Nothing else moved here; the hidden player groups, the accessibility window
  and the list icon size are in the map service — see its changelog.

## 0.46.0

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service, which keeps logins across a restart — see its changelog.

## 0.45.3

**Deploy note:** both halves, upgraded together; nothing is cleared. The game
server restarts before the new map service is live.

- Pinning somebody else's marker from the in-game map no longer fails with
  "Not a valid index". Shared markers are drawn on a map layer of this mod's own
  rather than borrowed into the game's waypoint list, where the game's edit
  window addressed them by a place they did not have. A right click on one opens
  a Witchlight window that keeps it in sight, and offers the name, colour and
  picture when the server allows the change.
- The date the map shows matched the game's a day late. The game counts the day
  of the year from zero and the clock subtracted one more.

## 0.45.2

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service, which draws a reader's tiles far cheaper — see its changelog.

## 0.45.1

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service, which encodes tiles for the wire far faster — see its
changelog.

## 0.45.0

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service: tiles addressed by their own last change, a ten-minute change
history, per-person compact tiles, and the sharing boxes always shown — see its
changelog.

## 0.44.0

**Deploy note:** both halves, upgraded together; nothing is cleared. Moved for
the map service, which now writes its zoom levels to disk once a tile has
stopped changing rather than on every rebuild — see its changelog.

## 0.43.0

**Deploy note:** both halves, upgraded together; nothing is cleared.

**Each player's view distance rides the player post** as `ViewChunks`: how
far the server loads ground around them, in chunks, never past its own
`MaxChunkRadius`. The map service reads it as how far standing somewhere adds
to that player's map and how far beside them it may ask for ground, as a disc
rather than a square. The service also asks `/exists` before `/load` now, so a
column the savegame does not hold is never generated on the map's behalf —
see the service's changelog for what else moved.

## 0.42.0

**Deploy note:** both halves, upgraded together. **The map is kept**: the
service reads the region files once into its new database on its first start
and says so in its log, after which `columns/` may be deleted. **Behaviour
changes on upgrade:** `private_map` is on by default, so every player starts
with an empty map that fills in as they move, positions are their own group's
to see, and a browser nobody has signed in on is shown the ground around spawn
and nothing else. Set `private_map = false` in `witchlight.conf` for a server
of friends that wants the one shared map it always had. Four new settings are
written into the file the next time the service writes one.

**Terrain goes to the service, not to disk.** The region files are gone. A
block a player places or breaks patches one column of a record held in memory
and reaches the service within a quarter of a second; anything the game changed
without a player is read whole, a few chunks per beat. Mining sends nothing. At
start the mod asks the service what it holds — a checksum per chunk — so a chunk
loading again is not a change. See `Map/Exporter.cs`.

**Who is in which group travels with the players**, offline members included,
so that a map shared with a group is shared with the group and not with whoever
of it is online. Positions post every second.

**`private_map`** keeps positions to each player's own group whatever
`players_public` says.

## 0.41.1

**Deploy note:** both halves, upgraded together. Nothing is cleared — a map part
way through rebuilding keeps what it has and finishes about ten times faster.

**Getting the ground back runs on its own clock.** The map asked the server for
columns on the export beat, which is the beat an operator sets to decide how often
the disk is touched — so a map rebuilding itself did so at whatever rate somebody
had chosen to *write* at, and every recovered column waited out a gap sized for
writing rather than for loading. Eight columns every ten seconds is fine for
healing a hole; it is 65 minutes for a map of six thousand. Loading now steps four
columns every 250ms, which is the rate `Seeding` established as safe years of
nothing going wrong ago, and which is now said once for both of them rather than
twice. **Measured on a map rebuilt from nothing: 92 chunks a minute became 909.**

## 0.41.0

**Deploy note:** both halves, upgraded together. **The map is cleared and
rebuilds**, because the region format has changed — it fills back in over the
following minutes without anyone walking anywhere, since this release also draws
the ground the savegame already holds. The new `export_interval_ms` setting is
written into the settings file the next time the service writes one; a file
without it keeps the ten seconds the map has always used.

**A chunk is stored and written on its own.** Version 4 was one gzip stream over
a whole region, so a single column moving meant repacking the other two hundred
and fifty-five: a quarter of a megabyte to record six kilobytes of change, and on
a server with people on it half a gigabyte an hour to keep a map that had barely
moved. Version 5 puts a directory of fixed size at the head of each region and
compresses every chunk behind it, so a chunk that changes costs its own kilobyte
and the entry naming it. Measured on a running server: **110 KiB per change
became 678 bytes.** The files are about five per cent larger for it.

The export line says what that cost — `wrote 3 chunks (2716 bytes) of 4 regions`
— because "wrote 5 regions" was the number that hid this for as long as it did.

**A payload is appended and never overwritten**, so a run that dies mid-write
leaves a directory still pointing at the bytes it always pointed at. Each chunk
carries a CRC-32, and a chunk whose bytes do not answer to it is read as one the
map does not hold — which the repair then fetches and writes again. A map damaged
by a power cut heals rather than having to be thrown away. Regions are packed
down when the bytes nothing points at outweigh the bytes something does.

**A season now lives in the directory**, so a year turning from summer to autumn
is sixteen bytes per chunk rather than a repacking of every region on the map.
The start-up survey reads seasons and holes out of directories too, so a map of
ten thousand regions starts without decompressing a byte of it.

**`export_interval_ms` is how often the terrain is written**, in milliseconds,
starting at the 10000 that was built in. It is the map's coalescing knob:
everything a chunk does inside one beat is written once, so raising it trades how
current the terrain is against how often the disk is touched, and a world save
exports whatever the gap was holding either way. Held between 1000 and 600000 —
an export runs on the server's own tick. Read and enforced by the mod.

**The map draws the ground the game already has.** A map only ever knew what it
had been told, so terrain explored before witchlight was installed was invisible
to it: on one world the savegame held 7957 columns and the map had drawn 6011.
The mod now walks outward from the edges of what the map holds and asks the game
whether the savegame has the column beside it — and hands the ones it does to the
repair, which already knows how to fetch a column and write it. It cannot make
the server generate world: a column is only considered because a mapped one sits
beside it, and only asked for when the save already holds it. `/witchlight status`
says how much is left.

## 0.40.3

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited.

**An export says what moved, not just that something did.** A column counts as
changed the moment one of its 1024 positions differs, so a count of changed
columns says how much of the map was rewritten and nothing at all about why. The
line now reads `9 of 21 columns re-read changed (280 positions: 280 blocks, 280
heights, 280 climate)` — a handful of positions with a new block on them is
somebody building; a thousand whose climate moved on its own would be a field this
map has no business re-reading drifting under it. Only columns already on disk are
counted, since one being written for the first time differs everywhere and would
drown the rest.

Written because a map that rewrote five regions every ten seconds for
three-quarters of an hour, with the count of mapped chunks never moving, could not
be told apart from one that was following the world honestly. It was following the
world honestly — but nothing in the log said so, and reading a re-read is not
something to do from memory.

**The quiet export line says what it could not read**, which the loud one already
did. An export that read everything it was asked for and found nothing changed
still had columns it could not reach, and said nothing about them.

## 0.40.2

**Deploy note:** both halves, upgraded together. Nothing is cleared. Every hole a
map is carrying fills in within a few seconds of the server starting, and no new
one is made.

**A rain height above the world is not a height, and that is what has been putting
holes in the map since 0.38.1.** The export checks that a column's blocks are
really in memory — 0.38.1 added it, and it was right to — by asking for the
vertical chunk holding that chunk's highest ground. It took the highest number in
the chunk's rain heightmap, and the game leaves `ushort.MaxValue` in that map
wherever rain never stopped. So one such position spoke for a whole chunk: the
check asked the server for the vertical chunk **two thousand layers up**, was told
there is none, and set aside a column whose blocks were every one of them in
memory as a column whose blocks had gone.

Nothing about a column ever changes what its rain map says, which is why those
holes were permanent, why walking back to one never filled it, and why three
rounds of asking the server to load them changed nothing — the server had already
loaded them, every time, and handed back exactly what was asked for. Heights at or
above the top of the world are no longer counted, and the downward search that
reads the surface is held inside the world for the same reason: started at 65535
it walked sixty thousand positions of nothing, and stored the number it gave up at
as a surface height of -1.

Found by standing a server up on a world of its own with the holes in it and
asking the check what it could see, rather than by reading the loader again.

**The borrow-and-return machinery added in 0.40.1 is gone.** It force-loaded each
column the map asked for and gave it back afterwards, against a cause that turned
out not to be the cause. Asking the server for a column and reading it when the
game says it has arrived is enough on its own — six holes punched into a map are
filled three seconds after the server starts — so the ninety lines that could
unload ground out from under a player are not there any more.

## 0.40.1

**Deploy note:** both halves, upgraded together. Nothing is cleared. A map holding
chunk-shaped holes should fill them within a minute of a player being on; the
export line says what happened either way.

**The repair holds the ground it asks for.** Reverted in 0.40.2: the columns it
was written for were never the problem. The line below is the part of this release
that mattered.

**The export line says which way a column was unreadable.** "no longer loaded"
was said both to a column the server holds nothing for and to one it holds a map
chunk for with no blocks under it. Those are a load that never happened and a load
that was undone, they want different fixes, and saying one word for both sent a
whole session's reading the wrong way. It now says `3 not there to read (3 with no
map chunk, 0 with no blocks under it)`.

## 0.40.0

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited. The map stops drawing the perimeters round trader camps on the
first beat after the upgrade; `[claims] worldgen = true` in the settings puts them
back. A map already holding chunk-shaped holes fills them in within a minute of
the server starting rather than never.

**The claims the world made for itself are not sent to the map.** The game
protects a trader camp, a story structure and a tiled dungeon with a land claim,
and those carry an owner's name with no owner behind them — which is the whole of
what tells them from a player's. They exist from the moment that ground generated
rather than from the moment somebody found it, so a web map drawing them handed
every reader the location of every trader on the server. They are left out of the
feed rather than left to the page to hide, because a claim that reached a browser
is a claim anybody may read out of it. Whose a claim is now has one owner in the
code as well: who may draw one and what counts as somebody's land are answered
from the same fact.

**The map repairs its own holes instead of asking for them forever.** A chunk the
map could not read was asked for at the head of an export and read on the next
one — and the server ages an untouched column out of memory in under ten seconds,
where the export beat is ten. So every column asked for arrived, sat there, and
was gone again before anything looked at it: the count of columns owed a read
stood still while every beat faithfully asked for them, and four chunk-shaped
holes stayed in the middle of finished terrain for as long as the world ran. The
ground is now read when the game says it has arrived, a second later rather than a
beat later, which is well inside the life of a column nobody is standing near.

**Repairing the map is its own thing.** `Repair` owns the columns the map is owed,
the asking for them and the reading of them when they land; `DirtyColumns` is back
to the two states it is actually about — changed, and on disk. The timing bug
above was a direct consequence of the repair having no clock of its own and
borrowing the export's.

**The export line about the repair reads.** "asked for 8 of 4 owed a read" was a
sentence this printed: what was asked for is counted when the asking happens and
what is still owed when the line is written, and columns settle in between. It
says "asked the server for 8, 4 still owed" now.

**`/wl status` says how many claims the map draws**, beside how many the server
has, so a map showing fewer is visibly a setting rather than a claim gone missing.

## 0.39.2

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited. A map already holding chunk-shaped holes fills them in on its
own over the following minutes, or at once on `/wl export`.

**A chunk the map could not read is asked for rather than forgotten.** A map
chunk outlives the blocks under it, so a column marked dirty and reached after
its blocks had gone cannot be read — 0.38.1 stopped recording that as air, which
was right, but it then set the column aside and waited for the server to load it
again. Nothing was going to. The server loads a column when somebody walks to it,
and a column at the trailing edge of a path nobody retraces is never loaded
again, so it stayed a single chunk of nothing in the middle of finished terrain,
one per few hundred explored, permanently. `/wl export` could not clear them
either: it exports what is in memory, and what was missing was precisely what was
not. Columns owed a read are now remembered and the server is asked to load them
— a few on every export, and up to 256 on the command, walking the list from
where the last pass stopped. They arrive, the load marks them changed the way any
load does, and the next export writes them; nothing else in the export had to
change to make that work.

**A map on disk says where its own holes are.** The list of columns owed a read
is memory, and a restart would forget every hole already made — so the start now
reads them back off the map itself: a chunk the map does not hold, with mapped
terrain on all four sides, is somewhere a player has been that never got written.
The map's edge is not that and is left alone; enclosure is the whole of the
difference, and without it a repair would have the server generate the world
outward for as long as it ran. The count is logged at start.

**`/wl status` says how many columns are owed.** Beside the count of columns
waiting to be read, which is work in hand, is the count of columns that cannot be
read until the server hands their blocks back — a number that should sit near
zero and fall on its own.

## 0.39.1

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited. A server that has never made a player group will find its map's
group tab holding one person rather than everybody, which is what it always meant
to say.

**"My group" on the map is the group, not the server.** A player's memberships
are not only the groups they joined: the game puts everybody in its own channels
— general chat, server info, the damage and info logs — and those arrive as
memberships like any other. Every pair of players therefore shared one, so the
map's group tab listed the whole server and was the same list as the tab beside
it. A membership now counts only where the server can name a player group behind
it, read off the server's own list rather than by refusing the numbers those
channels happen to use. A group the game made to carry a private message is a
real group and still not one of these: it is two people talking, and a map that
read it as a party would put whoever you last messaged on your group list and
leave them there.

## 0.39.0

Moved for the map service, which gained a `live_refresh_ms` setting for how often
the page asks where everybody is. Nothing here changed, and nothing in this half
reads it: the beat is the page's.

## 0.38.1

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited. Chunks already exported as flat brown squares repair themselves
the next time they load.

**A chunk is not exported until its blocks are there to read.** A map chunk is
the flat record a column keeps — its heightmaps, its climate — and the server
holds one long after it has let go of the blocks underneath it. `GetMapChunk`
answering was taken as the chunk being loaded, so a chunk marked dirty and
exported in that window read as air at every position, was recorded as air at the
height its own heightmap claimed, and was drawn as a flat brown square, chunk
aligned, in the middle of finished terrain. It stayed, because a stored record is
only replaced when the chunk is read again.

**A preset that could not be kept says so.** Nothing waits on keeping one — a
marker that landed must not be undone by a service that would not take the preset
beside it — so the log was the only thing that could report it, and it reported
nothing at all. It now says which pattern was kept, and how many that player then
has, or that the map would not take it.

## 0.38.0

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited.

**A marker carries which block it is about.** A waypoint is a place and nothing
else, so what a marker was put on is a question only this side can answer — and
only while the chunk is loaded and the block is still what it was. It is read
under the marker when one is made, read again wherever one moves, and it travels
with the marker as `Block`, which is what a preset made from that marker is keyed
on. The map may say it instead, where it knows something this side does not: the
pattern of a preset a screenful of markers has just been made to look like. An
empty `Block` on an ask means "read the world", never "forget what was known".

**One store owns what this mod knows about a waypoint that the game does not.**
`Beside<T>` does the reading, the writing and the forgetting; the visibility
choices and the blocks are two uses of it. Neither format has changed — a store
that already exists is read back exactly as it was written.

**`/witchlight status` says how many markers know their block**, beside the
visibility choices and the pins.

**The in-game marker window reads left to right.** Both switches are before the
words they answer rather than after them, in one column, and the two ways out
line up under them. The preset switch says *Set as preset* — it read *Keep as
what game:rock-granite-\* starts as*, which is a question nobody finishes.

## 0.37.1

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited.

**A marker can be kept in sight on a player's own map.** The game holds a pinned
waypoint against the edge of the map instead of letting it scroll off, and the
web map now offers that pin. It is one person's choice about one marker — pinning
somebody's marker puts it on the pinner's map and on no other — so whether they
may is whether they may *see* it: their own always, and anybody else's while it
is public. That is a lower bar than changing a marker and deliberately so.

The answer lives in two halves and one type owns both, because "is this pinned
for this person" is one question. A player's own marker is answered by the
waypoint, which is where the game's own map dialog writes it, so pinning from the
web and pinning in game are the same switch. Everybody else's arrives on a client
as a temporary waypoint rebuilt from what the server sends, so a flag on it has
nowhere to live — that half is kept beside the visibility choices, in the
savegame, and rides the per-player share.

`/witchlight status` says how many pins there are, beside how many markers have a
chosen visibility.

**The map's asks carry a fourth verb.** `{Markers:{Make,Change,Remove,Pin}}`,
where a pin is a key, whose ask it was, and which way it goes. A marker carries
nothing else on it: a pin names a waypoint rather than describing one, and this
side reads the waypoint itself before it acts.

**A marker no longer says whether its owner pinned it.** That field went out with
every marker to every reader and answered a question nobody asked — whether *this*
reader keeps it in sight is what a page wants, and that now travels sorted by
reader with the markers, the way the private ones already do.

## 0.37.0

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited.

**A claim can be renamed, re-permissioned and given up from the map.** The mod
takes three asks about claims now rather than one, and every one of them is
judged the way the game judges its own: a change or a removal is its owner's, or
somebody holding `commandplayer` — which is what vanilla's `/land adminfree`
asks of anybody deleting a claim that is not theirs.

**Who a claim lets in travels both ways.** The game's `AllowUseEveryone` and
`AllowTraverseEveryone`, and the players named on it, go out with the claim so a
form can show them; names typed into that form are turned into uids here,
because this is the half that can. A name the server has never seen is dropped
with a line in the log rather than refusing the whole change — it is a typo in
one row of a form, and losing the other rows to it would be worse.

**A claim carries a name again.** It is worked out from what the claim is — its
owner and every corner — because the game gives a claim nothing to be known by,
and without one "this claim" is a thing only a person looking at a screen can
mean. A claim whose ground has moved gives a different name, so a page holding a
stale one is refused rather than quietly editing land it was not looking at.

**What the map asks for now travels grouped by kind.** `{Markers:{Make,Change,
Remove}, Claims:{Make,Change,Remove}}`, because the two kinds answer the same
three verbs; flat, it had grown a `Claims` beside a `Make` that only meant
markers.

## 0.36.2

**Deploy note:** both halves, upgraded together. Nothing is cleared. Packaging on
a fresh checkout now needs `.env` — `cp .env.example .env` and put the map
service's repository in it.

**`package.sh` builds both halves.** It built the mod and then looked around for
a map service binary somebody else had built, which made "one archive, one
release" true of the version check and not of the build: the check catches a
version bumped without a rebuild, and nothing could catch a source file edited
without one. That shipped a stale map service under a version that looked right.
It now runs `cargo build --release` on the service itself, so the rebuild is not
something to remember.

**Where that repository is comes from `WITCHLIGHT_SERVICE_REPO`**, in the
environment or in a `.env` file beside the script — `.env.example` is what it
should look like, and `.env` is not committed, because a path on one machine is
not a fact about the project. The two guessed-at paths it used to search are
gone; one of them had not existed for as long as the build output has been
somewhere else, and nothing said so.

**Where the binary landed is asked of cargo** rather than assembled from the
repository's layout, since `CARGO_TARGET_DIR`, a `.cargo/config.toml` and a
`target` symlink can each send it elsewhere.

## 0.36.1

**Deploy note:** both halves, upgraded together. Nothing is cleared.

Moved for the map service, whose land claim buttons were drawn where nothing
could click them. This half did not change.

## 0.36.0

**Deploy note:** both halves, upgraded together. Nothing is cleared and nothing
has to be edited. A `witchlight.conf` written before this says nothing about
`[claims]`, and an absent table means the defaults — anybody may see where the
claims are, and drawing one from the map asks what `/land claim` already asks.

**The web map draws the land claims, and can take one.** Every claim on the
server goes to the map on the same slow beat the markers do, one shaded rectangle
per area, with its owner, its description and how far up and down it reaches.

**Who may see them and who may draw one are two settings.** `[claims] view` and
`[claims] create` in `witchlight.conf`, spelled the way `[commands]` is: `admin`,
`player`, or any privilege the game knows. `view` starts open because the game
already sends every claim to every client; `create` starts at `claimland`.

**A claim taken through the map is a claim `/land claim` would have taken.** The
world's `allowLandClaiming`, the game's own `claimland` privilege, how many claims
that person may hold, their role's allowance and smallest permitted size, and
whether the rectangle overlaps anybody — every one of the game's rules is checked
here, in `Claims/Claiming.cs`, and the map's own setting is asked in addition and
never instead. A refusal says who and why in the server log.

**Nobody is sent a claim they may not see.** The mod works out who may against
the stored player data rather than against who is online, so somebody reading the
map from a browser while their player is offline is answered properly instead of
being refused for not being in the world.

**`Mod/Permissions.cs` now answers for gates that are not commands.** It was a
table of command names read out of `[commands]`; each entry now carries the
settings key it is written under, so the two claim gates are the same question
answered in the same place rather than a second class with its own idea of what a
misspelled privilege means. `wl status` prints a line per table.

**`Markers/Pending.cs` is now `Web/Pending.cs`**, because it is no longer about
markers: one envelope carries everything asked for on the web, and the mod
collects it on the tick it already had. The route it collects from is `/pending`.

## 0.35.3

**Deploy note:** both halves, upgraded together. Nothing is cleared. The white
goes on the next server start, because the shipped base-game palette is corrected
and the server re-seeds from it — those blocks draw as plain ground straight
away. `/witchlight export` with the chunks loaded is what turns them into the
floor they are standing on. For colours built from a real asset set rather than
the recording, run the palette command from a client once after upgrading.

**Nothing white is left on the map that is not snow or ice.** `unknown.png` is
the game's missing-texture checker — white, and opaque over the whole square —
and it was being averaged as though it were a colour. Because it covers the whole
square it beat every real texture that covers less than one, so every block
wearing it came out `#fff9f9`, which is that file's own average. Clutter,
cluttered bookshelves, banners, pies, fire, ground storage, rubble and the
chiselled blocks all drew as white patches on ground that was the right colour.

A placeholder for a texture is not a texture. The rule already existed for the
textures a *shape* names and is now the same rule for the textures a *block*
names, in one place both ask — a block whose every texture is the checker has
none to try, which the palette already knows how to say. Such a block is drawn as
whatever it is standing on: the floor of the ruin under the bookshelf, the wall
behind the banner, the ground under the rubble.

**The base-game palette shipped with the mod is corrected.** It was recorded
before that rule existed, so it carried the checker's colour for 22 blocks, and a
dedicated server seeds from it — which is why the white survived on servers that
had never been sent a palette by a client. Those 22 entries now say what they
mean, which is that this recording has no colour for them. They are exactly the
entries whose recorded colour was the checker's own average, and nothing else was
touched.

**Chiselled blocks are resolved to their material before the palette is asked
whether they show**, rather than after. The shell's only texture was the checker,
so the palette now rightly says it draws nothing — asked in the old order, the
column search would have walked past every ruin wall in the world and recorded
the ground beneath it.

**Snow-covered chiselled blocks are resolved to their stone as well.** They were
left alone while the shell still had a colour, on the grounds that snow is what
somebody looking down sees. The colour it had was the checker rather than snow,
and a ruin drawn in white against snow cannot be seen at all.

## 0.35.2

**Deploy note:** both halves, upgraded together. Nothing is cleared. Ruins
already on the map keep their old colour until the chunks holding them are
exported again — `/witchlight export` does that for everything loaded, so
standing near one and running it repaints it.

**Ruins no longer draw as white patches.** A chiselled block is a shell: the
shape and the material both live in the block entity beside it, and the world
reports the same `chiseledblock` whether it was cut from granite or from
cobblestone. The palette can only answer for the block it is handed, and that one
has no texture of its own — so it answered near-white, and every ruin on the map
was a white patch on ground that was otherwise the right colour.

What a chiselled block is made of is now asked of the block entity, which is the
only thing that knows, and the game's own `GetMajorityMaterialId` is what answers
— it is already the question a map pixel is asking. The material is chosen from
the ones the palette can paint, so a block chiselled partly out of something
invisible draws as the part that shows. A chiselled block whose entity cannot be
read is drawn the way it was before rather than as a hole.

Snow-covered chiselled blocks keep the white. Snow lying over the chiselling is
what somebody looking down actually sees.

**The cost falls on ruins alone.** Which block ids are chiselled is read off the
block list once, when the world finishes loading, so every column that is not a
ruin costs one lookup in a set of a dozen ids and no block entity read at all.

**`/witchlight status` says how many kinds of chiselled block it is resolving**,
which is how to tell the feature is running at all.

**Clicking a ruin on the map now names the stone rather than the chiselled
block.** One id is stored per column and it is now the material's, so the readout
follows the colour. That is the trade for not changing the stored format.

## 0.35.1

**Deploy note:** both halves, upgraded together. Nothing is cleared and there is
nothing to edit.

**The map service's own log could be replaced while another thread was writing
to it.** Everything the service says arrives on a threadpool thread, from the
process's output, while a start comes off the game thread — and the writer was
published outside the lock that every write to it already took, so it was
reachable before it had been told to flush. A start that followed a service which
had fallen over on its own also left the previous writer holding its file handle.
Both are now done under that lock.

**A stop somebody asked for could be reported as the service falling over.** The
flag saying which kind of stop this is was written on the game thread and read on
whichever thread the exit event arrived on, with nothing making the write visible
to the read.

**One outage could be complained about three times.** Players, markers and the
world clock post independently and at the same time, and each checked and set the
shared "already said" flag without holding anything, so two feeds failing in the
same second could each say so. The health lines `/witchlight status` reads were
written by those same threads and read by the game thread. Both now sit behind one
lock — the log line itself is still written outside it.

**Four constructors that did nothing but assign their parameters are now primary
constructors.** No behaviour change. The other four in the mod stay as they are:
their constructors are private on purpose, funnelling construction through a
factory that checks something first, and a primary constructor cannot be private.

## 0.35.0

**Deploy note:** both halves, upgraded together. A server running Rustbound Magic
gets the mana and magic bars on the next restart, with nothing to edit.

**A settings file with no `[bars]` section now means the two the service writes
into a fresh one**, rather than meaning none. Every server already running had a
file written before that section existed, so every one of them sent no bars and
the map's Bar display section had nothing to build itself from — a feature that
looked broken and was only unasked. `[commands]` has read an absent table as its
defaults since it was added; this is the same rule, and the two are now the only
two tables so it is worth stating once: **a table nobody has written is the
defaults, and a table somebody has emptied is none.**

## 0.34.0

**Deploy note:** both halves, upgraded together. A `[bars]` entry gains an
optional fifth part and an existing one keeps working without it.

**A bar can say which mod it came from**, so the map can group the switches it
offers for them. It cannot be worked out: an entity attribute is a name and a
number, and the game keeps no record of what wrote it. So an entry names its
group, and where it does not this looks for an installed mod whose id appears in
the attribute's own name — which answers for a mod that names its attributes
after itself and for no other. Rustbound Magic's
`entitybehavior-resource-currentmana_rm` names nothing, which is why the entries
that ship carry `Rustbound Magic` outright.

## 0.33.0

**Deploy note:** both halves, upgraded together. Nothing is cleared. A server
running Rustbound Magic gets the mana and magic bars once its settings file has a
`[bars]` section — the service writes one into a fresh file, and an existing file
keeps what it has until `witchlight -c <file> -S` rewrites it.

**A player's card can carry whatever else a server gives them.** A mod that gives
players mana, stamina or a level keeps it on the player's own entity, in the same
watched attributes the game keeps health and hunger in — and this mod is already
holding that object to read those two. So the numbers travel to the map without
this referencing any mod, compiling against one, or breaking when one is
uninstalled: an operator names the attributes in `[bars]` and this reads whatever
is under them.

The kind of number is read off the attribute rather than assumed, because the
game's own readers answer their default for an attribute of the wrong kind and a
mod is free to keep mana as an int and the experience toward the next level as a
float — which is exactly what Rustbound Magic does.

**A bar is drawn only for a player who has one.** An attribute that is not there,
or one whose maximum is still zero, is a player this does not apply to, and no bar
is the honest picture of that. A mage who has spent their last mana still has a
bar, at empty: having none and having none left are different sentences.

## 0.32.0

**Deploy note:** both halves, upgraded together. The palette is rebuilt on the
first start and the colours of branchy leaves, reeds and a few hundred other
plants move, so the tiles holding them redraw once. No map is cleared.

**Branchy leaves and reed beds were drawn near-black.** Both came out within a
few shades of the colour this map paints ground nobody has ever walked into, and
for two different reasons that met in the same place.

A block with nothing to say about which of its textures faces up was coloured
from whichever its own dictionary listed first — the game's own rule, and an
arbitrary one. Branchy leaves list `branch` before their leaves, and a branch is
the one thing on that block nobody looking down at a tree can see: every branchy
tree took the colour of the twig hidden under it, `#483a1e`, and then had the
leaf tint multiplied onto it. The texture that covers most of the block is the
one that stands for it now, which for branchy leaves is the leaf mask the plain
ones already use — the two are the same colour again.

And a block's shape was only ever asked when its own textures answered nothing.
A reed declares two textures, both a seed head covering three per cent of the
square, with the whole plant in its shape file — so the seed head answered and
the shape was never asked. The two are weighed against each other now, and what
covers more of the block wins.

**The map's white blobs were the game's missing-texture checker.** The shape
`block/basic/cube` declares `all: unknown`, which is the placeholder the game
draws when a texture is absent: white, and opaque over the whole square.
Averaged as though it were a texture it is the loudest thing in any shape that
uses it, and it is why `game:air` has been carrying the colour `#fff9f9` in every
palette ever built here — so a fresh map had white blobs across it wherever a
chunk had not finished loading. It is a placeholder for a texture rather than a
texture, and it is skipped. Air now records what it is, which is a block that
draws nothing.

## 0.31.0

**Deploy note:** both halves, upgraded together. No map is cleared and no palette
is thrown away. Columns are re-read as their chunks load, so the specks fill in on
their own rather than needing an export.

**A preset now names a family of blocks rather than one of them.** A block code
carries its variant as a number — `game:tallgrass-3`, `game:leaves-grown7-oak`,
`game:water-still-7` — so a preset kept against the code answered for one stage of
grass out of eight. Keeping a preset for grass meant keeping it again seven more
times, and changing it meant finding all eight.

The number is now where the wildcard goes by default, so the window opens on
`game:leaves-grown*-oak` and one preset covers the lot. It is only a default: the
star is a character in a text field, so move it, add another, or take it out to
name one block exactly. `*` stands for any run of characters anywhere in the
pattern, so `b1-b2-*` names `b1-b2-b3` and `b1-b2-c3` alike. The matching itself
has always worked this way — what changes is what a preset starts out as, in the
in-game window and on the map's own form both.

**The map had black specks scattered through explored ground.** A large structure
stands one real block beside a run of invisible placeholders, and the exporter
walked down a column until it found something that was not air — which is a
different question from finding something you can see. So a column stopped on a
placeholder and recorded a block with nothing to draw where there was grass, and
the map painted it the colour of a world nobody has ever walked into.

The exporter now walks past anything that shows nothing, and what counts as
showing is the palette's to say — it is the question the palette was built to
answer, so it is not answered a second way. It is asked again on every export, so
a better palette arriving from a client corrects what gets written as well as what
gets coloured.

**A palette from a client stopped saying which of its colourless blocks draw.**
An entry with no colour means one of two things — the block draws nothing at all,
or it draws something the sender could not colour — and only the sender's own
assets can tell them apart. That fact was built on both sides and then dropped on
the wire, so every colourless block in a client's palette arrived saying nothing:
the server could no longer see a gap worth asking about, and air and those
invisible placeholders read as blocks it knew nothing about. It travels now. A
palette from a client older than this says nothing rather than saying "draws",
which is what keeps it safe to take.

## 0.30.1

**Deploy note:** nothing in the mod changed. The version moves because the two
halves are one release and carry one version — see the service's own 0.30.1, which
is where the change is.

## 0.30.0

**Deploy note:** both halves, upgraded together. No map is cleared and no palette
is thrown away. On the first start every colour the palette was missing is filled
from the base game recording now shipped inside the mod, and the next admin to
join is asked once whether those are the colours this server should show. A
settings file written before this keeps working: an absent `[commands]` section
means the defaults, which are what the mod was already enforcing.

**A dedicated server draws a fully coloured map before anybody joins it.** Its
install ships 46 block textures against a full game's 9,587, so the palette it
built for itself coloured almost nothing and the map stayed flat until a player
happened to turn up with this mod on — which on a quiet server is days, and on a
server whose players do not run the mod is never. The base game's blocks look the
same on every server there is, so their colours are a fact this mod can know
before it is installed anywhere, and a recording of them now rides inside the
assembly at 70 KiB compressed.

Measured on a real dedicated server with no block textures at all: **13,936 of
14,091 blocks coloured, no gaps, before a single player connected** — the same
palette a full game install builds for itself. Rendering its first export reports
`0% waiting on a colour`. The asking is left for what is genuinely unknowable from
there: a mod's blocks, and a texture pack an admin has chosen.

It is a seed and not an answer. It fills only what has no colour, so a colour this
server worked out from its own assets always wins. `bake-palette.py` refreshes it
from a real `palette.json`, and refuses one with a mod's blocks or a gap in it.

**Air was drawn as a near-white block.** `game:air` has no textures, so nothing
found one — and it carries the default cube shape it never draws, so the shape
fallback averaged that cube and handed air the colour `#fff9f9`. The exporter
writes air for a column whose chunk holds no terrain, which on a freshly seeded
map is about half of it, so half a new map painted near-white instead of reading
as ground nobody has been to. Any palette built from a full asset set has carried
this since the shape fallback was added, which means every palette a client has
ever sent. What the game itself says a block draws is now asked before any colour
is looked for: `EnumDrawType.Empty` is recorded as drawing nothing, whatever a
texture or a shape would have averaged to. In the base game it moves exactly one
block, and that block is air.

**Who may run each `wl` command is the operator's to decide.** It was fixed in the
code — `login` and `mark` for anybody, everything else for an admin — with no way
to say otherwise short of rebuilding. `[commands]` in `witchlight.conf` now says
it, defaulting to what the old code did with two exceptions: asking a client for a
palette or for the marker pictures no longer takes an admin. It never needed one.
Whose colours win is decided when the answer arrives and not when the ask goes out,
so requiring an admin to *ask* bought nothing and cost a map on every server whose
operator does not play on it. Any privilege the game knows works in place of
`admin` and `player`; a name it does not know is refused to everyone but an admin
and said in the log, so a typo locks a command rather than opening it. `witchlight
status` prints the table in force.

**An admin is asked for the palette first, and asked once even when nothing is
missing.** The server used to ask whoever was nearest the front of its player list
— stable across a restart, so one player's client answered for the whole server
for as long as they kept playing there. An admin in the room is now asked before
anybody else, since theirs is the tileset the map should look like and a colour
taken from a player is a colour their answer will only have to replace; where there
is no admin, one of the others is picked at random.

And a palette can be complete and still unsettled. A texture pack changes every
colour on the map without changing a single block code, so a map drawn entirely
from the recording above is a map nobody who could decide has decided about. The
palette now records whether an admin's own assets settled it, and while none has,
the first admin to join is asked. Their answer either matches what is stored — in
which case nothing is written and nothing is redrawn — or replaces it. `status`
says which of the two the map is in.

## 0.29.1

**Deploy note:** nothing in the mod changed. The version moves because the two
halves are one release and carry one version — see the service's own 0.29.1, which
is where the change is.

## 0.29.0

**Deploy note:** both halves, upgraded together. No map is cleared and no palette
is thrown away. On the first start the palette is rewritten to say which of its
colourless blocks actually draw, the chunks stored with a hole in them are queued
to be read again as they load, and the server asks the next player for the colours
it is missing. None of it needs a command.

**Digging left holes on the map, and the only way to fill one was to have somebody
send a palette by hand.** Two unrelated faults produced the same picture — the
near-black the map paints ground nobody has ever explored — and neither of them
was about the world having changed. On the test server they accounted for 244
columns across 49 dug pits, every one of them somewhere a player had been working.

**A colour the palette did not have was drawn as absence.** An entry with no colour
meant two different things at once: a block that draws nothing, which is air and
the invisible helpers, and a block whose colour the builder could not work out.
The renderer could not tell them apart, so it painted both as unexplored ground.
Bare soil is in the second group — `soil-*-none` is what grass becomes when it is
dug off and what placed soil starts as — along with forest floor, peat, cob and
raw clay. So the map went black in exactly the places the world had changed. The
palette now records which kind of colourless each entry is, and the map draws a
block waiting on a colour as bare earth, with the slope shading on it like any
other terrain.

**Nothing ever went looking for the missing colour.** A palette was asked for when
it coloured less than 90% of the block registry, and a palette missing bare soil
scored 98.5% — so a server whose map had holes in it was a server that considered
its palette good enough, for ever, and `/witchlight palette` was the only way out.
That question is now asked the other way round: a specific block this palette says
draws something and has no colour for is worth asking about, however good the
number is. Asked on the export beat as well as on join, since a colour the map has
not got is not something a player fixes by rejoining.

**And the asking stops on its own.** Each player is asked once, because one is all
a player has to give — a client sends the whole of what its assets can colour, and
the merge takes everything it could fill. Somebody who joins later has not been
asked and may have the mod set that answers, so the map goes on repairing itself as
people arrive, while a server where nobody can supply the last colour stops asking
instead of going round the room for ever. What is still missing is named in the log
and in `/witchlight status`, which is a report to make to the mod shipping those
blocks rather than a command to run again here.

**A pit deeper than eight blocks was stored as air.** The surface read starts at
the rain height map, which marks where rain stops rather than where the ground is,
and stepped down at most eight blocks looking for something real. A dug shaft is a
column of air below where the sky still says the ground is, so anything deeper than
that ran out of steps and stored air — which is drawn as unexplored ground. It
steps all the way down now. The depth is paid by the columns that need it and no
others: ordinary ground answers on the first or second read.

**A hole already stored is read again.** Storing air is a reading that failed, not
a fact about the world, and once stored nothing would ever have made the server
look at that column again — the chunk was in the export, so loading it did not
count as a change. The walk over the map at start now notes which chunks hold a
column stored as air and leaves them out of what counts as exported, so the
server's own `ChunkDirty` brings them back as they load. It asks this of bytes it
has already decompressed to read the seasons, so a map with no holes in it pays
nothing for the question.

## 0.28.2

**Deploy note:** the mod half only; the service stays at 0.28.1. Nothing is
cleared, but the palette must be **rebuilt** for this to reach a map that already
exists — run `/witchlight palette` and let an admin's client answer. Nothing
happens on its own: the existing palette is complete enough that nobody is asked.

**Dug and placed ground went black.** Bare soil — every `soil-*-none`, which is
what grass becomes when it is dug off and what placed soil starts as — had no
colour in the palette, and a column the palette knows but has no colour for is
painted the same near-black as ground nobody has ever exported. So the map looked
like it had stopped updating in exactly the places the world had changed, when
what it was doing was drawing the new block faithfully in the only colour it had.

**A texture being preferred is not the same as it having a colour in it.** The
builder took the one texture the game would use for a block's map colour and gave
up on the block if it drew nothing. A soil block wears its grass as a coverage
layer and that layer is the preferred texture — correctly, or every meadow would
be the colour of the dirt underneath — but on the grassless variant the layer is
a file with all 2,048 pixels transparent. It is tried in turn now: the coverage
layer, then the top face, then whatever else the block has, then its shape, until
one of them actually draws something. Sixteen of the game's block textures are
fully transparent, so soil was the visible case rather than the only one.

## 0.28.1

**Deploy note:** the mod half only; the service stays at 0.28.0. Nothing is
cleared. A server that already has a good palette and its marker pictures behaves
exactly as before.

**Nobody was ever asked for a palette, on any server.** `OpenTheMap` built the
palette exchange from a field four lines before the method that fills it ran, so
the exchange was built against a null every time and `_palettes?.AskIfNeeded` did
nothing — silently, because of the `?.`. A server whose own assets cannot colour
the map therefore drew nothing for ever: no ask on join, `/witchlight palette`
accepting the command and doing nothing, and `/witchlight status` reporting
`palette: not built yet` with `wanted: nothing, the palette is good enough`. The
value is handed back from the method that settles it now rather than left in a
field, because a value that is returned cannot be read early.

**A server whose admin never joins in game now gets a map.** The palette and the
marker pictures were asked of admins alone, which is the right instinct and the
wrong rule. A dedicated server is run from a console and the person who runs it
may never have a character on it; the role a joining player gets by default is
`suplayer`, which carries no `controlserver`. So a server set up and left to its
players drew almost nothing — near-zero palette coverage — and every marker on the
web map as a plain diamond, with nothing anywhere saying why.

**Anybody may fill in what is missing; only an admin may replace what is there.**
The two cases are not the same risk, which is what the rule turns on: a colour or
a picture laid where there is none can only improve on nothing, and one laid over
what somebody chose is a change to what is already right. An admin's palette is
preferred over what is stored, anybody else's is merged as filler, and a
non-admin's marker pictures are filtered to names the server has no file for.
`/witchlight palette` and `/witchlight icons` are the way back either way.

**The bounds that "only an admin can reach it" used to supply are now written
down.** A palette slice naming a part outside its own total, or claiming more
parts than this server's block registry could produce, ends the whole attempt
rather than that one packet — a client claiming a million parts and streaming them
would otherwise have grown the server until it died. A half-sent palette is
dropped when its sender disconnects, and marker pictures from a non-admin stop at
512 files, which is well past a heavy mod set and well short of a disk.

**A palette that cannot be read now says so.** `Palette.Read` swallowed every
failure and answered null, which the caller cannot tell from "there is no palette
yet" — and the two have opposite consequences: one is a fresh map, the other is a
map's colours being thrown away and replaced by the server's own empty set. The
warning that reports lost colours is guarded on there having been a palette to
lose, so that path said nothing at all. It is the only way a good palette is lost
without the block registry having moved.

**Singleplayer was never affected and still is not.** The game forces a
single-player client to the highest-privilege role on every join, whatever is
stored against that uid, so the player is always an admin there.

## 0.28.0

**Deploy note: both halves.** Minor is the compatibility generation and it moves
together, so the service goes up with the mod. Nothing is cleared and no map is
rebuilt. A **new key** appears in the controls settings — *Create from Witchlight
preset*, bound to `[` — and takes that binding from anything else the operator's
players had put there.

**A marker can be made without leaving the game.** *Create from Witchlight
preset*, or `/wl mark`, marks what a player is looking at the way they have said
that kind of thing is marked: the server reads the block at the spot, asks the map
service for that player's presets, and makes the marker from the one whose pattern
names it. One press, and a line of chat saying what was marked and who can see it.

**Where no preset answers, a window opens with the answer half filled in.** It
carries the game's own colours and pictures, read off the waypoint layer, and the
two things the game has no idea about: who may see the marker, and whether this is
what that block starts as from now on. Both start where that person set them on
the map, so somebody whose new markers are private gets a private one without
saying so again. Saving makes the marker and, where the second is on, keeps the
preset on the map service — which is the first time a preset can be made from
inside the game at all.

**Every decision about a mark is the server's.** It reads the block at the
position itself rather than taking a code from a client, since the code is what
picks the preset and a client that could name the block could apply somebody
else's preset to somebody's own marker.

**`mark` answers to a slash and a dot, and is one behaviour.** Which block
somebody is looking at exists only on their own machine, so the server's copy of
the command asks that machine rather than answering a poorer version of the
question from where the player happens to be standing.

**Markers can be deleted from the map.** The form that changes one now has a bin
beside its Cancel, which asks once and does it on the second press. Only its owner
may: `public_markers_editable` lets somebody correct a marker they can see, which
is not the same permission as taking it off the map of the person who made it, and
that is decided here against the waypoint itself.

**Making sure an owner sees a change has one answer.** Changing a marker took the
waypoint out of the layer's list and put it back, because adding one is what makes
the layer resend a player's set; removing one had no such trick available. Both go
through the call the layer makes when a map view moves, which is what has happened
as far as the client is concerned.

## 0.27.1

**Deploy note:** the mod half only; the service stays at 0.27.0. Nothing is
cleared.

**A player faces the way they are looking rather than the way they came from.**
The game's yaw is measured from south, which is what `BlockFacing.HorizontalFromYaw`
reads it as and what the client's own map turns its marker by. It was converted
here as though it were measured from north, so every player on the map pointed
exactly backwards.

## 0.27.0

**Deploy note: both halves.** Minor is the compatibility generation and it moves
together, so the service goes up with the mod. Nothing is cleared and no map is
rebuilt.

**A player says which way they are looking.** `Facing` travels with each player's
position: degrees clockwise from north, which is a compass bearing and therefore
the angle a north-up map turns a picture by. The game holds a yaw in radians that
counts the other way round, and this is the half that knows that, so the turn
happens here once rather than wherever a map is drawn. The map service draws it
as the game's own player marker — a dot with a cone for the heading.

## 0.26.3

**Deploy note:** nothing to do by hand, nothing is cleared, and nothing behaves
differently. A refactoring pass with no change to what the mod does.

**One owner for what the channel carries.** Both halves registered the same seven
message types in the same order, written out twice. The game numbers them by that
order and matches a packet to a reader by the number, so a list that differed by
one entry would have been every message after it read as the wrong thing — with
nothing anywhere saying so. `Channel` holds the name and the list, and each side
asks for them.

**`Live.cs` was three subjects in the markers folder.** Where a player is
standing, what the world's clock says and what markers exist travelled on the same
beat and had nothing else in common. They are `Players/PlayerFeed`,
`Map/WorldClock` and `Markers/MarkerFeed` now, and the floor that puts a position
on the block grid — which two of them needed — is `Util/Blocks`.

**The greeting and the asset window left the system file**, which was doing five
jobs in seven hundred lines. Both are `partial class WitchlightSystem`, the way
the commands already were: a view of the whole system rather than a type of its
own. `Readback` has its own file, being its own class in a file named for another.

**`world.json` and `service.json` name themselves once**, the way the palette, the
block names, the colour maps and the icons already did. Each had been spelled at
three call sites.

## 0.26.2

**Deploy note:** nothing to do by hand, and nothing is cleared. The service half
is untouched and stays at 0.26.1.

**Fixes 0.26.1, which stopped the greeting reaching anybody at all.** That release
guarded the greeting on the player being in `EnumClientState.Playing`, on the
assumption that a player who is in the world is playing. They are not: that state
is set by a packet the client sends once it has finished loading and, on a first
join, once the character screen closes — which is the same signal `PlayerReady`
waits on, and on a server where that signal does not arrive the guard refused
every player. It refused them before the retry that was meant to cover a slow
start, so nothing was said and nothing was logged.

A player at `PlayerNowPlaying` is `Connected`: in the world, and able to be sent a
line of chat. The greeting now stops only for somebody who has gone offline, which
is the thing that check was ever for.

**The log line says which state the player was in when they were told**, because
one wrong belief about that value is the whole of what this release fixes, and
nothing in a log would have shown it.

## 0.26.1

**Deploy note:** nothing to do by hand, and nothing is cleared. The service half
is untouched and stays at 0.26.1.

**This release did not work — see 0.26.2, which fixes it.** What follows is what
it set out to do, and 0.26.2 does.

**The map's address is said the moment a player is in the world, rather than up
to twenty seconds later.** It was said on `PlayerReady`, with a twenty second
timer behind it in case that event never arrived — and on a server where it does
not arrive, the timer was what said it every time. That timer is gone. The
greeting now goes out on `PlayerNowPlaying`, which is the earliest moment a
player can be sent anything at all.

`PlayerReady` still speaks, but only when the first line cannot have been read.
On a first join the character and class screen is up while `NowPlaying` fires and
a line of chat behind it is a line nobody sees; `PlayerReady` comes after that
screen closes. For everybody else the two arrive in the same breath. The gap
between them is what tells those two cases apart, so nothing has to guess which
kind of join it is looking at.

**A player who joined before the service had published its address is no longer
told nothing at all.** The greeting was marked as given before there was anything
to give, so a join in the second or two between the world coming up and the
service saying where it is listening consumed the one chance and went unanswered
until that player reconnected. A greeting now counts as given only once something
has been said, and is tried again a few seconds later until there is.

**And it says in the log that it said it.** How long a player waits for that line
was the one thing nothing anywhere recorded, which is why this took a session to
find rather than a look at a log.

## 0.26.0

**Deploy note:** both halves go together, as a minor always means. Nothing here
changed — no format, no protocol, no map cleared. The number moves so that this
half stays on the same compatibility generation as the map service, which fixed
two windows that could not be closed and gave the marker list a switch for who
may see each marker. See the [service changelog](../rust/witchlight/CHANGELOG.md).

## 0.25.0

**Deploy note:** both halves go together, as a minor always means, and this one
means it literally — the shape this posts players in changed, so a mismatched pair
shows an empty player list and the service says why in its log. No map is cleared.
An operator who wants the old behaviour has it: `players_public` defaults to on.

**Who is online is now sorted by who may see them**, the way the markers already
were. Where `players_public` is on, everybody goes in one list and nothing else is
worked out. Where it is off, each person gets a list of their own — themselves,
and whoever the game has in a group with them — and the service hands out lists it
never looks into.

Sorted here rather than by the service because this is the half that knows what
groups the game has people in, and a service holding positions it must not send is
one bug away from sending them. How many are online travels with it and is said to
everybody: that is a fact about the server rather than about anybody on it.

Group membership is read off the players who are on, which the game answers for
directly. Somebody reading the map while their own player is offline is therefore
shown what everybody is shown.

**`players_public`, on by default.** Vintage Story has no setting of its own to
follow: its server config says nothing about who may see whom, and `allowMap` in
the world config decides whether there is a map at all, which is a different
question. So this is witchlight's own, and it sits with the other map settings
because it is the map it is about.

## 0.24.0

**Deploy note:** both halves go together, as a minor always means. Nothing here
changed — no format, no protocol, no map cleared. The number moves so that this
half stays on the same compatibility generation as the map service, which grew a
window listing every marker there is and reworked the one that holds the presets.
See the [service changelog](../rust/witchlight/CHANGELOG.md) for what a player
will actually see.

## 0.23.0

**Deploy note:** both halves go together, as a minor always means — this posts
the world's clock on a channel service 0.23.0 is the first to answer. No map is
cleared. An older service refuses the post and says so in the log; nothing else
about the map changes.

**The world's clock is sent, not filed.** The date, the year, the time and the
season now go out with the players every two seconds, on `/live/world`. They were
in `world.json` before, which was wrong twice over: that file is written once when
the world comes up and never again, so the page showed the moment the server
started for as long as it ran — and a clock is the thing a map has least business
writing to a disk, since it is stale before the write has finished.

Each part is worded by the game rather than by the page, because the game holds
the month names and the operator's language. The season is read at spawn: a season
is a fact about a place, and the hemispheres are in opposite ones.

`world.json` keeps what genuinely does not change while a world runs — where it
counts from, what it is called, and where its sea is — and goes back to being
written once, which is what it was built for.

## 0.22.7

**Deploy note:** nothing to do by hand, and nothing is cleared. Pairs with
service 0.22.6, which needs what this adds to draw the right colours.

**`world.json` says where the sea is, and what the date is.** The sea level is
wanted by the renderer: how much of the season's colour a block takes depends on
how far above the sea it stands, which is how the game keeps a mountainside from
turning autumn with the valley. The date and the season are wanted by the page,
which shows them in the corner. The season is read at spawn, because a season is
a fact about a place and the hemispheres are in opposite ones.

They ride in this file rather than on the live channel because it is already
rewritten whenever the world's facts move and already read by the page every
couple of seconds, and because both change slowly.

**The colour maps travel with their borders.** A climate map is a 256 square
drawn inside a 264 one; the border is for the game's texture atlas and is not
part of the lookup. The number is the asset's own and cannot be told from the
picture, so it is written beside them as `colormaps/padding.json` — without it
the service read every climate lookup a few pixels out, worst at the extremes.

## 0.22.6

**Deploy note:** nothing to do by hand, and nothing is cleared. The first export
after this rewrites most regions once, because almost every column's stored
season now rounds to a different value than it did — one full redraw, and then a
great deal fewer of them.

**The map redraws for the season twelve times a year rather than 255.** Where a
chunk sits in the year is stored as one byte, and at full precision that byte
moved 255 times a year. Every step rewrote every region holding a column that
crossed it — ground nobody had been near, redrawn because the year had inched
along. One export seen on a live server rewrote 59 regions for exactly this
reason, which is a whole map repainting while somebody was stood still.

It is rounded to the month now: a dozen steps a year, each landing on a month
boundary, holding the middle of the month it is in so what is drawn is the
month's own colour rather than the colour of the moment it began. The number of
months is asked of the world's calendar rather than assumed, so a world with a
longer month steps on its own months and not on somebody else's twelve.

Nothing stored changes meaning. The byte is still a position in the year from 0
to 255 and the map service still reads it as the coordinate to sample the
season's colours at; it simply takes twelve values now instead of 256. Old maps
are read unchanged, and an older service reads new ones.

## 0.22.5

**Deploy note:** nothing to do by hand, and nothing is cleared.

**Joining players are told where the map is again.** They were being told
nothing: no address and no sign-in link. The greeting hangs off `PlayerReady`,
which the game raises once a player has chosen a character and a class, or at
once for anybody who has played before — the right moment, because a line of
chat behind the character screen is a line nobody reads. On a dedicated server
it was not arriving.

Why it did not arrive is not established. What is: the address was readable at
the time, the settings asked for the greeting, nothing was thrown — the failure
path logs — and the handler on `PlayerNowPlaying` ran for the same player eight
seconds later, so the mod was alive and listening. That leaves the event itself.

Rather than guess at it, the greeting is now said from whichever of two events
gets there first. `PlayerReady` still goes first, because when it works it works
at the best possible moment. `PlayerNowPlaying`, which does arrive, now starts a
twenty second timer behind it. A player is greeted once whichever way it lands,
and once more if they rejoin.

**And a greeting that cannot be given now says so.** The one silent failure left
was a server with `announce` on, no `announce_url`, and no address from the
service: every join said nothing, and nothing anywhere recorded that anything
had been meant to happen. That is a warning in the log now, once per start,
naming the file it wanted and the file to set instead.

## 0.22.4

**Deploy note:** nothing to do by hand, and nothing is cleared. The surface is
now exported every ten seconds rather than every thirty, which writes region
files about three times as often while somebody is exploring. A server keeping
its map on a disk it is careful with should know that; nothing else changes.

**Ground nobody has been to appears about three times sooner.** The export timer
was most of the wait. Everything after it — the map service noticing the write,
building the levels above, the page asking what changed — comes to around three
seconds together, against up to thirty spent waiting for this. It is ten now.

It costs the server essentially nothing extra, because the work is per column
rather than per export: the same ground is read either way, in smaller pieces.
Those pieces are the point. An export is timed, and thirty seconds of somebody
exploring gathered 632 columns into a single 380ms pass on the server's own tick
— a tick is 33ms, so that is a visible stutter. The same ground taken in ten
second pieces is three passes of about a hundred milliseconds.

What it does cost is disk. A region file is rewritten whole however few of its
columns moved, so a region under somebody walking through it is written three
times where it used to be written once. That is the trade, and the reason the
number is not lower still.

## 0.22.3

**Deploy note:** nothing to do by hand, and nothing is cleared. No behaviour
changed — this release only moves files.

**The source is in folders.** Forty-three files sat in one directory, which is a
list you read rather than a shape you understand. They are now grouped by subject:
`Mod/`, `Map/`, `Palette/`, `Markers/`, `Portraits/`, `Icons/`, `Network/`,
`Service/`, `Util/`. Nothing was renamed except `Shared.cs`, which held the marker
wire types and is now `Network/MarkerShare.cs` beside its three siblings. No type,
namespace, or line of logic changed, and the project file needed no edit.

The three `*Exchange.cs` drivers went to their subjects rather than to `Network/`.
`Network/` is what crosses the wire; who asks for it and what is done with the
answer belongs with the palette, the icons, or the portrait.

## 0.22.2

**Deploy note:** nothing to do by hand, and nothing is cleared.

**A joining player is signed in without having to know a command.** The greeting
now carries a link that opens the map as them, which is the same link
`/witchlight login` hands out — good for one press, for ten minutes. Nobody has to
be told to type anything. It is offered to whoever could have typed the command
and only where there is a service to mint one; a link that could not be minted is
passed over in silence, since somebody who merely joined a server never asked.

The greeting also moved to `PlayerReady` from the join event. On a first join the
character and class screen is still up when a player starts playing, and a line of
chat behind it is a line nobody reads.

**The address is a link.** It was plain text somebody had to retype into a
browser. Both it and the signed-in link are pressable now — except an
`announce_url` with no scheme, which is said as words: the game makes a link only
out of an address beginning `http`, and a press that goes nowhere is worse than
text that can be copied.

The command and the greeting now ask for a link through one function rather than
two copies of the round trip, so what a link is and how one is asked for cannot
drift between them. Only what is said about it differs.

## 0.22.1

**Deploy note:** nothing to do by hand, and nothing is cleared. The square at
spawn fills in on the next start.

**The chunks around spawn were never on the map.** A 7x7 square of them, centred
exactly on spawn, black on a map that was correct in every direction around it —
and no amount of walking through it made any difference.

The server loads that square while it is starting, and it does so before the mod
has a directory to write into, so the one ChunkDirty each of those columns raises
arrives with nothing listening. The server then holds them for as long as it runs,
so none of them ever raises another. Seeding did not help either: a request for a
column already in memory is answered from memory and queues nothing, so it asked
for all 289 columns around spawn and the 49 that most needed reading were the ones
it skipped.

The export now notes what is already in memory at the moment it starts watching,
for anything not already on disk — a column in memory that has never been read is
a column to read. A chunk merely coming back into memory is still not a change, so
a player walking a circuit still re-reads nothing.

This was not new. It has been true since the export was written, and a dedicated
server hides it: any block placed near spawn dirties those columns and fills the
hole in. A world whose map starts empty has nothing to hide it, which is what
giving each world its own directory did.

## 0.22.0

**Deploy note:** deploy both halves together. The settings file gains `map_data`
and `per_world`, and a 0.21 map service refuses a file naming settings it has never
heard of — this one reads a 0.21 file perfectly well, so the order that fails is an
old service against new settings. **Nothing changes for a dedicated server:**
`per_world` is off there, the map stays exactly where it is, and no map is cleared.
In singleplayer it is on, and the first start after upgrading **moves** the map
already in `<data path>/witchlight` down into a directory of its own rather than
leaving it to be written over. A settings file written before this release says
nothing about `per_world`, and that silence reads as the answer for the side asking
rather than as "off".

**Each world keeps its own map.** A dedicated server runs one world out of one data
path, so its map has always sat in one folder. A client runs every save it has out
of the same data path, and one folder for all of them meant the second world wrote
its terrain into the first world's map at the same region coordinates — a map of
two worlds at once, with nothing anywhere saying so.

The directory is named for the world: its own name, so a listing reads as a list of
worlds, and eight characters of its savegame identifier, because two saves called
"New World" are not rare and one directory between them is the whole failure this
prevents. A world made by a build too old to carry an identifier is filed under its
name and seed instead, which are as fixed as it is.

Nothing is shared between them, including what happens to be identical. A palette
written once and then left alone costs nothing to keep; one rewritten on every
switch between a world with no mods and a world with fifty costs a disk.

**A map found loose in the folder is moved, never written over.** Turning the
setting on moves what is already there down into a directory before anything else
runs — this world's, where `world.json` says the map is this world's, and one named
after whoever it does belong to otherwise. A folder that already holds this world's
directory is left alone and said so, since a move onto it would be the merge this
exists to prevent.

**`map_data` says where maps are kept**, for a larger disk or a directory a web
server already serves. Empty is `<data path>/witchlight`, which is where it has
always been.

**The palette is written when the world is up rather than when the assets load.**
It is still *built* at asset load, because the server frees block textures straight
afterwards and there is no second chance — but which directory it belongs in is not
known until the world can be asked its name. The colour maps, the marker pictures
and the block names come out of assets the server keeps, so they moved with it; the
only reason they were up there was that the palette had to be.

`world.json` now carries the savegame identifier as well as the world's name, so a
map can be matched to the world that wrote it rather than to the world that
happens to be starting. `/witchlight status` says which layout is in use.

## 0.21.1

**Deploy note:** nothing to do by hand. No format change and no map is cleared.

**Seeding a fresh map took the server down in singleplayer.** The map is filled in
around spawn at startup so that a server nobody has walked yet still has a map,
and it asked for that whole square in one call. `LoadChunkColumnPriority` is
documented as asynchronous for a rectangle and is not: the server queues the
rectangle and its chunk thread drains it with a blocking area load, holding that
thread until every column is generated or twelve seconds have gone.

On a dedicated server that is nobody's problem — the seed runs at startup with
the queue empty. In singleplayer the player joins in the same tick, with the
view distance the game raises to 1152 blocks because it is singleplayer, and the
thousands of columns they ask for pile up behind the seed. The request queue
holds two thousand. Once past that the server clears the queue out from under its
own chunk thread and dies with `In queue but missed from index!`, nine seconds
into the world.

The seed now asks for four columns every quarter second, which is four short
loads with the thread free between them rather than one nine-second hold. It also
works outward from spawn in rings rather than in rows, so a map filling in grows
from the middle and one cut short by a shutdown is still centred; and it writes
the map as it goes, because a column nobody is standing near is freed again after
fifteen seconds and a single export at the end would find the earliest ones gone.

Columns already in memory cost nothing — the server answers those without
queueing anything at all, which in singleplayer is most of them, since the
player's own view distance covers this square several times over.

**The seed could also silently never run.** It hung off a ten-second timer
started when the mod started, while the exporter it writes through is not built
until the world is ready. Whichever arrived first decided whether a fresh server
got a map, and nothing said which had happened. It now starts where the exporter
is built. `/witchlight status` says how far a running seed has got.

## 0.21.0

**Deploy note:** nothing to do by hand. The map format has not changed and no map
is cleared; the minor moves because both halves were reworked together and should
be deployed together.

**Marker visibility could erase itself.** Every world save handed the store the
live waypoint list so that decisions about deleted markers went with them — and
where the waypoint layer could not be read, it was handed an empty list instead,
which reads as "every marker has been deleted". One save landing in that state
took every private marker on the server back to whatever the operator's default
says. It is told nothing at all now when the list cannot be read, and forgets
nothing.

**Every file is written whole.** `palette.json` is the better part of a megabyte
and was written straight over the top of itself, so the map service — which
watches it every second — could read half of one. It then recorded the file as
seen and moved on, and on a settled server nothing would ever move that timestamp
again, so the colours stayed missing until a mod set changed. Every write now
goes beside the file and is renamed into place, and every write is still skipped
where the bytes would not differ.

**One place decides whether an undecided marker is private.** Three separate
places each negated `markers_public` for themselves — the web feed, the in-game
share, and the collector that applies web edits — which is three chances to get
the polarity backwards and show somebody's markers to a server. `Settings.cs` now
owns every question about what the operator wants, and every caller is a thin
read of it.

**The status line says what the palette should be as well as what it is.** A
palette built for a different block registry now names both fingerprints and says
STALE, rather than printing one number and leaving the reader to compare it with
another line. Icon and colour-map exports report how many exist as well as how
many were written, so "0 written" on a restart no longer reads like a server that
found none.

**The two big files are gone.** `WitchlightSystem.cs` was 1,384 lines doing six
jobs; it is 400 doing one — wiring — with the export, the three client exchanges,
the command surface and the fault reporter each in a type of its own.
`WitchlightClient.cs` split the same way. `Palette.cs` became a palette, a
builder, a texture-colour utility, a shape reader and a colour-map export.
`Wanted` and `Edit` were the same nine fields written twice and are one type. The
region header was checked in three places with the same six comparisons and is
checked in one.

**Smaller things.** A stray platform warning is gone, the log's running-average
code no longer passes four numbers by reference, four doc comments that had
drifted onto the wrong method are back where they belong, and `README.md` has a
table saying which file holds what.

## 0.20.2

Viewer tweaks; the mod is unchanged and the version moves only to keep the halves
reporting the same number.

## 0.20.1

A viewer tweak; the mod is unchanged and the version moves only to keep the
halves reporting the same number.

## 0.20.0

Viewer only: settings that persist, a grey measured palette, and a clearer marker
box. The mod is unchanged and the version moves only to keep the halves reporting
the same number.

## 0.19.3

Viewer fixes and preset creation; the mod is unchanged and the version moves only
to keep the halves reporting the same number.

## 0.19.2

Window fixes in the viewer; the mod is unchanged and the version moves only to
keep the halves reporting the same number.

## 0.19.1

**Shared markers stay on the in-game map, and edits reach it.** They were added
once each and never touched again, which was wrong twice: a marker somebody edited
kept whatever it said when it first arrived, and the whole set vanished the first
time anybody closed their world map — the game drops every temporary waypoint on
close, and nothing put them back. The client now holds what the server sent and
lays the set down again when it changes or when the map has been emptied under it.
No reconnect, and nothing to do by hand.

## 0.19.0

**Deploy both halves together.** The mod collects markers in a new shape — makes
and changes rather than makes alone — and posts a block name table the service
serves from.

**Markers can be edited.** Right click one on the web map to open it in the form.
Its owner always may; `markers_public_editable`, new and off, lets anybody correct
a marker anybody can see. Whoever asks, the mod decides again against the waypoint
itself before anything moves.

**Marker presets.** Right click a block and the form starts as that block: named
what the game calls it, and — once you have saved a preset for it — coloured,
pictured and shared the way you last chose. "Set as preset" on the form keeps one;
the presets window edits and deletes them. A pattern may use `*`, so one preset
saved on basalt copper ore can be widened to `game:ore-*-nativecopper-*` and
cover every rock it appears in.

**A settings window of your own**, behind your name in the corner. It holds
whether new markers become presets, whether they are private — over the server
default, either way — and three size sliders for the player list, the windows and
the map buttons. What is about you follows your account to any browser; what is
about the screen stays in it.

**The map pans past what has been drawn**, by a world's width on every side,
rather than pinning the edge of the export to the edge of the screen.

**The settings button has moved to the top left**, beside your name.

## 0.18.1

The marker form is a movable window now; the mod is unchanged and the version
moves only to keep the halves reporting the same number.

## 0.18.0

**Deploy both halves together.** The mod posts markers in a new shape — sorted by
who may see them — and the service does not read the old one. A service on 0.17
paired with a mod on 0.18 shows an empty map, and the reverse shows one that never
updates.

**Markers can be made from the web map.** Right click the map for a form on the
left, or press the flag in the corner and type the coordinates — or press ⌖ and
click the spot. The form offers the same colours and the same pictures the game's
own waypoint dialog does, read off the game rather than written down, so a mod
that adds either adds it here. Making one needs a login link; the flag appears
once you have followed one.

**A marker can be kept to its owner.** The box beside Save decides, and it starts
where `markers_public` puts it. That setting now means what it always said it
meant — the default for a marker nobody has decided about — and a choice made on
the form overrides it both ways, on the web map and on other players' in-game maps
alike.

**Markers a viewer may not see no longer reach their browser.** The web map used
to send every marker to everybody regardless of the setting. With `markers_public`
off, which is the default, markers made in game are now their owner's alone there
too, and a map that showed everything will show less until their owners share them.

## 0.17.1

**Marker colours match the game again.** A marker read the game's packed colour
back with red and blue exchanged, so a red marker reached the browser blue and a
blue one red. Grey and green were unaffected, which is why the map looked right
until it did not.

## 0.17.0

**Deploy both halves together.** The mod and the service no longer speak the
protocol they did before this, and neither reads what the other minor wrote.

**A settings file older than this stops the service**, which says which name
replaced which: `api_socket` is now `api_bind`, an address rather than a unix
socket path. Leave it empty for loopback on a free port, which is where the mod
now looks.

**Markers are private now.** Every marker used to reach every player's in-game
map; `markers_public` decides that, and it is off. Until a player can mark a
single waypoint as shared, off means nothing is shared in game at all — a server
that wants what it had sets `markers_public = true` for now.

The map is **not** cleared: the region format is untouched.

### A palette is asked for and written only when something changed

Every admin joining used to be asked for a palette, and every answer rewrote
`palette.json` — which the map service watches, so it dropped every tile and
redrew the stored zoom levels. Two admins on a server meant the map blanked twice
over for two palettes identical to the one already on disk.

Two things were wrong. A moved mod stamp forced the ask, and witchlight ships no
block textures — so its own releases moved the stamp and every admin was asked for
a palette that was already correct. Coverage alone decides now: a palette that
colours what there is to colour is good, and a version number says nothing about
that. A texture changing under an unmoved block id is the case the server cannot
see, and it is the case `/witchlight palette` exists for; asking on suspicion
costs a blank map every time, asking by hand costs a command on the rare occasion
it is true.

And a palette is written only when it differs from what is on disk. The saving is
not the megabyte — it is that a file nobody changed no longer costs a redraw of
the whole map.

### A player can log in to the map

`/witchlight login` sends you a link, privately, that logs your browser in as you.
It is the only subcommand that is not an operator's: it acts on nothing but its
own caller, so it wants `chat` and a player, not `controlserver`.

Identity exists in one place, which is the game. The mod asks the service for a
word on the private API channel — the only listener it can reach, and the only
place that knows which uid belongs to which player — and hands it over in chat.
The link is good for ten minutes and works once; what it buys is a session in a
cookie, so the address stays shareable.

Nothing is gated behind it yet. The map is public and stays public; a session
decides only whose settings and whose markers a page may act on, once there are
any.

### Markers are private unless the operator says otherwise

`markers_public` in the map's settings, off by default. A marker a player drops is
theirs; before this, every marker on the server went to every player's in-game map
whether or not its owner meant it to.

**Off means nothing is shared in game at all**, because there is nowhere yet for a
player to mark one waypoint as shared — that is the marker work this is the
groundwork for. A server that wants what it had before sets `markers_public = true`
until the per-marker choice lands.

The reader for these settings now takes the default at the call rather than baking
one in. They do not all lean the same way — a map runs and announces itself unless
told not to, and shares nobody's markers unless told to — and one reader assuming
the first would have made the third quietly wrong.

### A palette is never thrown away for a moved mod

A stored palette was kept only if the block registry matched **and** the mod set
had not moved. When either failed it was not set aside — it was overwritten, by
the one this server can build for itself, which on a dedicated server has no
colours at all. A map with every colour lost them on the next restart and drew
nothing above its stored zoom levels until an admin happened to join.

The two conditions were never the same question. A colour is keyed on a block id,
so the fingerprint — the block registry and nothing else — is the whole of whether
stored colours still mean anything. A moved mod stamp means some mod's textures
may have changed underneath colours that are still keyed correctly: stale, not
wrong. Stale colours beat none, so they are kept and an admin is asked for a fresh
set instead. The written palette carries the current stamp, so one move of the mod
set costs one ask rather than an ask on every start after it.

Where the registry really has changed the stored colours are genuinely invalid and
are still replaced — but that is now said out loud, with both counts, because it
is the one case where the map goes flat until somebody joins to fix it.

### Live data goes to loopback, not a unix socket

The mod posted players and markers to a unix socket in `/tmp` whose name both
sides derived from the export path. Rust has no unix sockets on Windows, and a
Vintage Story server runs there, so the transport went rather than the shape.

The service now listens on `127.0.0.1` on a port the machine picks, and publishes
that port with a token in `api.json` beside the map. The mod reads it and sends
`Authorization: Bearer {Token}` with every post. Both halves already had the plain
TCP path; what is gone is the socket-only one, so what Linux runs is now what
Windows would run.

Where the service is is read again whenever a post fails or comes back `401`,
because the port moves with every service start — and because the mod is usually
the thing that started it, and looks before the file is written. That first miss
is one warning and one tick, and then the map has data.

`api.json` is removed along with `service.json` when the mod stops the service. A
published address that has gone merely fails; a published port that something else
has taken answers, and there is no telling what.

Set `WITCHLIGHT_API_BIND` and `WITCHLIGHT_API_TOKEN` — replacing
`WITCHLIGHT_API_SOCKET` — only for a service on another machine, and set
`api_bind` and `api_token` to match on it.

### A new portrait shows without a reload

A player who sends a new picture keeps the name they had — it is derived from who
they are, not from what the picture holds — so nothing downstream could tell that
anything had happened. The card compared one name against the same name, decided
nothing had changed, and left the picture alone; the browser, handed an address it
had seen before, was entitled to do the same. The map went on showing the old face
until somebody reloaded the page.

A live player now carries `PortraitAt`, the time the stored file was written, read
in the same look that decides whether there is a picture at all. The map asks for
`/portraits/{name}.png?v={PortraitAt}`, so a redrawn player changes the address
rather than only the file behind it.

The time moves only when bytes were actually written, so a character taken apart
and put back the same way costs no refetch.

### The project is called Witchlight

Every mapstique in the mod and the map service is now witchlight — the namespace,
the assembly, the mod id, the network channel, the log prefix, the commands, and
the names things are called on disk. Nothing about how either half works changed,
which is why the version did not move.

What is on disk moved with it: the export folder beside the save is `witchlight`,
the settings are `ModConfig/witchlight.conf`, the service logs to
`Logs/witchlight-service.log`, the socket is `/tmp/witchlight-*.sock` and the
variable that moves it is `WITCHLIGHT_API_SOCKET`. **The map rebuilds**, since none
of the old files are read under their old names, and the old `mapstique` folder,
config and `mapstique.zip` want removing by hand — the mod id changed, so the game
sees two different mods and will load both.

The region format is untouched. `MSQR` and `.msqr` never carried the old name and
changing them would be a format change rather than a rename, which is the one thing
this was not.

### /wl

The command tree answers to `wl` as well as `witchlight`, on both sides. Claimed
only where nothing already answers to it: the game's `WithAlias` writes into the
command table without looking first, so taking two letters another mod holds would
break that mod silently and leave nothing anywhere saying where its command went.
Where the short name is taken the long one is registered alone and the log says so,
which costs nothing — every piece of documentation gives the long name.

## 0.16.1

### Everyone is drawn, and drawn again when they change

Every player is asked for a picture when they join, rather than only an admin
typing a command. A portrait decides what one card looks like and that card is the
sender's own, so there is nothing in it to be trusted with — the rule that guards
the palette and the marker pictures was guarding the wrong thing here. Asked on
every join and not once ever: a seraph that changed while its player was away is a
picture that is now wrong, and only the machine that can see it knows.

A client also watches its own character and sends a new picture **thirty seconds
after it last changed**. A change is not a signal to send — it is a signal to
start waiting again — so somebody trying on six hats sends one picture rather than
six, and a whole afternoon at the dressing table costs one. What counts as a change
is the character inventory, which is every piece of clothing and armour worn, and
the skin configuration. Neither the hotbar nor the backpack: a portrait is cut to
the head and shoulders and what is carried never appears in it.

The ask waits eight seconds after a join. A client at the moment the server calls
it playing is still finding its feet, and a seraph that is not loaded yet renders
as a picture of nothing which the client then reports it could not draw.

Nothing unprompted reaches a player's screen any more. A portrait that could not be
drawn is said in chat only to somebody who asked for one, and to the log otherwise.

`.witchlight portrait` is now **once every five minutes**, and refused before
anything is drawn rather than after. Nothing about the map being current rests on
anybody typing it — the ask on join and the wait after a change cover that between
them — so the command is for wanting a picture sooner than that, and wanting one
twice over is wanting it once. The five minutes are counted from a picture that
actually went, so an attempt that drew nothing may be made again straight away.

### Fewer writes, and a floor under them

A picture identical to the one already stored is not written again. A character can
be taken apart and put back exactly as it was, and what comes back is the file that
is already there.

A picture a client sends **unasked** is taken no closer together than twenty-five
seconds. Opening a write to every client rather than to a handful of admins is what
makes that worth saying out loud: how large a portrait may be was already answered
where it is filed, and how often one may arrive is now answered beside it.

The floor is derived from the quiet period rather than picked next to it — the
fastest an honest client sends on its own is one settle, so anything at or above
that would start refusing honest pictures. Both numbers live in `Portraits` for
that reason: they are a pair, and a pair kept in two files drifts apart the first
time either is tuned.

**What the server asked for is never counted against the floor.** The server knows
how often it asks, one ask buys one picture, and nothing but an ask can put a name
in that table. Without this a player who joined and then typed the command a second
later would watch their own screen say the picture was sent while it went into a
log line instead — the join ask and the command are both legitimate, and they can
land as close together as a person can type.

## 0.16.0

### The portrait is a face, and the old one is gone

A portrait is cut to the head now. Three tenths of the figure measured down from
the crown, squared, and centred on the head's own columns rather than the figure's
— a seraph in a coat is wider at the shoulder than at the ear, and centring on all
of it walks the face off to one side. The fraction was chosen by cutting a real
seraph at several and looking at the results.

Everything that drew the old face is removed: `SkinColors`, the two packets that
carried the table, the `colors` command on both sides, the appearance fields on a
live player, and the sampling that filled them. A player with no picture shows
their initial.

That whole mechanism was a good answer to a question that turned out to have a
better one. It read the names of the skin parts a player wore, asked an admin's
client what colour each name was, and drew a face from three of them. It worked,
and it looked like what it was.

### Fixed

The canvas reported which edge a figure ran off, and named the wrong one. Row zero
is the top of the picture, since the rows are read back in the order they are kept
and never reversed — so a figure against the last row is cut off at its feet, not
its crown. It said the opposite, which is the sort of thing that sends the next fix
in exactly the wrong direction.

The canvas is larger and the seraph stands further down it, so a whole one fits
with room to spare.

## 0.15.8

### The seraph looks straight at you, level, in the middle

Following the equipment screen's render all the way down settled what was styling
and what was necessary. `RenderEntityToGui` hands off to the entity's own renderer,
whose `loadModelMatrixForGui` turns the model half a turn about X itself — so the
orientation was never this half's to arrange. What that screen adds on top is a
fourteen degree downward tilt and three tenths of a radian of extra turn, which is
what gives it that regarded, three-quarter look.

Both are for a screen somebody is looking at. A picture a face gets cut out of
wants to be square on and level, so this now takes the quarter turn without the
flourish and leaves the tilt off.

The figure is centred again. The square being cut was clamped back inside the
canvas whenever it reached an edge, which kept every pixel of it real and shoved
the seraph off to one side. It may now run off the canvas, and what lies outside is
left transparent — which is exactly what belongs around a portrait.

The light is set to the value the game itself rests it at, which is both a sensible
light and the right thing to put back afterwards.

## 0.15.7

### The seraph faces forward, stands upright, and is drawn whole

Three things were wrong with the first picture that came out.

It was upside down. This reversed the rows on the way out, on the reasoning that a
frame buffer's first row is its bottom one — a convention that is usually true and
was not true here. The test that was supposed to catch it painted a head at the top
and checked one came back at the top, which held whether or not the rows were
reversed, because the assertion was built from the same assumption as the code.
Rows are now kept as they come, and the test checks that instead.

It stood in profile. `RenderEntityToGui` takes a yaw in radians and was being given
zero. The character screen passes a quarter turn back from side on plus a little,
which is what puts a seraph face to the viewer with enough of a turn to read as a
person rather than a diagram. That is what this passes now.

It was cropped to where a head ought to be. The whole figure is kept instead,
squared so nothing is stretched and with a little air around it, at 256 pixels
rather than 128. Deciding where a face is belongs to a picture of a whole seraph
rather than to arithmetic about one.

## 0.15.6

### Fixed

The portrait was a sliver of a seraph's left edge. Thirteen pixels of one.

A seraph does not appear where it is placed. Measured, it lands a good hundred and
twenty pixels to the right of the position given, so putting it at the middle of a
256 pixel canvas put nearly all of it past the right edge, and what survived to be
cropped was the strip that happened to fall inside.

Rather than work out that relationship exactly and depend on it, the canvas is now
four times the area and the figure is placed near its left, with room to land
wherever it actually lands. The picture is cut from wherever that turns out to be,
which is what the framing has always done — it was only ever given too small a
place to do it in.

A figure that runs off the canvas now says which edge it ran off. The difference
between a small seraph and a piece of a large one is invisible in the picture and
decides entirely where to look next.

## 0.15.5

### Fixed

The portrait was drawn outside the picture rather than not drawn at all.

`OrthoMode` takes a flag that chooses between two orthographic depth ranges, and
the shallow one is meant for flat GUI work at the very front. A seraph stands four
hundred units back, so with that flag the whole of it fell beyond the far plane and
was clipped — which arrives as an empty canvas with nothing to report, the same as
never having tried. The game passes the other value everywhere it draws something
solid off screen, and so does this now.

`OrthoMode` also pushes both matrix stacks, so its partner is `PerspectiveMode` and
not a second call to itself. Pairing it with itself leaked two stack entries every
time a picture was taken, and put the projection back by guessing at it rather than
by restoring what was there.

The seraph is placed with the character screen's own figures instead of ones
invented here. Where one lands for a given position and size is not worth deriving
twice.

A picture that cannot be drawn now says so to the player whose client drew it, even
when the server asked rather than the player. A server that asks and hears nothing
cannot tell a refusal from a slow answer.

## 0.15.4

### Fixed

Drawing a portrait ended the client: `Can't set uniform on not active shader gui!`,
thrown by the game's own aim renderer a moment later.

Rendering happens inside a frame the game is already drawing, and this had
activated the GUI shader and then stopped it — leaving the rest of that frame
writing to a shader that was no longer in use. The character screen, which draws
the same seraph the same way, never activates anything: it writes to whatever is
already in use. This now does the same, borrows activation only when there is
nothing to borrow, and puts it back exactly as found whether the render succeeds
or throws.

It also set depth, culling and blending, none of which the character screen sets
and one of which it restored to the wrong value. Every piece of state set inside
somebody else's frame is a piece that has to be put back exactly right, so it now
sets none of them and leaves that to the call that knows what it needs.

## 0.15.3

### Fixed

A portrait came back empty. Every pixel of it was the colour the buffer had been
cleared to, and it was sent anyway.

Drawing a seraph flat takes more than a framebuffer, and the character screen —
the one place in the game that does it — says what: the GUI shader has to be the
one in use, the model view matrix has to be pushed and tilted back fourteen
degrees, and `lightPosition` has to point somewhere. Without them nothing is
drawn, and nothing is reported either. This now does all three, the way that
screen does.

Where the seraph lands is no longer guessed at. It is drawn into a canvas with
room to spare, and the picture is cut from wherever it actually appeared rather
than from where it was expected to. A canvas with nothing in it is refused and
says so, instead of travelling to the server as a hundred and fifty bytes of
transparent nothing.

Sizes are reported in bytes. "0 KiB" is what an empty picture rounds to, and it
reads as a unit being silly rather than as a render that failed.

## 0.15.2

### Fixed

`/witchlight portrait` did not exist. The command was registered on the client, and
the game gives client commands a dot rather than a slash — so the one that was
added answered to `.witchlight portrait`, while `/witchlight portrait` went to the
server, which had no such subcommand.

The server has one now, shaped like every other thing it asks a client for: it
sends a request, the client draws, and the picture comes back. `.witchlight
portrait` still sends one unprompted.

The same split has always applied to `palette`, `icons` and `colors`, and was
never written down. It is now.

## 0.15.1

### Fixed

A player joining could take the server down with them.

Reading which skin parts a player has applied goes through `Entity.GetBehavior`,
which reads straight through the entity's `SidedProperties` without checking it —
and an entity has none until it has finished spawning. A player is at their least
ready in the moment they join, which is exactly when the map first asks about
them. The answer was a null reference, and how often it landed in that window was
a race the map lost this time and had won before.

That was a bug. What made it an outage is that the server records a tick listener
as having run only after its handler returns, so a handler that throws leaves the
listener permanently due and it fires again on the very next pass of the loop.
One unready entity became a hundred thousand identical errors in four seconds,
which is the server's own error threshold, and it shut itself down and took every
connected player with it.

Both halves are fixed. The entity is checked for being ready rather than merely
existing, and nothing this mod hands the server can throw any more: every tick,
every callback and every event handler it registers now keeps its failures to
itself. Each kind is reported once and then held quiet — the hundredth copy of a
stack trace says nothing the first did not, and burying the log is its own kind of
outage.

Nothing this mod does is worth a server.

## 0.15.0

### A player can send the map a picture of themselves

`/witchlight portrait [player]` asks a player's client to draw them. It renders the
seraph the way the character screen does — into a buffer of its own rather than
onto the screen — and sends the result to the server as a PNG. `.witchlight
portrait` on a client does the same unprompted; the game gives client commands a
dot and server commands a slash, and they are separate registries. What arrives is the real thing: skin, hair, clothes, armour, whatever
you are wearing at that moment.

Only your own client can do it. Nobody else's machine has your seraph loaded,
which is why the picture travels rather than a description of it. The server files
it under a name derived from your uid, because a uid is base64 and carries `/` and
`+` — a path, not a filename — and hands that name to the map.

Nothing is written that has not been checked: the bytes come off the network, they
are refused unless they are a PNG under half a megabyte, and a server that writes
whatever it is handed under a name it will later serve is a server that hosts
whatever it is handed.

The game's mod API will upload a texture and render into one but never hand the
result back, so reading the picture off the graphics card uses the same OpenGL
binding the game itself uses for this. Every mention of it stays inside a method
body, so a dedicated server — which ships no such library — never looks for one.

### Admins only, for now

Every `/witchlight` command already required `controlserver`; subcommands inherit it
from the root, which is how the game's command tree resolves a privilege. The new
one is no different, and the client half now says so rather than letting a player
run a command whose result the server will quietly refuse.

Skin colours arriving from a player who is not an admin were dropped in silence
while every sibling said so in the log. They now say so too.

## 0.14.0

### The server log says where the map is

`[witchlight] the map is being served at http://192.168.1.145:8080` now appears in
the server's own log, beside everything else an operator is already reading,
rather than only in the service's. The service publishes the addresses it answers
on as it binds and this waits for that, because which addresses a bind of
`0.0.0.0` actually covers is the service's question and it has already answered it.

### And so does a player joining

A player is told where the map is as they join. `announce` in
`ModConfig/witchlight.conf` turns it off, and `announce_url` says what to tell them
when it is not simply where the service is listening — a server on the open
internet is reached at a name, through a proxy, on a port the service never sees,
and only an operator knows that address. Both are read at each join, so turning
the message off takes effect on the next one rather than on the next restart.

Nothing is said when there is no address to give: a service that is not running,
one somebody else runs somewhere this cannot see, or a server whose real address
its operator has not said. The published address is cleared when the mod stops the
service, so nothing hands a player the address of a map that is gone.

**Both halves must be upgraded together.** The settings file gained two keys, and
a 0.13 service refuses a file it does not recognise every field of. An existing
file is brought up to date, values kept, with `witchlight -c <file> --save-config -p`.

## 0.13.0

### This starts the map service

The service binary rides along in this archive and is started once the world is
ready, so installing the mod installs the map. It is still a separate program —
this half knows the game, that half knows pixels, and a Vintage Story update can
only break this one — but it is no longer a second thing to fetch, configure and
remember to start.

`ModConfig/witchlight.conf` holds its settings, written by the service itself on a
first run so that the file's format keeps the one owner it always had. Every
option is live and editable there, including the new `autostart`: turn it off to
run `witchlight serve` yourself, which is what a map that should stay up while the
game server is down wants.

Everything the service prints goes to `Logs/witchlight-service.log`, on its own so
it can be tailed while it runs without a game server's log interleaved through it.

`/witchlight service` says whether it is up; `start` and `stop` do what they say,
and `start` runs it whatever `autostart` says, because somebody typing the command
has asked. It is stopped with the game server, which is safe to do outright:
everything it writes is put beside itself and renamed into place.

A service that stops on its own is reported and left stopped. One that will not
start — a port already taken, settings it cannot read — fails the same way every
time, and a restart loop turns one legible error into a log nobody can read.

The archive carries a Linux x64 build. On any other machine the mod says so and
carries on exporting, and a service run by hand serves the map as before.

`./package.sh --target client` builds the same mod without it, named
`<modid>_<version>_client.zip` so both archives can sit in `dist/` at once. The
assembly is identical — the mod already decides for itself which half to start —
so this only spares a client a megabyte it would never run.

**Both halves must be upgraded together.** The settings file gained a key, and a
0.12 service refuses a file it does not recognise every field of.

## 0.12.1

### Fixed

`world.json` was never written, so the map counted from absolute zero rather than
from spawn and every coordinate it showed disagreed with the one on the player's
own screen by half a million blocks.

Spawn was asked for while the mod was still starting, which is before the world
has one. The exception was caught and logged as a warning and the file was simply
not written, so the only sign was a line in the server log and a map whose numbers
looked plausible but were wrong.

Where the world counts from now has one owner, asked once the world is ready and
again on every export until it answers. Seeding the map around spawn goes through
the same function — it wanted the same answer, had its own copy of the same
question, and silently used the origin whenever the answer was missing, so a
server could seed its map half a million blocks from anywhere anyone lives.

There are two spawn points in the API and they answer different questions. The
world manager's is the one an admin set explicitly, which on most worlds is
nothing at all, and its getter reads through that nothing rather than returning
it. The world accessor's is the one the world actually counts from, and it is the
one every coordinate a player reads is relative to.

Nothing is written when spawn cannot be read. A file saying spawn is the origin
reads exactly like a world whose spawn is the origin, while no file at all is a
state the map service names out loud on start.

### The version now tracks the map service again

This is numbered 0.12.1 to match [witchlight 0.12.1](../rust/witchlight). The skin
colour work shipped here as 0.11.0 and there as 0.12.0, which left the two halves
disagreeing about their own compatibility generation — the one thing the version
is for.

## 0.11.0

### A player's appearance travels as names, and the colours come from a client

The colour of a skin part is not written down anywhere the server can see. The
definitions give each variant a texture and no colour at all, and the textures are
images a dedicated server does not ship — the same gap that leaves it unable to
build a block palette or draw a marker. The game resolves each colour on the
client by sampling eight pixels of the texture at load.

So who is online now carries the **names** of the parts a player applied —
`skin4`, `mossgreen`, `azure` — which the server can read, and an admin's client
is asked once for what those names look like. One table covers everybody, it
changes only when a mod adds variants, and it is merged across admins like
palettes and icons. `/witchlight colors` asks for it again; `/witchlight colors` on
a client sends it unprompted.

### Fixed

The hair colour was read from `hairbase`, which is *which hairstyle*, not what
colour it is — the colour is its own part, `haircolor`. Reading the wrong one gave
a name no colour table would ever have.

## 0.10.0

### Health, food, and the colours a player chose

Who is online now travels with how much health and food they have, and with the
skin, hair and eye colours from their applied skin parts. All of it is already in
the entity's watched attributes, so nothing is asked of any client to show it.

### Where the world counts from

`world.json` records the world's spawn position. The game shows every coordinate a
player sees relative to it, so a map counting from absolute zero agrees with
nothing on their screen — a marker the map called `511900` is `-100` to them.

`/witchlight status` reports whether those colours can actually be read, because a
player who comes out as a plain initial on the map and one whose colours are
genuinely unreadable look identical from the far end.

**These shipped in a build already numbered 0.9.0 and went out without a version
of their own, so a server running the older 0.9.0 looked identical to one running
this and behaved differently.** Hence 0.10.0.

## 0.9.0

### Markers are drawn with the game's own pictures

A waypoint names its icon — `gravestone`, `home`, `trader` — and the game draws
each from an SVG. Those are exported to `icons/` for the map service, and because
every asset domain is asked rather than just the game's own, a mod that adds
markers contributes its icons without anything here knowing about it in advance.
The file names carry the sort prefix the game orders its own menu by, `0-circle`
and `01-turnip`, which a waypoint omits; it is dropped on the way out.

### Which a dedicated server cannot supply

Checked rather than assumed, against a 1.22.7 server package: its `textures`
directory exists and contains **no SVG at all**, against 55 in a full install. So
the pictures can only come from a client, and an admin joining is asked for them
the way one is already asked for the palette — a separate message, because a mod
adding a marker should not force a palette to be sent again.

Only what the server lacks is asked for, so a mod added later costs one icon
rather than the whole set. Sliced by measured size rather than by count: an
oversized packet disconnects the player sending it, and how many icons a mod set
adds is not something this end gets to assume. An icon too large to be a map
marker is dropped rather than sent. `/witchlight icons [player]` asks for
everything again, which is the way back if an icon on disk is wrong rather than
missing. Icons from different admins are merged, like palettes.

### Health, food, and where the world counts from

Who is online now travels with how much health and food they have, and with the
colours they chose for their character — all of it already in the entity's watched
attributes, so nothing is asked of anyone to show it.

`world.json` records the world's spawn position, because the game shows every
coordinate a player sees relative to it and a map that did not would agree with
nothing on their screen.

`/witchlight status` reports how many marker pictures are stored.

## 0.8.0

**Clears the map on upgrade.** The region format changed and there is no reader
for the old one. Deploy with map service 0.8.0.

### A region is sixteen chunks square

512 blocks, which is one of the service's tiles at its finest zoom level and the
same square the game itself calls a map region. Region file, tile and game region
became one thing, which removes a whole class of off-by-one from the service's
zoom levels.

A region file is larger for it — a block changing rewrites about 200 KiB rather
than 50 — which at a handful of writes an hour is a trade worth making.

## 0.7.2

### Fixed

The record of which markers the service held was a belief about it, never checked
against it. Once a set had been sent, an unchanged set was never offered again —
so a service that restarted having lost its markers was never told them a second
time, and markers change rarely enough that "never again" was effectively
permanent. The list now goes again on a slow timer whether or not it changed, so
any disagreement heals itself within one interval.

## 0.7.1

### Fixed

`/witchlight status` reported one health reading for both halves of the live data,
and it was worse than useless. Players post every two seconds and markers every
fifteen, so a succeeding player post overwrote a failing marker post almost at
once: the line read `reaching …` while every marker was being refused. It now
reports the two separately, and the marker line says how many bytes the service
accepted.

The warning in the log now names which half failed, rather than claiming nobody
will show on the map when only one stream is down.

## 0.7.0

**Clears the map on upgrade.** The terrain format changed and there is no reader
for the old one.

### Only what moved is exported

The server marks a chunk dirty when a block in it moves and when it loads one, and
the export now reads those columns alone rather than walking every chunk in memory
every thirty seconds. A chunk coming back into memory unchanged is not a change,
and what is read is compared against what is stored — so mining, which marks a
chunk dirty without altering anything visible from above, does not reach the file.

A server where nothing has happened writes nothing at all. The season is the
exception: it is stored per chunk, so a year advancing a step rewrites the regions
holding the chunks whose season moved, about three times an hour on the default
calendar.

### Terrain is stored per region

One file per 8×8 chunks — 256 blocks, exactly one of the service's tiles — each
gzipped, measured between five and eight times smaller than the records alone.
Only the regions about to be written are read back; the rest of the map is never
touched.

### Players and markers are posted, not written

`live.json` is gone. Positions go to the service's API socket every two seconds
and are never written to disk, because a position is worthless by the time a disk
has finished with it. Markers go every fifteen seconds and only when they differ
from the last post. `WITCHLIGHT_API_SOCKET` moves the socket; the service's
`api_socket` must agree.

A service that cannot be reached is logged once rather than every tick, and the
game does not wait on any post.

### Fixed

- A marker post dropped because the previous one was still in flight was recorded
  as sent, so an unchanged list was never offered again and those markers went
  missing until they next changed.
- A column marked dirty and then unloaded before the export ran stayed dirty
  forever, which kept the waiting set permanently non-empty and cost every export
  a full pass over the map.

### Says which build it is

The version is logged on start and shown by `/witchlight status`, which also now
reports how many markers are saved on the server and whether the service is being
reached.
