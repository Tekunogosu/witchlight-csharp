# Witchlight — todo

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

Its only settings are `WITCHLIGHT_API_BIND` and `WITCHLIGHT_API_TOKEN`, read from
the environment, which move the API channel off loopback and change nothing else —
the export interval, the seed radius and the coverage threshold are all constants.
A server owner who wants a thirty-second export to be five minutes has to rebuild
the mod.

## Send only changed markers

Every player receives every marker they do not own, every 15 seconds, and clients
discard everything they already hold. Fine at a handful of markers; pointless
repetition at a few thousand. Key on the waypoint guid and send changes only.

## Scope markers to groups

A marker is its owner's or everybody's, and there is nothing between the two. The
per-player filtering already happens in `SharedServer.For` and in the service's
`Live::body`, so a group is a third answer to a question both of them already ask
rather than a new mechanism — what is missing is somewhere to keep who is in one,
and a way for a player to say so without leaving the game.

Sending owner uids to clients that have no use for them belongs in the same pass.
