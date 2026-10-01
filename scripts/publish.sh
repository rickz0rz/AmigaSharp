#!/bin/sh
# Build the launcher for Prevue and the listings tool as native programs (Native AOT). The people who use them do not
# need .NET. The launcher for Prevue also runs other AmigaOS programs. To build only the generic launcher, run:
#   dotnet publish AmigaSharp.Launcher -c Release -r <runtime identifier> -p:PublishAot=true
#
# Usage: scripts/publish.sh [runtime identifier]
#
# The default runtime identifier is the one of this host, for example osx-arm64. Native AOT compiles only for the
# operating system of the host: on macOS for osx-arm64 and osx-x64, on Linux for linux-x64 and linux-arm64, and on
# Windows for win-x64 and win-arm64. For another operating system, for example win-x64 on macOS, the script makes a
# self-contained .NET build: a directory with the programs, their libraries and the .NET runtime. The users do not
# need .NET for it either. It is larger (about 120 MB), and its launcher translates programs at run time, so it runs
# them faster than a native launcher.
#
# The script writes the programs to dist/<runtime identifier>/. The launcher needs the SDL2 library next to it, so
# keep the files of the directory together.
#
# Environment variables:
#   EMBEDDED_PROGRAM  An executable to translate and compile into the launcher, for example build/target/ESQ. The
#                     launcher then runs this program from its translation, and other programs in the interpreter.
#                     The launcher contains the code of the program, so give it only to people who can have it.
#   EMBEDDED_LISTING  The vasm listing of EMBEDDED_PROGRAM (optional, but it gives a better translation).
#   EMBEDDED_KNOWN_CODE
#                     A map of the code that ran (the launcher option --code-map writes one), for an EMBEDDED_PROGRAM
#                     without a listing. The translation then has the code that the map gives.
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
HOST_RID=$(dotnet --info | awk '/^ *RID:/ { print $2; exit }')
RID=${1:-$HOST_RID}
OUT=$ROOT/dist/$RID

# The operating system is the first part of the runtime identifier: osx, linux or win.
if [ "${RID%%-*}" = "${HOST_RID%%-*}" ]; then
    BUILD="-p:PublishAot=true -p:StripSymbols=true"
    NATIVE=1
else
    BUILD="--self-contained -p:PublishReadyToRun=true"
    NATIVE=0
    echo "Native AOT cannot compile for ${RID%%-*} on ${HOST_RID%%-*}. The build is a self-contained .NET build."
fi

EMBEDDED=""
if [ -n "${EMBEDDED_PROGRAM:-}" ]; then
    EMBEDDED="-p:EmbeddedProgram=$(cd "$(dirname "$EMBEDDED_PROGRAM")" && pwd)/$(basename "$EMBEDDED_PROGRAM")"
    if [ -n "${EMBEDDED_LISTING:-}" ]; then
        EMBEDDED="$EMBEDDED -p:EmbeddedListing=$(cd "$(dirname "$EMBEDDED_LISTING")" && pwd)/$(basename "$EMBEDDED_LISTING")"
    fi
    if [ -n "${EMBEDDED_KNOWN_CODE:-}" ]; then
        EMBEDDED="$EMBEDDED -p:EmbeddedKnownCode=$(cd "$(dirname "$EMBEDDED_KNOWN_CODE")" && pwd)/$(basename "$EMBEDDED_KNOWN_CODE")"
    fi
    echo "The launcher gets the translation of $EMBEDDED_PROGRAM."
fi

rm -rf "$OUT"
for project in AmigaSharp.PrevueLauncher AmigaSharp.PrevueListings; do
    echo "Publishing $project for $RID."
    # BUILD and EMBEDDED are not in quotes, so that each option is a separate argument. Paths with spaces are not
    # supported.
    # shellcheck disable=SC2086
    dotnet publish "$ROOT/$project" -c Release -r "$RID" -o "$OUT" -nologo -v quiet -p:DebugType=None \
        $BUILD $EMBEDDED
done

# The build also writes the translator as a program of this host. The launcher uses only its library.
rm -f "$OUT"/AmigaSharp.Translator "$OUT"/AmigaSharp.Translator.exe "$OUT"/AmigaSharp.Translator.runtimeconfig.json \
    "$OUT"/AmigaSharp.Translator.deps.json "$OUT"/*.pdb
rm -rf "$OUT"/*.dSYM
if [ "$NATIVE" = 1 ]; then
    # A native program does not use the configuration files of .NET.
    rm -f "$OUT"/*.runtimeconfig.json
fi

# The script and the instructions for the people who use the programs.
case "$RID" in
    win-*) cp "$ROOT/scripts/dist/run-prevue.ps1" "$ROOT/scripts/dist/run-prevue.cmd" "$OUT"/ ;;
    *) cp "$ROOT/scripts/dist/run-prevue.sh" "$OUT"/ && chmod +x "$OUT/run-prevue.sh" ;;
esac
cp "$ROOT/scripts/dist/README.txt" "$OUT"/
echo "The programs are in $OUT:"
ls -la "$OUT"
