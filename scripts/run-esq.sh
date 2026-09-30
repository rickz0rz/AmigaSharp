#!/bin/sh
# Run Prevue (ESQ) in a window.
#
# The first run copies the drive to build/drive/ and unpacks the saved 2020 listing files there. ESQ writes to its
# drive, so the script never uses target-source/binaries directly. Delete build/drive/ to start again from the
# original drive. With CHANNELS_DVR, the script uses a separate copy in build/drive-channels-dvr/.
#
# The 68000 runs as fast as the host can until the main loop of ESQ starts, and then at its real speed. Add
# --fast-cpu to run as fast as the host can all the time.
#
# Arguments after the script name go to the launcher. For example:
#   scripts/run-esq.sh --scale 1
#   scripts/run-esq.sh --serial-port 0
#
# Environment variables:
#   CHANNELS_DVR  The address of a Channels DVR server, for example http://channels-dvr.local:8089. The script then writes
#                 new listing files from the guide of the server before each run, and the Amiga uses the time of the
#                 host. While ESQ runs, the listings tool sends the changes of the guide to the serial port (TCP port
#                 5400), so the grid stays current.
#   CHANNELS_DVR_INTERVAL
#                 The minutes between two reads of the guide. The default is 10.
#   CHANNELS_DVR_PREMIUM
#                 The premium channels: channel numbers or call signs, with commas between them, for example
#                 222,HBOHD. Their programs have a red background.
#   ESQ_DATE      The date and the time of the Amiga at the start. Without CHANNELS_DVR, the default is
#                 2020-11-01T16:00, the date of the saved listings. With CHANNELS_DVR, the listings tool uses it
#                 too, to choose the current and the next broadcast day.
#   ESQ_SCALE     The size of the window. The default is 2 (1536 by 960 pixels).
set -eu

ROOT=$(cd "$(dirname "$0")/.." && pwd)
DRIVE=$ROOT/build/drive
if [ -n "${CHANNELS_DVR:-}" ]; then
    DRIVE=$ROOT/build/drive-channels-dvr
fi
ESQ=$ROOT/build/target/ESQ
LAUNCHER="dotnet run --project $ROOT/AmigaSharp.PrevueLauncher -c Release --"

if [ ! -f "$ESQ" ]; then
    "$ROOT/scripts/build-target.sh"
fi

if [ ! -d "$DRIVE" ]; then
    echo "Copying the drive to $DRIVE."
    mkdir -p "$ROOT/build"
    cp -R "$ROOT/target-source/binaries" "$DRIVE"
    $LAUNCHER unpack "$DRIVE"/curday.dat "$DRIVE"/nxtday.dat "$DRIVE"/PWI? --output "$DRIVE"
fi

EXTRA_OPTIONS=""
if [ -n "${CHANNELS_DVR:-}" ]; then
    # The listings tool writes the files, makes the ready file, and then sends the changes of the guide to the
    # serial bridge of the launcher until the script stops it.
    READY=$DRIVE/.listings-ready
    rm -f "$READY"
    dotnet build "$ROOT/AmigaSharp.PrevueListings" -c Release -v quiet -nologo >/dev/null
    dotnet "$ROOT/AmigaSharp.PrevueListings/bin/Release/net10.0/AmigaSharp.PrevueListings.dll" \
        --server "$CHANNELS_DVR" --output "$DRIVE" --ready "$READY" --serve localhost:5400 \
        --interval "${CHANNELS_DVR_INTERVAL:-10}" ${CHANNELS_DVR_PREMIUM:+--premium "$CHANNELS_DVR_PREMIUM"} \
        ${ESQ_DATE:+--clock $ESQ_DATE} &
    LISTINGS=$!
    trap 'kill $LISTINGS 2>/dev/null' EXIT INT TERM
    while [ ! -f "$READY" ]; do
        if ! kill -0 "$LISTINGS" 2>/dev/null; then
            echo "error: the listings tool stopped." >&2
            exit 1
        fi
        sleep 1
    done
    # A feed of changes can be large, for example the listings of the next day. At 4 times 2400 baud, ESQ parses the
    # bytes faster than they arrive.
    EXTRA_OPTIONS="--serial-speed 4 ${ESQ_DATE:+--date $ESQ_DATE}"
else
    EXTRA_OPTIONS="--date ${ESQ_DATE:-2020-11-01T16:00}"
fi

# EXTRA_OPTIONS is not in quotes, so that each option is a separate argument.
# shellcheck disable=SC2086
$LAUNCHER "$ESQ" --listing "$ROOT/build/target/ESQ.lst" \
    --drive "$DRIVE" --volume "DH1=$DRIVE" \
    --assign DF0=DH1: --assign ENV=DH1: --arguments GA24005 --command-name esq \
    --turbo-until _ESQ_MainLoopUiTickEnabledFlag $EXTRA_OPTIONS --scale "${ESQ_SCALE:-2}" "$@"
