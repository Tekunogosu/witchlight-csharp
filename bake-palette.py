#!/usr/bin/env python3
"""Records a vanilla palette into the resource the mod ships.

A dedicated server's install has almost no block textures in it, so the palette
it builds for itself colours nearly nothing and the map stays flat until a player
joins with the mod and sends one. The base game's colours are the same on every
such server, so the mod carries a recording of them and seeds itself with it —
see Witchlight/Palette/Vanilla.cs for what is done with the file this writes.

The recording is made from a real palette.json rather than derived here: working
out what colour a block is takes the game's own asset pipeline, and a second
implementation of that rule would drift from the first the week it was written.

To refresh it for a new game version:

  1. Run a server with nothing but this mod installed, from a full game install
     so that the textures are there, and join it with a vanilla client.
  2. `.wl palette` on the client, so the colours come off a full asset set.
  3. Point this at the palette.json that server wrote.

    ./bake-palette.py /path/to/witchlight/palette.json

It refuses anything that is not vanilla or not complete, because a recording with
a mod's blocks in it would seed every server with colours for blocks they do not
have, and one with gaps in it would seed the very holes it exists to prevent.
"""

import argparse
import gzip
import json
import pathlib
import sys

# What travels. The block id is this world's and means nothing on another
# server; the fingerprint and the mod stamp say which registry it was built
# against, which is the one thing a recording must not claim.
KEPT = ("Rgb", "Invisible", "ClimateMap", "SeasonMap")

HERE = pathlib.Path(__file__).resolve().parent
RESOURCE = HERE / "Witchlight" / "Palette" / "vanilla.json.gz"


def refuse(why):
    print(f"bake-palette: {why}", file=sys.stderr)
    raise SystemExit(1)


def main():
    parse = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parse.add_argument("palette", type=pathlib.Path, help="a palette.json to record")
    parse.add_argument(
        "-o", "--out", type=pathlib.Path, default=RESOURCE,
        help=f"where to write it (default: {RESOURCE.relative_to(HERE)})")
    said = parse.parse_args()

    try:
        source = json.loads(said.palette.read_text())
    except (OSError, ValueError) as error:
        refuse(f"could not read {said.palette}: {error}")

    blocks = source.get("Blocks") or {}
    if not blocks:
        refuse(f"{said.palette} has no blocks in it")

    modded = sorted(code for code in blocks if not code.startswith("game:"))
    if modded:
        refuse(
            f"{len(modded)} block(s) in {said.palette} are not the base game's — "
            f"record this from a server running nothing but witchlight. First: "
            + ", ".join(modded[:5]))

    gaps = sorted(
        code for code, entry in blocks.items()
        if entry.get("Rgb") is None and entry.get("Invisible") is not True)
    if gaps:
        refuse(
            f"{len(gaps)} block(s) in {said.palette} draw something and have no colour, so "
            f"this palette is not complete enough to ship. First: " + ", ".join(gaps[:5]))

    recorded = {
        "Version": source.get("Version", 1),
        "GameVersion": source.get("GameVersion", ""),
        "Blocks": {
            code: {name: entry[name] for name in KEPT if name in entry}
            for code, entry in sorted(blocks.items())
        },
    }

    body = gzip.compress(
        json.dumps(recorded, separators=(",", ":")).encode("utf-8"), 9, mtime=0)
    said.out.parent.mkdir(parents=True, exist_ok=True)
    said.out.write_bytes(body)

    coloured = sum(1 for entry in blocks.values() if entry.get("Rgb"))
    print(
        f"{said.out}: {len(blocks)} blocks, {coloured} coloured, "
        f"{len(blocks) - coloured} that draw nothing, "
        f"game {recorded['GameVersion']}, {len(body)} bytes")


if __name__ == "__main__":
    main()
