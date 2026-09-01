# Witchlight (server mod)

The Vintage Story half of Witchlight. It exports what a map renderer needs, and
shares every player's map markers with everyone on the server.

The renderer is a separate program, [`witchlight`](../rust/witchlight), which takes
what this side produces and serves a browsable map. This side knows the game; that
side knows pixels. Keeping the game-coupled code here and small is deliberate: a
Vintage Story update can only ever break this half.

Every interface between the two — files, sockets, HTTP, game packets, commands —
is written up in **[API.md](API.md)**.

Three things a dedicated server cannot supply come from a client, for the same
underlying reason: its install ships almost no images. The **block palette**
decides what terrain looks like and the **marker pictures** are SVGs it has none
of; both are asked of whoever joins, merged across whoever answers, and stored so
they are asked once rather than per join. Anybody may supply what the server does
not have; only an admin may replace what it does. A **portrait** is the third and is different in kind:
what a seraph looks like exists only where it is rendered, so a player's own client
draws one and sends the picture. Every player is asked on every join, and a client
sends another thirty seconds after its character last changed.

Versions track the [map service](../rust/witchlight) and **are always the same
number**: the two ship as one archive and are one release, so a change to either
half moves both. `package.sh` will not build a pair that disagrees. A format change
clears the map while Witchlight is alpha — see [CHANGELOG.md](CHANGELOG.md).

## What it does

Terrain and colours go to files, because they accumulate and must survive both
programs stopping. Players and markers go over a socket, because a position is
worthless by the time a disk has finished with it.

| What | Where | When |
|---|---|---|
| every block: id, average colour, tint maps | `palette.json` | at asset load, or when a client sends one |
| the game's climate and season lookup images | `colormaps/*.png` | at asset load |
| the surface of every chunk exported so far | `columns/r.{x}.{z}.msqr` | the regions whose columns or season moved, checked every 30s |
| who is online, where, their health and food, and which portrait is theirs | posted to the service | every 2s |
| every marker | posted to the service | every 15s, when they differ from the last post |
| the picture each marker is drawn with | `icons/{name}.svg` | at asset load, or when a client sends them |
| a picture of a player's seraph | `portraits/{uid in hex}.png` | on their every join, and 30s after their character last changed |
| where the world counts from | `world.json` | at start |

The files land in `<data path>/witchlight`, or wherever `map_data` names. With
`per_world` on they land in a directory named for the world inside it — the world's
own name and eight characters of its savegame identifier, because two saves called
"New World" are not rare and one directory between them is one map of two worlds.
It is off for a dedicated server, which runs one world, and on for singleplayer,
where every save shares one data path. Turning it on moves the map already there
down into its own directory rather than leaving it to be written over.

The service listens for posts on its
API channel — loopback, on a port it writes into `api.json` in that same directory
so both sides find each other without being told. See [API.md](API.md). Once the
world is up it loads a 17×17 block of chunk columns around spawn, four columns at a
time and working outward in rings, so a fresh server has a map without waiting for
anyone to walk the world and without holding up the chunk thread while it does.

Exports **accumulate**. A server holds only the chunks players are near, so an
export that replaced the file would shrink the map back to spawn every time it
ran.

The map is a **directory of regions**, eight chunks on a side. That is 256 blocks,
which is exactly one of the renderer's tiles, so a chunk that changes belongs to
one file and one tile: the export writes that square, and the renderer reloads
that square and redraws that tile. Each region is gzipped, which runs between five
and eight times smaller on real exports.

Only what moved is read again. The server marks a chunk dirty when a block in it
moves and when it loads one, and the export reads those columns alone — a chunk
coming back into memory unchanged is not a change. What is read is then compared
against what is stored, so mining, which marks a chunk dirty without altering
anything visible from above, does not reach the file either. Only the regions
about to be written are read back off disk; the rest of the map is never touched.
A server where nothing has happened writes nothing.

The season is the one thing that moves a region without anybody touching it: it is
stored per chunk, so a chunk whose season changes rewrites the region holding it
whether or not a player has ever been near. What decides how often that happens is
how finely the season is counted — and it is counted **by the month**, which is
the coarsest step the eye does not notice and the one the game itself counts in.
Twelve steps a year rather than the 255 the stored byte could express, so a
region is rewritten for the calendar about once an in-game month instead of three
times an in-game day.

**The format still moves.** Witchlight is alpha, so a map on disk that this build
cannot read is cleared on start and rebuilt as players explore, rather than
upgraded in place. Carrying a reader for every shape the file has ever had is a
permanent cost for the sake of maps that are days old. It says so in the log when
it happens.

`/witchlight export` ignores all of that and reads every loaded chunk again, which
is the way back if the map and the world ever disagree.

It also pushes every player the markers belonging to everyone else, and adds them
to their in-game map as temporary waypoints.

## The map service

The renderer is a separate program and stays one: this half knows the game, that
half knows pixels, and a map worth keeping outlives any single game server. It is
not, however, a second thing to install. A Linux x64 build rides along inside this
archive, is unpacked to the game's `Cache` folder on first run, and is started
once the world is ready.

Its settings are `witchlight.conf` in the game's `ModConfig` folder, written by the
service itself on a first run — the format has one owner and this half does not
write it. Every option is editable there, including:

```toml
# Whether the server mod runs this service itself.
autostart = true

# Who may run each `wl` command in game.
[commands]
export = "admin"
login = "player"
```

Turn `autostart` off to run `witchlight serve` by hand instead, which is what a map
that should stay up while the game server is down wants. `/witchlight service start`
still runs it on demand. `[commands]` is under [Server commands](#server-commands).

Where the map ended up listening goes in the server's own log as it comes up:

```
[witchlight] the map is being served at http://192.168.1.145:8080
```

The service works that out — `0.0.0.0` is not something anyone can type into a
browser — and writes it to `service.json` beside the export, which is where both
that line and the message below get it from.

### Telling players where it is

A player is told where the map is once they are in the world, and handed a link
that opens it already signed in — so nobody has to know to type
`/witchlight login`. Both are pressable in chat. The plain address is the one to
bookmark; the signed-in link is spent on one press and expires in ten minutes,
which is the same link the command hands out and is offered to whoever could have
typed it.

Said at `PlayerReady` rather than on join: on a first join the character and class
screen is still up, and a line of chat behind it is a line nobody sees.

Two settings govern it:

```toml
announce = true          # say it at all
announce_url = ""        # empty: wherever the service says it is listening
```

Set `announce_url` on any server a player cannot reach directly. The address the
service works out is the one its own machine can see, which on a server behind a
proxy, a domain or NAT is not the address anybody types — only an operator knows
that one:

```toml
announce_url = "https://map.example.com"
```

An `announce_url` without a scheme is said as words rather than as a link: the
game makes a link out of an address beginning `http`, and one it does not
recognise is a press that goes nowhere.

Both are read at each join, so turning the message off takes effect on the next
one rather than on the next restart. Nothing is said when there is no address to
give: a service that is not running, one somebody else runs somewhere this cannot
see, or a server whose real address its operator has not said.

Everything the service prints goes to `Logs/witchlight-service.log`, on its own so
it can be tailed while it runs:

```sh
tail -f VintagestoryData/Logs/witchlight-service.log
```

The service is stopped with the game server, which is safe to do outright:
everything it writes is put beside itself and renamed into place, so there is no
half written file to catch it in the middle of. A service that stops on its own is
reported and left stopped — one that will not start fails the same way every time,
and a restart loop turns one legible error into a log nobody can read.

On a machine the archive carries no build for — another platform, or a client
archive handed to a server — the mod says which file and which path, then carries
on exporting; a service run by hand serves the map as before.

## Where the palette comes from

The palette is the one thing that cannot always be built here, and the reason is
the files rather than the API.

Nothing in the API stops a server building it: the `textures` asset category is
`EnumAppSide.Universal`, `Block.Textures` is readable during asset loading, and
the rule the game uses to pick a block's map colour is short enough to follow —
the texture named by `textureCodeForBlockColor`, else the coverage layer, else
`up`, else the first. On a **full game install**, where the server runs from the
same directory as the client, this works: 13,936 of 14,091 blocks get a colour.

**What stands for a block is whatever covers most of it.** A block naming a
texture for its map colour, wearing a coverage overlay, or declaring an `up` face
has said which one, and that is taken. A block that has said none of those has
said nothing, and its own dictionary order is not an answer — branchy leaves list
`branch` before their leaves, and a branch is the one thing on that block nobody
looking down at a tree can see. Its shape is weighed on the same terms rather
than kept as a last resort, since a reed declares two textures that are a seed
head covering three per cent of the square and keeps the whole plant in its shape
file. The game's own rule here is `named, else up, else the first one listed`,
which is where this started and where both of those went wrong.

A shape's own textures are skipped where they are the game's stand-in for one it
has not been given: `block/basic/cube` says `all: unknown`, which is the
missing-texture checker — white, opaque over the whole square, and the loudest
thing in any shape it appears in.

A block that ends with no colour is recorded as one of two things, because they
are not the same fact and the map must not draw them the same way. A block with no
texture and no shape colour **draws nothing** — air, an invisible helper — and bare
ground is the right picture of it. A block whose textures were there and drew
nothing has a **colour missing**, and that is a gap in this palette rather than a
hole in the world. Filing the two together is what made the ground under dug-up
grass share a colour with a world nobody had ever walked into.

A **dedicated server download does not ship block textures**. Measured on a real
one: 46 PNGs against a full install's 9,587, and no `textures/block/` at all. The
mod has nothing to average, and the map renders as empty background.

### The base game's colours travel with the mod

So they are not asked for. The blocks of the base game look the same on every
server there is, which makes their colours a fact this mod can know before it is
installed anywhere — so a recording of them rides inside the assembly, 70 KiB
compressed, and fills whatever the server's own build could not colour.

Measured on a real dedicated server with no block textures at all: **13,936 of
14,091 blocks coloured, no gaps, before a single player connected**, which is the
same palette a full game install builds for itself. The remaining 155 are the
blocks the game draws nothing for. Rendering that server's first export reports
`0% waiting on a colour`.

It is a seed and not an answer. It fills only what has no colour, so a colour this
server worked out from its own assets always wins, and it says nothing about a
mod's blocks — those are still asked for, which is now the whole of what the
handshake below is for. It is keyed by block code and carries no ids, since an id
is assigned per world; each entry is given this world's id as it is read, and a
code this server has never heard of is dropped.

Refresh it with `bake-palette.py`, which records a real `palette.json` rather than
deriving one — working out a block's colour takes the game's own asset pipeline,
and a second implementation of that rule would drift from `PaletteBuilder` the week
it was written. Run a server with nothing but this mod, from a full game install,
and point the script at the palette it wrote. It refuses anything with a mod's
blocks in it or with a gap in it, because a recording shipped to every server must
be neither.

So when the server still cannot build a usable palette, it asks for one.

### The handshake

```
server boot ─► fingerprint the block registry
             ─► palette stored for this fingerprint, and good enough?  ──yes──► done
                          │no
                          ▼
      a player joins, or /witchlight palette ─► "send me a palette"
                          ▼
      client builds one from its own assets ─► six packets ─► merged and written
```

**The fingerprint is the block registry and nothing else** — game version, then
every `(id, code)` pair. The server sends its registry to clients on join, so a
connected client computes exactly the same value, which is what makes it usable as
a shared token. Mod lists deliberately play no part: a client has client-side mods
the server never sees and vice versa, so a fingerprint covering them could never
agree across the wire. The server keeps its own mod set as a separate `ModStamp`
it never sends, which still catches a mod changing its textures without moving any
block id.

**An admin is asked first, always.** Theirs is the tileset the map should look
like, so where one is in the room there is no reason to ask anybody else and every
reason not to — a colour taken from a player is a colour an admin's answer will
only have to replace. Where there is no admin, one of the others is picked at
random rather than by connection order, so a server does not put the same person's
client to work every time it comes up. Nobody outside `commands.palette` is asked
at all: a server that has narrowed who may ask for a palette by hand has said
something about whose assets it trusts, and asking round the room past that would
be the mod overruling its own operator.

**An admin is asked once even when nothing is missing.** A palette drawn entirely
from the recording above is complete and still nobody who could decide what this
server looks like has decided — a texture pack changes every colour on the map
without changing a single block code. So the palette records whether an admin's
own assets settled it, and while none has, the first admin to join is asked. Their
answer either matches what is stored, in which case nothing is written and nothing
is redrawn, or it replaces it. `status` says which of the two the map is in.

**Anybody is asked; only an admin may overwrite.** Admins alone used to be asked,
which is the right instinct and the wrong rule: a dedicated server is run from a
console, and the person who runs it may never have a character on it — so a server
whose operator never joins in game had no map at all. The two cases are not the
same risk, and that is what the rule turns on. A colour laid over a block that has
none can only improve on nothing; a colour laid over one somebody chose is a
change to what is already right. So **an admin's palette is preferred over what is
stored and anybody else's is merged as filler**, and `/witchlight palette` is the
way back either way. The privilege is read when the palette arrives rather than
when the ask went out, because a packet handler is an untrusted entry point
regardless of who was asked.

One player at a time, with a two-minute timeout before the next is tried. Thirty
seconds after an unanswered ask the server says so in the log. A slice naming a
part outside its own total, or claiming more parts than this server's registry
could produce, ends the whole attempt — the sender no longer has to be an admin,
and "only admins can reach it" was the whole of what bounded that memory before.
A half-sent palette is dropped when its sender disconnects.

**Palettes are merged, not replaced.** A client only has textures for the mods it
has installed, so two players with different sets can between them produce a
complete palette where neither could alone.

**Which kind of colourless a block is travels with it.** An entry with no colour
means one of two things — the block draws nothing at all, or it draws something
the sender could not colour — and only the sender's own assets can tell them
apart. Without that on the wire every colourless block in a client's palette
arrived saying nothing, so the server stopped being able to see a gap worth
asking about, and the map drew air and those invisible placeholders as ground
nobody had ever explored. A palette from a client older than the field says
nothing rather than saying "draws", which is what keeps it safe to take.

**Two things make a palette worth asking for, and the second is the one that keeps
a working map correct.** The first is coverage: below 90% of blocks coloured there
is no map to speak of, and a dedicated server scores about 15% on its own against
a full install's 99%, so the threshold separates them cleanly. The second is a
named gap — a block the palette says draws something and has no colour for. A
palette can be 98% covered and still have no colour for bare soil, which is the
block a player uncovers every time they dig; coverage is one number over fourteen
thousand blocks and cannot see that, so the gaps themselves are watched. A gap is
asked about on the export beat as well as on join, because a colour the map has not
got is not something a player fixes by rejoining and on a small server nobody may
rejoin for days.

**Each player is asked once, and that is what stops the asking.** One is all a
player has to give: a client sends the whole of what its assets can colour, and
everything it could fill the merge has filled. Somebody who joins later has not
been asked and may have the mod set that answers, so the map goes on repairing
itself as people arrive — and a server where nobody can supply the last colour
stops asking rather than going round the room for ever. What is still missing is
named in the log and in `status`, which is a report to make to the mod shipping
those blocks rather than a command to run again here. `/witchlight palette` opens
the question again, for the one case none of this can see: the colours themselves
moving under the same block ids.

**It is sent in slices.** Block codes are not sent at all: the server turns ids
back into codes from its own registry, and those codes were the bulk of the
message. What remains is ids and colours, packed and zigzag-encoded because most
entries carry `-1`. A 45,418-block palette travels as six packets, the largest
66 KiB, 361 KiB in total. An earlier single-packet version produced
`too large packet of 3478354 bytes received` and disconnected the sender — see
`TODO.md` for the remaining hardening.

Rebuild the palette whenever the block registry changes, which is what a mod
change does.

## Server commands

All under `/witchlight`. `/wl` is the same tree under a shorter name, on both sides
— claimed only where no other mod already answers to it, since the game hands out
an alias by overwriting whatever holds the name. The long name is always
registered, so it is the one written down here.

**Who may run which is the operator's**, in the `[commands]` section of
`witchlight.conf`. The defaults are in the table below and split the commands that
change what the server is doing from the ones that answer a question about the
person typing them. `admin` and `player` are spelled out; any privilege the game
knows works in their place, so a server with a moderator role can name it, and a
name it does not know is refused to everyone but an admin and said in the log.

The same setting decides whom a thing may be taken *from*: a server that lets its
players fetch a palette lets its players be fetched from, because that is the same
question asked from the other end. What guards the map either way is what is done
with the answer — see the palette section above.

`witchlight status` prints the table in force. That is where to look on a server
upgrading into this, since a settings file written before `[commands]` existed says
nothing about it and nothing rewrites a file an operator owns just to add a section
of defaults it is already following.

| | |
|---|---|
| `status` | where exports live, which source the palette came from, its coverage and fingerprint, whether that fingerprint is stale, where the world counts from, whether the map service is up, and when terrain was last written |
| `service [status\|start\|stop]` | the map service this mod runs. `start` runs it whatever `autostart` says, because somebody typing the command has asked |
| `palette [player]` | ask for a palette now, rather than waiting for the next join. An admin's replaces what is stored, which is what makes this the way to correct a map |
| `icons [player]` | ask for every marker picture again, replacing what an admin sent |
| `export` | write the surface of every loaded chunk immediately |
| `login` | send yourself a link to your own page of the map. Acts on nobody but its caller, which is why it is anybody's by default |
| `mark` | mark where you are looking, using your preset for that block. Anybody's too, and asks the caller's own client — which is the only side that knows what they are looking at |

| `portrait [player]` | ask a player's client for a picture of their character now |

Only that player's own machine can draw it — nobody else's has their seraph loaded
— so the server asks and the picture comes back. The map then shows it in their
card, and their initial where there is none.

**Nobody has to ask.** Every player is asked eight seconds after joining, and their
client sends another thirty seconds after their character last changed — a change
restarts that wait rather than sending, so a run of them is one picture. The
command is for wanting one sooner than that, and a player may use it **once every
five minutes**: nothing that keeps the map current depends on it, so nobody waiting
that out is waiting for their own face to appear.

**Any player, not only an admin.** A portrait decides what one card looks like and
that card is the sender's own, unlike the palette and the marker pictures, which
decide what everybody sees. What that opens is a write to every client, so a
picture is capped at 512 KiB, one identical to the file already stored is not
written at all, and one a client sends **unasked** is taken no closer together than
twenty-five seconds — just under the quiet period, which is the fastest an honest
client sends on its own. A picture the server asked for is not counted against
that floor: it knows how often it asks, and one ask buys one picture.

**A dot, not a slash, on a client.** The game keeps client and server commands in
separate registries and gives them different prefixes, so `.witchlight portrait`
is the client drawing itself unprompted while `/witchlight portrait` is the server
asking it to. The same holds for `palette` and `icons`, which exist on
both sides for that reason.

Every subcommand is registered under the privilege `[commands]` gives it, read once
before a single command is declared — the game bakes a privilege into its command
table as it registers it, so a value read again later could disagree with the one
the command is enforcing. Changing the setting therefore takes a restart. The tree
itself asks only for `chat`, since it does nothing but list what is under it.

`/witchlight status` is the first thing to look at when the map looks wrong: it
says whether the palette is the server's own poor one, the recording that ships
with the mod, or a good one from a client — and whether an admin has confirmed it.

## Reading the surface

The rain height map gives each column's height without searching down from the
sky, but it marks where rain *stops* — commonly the air just above the ground.
Sampling it directly maps the sky and every column comes back as air, so the pump
steps down until it finds something real.

All the way down, not a fixed few. A dug shaft is a column of air below where the
sky still says the ground is, and a search that gave up after eight of them stored
air — which the map painted as ground nobody has ever explored, so every pit deeper
than that became a hole on the map that no amount of exporting would fill. The
depth is paid by the columns that need it: ordinary ground answers on the first or
second read.

**Something that shows, not merely something that is not air.** A large structure
stands one real block beside a run of invisible placeholders, and a search that
stopped at the first non-air block recorded one of those — a block with nothing to
draw, where there was grass. What counts as showing is the palette's to say, since
that is the question the palette was built to answer; the exporter asks it through
`PaletteExchange.Shows` and asks again each export, so a better palette from a
client corrects what is written as well as what is coloured.

A column already stored as air is read again. It is a reading that failed rather
than a fact about the world, so the chunks holding one are left out of what counts
as exported when the map on disk is walked at start, and the server's own
`ChunkDirty` brings them back as they load. The walk asks this of bytes it has
already decompressed to read the seasons, so it costs nothing extra.

Columns are written as `u16 blockId, i16 surfaceY, u8 temperature, u8 rainfall`,
six bytes each, 1024 to a chunk, after a per-chunk header carrying the season.
Temperature uses the game's own `Climate.DescaleTemperature`, and the season comes
from `IGameCalendar.GetSeasonRel`, so the renderer can sample the colour maps
exactly the way the game's shader does.

Season is recomputed for **every** chunk on every export, carried-over ones
included. A season that only advanced where players were standing would leave the
rest of the map stuck in whatever month it was last visited.

What is *stored* is the middle of the month that point in the year falls in — the
middle rather than either edge, so a chunk is drawn in its month's own colour and
is the same distance from wrong at both ends of it. The byte means what it always
meant, a position in the year from 0 to 255, so nothing on disk changed when this
did; only the number of distinct values it takes. That is what keeps the calendar
from rewriting the whole map three times a day for a change nobody can see.

## Sharing markers

Waypoints live server-side in `WaypointMapLayer`, so every marker on the server is
readable here — which is also why the web map shows them all.

Each player is sent the markers belonging to everyone else, on join and every
15 seconds, and the client adds them as **temporary** waypoints. The game keeps
those apart from a player's saved list, so nothing here can edit, delete or
overwrite a marker anyone owns; they vanish on logout and are sent again on the
next join. Clients only add keys they have not seen, so the resend never
duplicates. Markers travel by owner *name*: clients have no use for account uids,
so those stay on the server.

A marker deleted on the server leaves everybody else's map on the next share.
The layer is laid down as a **set** rather than one waypoint at a time — every
temporary waypoint is cleared and the current set put back whenever it differs —
so a marker that is gone is gone by not being in the set.

### Marking a place without leaving the game

**Create from Witchlight preset** — a key under the character controls, bound to
`[` — and `/wl mark`, and `.wl mark`, all do the same one thing: mark what the player is looking at the
way they have said that kind of thing is marked. The client sends where they mean
and which block names it, which are two different answers, since somebody looking
at nothing means where they are standing and the block *there* is air.

Every decision is the server's. It reads the block itself rather than taking a code
from a client, asks the map service what that player has kept, and makes the marker
from the preset whose pattern names the block. Where none does, **nothing is made**:
the answer carries the block, a name, a colour, a picture and both of that person's
defaults, and the client opens a window on it — with the game's own colours and
pictures, plus the two things the game has no idea about. Saving makes the marker
and, where "keep as preset" is on, sends the preset to the map service.

A marker made this way is the same waypoint the web form's markers are, under a
guid, with a decision recorded about who may see it. Nothing about it is special
afterwards.

**A preset starts out naming a family, not a block.** A block code carries its
variant as a number — `game:tallgrass-3`, `game:leaves-grown7-oak` — so a preset
kept against the exact code answers for one stage of grass out of eight, and
keeping one for grass meant keeping it again seven more times. So the number is
where the wildcard goes by default and the window opens on
`game:leaves-grown*-oak`. It is only a default: the star is a character in a text
field, so move it, add another, or take it out to name one block exactly —
`BlockPattern.Fits` reads it wherever it ends up, and `*` stands for any run of
characters, so `b1-b2-*` names `b1-b2-b3` and `b1-b2-c3` alike. The map's own
form offers the same starting pattern, so a preset made from a key press and one
made from a right click begin the same.

`mark` is on both sides of the command tree because a slash is what anybody types
first, and the two are one behaviour: the server's copy sends that player's own
client a nudge and the client answers it as the key does, rather than the server
answering a poorer version of the question from where they happen to be standing.

## Findings worth keeping

Four things that each cost a debugging round, recorded so they are not
rediscovered:

- **Texture paths can be wildcards.** `CompositeTexture.Base` may be
  `.../coral/shelf/blue*`, which the client expands at bake time. Expanding it
  with `api.Assets.GetLocations` took a vanilla palette from 9,814 blocks to
  12,697.
- **Plants declare no textures.** `fern.json` has no `textures` key at all — its
  textures and its tints live in the shape file it points at, and the tints hang
  off *child* elements rather than the top one. Reading shapes took colourless
  blocks from 1,394 down to 205.
- **`ClimateColorMapForMap` is null server-side for leaves.** `BlockLeaves` fills
  it in `OnCollectTextures`, which never runs on a server, so the plain
  `ClimateColorMap` JSON fields are used as the fallback. Without it every forest
  renders grey.
- **Greyscale is correct.** Water, grass and leaves ship as greyscale masks that
  the game multiplies by a colour map. A grey entry in the palette is not a bug.

## Building and packaging

Needs the .NET 10 SDK and a Vintage Story install. The game directory is taken
from `$VINTAGE_STORY`, falling back to `~/.local/share/vintagestory`.

```sh
./package.sh                     # a server archive: dist/witchlight_<version>.zip
./package.sh --target client     # without the service: ..._<version>_client.zip
./package.sh --install ~/.config/VintagestoryData/Mods    # and drop it in place
./package.sh --no-build          # repackage what was built last
./package.sh --service FILE      # use this map service binary
./package.sh --no-service        # a server archive without one
```

One assembly, two archives. The mod runs on both sides and decides for itself
which half to start, so the code is identical either way; what differs is that a
server is handed the map service and a client has no use for a megabyte of it —
42 KiB against 971. The client archive carries `_client` in its name so that both
can sit in `dist/` at once rather than one quietly overwriting the other.

The base game's block colours are compiled in as an embedded resource,
`Witchlight/Palette/vanilla.json.gz`. It changes only when the game does:

```sh
./bake-palette.py /path/to/witchlight/palette.json    # record a new one
```

Record it from a server running nothing but this mod, out of a full game install
so the textures are there. The script refuses a palette with a mod's blocks in it
or with a gap in it, since a recording shipped to every server must be neither.

The map service is looked for in `/var/tmp/rust-target/release/witchlight` and
`../rust/witchlight/target/release/witchlight`, or wherever `$WITCHLIGHT_SERVICE`
says. Packaging **stops** when there is none, rather than quietly producing a mod
that exports a map it cannot serve; `--no-service` is how to mean it.

The archive holds `modinfo.json` and `Witchlight.dll` at its root, plus the map
service under `service/linux-x64/` on a server build, named
`<modid>_<version>.zip` — the layout the mod database expects on upload, and the
same file a server can be handed directly. `modinfo.json` is the only place the
version lives. A `modicon.png` or an `assets/` directory beside the project is
picked up automatically if you add one.

Install it on the server **and on clients**: the server half exports and shares,
the client half draws other players' markers and supplies palettes. Hand the
server archive to a server and the client one to players — a client given the
server archive works exactly the same, it has just carried a map service it will
never run. Mods load at
startup, so deploying means restarting.

## How it is laid out

One subject to a file, one subject to a folder, and a utility never sits inside
the system that first needed it. Two mod systems start, one per side, and neither
of them decides anything — every judgement is in a type of its own that they wire
together.

| | |
|---|---|
| `Mod/` | the two mod systems and the commands: what runs on each side, when, and what an operator can type |
| `Map/` | reading the surface, writing it, and settling where a world's map belongs |
| `Palette/` | what a block looks like, the base game's colours recorded once, and asking a client for the rest |
| `Players/` | where everybody is, what the map may say about them, and the bars a card carries |
| `Markers/` | markers, who may see them, and what one player is shown of another's |
| `Portraits/` | drawing a player, which only their own machine can do |
| `Icons/` | the marker icons, which arrive the same way a palette does |
| `Network/` | what travels between the two sides, and how it is sliced to fit |
| `Service/` | the other half: talking to it, running it, and what the operator set |
| `Gui/` | the one window this mod draws in game: making a marker from a preset |
| `Util/` | writing, failing, patterns, and identity — none of which know this mod's lifecycle |

Three of those folders each hold their own `*Exchange.cs`, because the three
things only a client can supply — a palette, the icons, a portrait — are each a
question about that subject rather than a question about the network. What
crosses the wire is `Network/`; who asks for it and what is done with the answer
stays with the subject.

Three files are worth knowing before changing anything. **`Util/Disk.cs` owns every
write**: a file is written only when it would differ, and always through a
temporary renamed into place, because the map service reads all of it while the
server runs. **`Service/Settings.cs` owns every question about what the operator
wants**, including what a marker nobody has decided about is — three places used
to negate that setting separately, which is three chances to show somebody's
markers to a server. **`Mod/Permissions.cs` owns who may do what**: the privilege a
command registers under, whether a named player may be asked for what a command
fetches, and who is in the room when the server needs a palette were a literal
`controlserver` at each site and a `bool admin` threaded between them, which is
three chances to disagree about what an operator asked for.

## Known gaps

`TODO.md` lists what is missing or fragile, including the palette transfer's size
handling, the cost of the terrain export on the server tick, and marker scoping.

## Reference material

Cloned alongside this repo for reference, all MIT: `~/Development/VS-LiveMap`,
`~/Development/WebCartographer`, plus the official `~/Development/vsapi` and
`~/Development/vssurvivalmod` sources — the last of which explained the leaves
behaviour.
