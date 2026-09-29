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

    ./run-prevue.sh --drive /path/to/drive --channels-dvr http://channels-dvr.local:8089

Show the programs of premium channels on a red background. Give their channel numbers or call signs:

    ./run-prevue.sh --drive /path/to/drive --channels-dvr http://channels-dvr.local:8089 --premium 222,HBOHD

Stream Prevue as a TV channel, without a window, with music:

    ./run-prevue.sh --drive /path/to/drive --channels-dvr http://channels-dvr.local:8089 \
        --headless --stream 8091 --audio /path/to/music.m3u

Then add http://<this computer>:8091/channels.m3u as a custom channel (M3U playlist) in Channels DVR.

Show a video behind the grid, as the Prevue channel did. Give a video file, or the URL of a live channel, for
example a channel of an HDHomeRun tuner:

    ./run-prevue.sh --drive /path/to/drive --channels-dvr http://channels-dvr.local:8089 \
        --headless --stream 8091 --genlock http://hdhomerun.local:5004/auto/v2

Or start with --genlock-control and no video, and queue the videos while Prevue runs. For example, a channel for 5
minutes, then a file to its end:

    curl -X POST http://localhost:8091/genlock/queue \
        -d '[{"source": "http://hdhomerun.local:5004/auto/v2", "seconds": 300}, {"source": "/videos/promo.mp4"}]'

GET /genlock gives the queue, POST /genlock/next skips to the next video, and POST /genlock/stop ends all the videos.
With curl, a POST without data needs -d '', for example: curl -X POST -d '' http://localhost:8091/genlock/next
Anyone who can connect to the port of the stream can change the queue, so use it only on a network that you trust.

Use ./run-prevue.sh --help to see all the options. The script copies the drive to its data directory on the first
run, and it does not change the original drive. Use --reset to copy the drive again.

In the window, the keys of the computer go to the Amiga. Escape opens the menu of the operator. F11 is the Help key.

macOS stops programs from the internet that Apple did not check. To run them, remove the mark:

    xattr -dr com.apple.quarantine /path/to/this/directory

Windows
-------

On Windows, use run-prevue.cmd in place of ./run-prevue.sh. It has the same options. Run it in a Command Prompt or
a PowerShell window, in this directory:

    run-prevue.cmd --drive C:\path\to\drive
    run-prevue.cmd --drive C:\path\to\drive --channels-dvr http://channels-dvr.local:8089

The copy of the drive goes to %LOCALAPPDATA%\AmigaSharp Prevue. Set PREVUE_DATA to use another directory.

Windows can stop programs from the internet with a SmartScreen message. To run them, select "More info" and then
"Run anyway". Or, before the first run, remove the mark from the files in PowerShell:

    Get-ChildItem C:\path\to\this\directory | Unblock-File

Install ffmpeg for --stream with: winget install Gyan.FFmpeg

With --stream, Windows lets only an administrator listen on all the addresses of the computer. Do these steps one
time, in a PowerShell window that runs as administrator. The example is for port 8091:

    netsh http add urlacl url=http://*:8091/ user=$env:USERDOMAIN\$env:USERNAME
    netsh advfirewall firewall add rule name="AmigaSharp stream" dir=in action=allow protocol=TCP localport=8091
