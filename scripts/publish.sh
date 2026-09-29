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
#
# Environment variables:
#   EMBEDDED_PROGRAM  An executable to translate and compile into the launcher, for example build/target/ESQ. The
#                     launcher then runs this program from its translation, and other programs in the interpreter.
#                     The launcher contains the code of the program, so give it only to people who can have it.
#   EMBEDDED_LISTING  The vasm listing of EMBEDDED_PROGRAM (optional, but it gives a better translation).
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
RID=${1:-$(dotnet --info | awk '/^ *RID:/ { print $2; exit }')}
OUT=$ROOT/dist/$RID

EMBEDDED=""
if [ -n "${EMBEDDED_PROGRAM:-}" ]; then
    EMBEDDED="-p:EmbeddedProgram=$(cd "$(dirname "$EMBEDDED_PROGRAM")" && pwd)/$(basename "$EMBEDDED_PROGRAM")"
    if [ -n "${EMBEDDED_LISTING:-}" ]; then
        EMBEDDED="$EMBEDDED -p:EmbeddedListing=$(cd "$(dirname "$EMBEDDED_LISTING")" && pwd)/$(basename "$EMBEDDED_LISTING")"
    fi
    echo "The launcher gets the translation of $EMBEDDED_PROGRAM."
fi

rm -rf "$OUT"
for project in AmigaSharp.Launcher AmigaSharp.PrevueListings; do
    echo "Publishing $project for $RID."
    # EMBEDDED is not in quotes, so that each property is a separate argument. Paths with spaces are not supported.
    # shellcheck disable=SC2086
    dotnet publish "$ROOT/$project" -c Release -r "$RID" -o "$OUT" -nologo -v quiet \
        -p:PublishAot=true -p:DebugType=None -p:StripSymbols=true $EMBEDDED
done

# The build writes the files of the configuration of the translator, which the native launcher does not use.
rm -f "$OUT"/*.runtimeconfig.json "$OUT"/*.pdb
rm -rf "$OUT"/*.dSYM

# The script and the instructions for the people who use the programs.
case "$RID" in
    win-*) cp "$ROOT/scripts/dist/run-prevue.ps1" "$ROOT/scripts/dist/run-prevue.cmd" "$OUT"/ ;;
    *) cp "$ROOT/scripts/dist/run-prevue.sh" "$OUT"/ && chmod +x "$OUT/run-prevue.sh" ;;
esac
cp "$ROOT/scripts/dist/README.txt" "$OUT"/
echo "The programs are in $OUT:"
ls -la "$OUT"
