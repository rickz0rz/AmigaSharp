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
#   --audio <path>           The sound of the stream: an M3U playlist or a directory of audio files.
#   --name <name>            The name of the channel of the stream. The default is "Prevue Guide".
#   --headless               Do not open a window. Stop with Ctrl-C.
#   --reset                  Delete the copy of the drive, and copy the drive again.
#
# Environment variables:
#   PREVUE_DATA              The data directory. The default is ~/Library/Application Support/AmigaSharp Prevue on
#                            macOS and ~/.local/share/amigasharp-prevue on other systems.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
LAUNCHER=$HERE/AmigaSharp.Launcher
LISTINGS=$HERE/AmigaSharp.PrevueListings

usage() {
    sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
}

fail() {
    echo "error: $1" >&2
    exit 2
}

DRIVE="" ESQ="" CODE=GA24005 CHANNELS_DVR="" INTERVAL=10 PREMIUM="" DATE="" STREAM="" AUDIO="" NAME="Prevue Guide"
HEADLESS="" RESET=""
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
        --name) [ $# -ge 2 ] || fail "$1 needs a value."; NAME=$2; shift 2 ;;
        --headless) HEADLESS=1; shift ;;
        --reset) RESET=1; shift ;;
        -h|--help) usage; exit 0 ;;
        --) shift; break ;;
        *) usage >&2; fail "unknown option $1." ;;
    esac
done

[ -n "$DRIVE" ] || { usage >&2; fail "--drive is necessary."; }
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
if [ -n "$STREAM" ]; then
    set -- "$@" --stream "$STREAM" --stream-name "$NAME"
    [ -n "$AUDIO" ] && set -- "$@" --stream-audio "$AUDIO"
fi

if [ -n "$CHANNELS_DVR" ]; then
    # The listings tool writes the listing files, makes the ready file, and then sends the changes of the guide to the
    # serial port of the launcher (TCP port 5400).
    READY=$PREVUE_DATA/listings-ready
    rm -f "$READY"
    "$LISTINGS" --server "$CHANNELS_DVR" --output "$WORK" --ready "$READY" --serve localhost:5400 \
        --interval "$INTERVAL" ${PREMIUM:+--premium "$PREMIUM"} ${DATE:+--clock "$DATE"} &
    TOOL=$!
    trap 'kill $TOOL 2>/dev/null' EXIT INT TERM
    while [ ! -f "$READY" ]; do
        kill -0 "$TOOL" 2>/dev/null || fail "the listings tool stopped. Is $CHANNELS_DVR correct?"
        sleep 1
    done
    # ESQ parses the feed faster than 4 times 2400 baud, so a large update arrives sooner.
    set -- "$@" --serial-speed 4
fi

"$LAUNCHER" "$@"
