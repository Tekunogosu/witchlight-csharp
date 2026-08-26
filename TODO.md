# Mapstique — todo

Work that is known and wanted but not done. Ordered roughly by how much it bites.

## Slice the palette by measured size, not entry count

The palette is currently split into packets of 8,000 blocks, which measured 66 KiB
each on a 45,418-block server. That is a proxy for the thing that matters, not the
thing itself: nothing stops a differently shaped palette — longer colour map
lists, a future field, a mod set with unusual proportions — from producing a slice
over the server's packet limit, and an oversized packet **disconnects the player
sending it** rather than failing quietly.

Serialize and measure instead. Fill a slice until it approaches a size ceiling
(~500 KiB, well under any server's limit), then start another. The count then
follows from the data rather than being guessed ahead of it.

Seen for real: a single 3.48 MB packet produced
`Client ... disconnected, too large packet of 3478354 bytes received`.

## Store the season outside the record

Regions bound a block change to one file, but not a season change. The season is
stored per chunk in the record header and is recomputed for every chunk on every
export, so when the year advances a step — about three times an hour on the
default calendar — every region is read back and rewritten to change one byte per
chunk.

A season is a function of latitude and the calendar, so it does not belong beside
the columns. A small file of its own, one entry per chunk, would be about nine
bytes per chunk against the 6,154 a record costs, and would leave terrain files
untouched by the passage of time. The renderer would then read the season from
there rather than from `Column`.

## Give the mod a configuration file

The only setting it has is `MAPSTIQUE_API_SOCKET`, read from the environment,
which moves the socket but changes nothing else — the export interval,
the seed radius and the coverage threshold are all constants. A server owner who
wants a thirty-second export to be five minutes has to rebuild the mod.

## Send only changed markers

Every player receives every marker they do not own, every 15 seconds, and clients
discard everything they already hold. Fine at a handful of markers; pointless
repetition at a few thousand. Key on the waypoint guid and send changes only.

## Give the map zoom levels

Tiles are drawn at one pixel per block at every zoom, and the browser scales them.
The number on screen therefore grows as the square of how far out the viewer is:
about fifty to fit a world, twelve thousand at 0.05 pixels per block, and eighty
thousand at the zoom-out limit — which is minutes of rendering and a browser
holding eighty thousand images. Threads divide that; they do not fix it.

A pyramid, each level half the resolution of the one below, keeps the count near
constant at every zoom. The viewer also asks for tiles outside the world's own
bounds, which is most of that eighty thousand and is a much smaller fix.

Its tile cache is also unbounded.

## Scope markers to groups

Every marker is shared with everyone. The export builds a packet per player in
`SharedServer.For(api, player)`, and per-player filtering belongs there — as does
the decision to stop sending owner uids to clients that have no use for them.
