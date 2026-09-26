AmigaSharp: Prevue Guide
========================

These programs run Prevue Guide (ESQ), the program of the Prevue channel, on this computer. They do not need .NET.

You need:
- The drive of a Prevue machine (a directory with ESQ, the fonts and the data files). These programs do not contain it.
- ffmpeg, only to stream the display as a TV channel.

Keep the files of this directory together. The launcher needs the SDL2 library next to it.

Run Prevue in a window:

    ./run-prevue.sh --drive /path/to/drive

Show the saved listings of the drive on their date, for example:

    ./run-prevue.sh --drive /path/to/drive --date 2020-11-01T16:00

Show the listings of a Channels DVR server. The listings stay current while Prevue runs:

    ./run-prevue.sh --drive /path/to/drive --channels-dvr http://192.168.0.195:8089

Stream Prevue as a TV channel, without a window, with music:

    ./run-prevue.sh --drive /path/to/drive --channels-dvr http://192.168.0.195:8089 \
        --headless --stream 8091 --audio /path/to/music.m3u

Then add http://<this computer>:8091/channels.m3u as a custom channel (M3U playlist) in Channels DVR.

Use ./run-prevue.sh --help to see all the options. The script copies the drive to its data directory on the first
run, and it does not change the original drive. Use --reset to copy the drive again.

In the window, the keys of the computer go to the Amiga. Escape opens the menu of the operator. F11 is the Help key.

macOS stops programs from the internet that Apple did not check. To run them, remove the mark:

    xattr -dr com.apple.quarantine /path/to/this/directory
