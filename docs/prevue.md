# Prevue Guide

This document is a part of the [README](../README.md). It tells how to run Prevue Guide (ESQ), the Amiga program of
the Prevue Channel, with AmigaSharp: with its saved listings, with the listings of a Channels DVR server, as a TV
channel with videos and music, and with the promos and the logos of its control line.

![Prevue Guide in AmigaSharp, with the saved listings of November 1, 2020](prevue-guide.png)

The picture shows Prevue Guide with the saved listings of November 1, 2020. The top half of the screen is black,
because the Prevue Channel showed a video in that area.

## The files of Prevue

This repository does not contain Prevue Guide or its data. The steps in this document use two directories that are
not public:

- `target-source/binaries/` contains a copy of the drive of a Prevue machine: ESQ, the fonts, and the listing files.
  The steps need it.
- `target-source/asm/` contains the assembly source of ESQ. It is optional. `scripts/build-target.sh` assembles it to
  `build/target/ESQ` and its listing, and compares the result with a SHA-256 hash. The result is the same file as ESQ
  of the drive, so Prevue runs the same. The listing adds the development features of
  [The listing of a program](../README.md#the-listing-of-a-program). Without the assembly source,
  `scripts/run-esq.sh` runs ESQ of the drive without a listing.

Without these directories, the tests of the target program skip. If you have a copy of ESQ and its drive, give their
paths to the launcher or to `run-prevue.sh` (`run-prevue.ps1` on Windows).

## The launcher for Prevue

`AmigaSharp.PrevueLauncher` is the launcher with the parts for Prevue: its control line, the requests of
`/prevue/ctrl`, and the defaults of a Prevue machine. The drive is also DH1:, and DF0: and ENV: are DH1:. The command
name is esq, and the arguments are the selection code GA24005. Use `--help` to see all the defaults.

This command runs Prevue with a copy of the drive of the original machine. Prevue writes to its drive, so do not use
`target-source/binaries` directly:

```sh
cp -R target-source/binaries /tmp/prevue-drive
dotnet run --project AmigaSharp.PrevueLauncher -c Release -- build/target/ESQ --listing build/target/ESQ.lst \
    --drive /tmp/prevue-drive --turbo-until _ESQ_MainLoopUiTickEnabledFlag
```

With the assembly source of ESQ, run `scripts/build-target.sh` first to make `build/target/ESQ` and its listing.
Without it, use ESQ of the drive (`/tmp/prevue-drive/ESQ`), leave out `--listing`, and use `--turbo 3` in place of
`--turbo-until`: `--turbo-until _ESQ_MainLoopUiTickEnabledFlag` ends the fast start when the main loop of ESQ starts.
`scripts/run-esq.sh` does all of these steps.

The settings of `scripts/run-esq.sh` are environment variables, and the settings of `scripts\run-esq.ps1` are
parameters:

| Shell script | PowerShell script |
|---|---|
| `CHANNELS_DVR=<url> scripts/run-esq.sh` | `scripts\run-esq.ps1 -ChannelsDvr <url>` |
| `CHANNELS_DVR_INTERVAL`, `CHANNELS_DVR_PREMIUM` | `-Interval`, `-Premium` |
| `CHANNELS_DVR_LOGOS`, `ESQ_LOGOS` | `-ChannelLogos`, `-Logos` |
| `ESQ_DATE`, `ESQ_SCALE` | `-Date`, `-Scale` |
| `VASM`, `TARGET_SOURCE` of `build-target.sh` | `-Vasm`, `-TargetSource` |

The other arguments of `run-esq.ps1` go to the launcher, as with `run-esq.sh`. For example, use
`scripts\run-esq.ps1 --scale 1`. Use `Get-Help scripts\run-esq.ps1 -Detailed` to see the parameters.

## Show the saved listings

The quickest way is the script. It opens Prevue in a window, with the saved 2020 listings:

```sh
scripts/run-esq.sh
```

The first run copies the drive to `build/drive/` and unpacks the listing files there. Delete `build/drive/` to start
again from the original drive. The script sends its arguments to the launcher, for example `--scale 1`.

To do the same steps by hand, follow the procedure below.

The listing files of the drive (`curday.dat` and `nxtday.dat`) are packed with PowerPacker, and Prevue cannot read
packed files. Unpack them in a copy of the drive, and run Prevue on the date of the data:

```sh
cp -R target-source/binaries /tmp/prevue-drive
dotnet run --project AmigaSharp.Launcher -c Release -- unpack /tmp/prevue-drive/curday.dat \
    /tmp/prevue-drive/nxtday.dat /tmp/prevue-drive/PWI? --output /tmp/prevue-drive
dotnet run --project AmigaSharp.PrevueLauncher -c Release -- build/target/ESQ --listing build/target/ESQ.lst \
    --drive /tmp/prevue-drive --date 2020-11-01T16:00
```

## Show the listings of a Channels DVR server

`AmigaSharp.PrevueListings` reads the guide of a Channels DVR server and writes the Prevue listing files
`curday.dat` and `nxtday.dat`. The files have the HD channels of the guide, in the order of their numbers. ESQ keeps
a maximum of 200 channels. Set `CHANNELS_DVR` to use it with the script:

```sh
CHANNELS_DVR=http://channels-dvr.local:8089 scripts/run-esq.sh
```

On Windows, give the address as a parameter:

```powershell
scripts\run-esq.ps1 -ChannelsDvr http://channels-dvr.local:8089
```

The script then writes new listing files to a separate drive copy, `build/drive-channels-dvr/`, before each run,
and the Amiga uses the time of the host. While ESQ runs, the tool reads the guide again every 10 minutes
(`CHANNELS_DVR_INTERVAL`). It sends the changes to the serial port as a Prevue data feed, so the grid stays current.
The feed has the same commands as the satellite feed of Prevue: `C` for the channel lineup and `P` for each program.
A day that ESQ does not have yet, for example the next day after the change at 5:00 AM, gets all its programs. The
launcher receives the feed at 4 times 2400 baud (`--serial-speed 4`). ESQ has no flow control, and it parses about
6 times 2400 baud, so a larger factor can fill its receive buffer.

To show premium channels on a red background, set `CHANNELS_DVR_PREMIUM` to their channel numbers or call signs,
with commas between them. For example, use `CHANNELS_DVR_PREMIUM=222,HBOHD`. The tool itself uses
`--premium <list>`. The text of a movie has the title in quotation marks, the year, the summary, and the rating.

To write the files to another drive, run the tool directly:

```sh
dotnet run --project AmigaSharp.PrevueListings -- --server http://channels-dvr.local:8089 --output <drive directory>
```

Use `--insecure` for an HTTPS address with a certificate that does not match the server. Use `--feed <file>` to
also write all the listings as a feed, for `--serial-file` of the launcher.

The files use the time zone `6` in their configuration. ESQ adds (time zone - 6) hours to the times of the listings
and of the clock, so with `6`, the grid and the clock show the local time of the host. The saved 2020 files use `5`,
and that is why their clock is one hour behind.

## Stream Prevue as a TV channel

With `--stream`, Prevue is a TV channel. For example, a channel for Channels DVR with the listings of the same server:

```sh
CHANNELS_DVR=http://channels-dvr.local:8089 scripts/run-esq.sh --headless --stream 8091 --deinterlace blend \
    --stream-name "Prevue Guide"
```

A video can show behind Prevue, as the genlock of a Prevue machine, with music. See
[Stream the display as a TV channel](streaming.md).

## Replay a serial feed

Prevue gets its listings on the serial port. The launcher can replay a captured feed from a file:

- `--serial-file <file>` replays the bytes of the file in place of the TCP bridge. The bytes go at the baud rate of
  SERPER. The replay starts when the program enables the RBF interrupt.
- `--serial-start <seconds>` delays the replay. Prevue empties its receive buffer while it starts, so use 8 or more.
- `--serial-log <file>` writes each byte in the two directions to the file, with the time of the Amiga clock.
- `--prevue-feed-trace <file>` writes the commands that the Prevue feed parser reads, and the changes of its counters.
  For example, a change of `_DATACErrs` shows a checksum error. This option needs `--listing`, and the launcher for
  Prevue.

Use `--virtual-time` with a replay. The run is then the same each time. Run the replay on a copy of the drive,
because Prevue writes the data that it receives to the drive:

```sh
dotnet run --project AmigaSharp.PrevueLauncher -c Release -- build/target/ESQ --listing build/target/ESQ.lst \
    --drive /tmp/prevue-drive --virtual-time \
    --serial-file feed.bin --serial-start 8 --serial-log serial.log --prevue-feed-trace feed.log
```

## Send control commands

Prevue has a second input, a 110 baud control line. The Prevue channel used it to show promos of programs over the
genlock video. With `--stream`, the HTTP server sends commands on the line:

| Request | Result |
|---------|--------|
| `GET /prevue/ctrl` | Gives the bytes that wait for the line (`queued`), the seconds that the line needs to send them, and the bytes that the line sent. |
| `POST /prevue/ctrl/promo` | Shows a promo: `{"title": "Seinfeld", "channels": "*", "brush": "AT"}`, or a program that the launcher chooses: `{"auto": {"movies": true}}`. |
| `POST /prevue/ctrl/clear` | Removes the promo or the logo. The genlock video shows in the top half. |
| `POST /prevue/ctrl/logo` | Shows the current logo in the top half. |
| `POST /prevue/ctrl/packets` | Sends packets of the control line: `[{"type": 1, "body": "3"}]`. |
| `GET /prevue/guide` | Gives the programs of the listings of Prevue from now, for automatic promos. |
| `GET /prevue/state` | Gives what the top half shows, if Prevue read the commands, and the logos. |
| `GET /prevue/logos` | Gives the logos of `LOGO.LST`, the loaded logo, and the next line. |
| `POST /prevue/logos/show` | Shows a logo of `LOGO.LST` in about 5 seconds, and no other logo: `{"name": "Insider"}`. |
| `POST /prevue/logos/next` | Chooses the logo that Prevue loads at the next logo command: `{"name": "Insider"}`. |

`--schedule <file>` plays a schedule: segments of videos and pauses, with the settings of the music and, for Prevue,
the top half of the screen. `GET /schedule` gives its state. See [Schedules](orchestration.md#schedules).

For example, show a promo for Seinfeld from the saved listings, and then remove it:

```sh
curl -X POST http://localhost:8091/prevue/ctrl/promo -d '{"title": "Seinfeld", "brush": "AT"}'
curl -X POST -d '' http://localhost:8091/prevue/ctrl/clear
```

- Prevue finds the next time of the program in its listings. If it finds no program, it shows the current logo.
- `run-prevue.sh --channel-logos` makes channel logos from the logo images of Channels DVR, and `--logos <dir>`
  makes them from your PNG files. See [Make channel logos](ctrl-line.md#make-channel-logos).
- The logos (for example "TV Guide sportsview") come from `LOGO.LST` on the drive. They cover all the top half, and
  ESQ changes them about each 3 minutes. An empty `LOGO.LST` stops them. See [Logos](ctrl-line.md#logos).
- A promo can have a box on the right and a box on the left:
  `{"right": {"title": "Bob's Burgers"}, "left": {"title": "Seinfeld", "brush": "DT"}, "first": "left"}`. Prevue
  shows one box. It tries the box of `first` before the other box.
- The line sends 11 bytes each second, so a promo takes about 2 seconds. The requests wait in a queue. Prevue starts
  to read the line some seconds after the stream starts.
- `--prevue-ctrl-port <port>` opens a TCP port for the raw bytes of the line, and `--prevue-ctrl-file <file>` sends
  the bytes of a file. Do not send raw bytes and HTTP requests at the same time.
- These requests and options are in `AmigaSharp.PrevueLauncher`. The scripts for Prevue (`scripts/run-esq.sh` and
  `run-prevue.sh`) use it. `AmigaSharp.Launcher` has nothing of Prevue.

[docs/ctrl-line.md](ctrl-line.md) gives the format of the packets and the known commands.
[docs/orchestration.md](orchestration.md) tells how to use the videos, the music, the promos and the logos
together, with an example coordinator.

## Prevue for other people

`scripts/publish.sh` builds the launcher for Prevue and the listings tool as programs that do not need the .NET SDK,
with `run-prevue.sh` (`run-prevue.ps1` and `run-prevue.cmd` on Windows). It copies the drive of a user, unpacks the
saved listings, and starts Prevue with the options of a Prevue machine. See
[Build programs for other people](distribution.md).

## Sneak Prevue

Sneak Prevue is the program of another channel of the same company, on two disks. It is not complete in AmigaSharp: it
shows its first screens, and its second program (sts) cannot unpack itself from the disk images that the project has.

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- SneakPrevue_SystemDisk.adf:vd \
    --disk DF1=SneakPrevue_DataDisk1.adf --serial-port 5400
```
