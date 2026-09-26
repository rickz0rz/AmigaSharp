#!/bin/sh
# Build the launcher and the listings tool as native programs (Native AOT). The people who use them do not need .NET.
#
# Usage: scripts/publish.sh [runtime identifier]
#
# The default runtime identifier is the one of this host, for example osx-arm64. Native AOT compiles only for the
# operating system of the host: build on macOS for osx-arm64 and osx-x64, on Linux for linux-x64 and linux-arm64,
# and on Windows for win-x64.
#
# The script writes the programs to dist/<runtime identifier>/. The launcher needs the SDL2 library next to it, so
# keep the files of the directory together.
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
RID=${1:-$(dotnet --info | awk '/^ *RID:/ { print $2; exit }')}
OUT=$ROOT/dist/$RID

rm -rf "$OUT"
for project in AmigaSharp.Launcher AmigaSharp.PrevueListings; do
    echo "Publishing $project for $RID."
    dotnet publish "$ROOT/$project" -c Release -r "$RID" -o "$OUT" -nologo -v quiet \
        -p:PublishAot=true -p:DebugType=None -p:StripSymbols=true
done

# The build writes the files of the configuration of the translator, which the native launcher does not use.
rm -f "$OUT"/*.runtimeconfig.json "$OUT"/*.pdb
rm -rf "$OUT"/*.dSYM
echo "The programs are in $OUT:"
ls -la "$OUT"
