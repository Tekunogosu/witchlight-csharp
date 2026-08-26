# Mapstique (server mod)

The version tracks the [map service](../rust/mapstique), and the two **must match
on minor version** — they share a file format and a socket protocol, and neither
reads what the other half of a different minor wrote.

While Mapstique is alpha, a format change **clears the map** on start rather than
upgrading it. It rebuilds as players explore. Read the release note before
upgrading a server whose map you would rather keep.

## 0.7.1

### Fixed

`/mapstique status` reported one health reading for both halves of the live data,
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
from the last post. `MAPSTIQUE_API_SOCKET` moves the socket; the service's
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

The version is logged on start and shown by `/mapstique status`, which also now
reports how many markers are saved on the server and whether the service is being
reached.
