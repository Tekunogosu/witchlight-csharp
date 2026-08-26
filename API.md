# Mapstique interfaces

Every way the two halves of Mapstique talk: to each other, to the game, to a
browser, and to the disk.

Mapstique is two programs. The **mod** runs inside the Vintage Story server and
knows the game. The **service** (`mapstique`, a separate binary) knows pixels and
serves the map. Neither is useful alone, and the seams between them are all here.

```
   players ──in-game map──┐
                          │
  ┌───────────────────────┴────────────┐        ┌──────────────────────────┐
  │  Vintage Story server              │        │  mapstique service       │
  │  ┌──────────────────────────────┐  │        │                          │
  │  │ Mapstique mod                │  │        │                          │
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
{"Players":[{"Name":"ada","Uid":"...","X":511900,"Y":110,"Z":511901}],
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

## The service, on its API socket

A second listener that accepts **writes**, which is why it is not on the map port:
anything that could reach a public write endpoint could put people on the map who
are not there.

By default a unix socket in `/tmp`, named after the export directory:

```
/tmp/mapstique-{fnv1a32 of the export path}.sock
```

A socket is how two programs talk, not something either of them keeps, so it
belongs with the running system rather than beside the map. The name carries a
hash of the path so that two game servers on one machine do not collide, and both
sides derive it the same way from a path they already agree on — so neither needs
configuring, and both print what they resolved, because a mismatch is otherwise
silent. It also keeps the address at 28 bytes, far inside the hundred-odd a unix
socket allows; a socket beside a data directory several levels deep can exceed it.

Both sides read the same setting to move it: `api_socket` in the service's config
file (or `-a`), and `MAPSTIQUE_API_SOCKET` for the mod. A value with a colon and
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

All under `<data path>/mapstique`. The mod writes, the service reads, except
where noted.

| | Written | What it is |
|---|---|---|
| `palette.json` | at asset load, or when a client sends one | every block: id, average colour, which colour maps tint it |
| `colormaps/*.png` | at asset load | the game's climate and season lookup images |
| `columns/r.{x}.{z}.msqr` | the regions whose columns or season moved, checked every 30s | the surface of every chunk exported so far |
| `markers.json` | **by the service**, when markers arrive and differ | the last markers posted |

The API socket is **not** here; it lives in `/tmp`, as above.

**The format still moves.** Mapstique is alpha, so a map on disk the mod cannot
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

### Network channel `mapstique`

Registered on both sides. All three messages are protobuf.

| | | |
|---|---|---|
| `SharedMarkers` | server → client, every 15s | every marker not belonging to the recipient, added to their in-game map as temporary waypoints |
| `PaletteRequest` | server → one admin's client | asks for a block colour palette, carrying the fingerprint it must match |
| `PaletteTable` | client → server, in slices of 8,000 blocks | the palette that client's assets can build |

A `SharedMarker` carries a stable `Key` so a client can tell a new marker from
one it already holds, and an `Owner` name but no uid: clients need to know whose
a marker is, and identity is the server's business.

The palette travels because a dedicated server's own assets are nearly empty of
block textures, which is the usual reason a map renders blank. Only an admin is
asked — a palette decides what every block looks like and arrives from a machine
the server does not control — and one at a time, because the table is a few
hundred kilobytes and every copy after the first is discarded. Tables from
different admins are **merged**, so two admins with different mod sets can
together produce a palette neither could alone.

### Chat commands

All require the `controlserver` privilege.

| | |
|---|---|
| `/mapstique export` | read every loaded chunk again, whatever the server thinks moved — the way back if the map and the world disagree |
| `/mapstique status` | where the exports are, where the palette came from, how much terrain is stored, and how many columns are waiting |
| `/mapstique palette [player]` | ask an admin's client for a palette now, rather than waiting for the next one to join |

`status` is the first thing to look at when the map looks wrong. `palette: from
server` against `from client` says which machine's assets the colours came from,
and `waiting:` should settle to zero on a server where nothing is happening.
