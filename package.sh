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
#   ./package.sh --service-repo DIR     where the map service source lives
#
# The client archive is named <modid>_<version>_client.zip so that both can sit in
# dist/ at once rather than one quietly overwriting the other.
#
# Both halves are built here, because they are one release. The map service is a
# separate program in a separate repository, so where that repository is has to be
# said: `WITCHLIGHT_SERVICE_REPO`, in the environment or in a `.env` file beside
# this script. That file is not committed — a path on one machine is not a fact
# about the project — and `.env.example` is what it should look like.
#
# Building it here is what makes "one archive, one release" true of the build and
# not only of the version check below. The check catches a version bumped without
# a rebuild; nothing could catch a source file edited without one, so the rebuild
# is no longer something to remember.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Where this machine keeps things, which is nobody else's business and so is not
# in the repository. Read before the variables below are settled, and only into
# names nothing has already set — so a one-off `WITCHLIGHT_SERVICE_REPO=... ./package.sh`
# wins over the file, which is the way round every other tool reads one.
env_file="$here/.env"
if [ -f "$env_file" ]; then
    while IFS='=' read -r key value || [ -n "$key" ]; do
        # Whitespace and quotes are how people write these, and neither is part
        # of the name or the value.
        key="${key#"${key%%[![:space:]]*}"}"; key="${key%"${key##*[![:space:]]}"}"
        value="${value#"${value%%[![:space:]]*}"}"; value="${value%"${value##*[![:space:]]}"}"
        value="${value%\"}"; value="${value#\"}"
        value="${value%\'}"; value="${value#\'}"

        # Anything that is not a name and a value is a comment, a blank line, or
        # a mistake, and none of the three is worth stopping the packaging for —
        # this file is a convenience, not a manifest. A name that is not a shell
        # name is skipped rather than assigned, because `printf -v` would fail on
        # it and take the whole run down with it.
        case "$key" in
            ''|'#'*) continue ;;
            *[!A-Za-z0-9_]*|[0-9]*) continue ;;
        esac

        # Only into names nothing has already set, so the environment wins.
        [ -n "${!key-}" ] || printf -v "$key" '%s' "$value"
        export "$key"
    done < "$env_file"
fi

project="$here/Witchlight"
modinfo="$project/modinfo.json"
out="$here/dist"
build=1
install_to=""
service="${WITCHLIGHT_SERVICE:-}"
notices="${WITCHLIGHT_NOTICES:-}"
# The map service's own repository, so this script can build it as well as bundle
# it. Empty means nothing said, which is an error only when a service is wanted
# and no binary was named outright.
service_repo="${WITCHLIGHT_SERVICE_REPO:-}"
want_service=1
target=server

# What the map service's binary is called once it is built.
service_name=witchlight

while [ $# -gt 0 ]; do
    case "$1" in
        --install) install_to="${2:?--install needs a directory}"; shift 2 ;;
        --out)     out="${2:?--out needs a directory}"; shift 2 ;;
        --no-build) build=0; shift ;;
        --service) service="${2:?--service needs a file}"; shift 2 ;;
        --service-repo) service_repo="${2:?--service-repo needs a directory}"; shift 2 ;;
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

# The map service's repository, said or refused. A path on one machine is not a
# fact about the project, so it is never guessed at: `.env` is where it belongs,
# and the message says so rather than leaving somebody to find that out.
needs_repo() {
    [ -n "$service_repo" ] || {
        echo "package: nothing says where the map service is." >&2
        echo "  Put its repository in $env_file:" >&2
        echo "    WITCHLIGHT_SERVICE_REPO=/path/to/rust/witchlight" >&2
        echo "  or pass --service-repo DIR, or name a built binary with --service FILE," >&2
        echo "  or package without one with --no-service. See .env.example." >&2
        exit 1
    }
    [ -f "$service_repo/Cargo.toml" ] || {
        echo "package: $service_repo is not the map service's repository" >&2
        echo "  (no Cargo.toml in it)" >&2
        exit 1
    }
}

# Where cargo actually put it — asked of cargo rather than worked out from the
# repository's layout. `CARGO_TARGET_DIR`, a `.cargo/config.toml` and a `target`
# symlink can each send the output somewhere else, and a path assembled here
# would be a second opinion on a question cargo already answers.
service_binary() {
    local target_dir
    target_dir=$(cargo metadata --format-version 1 --no-deps \
        --manifest-path "$service_repo/Cargo.toml" 2>/dev/null | jq -r '.target_directory')
    [ -n "$target_dir" ] && [ "$target_dir" != "null" ] || {
        echo "package: cargo could not say where it builds $service_repo" >&2
        exit 1
    }
    echo "$target_dir/release/$service_name"
}

if [ "$build" -eq 1 ]; then
    # Quiet while it works, and the whole log the moment it does not.
    log=$(mktemp)

    # The map service first, because it is the half that takes twenty seconds and
    # the half whose failure is worth seeing before anything else has happened.
    # Skipped for a client archive, which carries none of it.
    if [ "$want_service" -eq 1 ] && [ -z "$service" ]; then
        needs_repo
        if ! cargo build --release --manifest-path "$service_repo/Cargo.toml" \
                > "$log" 2>&1; then
            cat "$log" >&2
            rm -f "$log"
            echo "package: the map service did not build" >&2
            exit 1
        fi
    fi

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
    needs_repo
    service=$(service_binary)
fi

if [ "$want_service" -eq 1 ] && [ ! -f "$service" ]; then
    echo "package: no map service binary at $service." >&2
    echo "  Drop --no-build and it is built from $service_repo," >&2
    echo "  or name one outright with --service FILE," >&2
    echo "  or package without it with --no-service." >&2
    exit 1
fi

# The two halves ship as one archive and are one release, so they carry one
# version. Checked against the binary itself rather than against a second file
# saying what it should be: the failure this catches is a mod rebuilt while the
# service was not, which leaves a map whose page reports the version before last
# — and, because the viewer's assets used to be addressed by that number, a
# browser that never fetched the new ones at all.
if [ "$want_service" -eq 1 ]; then
    built=$("$service" --version 2>/dev/null | awk '{print $NF}')
    if [ "$built" != "$version" ]; then
        echo "package: the map service is $built and the mod is $version." >&2
        echo "  They ship as one archive and must carry one version." >&2
        echo "  Set version in $modinfo and in the service's Cargo.toml to match," >&2
        echo "  then rebuild the service with: cargo build --release" >&2
        echo "  service: $service" >&2
        exit 1
    fi
fi

# Every permissive licence in the service binary asks the same thing of a copy:
# that the notice travel with it. Shipping the binary without one is the failure
# this refuses to make quietly, so a missing notice stops the packaging exactly
# as a missing binary does.
#
# It lives with the source rather than beside the binary, which is why it is
# looked for separately: a binary can be named with --service from anywhere, and
# the notice always belongs to the repository that compiled it.
if [ "$want_service" -eq 1 ] && [ -z "$notices" ] && [ -n "$service_repo" ]; then
    notices="$service_repo/THIRD-PARTY.md"
fi

if [ "$want_service" -eq 1 ] && [ ! -f "${notices:-}" ]; then
    echo "package: no third-party notice found for the map service. Generate it with" >&2
    echo "  ./licenses.py > THIRD-PARTY.md   (in the map service repository)" >&2
    echo "or name one with --notices FILE." >&2
    [ -n "$service_repo" ] && echo "Looked in: $service_repo/THIRD-PARTY.md" >&2
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
