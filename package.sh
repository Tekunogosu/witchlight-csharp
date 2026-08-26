#!/usr/bin/env bash
#
# Packages the mod the way Vintage Story wants it: modinfo.json and the assembly
# at the root of a zip named <modid>_<version>.zip. That is the layout the mod
# database expects on upload, and the same file a server can be handed directly.
#
#   ./package.sh                        build and package into dist/
#   ./package.sh --install DIR          also copy it into a Mods folder
#   ./package.sh --no-build             package whatever was built last
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
project="$here/Mapstique"
modinfo="$project/modinfo.json"
out="$here/dist"
build=1
install_to=""

while [ $# -gt 0 ]; do
    case "$1" in
        --install) install_to="${2:?--install needs a directory}"; shift 2 ;;
        --out)     out="${2:?--out needs a directory}"; shift 2 ;;
        --no-build) build=0; shift ;;
        -h|--help) sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "package: unknown argument $1" >&2; exit 2 ;;
    esac
done

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
    if ! dotnet build "$project/Mapstique.csproj" -c Release --nologo -v quiet > "$log" 2>&1; then
        cat "$log" >&2
        rm -f "$log"
        echo "package: build failed" >&2
        exit 1
    fi
    rm -f "$log"
fi

release="$project/bin/Release"
assembly="$release/Mapstique.dll"
[ -f "$assembly" ] || {
    echo "package: $assembly is missing — build first, or drop --no-build" >&2
    exit 1
}

mkdir -p "$out"
archive="$out/${modid}_${version}.zip"

# Everything the game reads, at the root of the zip. Optional pieces are included
# when they exist so adding an icon or assets later needs no change here.
python3 - "$archive" "$modinfo" "$assembly" "$project" <<'PY'
import pathlib, sys, zipfile

archive, modinfo, assembly, project = (pathlib.Path(p) for p in sys.argv[1:5])
included = []

with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zip_file:
    for path in (modinfo, assembly):
        zip_file.write(path, path.name)
        included.append(path.name)

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
echo "packaged $modid $version ($side), $((size / 1024)) KiB"
echo "  $archive"

if [ -n "$install_to" ]; then
    mkdir -p "$install_to"
    cp "$archive" "$install_to/${modid}.zip"
    echo "installed to $install_to/${modid}.zip"
fi
