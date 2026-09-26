#!/bin/sh
# Run Prevue (ESQ) in a window.
#
# The first run copies the drive to build/drive/ and unpacks the saved 2020 listing files there. ESQ writes to its
# drive, so the script never uses target-source/binaries directly. Delete build/drive/ to start again from the
# original drive.
#
# Arguments after the script name go to the launcher. For example:
#   scripts/run-esq.sh --scale 1
#   scripts/run-esq.sh --serial-port 0
#
# Environment variables:
#   CHANNELS_DVR  The address of a Channels DVR server, for example http://192.168.0.195:8089. The script then writes
#                 new listing files from the guide of the server before each run, and the Amiga uses the time of the
#                 host.
#   ESQ_DATE      The date and the time of the Amiga at the start. Without CHANNELS_DVR, the default is
#                 2020-11-01T16:00, the date of the saved listings.
#   ESQ_SCALE     The size of the window. The default is 2 (1536 by 960 pixels).
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

if [ -n "${CHANNELS_DVR:-}" ]; then
    dotnet run --project "$ROOT/AmigaSharp.PrevueListings" -c Release -- --server "$CHANNELS_DVR" --output "$DRIVE"
    DATE_OPTION=${ESQ_DATE:+--date $ESQ_DATE}
else
    DATE_OPTION="--date ${ESQ_DATE:-2020-11-01T16:00}"
fi

# DATE_OPTION is not in quotes, so that an empty value adds no argument.
# shellcheck disable=SC2086
exec $LAUNCHER "$ESQ" --listing "$ROOT/build/target/ESQ.lst" \
    --drive "$DRIVE" --volume "DH1=$DRIVE" \
    --assign DF0=DH1: --assign ENV=DH1: --arguments GA24005 --command-name esq \
    $DATE_OPTION --scale "${ESQ_SCALE:-2}" "$@"
