# Witchlight interfaces

Every way the two halves of Witchlight talk: to each other, to the game, to a
browser, and to the disk.

Witchlight is two programs. The **mod** runs inside the Vintage Story server and
knows the game. The **service** (`witchlight`, a separate binary) knows pixels and
serves the map. Neither is useful alone, and the seams between them are all here.

```
   players ──in-game map──┐
                          │
  ┌───────────────────────┴────────────┐        ┌──────────────────────────┐
  │  Vintage Story server              │        │  witchlight service       │
  │  ┌──────────────────────────────┐  │        │                          │
  │  │ Witchlight mod                │  │        │                          │
  │  │                              │  │ files  │                          │
  │  │   terrain, palette  ─────────┼──┼───────►│  reads on change         │
  │  │                              │  │        │                          │
  │  │   players, markers  ─────────┼──┼───────►│  API socket (POST)       │
  │  └──────────────────────────────┘  │        │                          │
  └────────────────────────────────────┘        └────────────┬─────────────┘
                                                             │ HTTP
                                                             ▼
                                                         browsers
```

Terrain goes by file because it accumulates and must survive both programs
stopping. Players and markers go by socket because a position is worthless by the
time a disk has finished with it.

## The service, on its map port

Bound to `0.0.0.0:8080` by default, so it is reachable from the rest of the
network as soon as it starts. **Read-only, and unauthenticated**: everything here
is visible to anything that can reach the port. Put a TLS-terminating reverse
proxy in front of it before exposing it to the internet.

| | | |
|---|---|---|
| `GET /` | the map viewer | HTML, with the world's bounds compiled in |
| `GET /info.json` | what the map is and whether it has changed | see below |
| `GET /tiles/{x}/{z}.png` | one tile, 256 blocks square at one pixel per block | PNG |
| `GET /live.json` | who is online and every marker | see below |
| `GET /icons.json` | the marker pictures that exist | JSON array of names |
| `GET /icons/{name}.svg` | one marker picture | SVG |
| `GET /portraits/{name}.png` | a picture a player's client drew of their seraph | PNG |

Tile coordinates may be negative, and one tile is exactly one terrain region.
Tiles are rendered when first asked for and then kept, so starting costs nothing
and only the part of the world someone looks at is ever drawn.

### `GET /info.json`

```json
{"minX":511744,"minZ":511744,"maxX":512288,"maxZ":512288,
 "tile":256,"chunks":289,"generation":3}
```

`generation` rises whenever the map changes. It versions tile URLs — `?v=3` — so
a redrawn world is never served out of a browser's cache, and a tile URL is
otherwise safe to cache forever.

Add `?since=N` to ask what has changed since generation `N`:

```json
{"...":"...", "generation":3, "tiles":[[1999,2001],[2000,2001]]}
{"...":"...", "generation":4, "all":true}
```

`tiles` lists the tile coordinates to fetch again, and nothing else needs
touching. `all` means every tile: the palette changed and recoloured the world, or
the caller has fallen further behind than the service still remembers (128
generations). Without `since` neither field appears, which is what a viewer
loading for the first time wants.

A region that changes also changes the western edge of the tile east of it and
the northern edge of the tile below, because slope shading reads the column to
the west and the one to the north. Those neighbours are in the list already.

### `GET /live.json`

```json
{"Players":[{"Name":"ada","Uid":"...","X":511900,"Y":110,"Z":511901,
              "Portrait":"6164...","PortraitAt":1756315231}],
 "Waypoints":[{"Title":"Forge","Icon":"circle","Color":"#00ff00",
               "X":511810,"Y":110,"Z":511810,"Owner":"ada",
               "OwnerUid":"...","Pinned":false}]}
```

Served from memory, out of whatever the mod last posted. **Players expire after
30 seconds** — a game server that stops leaves no dots behind, because a dot
saying someone is standing somewhere is worse than no dot at all. Markers do not
expire; they are the thing worth seeing when the game server is off.

Empty is empty. Nothing here reads a file this build does not write, so an empty
`Waypoints` means the mod posted none — not that a stale file could not be found.

`Color` is CSS; the game stores a packed integer and the mod converts it.

`Portrait` is the name of a picture that player's own client sent, served at
`/portraits/{Portrait}.png`, or absent where they have sent none. It is the uid in
hex — a uid is base64 and carries `/` and `+`, which is a path rather than a name —
and the mod decides it, so nothing else has to derive the same answer.

`PortraitAt` is when that picture was written, in seconds, or `0` where there is
none. **Ask for `/portraits/{Portrait}.png?v={PortraitAt}`**, not the bare path: a
player who is redrawn keeps the name they had, so the name alone is the same before
and after and neither a browser nor anything diffing one report against the last
could tell that the picture behind it had changed. The service ignores the query
when it routes and serves the file either way; the query exists so that the address
changes when the picture does. It moves only when bytes were actually written, so a
player who takes a hat off and puts it back gets the same address again.

## The service, on its API socket

A second listener that accepts **writes**, which is why it is not on the map port:
anything that could reach a public write endpoint could put people on the map who
are not there.

By default a unix socket in `/tmp`, named after the export directory:

```
/tmp/witchlight-{fnv1a32 of the export path}.sock
```

A socket is how two programs talk, not something either of them keeps, so it
belongs with the running system rather than beside the map. The name carries a
hash of the path so that two game servers on one machine do not collide, and both
sides derive it the same way from a path they already agree on — so neither needs
configuring, and both print what they resolved, because a mismatch is otherwise
silent. It also keeps the address at 28 bytes, far inside the hundred-odd a unix
socket allows; a socket beside a data directory several levels deep can exceed it.

Both sides read the same setting to move it: `api_socket` in the service's config
file (or `-a`), and `WITCHLIGHT_API_SOCKET` for the mod. A value with a colon and
no separator is a `host:port`; anything else is a unix socket path.

**Both programs must run as the same user**, or the socket's permissions must be
widened to a shared group. A unix socket needs write permission to connect, and
the default `umask` gives that to the owner alone. Where that is not possible, set
both sides to a `host:port` on `127.0.0.1` instead.

| | | |
|---|---|---|
| `POST /live/players` | who is online, a JSON array | every 2s |
| `POST /live/markers` | every marker, a JSON array | only when they differ from the last post |

Both answer `204` on success, `400` for a body that is not a JSON array, `404`
for another path, and `405` for anything but a POST. A post may carry 8 MB at
most. The service does not parse either payload — the mod knows what a waypoint
is, and the service knows it is a JSON array to hand to a browser, which is the
whole of the contract.

Markers are written to `markers.json` when they arrive and differ; players never
touch the disk. Posts are dropped rather than queued if the previous one has not
finished, and a service that cannot be reached is logged once rather than every
tick.

## Files

All under `<data path>/witchlight`. The mod writes, the service reads, except
where noted.

Two live elsewhere, because they belong to the server rather than to the map:
`<data path>/ModConfig/witchlight.conf` holds the service's settings and is written
by the service itself, and `<data path>/Logs/witchlight-service.log` is everything
the service prints while the mod is running it.

Three settings in that file are read by the mod and never by the service, because
they are about who runs the map rather than about how it is drawn: `autostart`,
`announce`, and `announce_url`. The mod looks for them by name rather than parsing
the file — the format has one owner and it is the service.

| | Written | What it is |
|---|---|---|
| `palette.json` | at asset load, or when a client sends one | every block: id, average colour, which colour maps tint it |
| `colormaps/*.png` | at asset load | the game's climate and season lookup images |
| `columns/r.{x}.{z}.msqr` | the regions whose columns or season moved, checked every 30s | the surface of every chunk exported so far |
| `icons/{name}.svg` | at asset load, or when a client sends them | the picture each marker is drawn with |
| `world.json` | once the world is ready, and on any export until it can be | where the world counts from, so coordinates match what a player reads in game |
| `markers.json` | **by the service**, when markers arrive and differ | the last markers posted |
| `portraits/{uid in hex}.png` | on that player's every join, and 30s after their character last changed | a picture of that player's seraph, drawn on their own machine |
| `service.json` | **by the service**, as it binds | the addresses the map answers on, the one worth giving somebody else first. Removed when the mod stops the service, so nothing hands a player the address of a map that is gone |

The API socket is **not** here; it lives in `/tmp`, as above.

**The format still moves.** Witchlight is alpha, so a map on disk the mod cannot
read — an older format, or a file it cannot parse — is deleted on start and
rebuilt as players explore. There is no upgrade path and no backup of the old
file, deliberately: a reader for every shape the format has ever had is permanent
cost for maps that are days old.

A region is 8×8 chunks, which at a chunk edge of 32 is 256 blocks — exactly one
tile. Each is a gzip stream of fixed-size records after a 20-byte header,
documented in the service's `src/columns.rs` and in the mod's `Regions.cs`. On
real exports the compression runs between five and eight times.

The service watches the `columns` directory's timestamp, and reloads only the
regions whose own timestamps moved. Every file is written beside itself and
renamed into place, so a reader never sees half of one — which is also what makes
the directory timestamp a reliable signal.

`live.json` is no longer written or read, and the mod deletes one left by an older
build on start. It briefly survived as a fallback and that was a mistake: a mod
posting no markers looked like a map that merely had none, right up until the game
server stopped, its players expired, and the map filled in from a file of unknown
age.

## In the game

### Network channel `witchlight`

Registered on both sides. All three messages are protobuf.

| | | |
|---|---|---|
| `SharedMarkers` | server → client, every 15s | every marker not belonging to the recipient, added to their in-game map as temporary waypoints |
| `IconRequest` | server → one admin's client | asks for marker pictures, carrying the names it already has |
| `IconTable` | client → server, sliced by size | the SVGs that client's assets can supply |
| `PortraitRequest` | server → one client, 8s after their join | asks that player to draw themselves |
| `PlayerPortrait` | client → server, on a request and 30s after their character last changed | a PNG of that player's own seraph, drawn on their machine |
| `PaletteRequest` | server → one admin's client | asks for a block colour palette, carrying the fingerprint it must match |
| `PaletteTable` | client → server, in slices of 8,000 blocks | the palette that client's assets can build |

A `SharedMarker` carries a stable `Key` so a client can tell a new marker from
one it already holds, and an `Owner` name but no uid: clients need to know whose
a marker is, and identity is the server's business.

**Coordinates.** Vintage Story shows every coordinate a player sees relative to
world spawn, while the world is a million blocks across with spawn near the
middle. `world.json` records where that is, so the map counts from the same place
the player's own screen does. Absolute positions remain what region files and tile
URLs use, and are a setting in the viewer.

Spawn is not known while mods are starting, so the file is written once the world
is ready rather than at start. It is absent rather than wrong when spawn cannot be
read: a file saying spawn is the origin reads exactly like a world whose spawn is
the origin, and the map service says out loud that it is counting from absolute
zero when the file is missing.


The marker pictures travel for a harder version of the same reason: a dedicated
server's install contains **no SVG at all**, so unlike the palette this is not a
fallback for a poor result but the only way they ever arrive. An icon name becomes
a filename and then a URL path, and it comes from whatever mods are installed, so
it is reduced to `[a-z0-9_-]` on the way in and checked again on the way out.

A portrait travels for a third reason, which is not scarcity: what a seraph looks
like exists only on the machine rendering it. Every player is asked eight seconds
after joining — long enough that their client has finished arriving, since a seraph
not yet loaded renders as a picture of nothing — and their client sends another
thirty seconds after their character last changed. A change restarts that wait
rather than sending, so a run of them costs one picture. A player may also ask for
one by hand, once every five minutes — a rule their own client keeps, since the
server cannot tell a picture somebody asked for from one their character settling
produced. Anybody may send one:
it decides what its own sender's card looks like and nothing else. That is a write
open to every client, so it is bounded three ways — 512 KiB a picture, twenty-five
seconds between two one player sends **unasked**, and nothing written at all where
the bytes match the file already stored. The floor is `Portraits.QuietMs` less a
margin, since one settle is the fastest an honest client sends of its own accord.
An answer to a `PortraitRequest` is exempt: the server knows how often it asks, and
one ask buys one picture.

The palette travels because a dedicated server's own assets are nearly empty of
block textures, which is the usual reason a map renders blank. Only an admin is
asked — a palette decides what every block looks like and arrives from a machine
the server does not control — and one at a time, because the table is a few
hundred kilobytes and every copy after the first is discarded. Tables from
different admins are **merged**, so two admins with different mod sets can
together produce a palette neither could alone.

### Chat commands

**The prefix says which side runs it.** The game gives server commands `/` and
client commands `.`, and they are separate registries — `/witchlight palette` asks
the server to request a palette from an admin, while `.witchlight palette` is that
admin's own client building one and sending it unprompted. The server side is the
one to reach for; the client side exists for sending something without being
asked.

**`wl` is the same tree under a shorter name**, on both sides: `/wl status` and
`/witchlight status` are one command. It is registered as an alias only when
nothing already answers to it — the game's `WithAlias` overwrites the command table
without looking, so claiming two letters another mod holds would break that mod
and say nothing. Where the name is taken, only the long one is registered and the
log says so. Every table below gives the long name, since that is the one that is
always there.

Every server command requires the `controlserver` privilege, inherited from the
root down the command tree. The client ones are not privileged — `.witchlight
portrait` is a player sending their own picture, and the server drops a palette or
a set of icons from anybody who is not an admin regardless of what asked for it.

| | |
|---|---|
| `/witchlight export` | read every loaded chunk again, whatever the server thinks moved — the way back if the map and the world disagree |
| `/witchlight status` | where the exports are, where the palette came from, how much terrain is stored, where the world counts from, whether the map service is up, and how many columns are waiting |
| `/witchlight portrait [player]` | ask a player's client for a picture of their character now, rather than waiting for their next join |
| `/witchlight service [status\|start\|stop]` | the map service the mod runs. `start` ignores `autostart`, which only decides what happens unasked |
| `/witchlight palette [player]` | ask an admin's client for a palette now, rather than waiting for the next one to join |
| `/witchlight icons [player]` | ask an admin's client for every marker picture again |

On a client, with a dot rather than a slash:

| | |
|---|---|
| `.witchlight portrait` | draw your character and send the picture. Once every five minutes; the map asks on its own besides |
| `.witchlight palette` | build a block palette and send it |
| `.witchlight icons` | send the marker pictures |

`status` is the first thing to look at when the map looks wrong. `palette: from
server` against `from client` says which machine's assets the colours came from,
and `waiting:` should settle to zero on a server where nothing is happening.
