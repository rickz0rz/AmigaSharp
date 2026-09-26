#!/bin/sh
# Run Prevue (ESQ) in a window, with the saved 2020 listings.
#
# The first run copies the drive to build/drive/ and unpacks the listing files there. ESQ writes to its drive, so
# the script never uses target-source/binaries directly. Delete build/drive/ to start again from the original drive.
#
# Arguments after the script name go to the launcher. For example:
#   scripts/run-esq.sh --scale 1
#   scripts/run-esq.sh --serial-port 0
#
# Environment variables:
#   ESQ_DATE   The date and the time of the Amiga at the start. The default is 2020-11-01T16:00, the date of the
#              saved listings.
#   ESQ_SCALE  The size of the window. The default is 2 (1536 by 960 pixels).
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
DRIVE=$ROOT/build/drive
ESQ=$ROOT/build/target/ESQ
LAUNCHER="dotnet run --project $ROOT/AmigaSharp.Launcher -c Release --"

if [ ! -f "$ESQ" ]; then
    "$ROOT/scripts/build-target.sh"
fi

if [ ! -d "$DRIVE" ]; then
    echo "Copying the drive to $DRIVE."
    mkdir -p "$ROOT/build"
    cp -R "$ROOT/target-source/binaries" "$DRIVE"
    $LAUNCHER unpack "$DRIVE"/curday.dat "$DRIVE"/nxtday.dat "$DRIVE"/PWI? --output "$DRIVE"
fi

exec $LAUNCHER "$ESQ" --listing "$ROOT/build/target/ESQ.lst" \
    --drive "$DRIVE" --volume "DH1=$DRIVE" \
    --assign DF0=DH1: --assign ENV=DH1: --arguments GA24005 --command-name esq \
    --date "${ESQ_DATE:-2020-11-01T16:00}" --scale "${ESQ_SCALE:-2}" "$@"
