# Mapstique (server mod)

The Vintage Story half of Mapstique. It exports what a map renderer needs, and
shares every player's map markers with everyone on the server.

The renderer is a separate program, [`mapstique`](../rust/mapstique), which takes
what this side produces and serves a browsable map. This side knows the game; that
side knows pixels. Keeping the game-coupled code here and small is deliberate: a
Vintage Story update can only ever break this half.

Every interface between the two — files, sockets, HTTP, game packets, commands —
is written up in **[API.md](API.md)**.

Three things a dedicated server cannot supply are asked of an admin's client, for
the same underlying reason: its install ships almost no images. The **block
palette** decides what terrain looks like, the **marker pictures** are SVGs it has
none of, and the **skin part colours** are worked out by sampling textures it does
not have. Each is asked separately, merged across admins, and stored so it is
asked once rather than per player or per join.

Versions track the [map service](../rust/mapstique) and **must match on minor
version**. A format change clears the map while Mapstique is alpha — see
[CHANGELOG.md](CHANGELOG.md).

## What it does

Terrain and colours go to files, because they accumulate and must survive both
programs stopping. Players and markers go over a socket, because a position is
worthless by the time a disk has finished with it.

| What | Where | When |
|---|---|---|
| every block: id, average colour, tint maps | `palette.json` | at asset load, or when a client sends one |
| the game's climate and season lookup images | `colormaps/*.png` | at asset load |
| the surface of every chunk exported so far | `columns/r.{x}.{z}.msqr` | the regions whose columns or season moved, checked every 30s |
| who is online, where, their health and food, and the names of the skin parts they wear | posted to the service | every 2s |
| every marker | posted to the service | every 15s, when they differ from the last post |
| the picture each marker is drawn with | `icons/{name}.svg` | at asset load, or when a client sends them |
| what each skin part variant looks like | `skincolors.json` | when a client sends them |
| where the world counts from | `world.json` | at start |

The files land in `<data path>/mapstique`. The service listens for posts on its
API socket — a unix socket in `/tmp`, named after that directory so both sides
find it without being told. See [API.md](API.md). Ten seconds after start it loads a 17×17
block of chunk columns around spawn and exports those, so a fresh server has a map
without waiting for anyone to walk the world.

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

The season is the exception, and the reason `TODO.md` wants it moved: it is stored
per chunk, so a year advancing a step rewrites every region that holds a chunk
whose season changed. That is roughly every twenty minutes on the default
calendar.

**The format still moves.** Mapstique is alpha, so a map on disk that this build
cannot read is cleared on start and rebuilt as players explore, rather than
upgraded in place. Carrying a reader for every shape the file has ever had is a
permanent cost for the sake of maps that are days old. It says so in the log when
it happens.

`/mapstique export` ignores all of that and reads every loaded chunk again, which
is the way back if the map and the world ever disagree.

It also pushes every player the markers belonging to everyone else, and adds them
to their in-game map as temporary waypoints.

## The map service

The renderer is a separate program and stays one: this half knows the game, that
half knows pixels, and a map worth keeping outlives any single game server. It is
not, however, a second thing to install. A Linux x64 build rides along inside this
archive, is unpacked to the game's `Cache` folder on first run, and is started
once the world is ready.

Its settings are `mapstique.conf` in the game's `ModConfig` folder, written by the
service itself on a first run — the format has one owner and this half does not
write it. Every option is editable there, including:

```toml
# Whether the server mod runs this service itself.
autostart = true
```

Turn `autostart` off to run `mapstique serve` by hand instead, which is what a map
that should stay up while the game server is down wants. `/mapstique service start`
still runs it on demand.

Where the map ended up listening goes in the server's own log as it comes up:

```
[mapstique] the map is being served at http://192.168.1.145:8080
```

The service works that out — `0.0.0.0` is not something anyone can type into a
browser — and writes it to `service.json` beside the export, which is where both
that line and the message below get it from.

### Telling players where it is

A player is told where the map is as they join, which two settings govern:

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

Both are read at each join, so turning the message off takes effect on the next
one rather than on the next restart. Nothing is said when there is no address to
give: a service that is not running, one somebody else runs somewhere this cannot
see, or a server whose real address its operator has not said.

Everything the service prints goes to `Logs/mapstique-service.log`, on its own so
it can be tailed while it runs:

```sh
tail -f VintagestoryData/Logs/mapstique-service.log
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
same directory as the client, this works: 13,886 of 14,091 blocks get a colour.

A **dedicated server download does not ship block textures**. Measured on a real
one: 46 PNGs against a full install's 9,587, and no `textures/block/` at all. The
mod has nothing to average, and the map renders as empty background.

So when the server cannot build a usable palette, it asks for one.

### The handshake

```
server boot ─► fingerprint the block registry
             ─► palette stored for this fingerprint, and good enough?  ──yes──► done
                          │no
                          ▼
      an admin joins, or /mapstique palette ─► "send me a palette"
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

**Only admins are asked**, and the privilege is re-checked when the palette
arrives, because a packet handler is an untrusted entry point regardless of who
was asked. One admin at a time, with a two-minute timeout before the next is
tried. Thirty seconds after an unanswered ask the server says so in the log.

**Palettes are merged, not replaced.** A client only has textures for the mods it
has installed, so two admins with different sets can between them produce a
complete palette where neither could alone. Asking stops once 90% of blocks have a
colour — a dedicated server scores about 15% on its own, a full install 99%, so
the threshold separates them cleanly.

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

All under `/mapstique`, requiring `controlserver`.

| | |
|---|---|
| `status` | where exports live, which source the palette came from, its coverage and fingerprint, whether that fingerprint is stale, where the world counts from, whether the map service is up, and when terrain was last written |
| `service [status\|start\|stop]` | the map service this mod runs. `start` runs it whatever `autostart` says, because somebody typing the command has asked |
| `palette [player]` | ask an online admin for a palette now, rather than waiting for the next join |
| `icons [player]` | ask an online admin for every marker picture again |
| `colors [player]` | ask an online admin what the skin part variants look like |
| `export` | write the surface of every loaded chunk immediately |

| `portrait [player]` | ask a player's client for a picture of their character |

Only that player's own machine can draw it — nobody else's has their seraph loaded
— so the server asks and the picture comes back. The map then shows it in their
card in place of a face assembled from three colours.

**A dot, not a slash, on a client.** The game keeps client and server commands in
separate registries and gives them different prefixes, so `.mapstique portrait`
is the client drawing itself unprompted while `/mapstique portrait` is the server
asking it to. The same holds for `palette`, `icons` and `colors`, which exist on
both sides for that reason.

Everything under `/mapstique` requires `controlserver`. Subcommands inherit it from
the root, which is how the game resolves a privilege down a command tree.

`/mapstique status` is the first thing to look at when the map looks wrong: it
says whether the palette is the server's own poor one or a good one from a client.

## Reading the surface

The rain height map gives each column's height without searching down from the
sky, but it marks where rain *stops* — commonly the air just above the ground.
Sampling it directly maps the sky and every column comes back as air, so the pump
steps down up to eight blocks until it finds something real.

Columns are written as `u16 blockId, i16 surfaceY, u8 temperature, u8 rainfall`,
six bytes each, 1024 to a chunk, after a per-chunk header carrying the season.
Temperature uses the game's own `Climate.DescaleTemperature`, and the season comes
from `IGameCalendar.GetSeasonRel`, so the renderer can sample the colour maps
exactly the way the game's shader does.

Season is recomputed for **every** chunk on every export, carried-over ones
included. A season that only advanced where players were standing would leave the
rest of the map stuck in whatever month it was last visited.

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

A marker deleted on the server stays on other players' maps until they relog.

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
./package.sh                     # a server archive: dist/mapstique_<version>.zip
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

The map service is looked for in `/var/tmp/rust-target/release/mapstique` and
`../rust/mapstique/target/release/mapstique`, or wherever `$MAPSTIQUE_SERVICE`
says. Packaging **stops** when there is none, rather than quietly producing a mod
that exports a map it cannot serve; `--no-service` is how to mean it.

The archive holds `modinfo.json` and `Mapstique.dll` at its root, plus the map
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

## Known gaps

`TODO.md` lists what is missing or fragile, including the palette transfer's size
handling, the cost of the terrain export on the server tick, and marker scoping.

## Reference material

Cloned alongside this repo for reference, all MIT: `~/Development/VS-LiveMap`,
`~/Development/WebCartographer`, plus the official `~/Development/vsapi` and
`~/Development/vssurvivalmod` sources — the last of which explained the leaves
behaviour.
