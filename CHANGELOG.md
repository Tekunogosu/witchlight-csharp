# Mapstique (server mod)

The version tracks the [map service](../rust/mapstique), and the two **must match
on minor version** — they share a file format and a socket protocol, and neither
reads what the other half of a different minor wrote.

While Mapstique is alpha, a format change **clears the map** on start rather than
upgrading it. It rebuilds as players explore. Read the release note before
upgrading a server whose map you would rather keep.

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

`/mapstique portrait` did not exist. The command was registered on the client, and
the game gives client commands a dot rather than a slash — so the one that was
added answered to `.mapstique portrait`, while `/mapstique portrait` went to the
server, which had no such subcommand.

The server has one now, shaped like every other thing it asks a client for: it
sends a request, the client draws, and the picture comes back. `.mapstique
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

`/mapstique portrait [player]` asks a player's client to draw them. It renders the
seraph the way the character screen does — into a buffer of its own rather than
onto the screen — and sends the result to the server as a PNG. `.mapstique
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

Every `/mapstique` command already required `controlserver`; subcommands inherit it
from the root, which is how the game's command tree resolves a privilege. The new
one is no different, and the client half now says so rather than letting a player
run a command whose result the server will quietly refuse.

Skin colours arriving from a player who is not an admin were dropped in silence
while every sibling said so in the log. They now say so too.

## 0.14.0

### The server log says where the map is

`[mapstique] the map is being served at http://192.168.1.145:8080` now appears in
the server's own log, beside everything else an operator is already reading,
rather than only in the service's. The service publishes the addresses it answers
on as it binds and this waits for that, because which addresses a bind of
`0.0.0.0` actually covers is the service's question and it has already answered it.

### And so does a player joining

A player is told where the map is as they join. `announce` in
`ModConfig/mapstique.conf` turns it off, and `announce_url` says what to tell them
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
file is brought up to date, values kept, with `mapstique -c <file> --save-config -p`.

## 0.13.0

### This starts the map service

The service binary rides along in this archive and is started once the world is
ready, so installing the mod installs the map. It is still a separate program —
this half knows the game, that half knows pixels, and a Vintage Story update can
only break this one — but it is no longer a second thing to fetch, configure and
remember to start.

`ModConfig/mapstique.conf` holds its settings, written by the service itself on a
first run so that the file's format keeps the one owner it always had. Every
option is live and editable there, including the new `autostart`: turn it off to
run `mapstique serve` yourself, which is what a map that should stay up while the
game server is down wants.

Everything the service prints goes to `Logs/mapstique-service.log`, on its own so
it can be tailed while it runs without a game server's log interleaved through it.

`/mapstique service` says whether it is up; `start` and `stop` do what they say,
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

This is numbered 0.12.1 to match [mapstique 0.12.1](../rust/mapstique). The skin
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
palettes and icons. `/mapstique colors` asks for it again; `/mapstique colors` on
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

`/mapstique status` reports whether those colours can actually be read, because a
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
marker is dropped rather than sent. `/mapstique icons [player]` asks for
everything again, which is the way back if an icon on disk is wrong rather than
missing. Icons from different admins are merged, like palettes.

### Health, food, and where the world counts from

Who is online now travels with how much health and food they have, and with the
colours they chose for their character — all of it already in the entity's watched
attributes, so nothing is asked of anyone to show it.

`world.json` records the world's spawn position, because the game shows every
coordinate a player sees relative to it and a map that did not would agree with
nothing on their screen.

`/mapstique status` reports how many marker pictures are stored.

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
