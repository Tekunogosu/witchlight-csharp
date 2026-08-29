# Witchlight (server mod)

The version tracks the [map service](../rust/witchlight), and the two **must match
on minor version** — they share a file format and a socket protocol, and neither
reads what the other half of a different minor wrote.

While Witchlight is alpha, a format change **clears the map** on start rather than
upgrading it. It rebuilds as players explore. Read the release note before
upgrading a server whose map you would rather keep.

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
