#!/bin/sh
# Run Prevue Guide (ESQ) with the programs in this directory.
#
# Usage: ./run-prevue.sh --drive <directory> [options] [-- <launcher options>]
#
# Options:
#   --drive <directory>      The drive of the Prevue machine (its DH1:). The script copies it to its data directory on
#                            the first run, and never changes the original.
#   --esq <file>             The ESQ program. The default is ESQ on the drive.
#   --code <code>            The selection code of the machine. The default is GA24005.
#   --channels-dvr <url>     Show the listings of a Channels DVR server, for example http://channels-dvr.local:8089. The
#                            listings stay current while Prevue runs.
#   --interval <minutes>     The minutes between two reads of the guide. The default is 10.
#   --premium <list>         The premium channels of the Channels DVR listings: channel numbers or call signs, with
#                            commas between them, for example 222,HBOHD. Their programs have a red background.
#   --date <date>            The date and the time of the Amiga, for example 2020-11-01T16:00. Use it to show the saved
#                            listings of the drive on their date. The default is the time of this computer.
#   --stream <port>          Stream the display as a TV channel on the HTTP port (ffmpeg must be installed). The playlist
#                            for Channels DVR is http://<this computer>:<port>/channels.m3u.
#   --audio <path>           Music for the stream: an M3U playlist or a directory of audio files, in a loop. By
#                            default, it stops while a --genlock video with sound plays.
#   --genlock <file or URL>  Show a video behind the grid of the stream, as the genlock of the Prevue channel did. A
#                            file plays in a loop. A URL plays live, for example a channel of an HDHomeRun tuner.
#                            The stream has the sound of this video.
#   --genlock-control        Start the genlock with no video, over black. The stream then takes a queue of videos
#                            from http://<this computer>:<port>/genlock. See the README of AmigaSharp.
#   --genlock-playlist <file> Start the genlock with the videos of a JSON file, for example to play a video and
#                            then a pause of "black" in a loop. See the README of AmigaSharp.
#   --schedule <file>        Play a schedule: a JSON file of videos, pauses, music, promos and logos. It needs
#                            --stream. See docs/orchestration.md of AmigaSharp.
#   --prevue-ctrl-port <port> A TCP port for the control line of Prevue. Its commands show promos of programs. With
#                            --stream, the stream port also takes them at /prevue/ctrl. See docs/ctrl-line.md.
#   --name <name>            The name of the channel of the stream. The default is "Prevue Guide".
#   --headless               Do not open a window. Stop with Ctrl-C.
#   --restart                Start Prevue again when it stops, or when its picture does not change for a minute,
#                            for a channel that runs without a person. It needs --headless. Ctrl-C stops it.
#   --reset                  Delete the copy of the drive, and copy the drive again.
#
# Environment variables:
#   PREVUE_DATA              The data directory. The default is ~/Library/Application Support/AmigaSharp Prevue on
#                            macOS and ~/.local/share/amigasharp-prevue on other systems.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
LAUNCHER=$HERE/AmigaSharp.PrevueLauncher
LISTINGS=$HERE/AmigaSharp.PrevueListings

usage() {
    sed -n '2,41p' "$0" | sed 's/^# \{0,1\}//'
}

fail() {
    echo "error: $1" >&2
    exit 2
}

DRIVE="" ESQ="" CODE=GA24005 CHANNELS_DVR="" INTERVAL=10 PREMIUM="" DATE="" STREAM="" AUDIO="" GENLOCK="" GENLOCK_PLAYLIST="" SCHEDULE="" CTRL_PORT="" NAME="Prevue Guide"
HEADLESS="" RESTART="" RESET="" GENLOCK_CONTROL=""
while [ $# -gt 0 ]; do
    case "$1" in
        --drive) [ $# -ge 2 ] || fail "$1 needs a value."; DRIVE=$2; shift 2 ;;
        --esq) [ $# -ge 2 ] || fail "$1 needs a value."; ESQ=$2; shift 2 ;;
        --code) [ $# -ge 2 ] || fail "$1 needs a value."; CODE=$2; shift 2 ;;
        --channels-dvr) [ $# -ge 2 ] || fail "$1 needs a value."; CHANNELS_DVR=$2; shift 2 ;;
        --interval) [ $# -ge 2 ] || fail "$1 needs a value."; INTERVAL=$2; shift 2 ;;
        --premium) [ $# -ge 2 ] || fail "$1 needs a value."; PREMIUM=$2; shift 2 ;;
        --date) [ $# -ge 2 ] || fail "$1 needs a value."; DATE=$2; shift 2 ;;
        --stream) [ $# -ge 2 ] || fail "$1 needs a value."; STREAM=$2; shift 2 ;;
        --audio) [ $# -ge 2 ] || fail "$1 needs a value."; AUDIO=$2; shift 2 ;;
        --genlock) [ $# -ge 2 ] || fail "$1 needs a value."; GENLOCK=$2; shift 2 ;;
        --genlock-playlist) [ $# -ge 2 ] || fail "$1 needs a value."; GENLOCK_PLAYLIST=$2; shift 2 ;;
        --schedule) [ $# -ge 2 ] || fail "$1 needs a value."; SCHEDULE=$2; shift 2 ;;
        --prevue-ctrl-port) [ $# -ge 2 ] || fail "$1 needs a value."; CTRL_PORT=$2; shift 2 ;;
        --name) [ $# -ge 2 ] || fail "$1 needs a value."; NAME=$2; shift 2 ;;
        --headless) HEADLESS=1; shift ;;
        --restart) RESTART=1; shift ;;
        --genlock-control) GENLOCK_CONTROL=1; shift ;;
        --reset) RESET=1; shift ;;
        -h|--help) usage; exit 0 ;;
        --) shift; break ;;
        *) usage >&2; fail "unknown option $1." ;;
    esac
done

[ -n "$DRIVE" ] || { usage >&2; fail "--drive is necessary."; }
[ -z "$RESTART" ] || [ -n "$HEADLESS" ] || fail "--restart needs --headless."
[ -d "$DRIVE" ] || fail "the drive $DRIVE does not exist."
[ -x "$LAUNCHER" ] || fail "$LAUNCHER is missing. Keep the files of this directory together."
if [ -n "$STREAM" ] && ! command -v ffmpeg >/dev/null 2>&1; then
    fail "--stream needs ffmpeg. Install it, for example with 'brew install ffmpeg'."
fi

if [ -z "${PREVUE_DATA:-}" ]; then
    if [ "$(uname)" = Darwin ]; then
        PREVUE_DATA="$HOME/Library/Application Support/AmigaSharp Prevue"
    else
        PREVUE_DATA="${XDG_DATA_HOME:-$HOME/.local/share}/amigasharp-prevue"
    fi
fi
WORK=$PREVUE_DATA/drive

# Prevue writes to its drive, so it runs on a copy. PowerPacker packs the saved listings, and Prevue cannot read packed
# files, so the script unpacks them in the copy.
if [ -n "$RESET" ]; then
    rm -rf "$WORK"
fi
if [ ! -d "$WORK" ]; then
    echo "Copying the drive to $WORK."
    mkdir -p "$PREVUE_DATA"
    cp -R "$DRIVE" "$WORK"
    for file in "$WORK"/curday.dat "$WORK"/nxtday.dat "$WORK"/PWI?; do
        [ -f "$file" ] && "$LAUNCHER" unpack "$file" --output "$WORK" >/dev/null
    done
fi

ESQ=${ESQ:-$WORK/ESQ}
[ -f "$ESQ" ] || fail "the program $ESQ does not exist. Use --esq."

set -- "$ESQ" --drive "$WORK" --volume "DH1=$WORK" --assign DF0=DH1: --assign ENV=DH1: \
    --arguments "$CODE" --command-name esq --turbo 8 --deinterlace blend --scale 2 "$@"
[ -n "$DATE" ] && set -- "$@" --date "$DATE"
[ -n "$HEADLESS" ] && set -- "$@" --headless
# The watchdog of the launcher stops a Prevue that does not work any more, so that the script starts it again.
[ -n "$RESTART" ] && set -- "$@" --watchdog 60
[ -n "$CTRL_PORT" ] && set -- "$@" --prevue-ctrl-port "$CTRL_PORT"
if [ -n "$STREAM" ]; then
    set -- "$@" --stream "$STREAM" --stream-name "$NAME"
    [ -n "$AUDIO" ] && set -- "$@" --stream-audio "$AUDIO"
    [ -n "$GENLOCK" ] && set -- "$@" --genlock "$GENLOCK"
    [ -n "$GENLOCK_CONTROL" ] && set -- "$@" --genlock-control
    [ -n "$GENLOCK_PLAYLIST" ] && set -- "$@" --genlock-playlist "$GENLOCK_PLAYLIST"
    [ -n "$SCHEDULE" ] && set -- "$@" --schedule "$SCHEDULE"
fi

# ESQ parses the feed faster than 4 times 2400 baud, so a large update arrives sooner.
[ -n "$CHANNELS_DVR" ] && set -- "$@" --serial-speed 4

TOOL="" LAUNCHER_PID="" STOPPED=""
stop_tool() {
    if [ -n "$TOOL" ]; then
        kill "$TOOL" 2>/dev/null || true
        TOOL=""
    fi
}
# Ctrl-C and SIGTERM stop the launcher as a normal stop, and then the script.
stop() {
    STOPPED=1
    if [ -n "$LAUNCHER_PID" ]; then
        kill -TERM "$LAUNCHER_PID" 2>/dev/null || true
    fi
}
trap stop INT TERM
trap stop_tool EXIT

start_listings() {
    # The listings tool writes the listing files, makes the ready file, and then sends the changes of the guide to the
    # serial port of the launcher (TCP port 5400).
    READY=$PREVUE_DATA/listings-ready
    rm -f "$READY"
    "$LISTINGS" --server "$CHANNELS_DVR" --output "$WORK" --ready "$READY" --serve localhost:5400 \
        --interval "$INTERVAL" ${PREMIUM:+--premium "$PREMIUM"} ${DATE:+--clock "$DATE"} &
    TOOL=$!
    while [ ! -f "$READY" ]; do
        kill -0 "$TOOL" 2>/dev/null || fail "the listings tool stopped. Is $CHANNELS_DVR correct?"
        [ -z "$STOPPED" ] || exit 0
        sleep 1
    done
}

# With --restart, the script starts Prevue again after it stops. The listings tool starts again too, so that ESQ reads
# listing files of now. The wait before a start doubles, to a maximum of a minute, while Prevue stops soon after it
# starts.
DELAY=5
while :; do
    [ -n "$CHANNELS_DVR" ] && start_listings
    STARTED=$(date +%s)
    "$LAUNCHER" "$@" &
    LAUNCHER_PID=$!
    CODE=0
    wait "$LAUNCHER_PID" || CODE=$?
    # A signal ends the wait before the launcher stops.
    while kill -0 "$LAUNCHER_PID" 2>/dev/null; do
        CODE=0
        wait "$LAUNCHER_PID" || CODE=$?
    done
    LAUNCHER_PID=""
    stop_tool
    # Exit code 2 is an error in the options: a new start does not help.
    if [ -z "$RESTART" ] || [ -n "$STOPPED" ] || [ "$CODE" -eq 2 ]; then
        exit "$CODE"
    fi
    [ $(( $(date +%s) - STARTED )) -lt 300 ] || DELAY=5
    echo "Prevue stopped (exit code $CODE). It starts again in $DELAY seconds." >&2
    sleep "$DELAY" &
    wait $! || true
    [ -z "$STOPPED" ] || exit "$CODE"
    DELAY=$(( DELAY * 2 > 60 ? 60 : DELAY * 2 ))
done
