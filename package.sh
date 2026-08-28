#!/usr/bin/env bash
#
# Packages the mod the way Vintage Story wants it: modinfo.json and the assembly
# at the root of a zip named <modid>_<version>.zip. That is the layout the mod
# database expects on upload, and the same file a server can be handed directly.
#
# One assembly, two archives. The mod runs on both sides and decides for itself
# which half to start, so the code is the same either way; what differs is that a
# server is handed the map service and a client has no use for a megabyte of it.
#
#   ./package.sh                        a server archive, into dist/
#   ./package.sh --target client        the same mod without the map service
#   ./package.sh --install DIR          also copy it into a Mods folder
#   ./package.sh --no-build             package whatever was built last
#   ./package.sh --service FILE         use this map service binary
#   ./package.sh --no-service           a server archive without one
#   ./package.sh --notices FILE         use this third-party notice file
#
# The client archive is named <modid>_<version>_client.zip so that both can sit in
# dist/ at once rather than one quietly overwriting the other.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$here/Witchlight"
modinfo="$project/modinfo.json"
out="$here/dist"
build=1
install_to=""
service="${WITCHLIGHT_SERVICE:-}"
notices="${WITCHLIGHT_NOTICES:-}"
want_service=1
target=server

# Where a release build of the map service usually lands. Named here rather than
# guessed at from the mod's own layout, so that a machine keeping its Rust output
# somewhere else needs one flag and not a rearrangement.
service_candidates=(
    "/var/tmp/rust-target/release/witchlight"
    "$here/../rust/witchlight/target/release/witchlight"
)

# The notice for everything compiled into that binary, which its own repository
# generates and keeps. Looked up separately because the binary can be named with
# --service from anywhere, while the notice always belongs with the source.
notices_candidates=(
    "$here/../rust/witchlight/THIRD-PARTY.md"
)

while [ $# -gt 0 ]; do
    case "$1" in
        --install) install_to="${2:?--install needs a directory}"; shift 2 ;;
        --out)     out="${2:?--out needs a directory}"; shift 2 ;;
        --no-build) build=0; shift ;;
        --service) service="${2:?--service needs a file}"; shift 2 ;;
        --notices) notices="${2:?--notices needs a file}"; shift 2 ;;
        --no-service) want_service=0; shift ;;
        --target)  target="${2:?--target needs client or server}"; shift 2 ;;
        # Everything the header says, however long it grows: a line range here
        # goes stale the first time a flag is added and says nothing about it.
        -h|--help) awk 'NR>1 && /^#/ { sub(/^# ?/, ""); print; next } NR>1 { exit }' \
                       "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "package: unknown argument $1" >&2; exit 2 ;;
    esac
done

case "$target" in
    server) ;;
    # A client has nothing to serve. Stated here rather than by the caller also
    # remembering --no-service, so that --target is the whole of the decision.
    client) want_service=0 ;;
    *) echo "package: --target takes client or server, not $target" >&2; exit 2 ;;
esac

# The mod's own metadata is the single source of truth for what this is called.
modid=$(jq -r '.modid' "$modinfo")
version=$(jq -r '.version' "$modinfo")
side=$(jq -r '.side' "$modinfo")
[ "$modid" != "null" ] && [ "$version" != "null" ] || {
    echo "package: $modinfo needs a modid and a version" >&2
    exit 1
}

if [ "$build" -eq 1 ]; then
    # Quiet while it works, and the whole log the moment it does not.
    log=$(mktemp)
    if ! dotnet build "$project/Witchlight.csproj" -c Release --nologo -v quiet > "$log" 2>&1; then
        cat "$log" >&2
        rm -f "$log"
        echo "package: build failed" >&2
        exit 1
    fi
    rm -f "$log"
fi

release="$project/bin/Release"
assembly="$release/Witchlight.dll"
[ -f "$assembly" ] || {
    echo "package: $assembly is missing — build first, or drop --no-build" >&2
    exit 1
}

# A mod that cannot start the map is the thing this is meant to prevent, so a
# missing service stops the packaging rather than shipping quietly without one.
if [ "$want_service" -eq 1 ] && [ -z "$service" ]; then
    for candidate in "${service_candidates[@]}"; do
        [ -f "$candidate" ] && { service="$candidate"; break; }
    done
fi

if [ "$want_service" -eq 1 ] && [ ! -f "$service" ]; then
    echo "package: no map service binary found. Build it with" >&2
    echo "  cargo build --release   (in the witchlight service repository)" >&2
    echo "or name one with --service FILE, or package without it with --no-service." >&2
    echo "Looked in:" >&2
    printf '  %s\n' "${service_candidates[@]}" >&2
    exit 1
fi

# Every permissive licence in the service binary asks the same thing of a copy:
# that the notice travel with it. Shipping the binary without one is the failure
# this refuses to make quietly, so a missing notice stops the packaging exactly
# as a missing binary does.
if [ "$want_service" -eq 1 ] && [ -z "$notices" ]; then
    for candidate in "${notices_candidates[@]}"; do
        [ -f "$candidate" ] && { notices="$candidate"; break; }
    done
fi

if [ "$want_service" -eq 1 ] && [ ! -f "$notices" ]; then
    echo "package: no third-party notice found for the map service. Generate it with" >&2
    echo "  ./licenses.py > THIRD-PARTY.md   (in the witchlight service repository)" >&2
    echo "or name one with --notices FILE." >&2
    echo "Looked in:" >&2
    printf '  %s\n' "${notices_candidates[@]}" >&2
    exit 1
fi

[ -f "$here/LICENSE" ] || {
    echo "package: $here/LICENSE is missing" >&2
    exit 1
}

if [ "$want_service" -eq 1 ]; then :; else service=""; notices=""; fi

mkdir -p "$out"
suffix=""
[ "$target" = "client" ] && suffix="_client"
archive="$out/${modid}_${version}${suffix}.zip"

# Everything the game reads, at the root of the zip. Optional pieces are included
# when they exist so adding an icon or assets later needs no change here.
python3 - "$archive" "$modinfo" "$assembly" "$project" "$service" "$here/LICENSE" "$notices" <<'PY'
import pathlib, sys, zipfile

archive, modinfo, assembly, project = (pathlib.Path(p) for p in sys.argv[1:5])
service, licence, notices = sys.argv[5], pathlib.Path(sys.argv[6]), sys.argv[7]
included = []

# Where the mod looks for it. One platform for now; a second is a second entry
# under service/ and the mod picking the one that matches the machine.
SERVICE_AT = "service/linux-x64/witchlight"

with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zip_file:
    for path in (modinfo, assembly):
        zip_file.write(path, path.name)
        included.append(path.name)

    # The mod's own terms, in both archives: the assembly is this project's code
    # whether or not a map service travels with it.
    zip_file.write(licence, licence.name)
    included.append(licence.name)

    if service:
        zip_file.write(service, SERVICE_AT)
        included.append(SERVICE_AT)

        # Only where the binary is. A client archive links none of it and would
        # be claiming to carry code it does not.
        zip_file.write(notices, "THIRD-PARTY.md")
        included.append("THIRD-PARTY.md")

    icon = project / "modicon.png"
    if icon.exists():
        zip_file.write(icon, icon.name)
        included.append(icon.name)

    assets = project / "assets"
    for path in sorted(assets.rglob("*")) if assets.is_dir() else []:
        if path.is_file():
            name = str(path.relative_to(project))
            zip_file.write(path, name)
            included.append(name)

print("\n".join(f"  {name}" for name in included))
PY

size=$(stat -c%s "$archive")
echo "packaged $modid $version for a $target ($side), $((size / 1024)) KiB"
echo "  $archive"

if [ -n "$install_to" ]; then
    mkdir -p "$install_to"
    cp "$archive" "$install_to/${modid}.zip"
    echo "installed to $install_to/${modid}.zip"
fi
