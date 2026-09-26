# AmigaSharp

AmigaSharp translates AmigaOS executables for the 68000 to C#, and runs them on a runtime that emulates the Amiga
libraries and a part of the chipset.

## Projects

| Project | Purpose |
|---|---|
| `AmigaSharp.Runtime` | The 68000 CPU and its interpreter, the memory, the HLE libraries, and the chipset model. |
| `AmigaSharp.Translator` | Translates an executable and its vasm listing to a C# class. |
| `AmigaSharp.Launcher` | Translates, compiles and runs an executable, and shows its display in a window. |
| `AmigaSharp` | Runs the translated Hello World sample. |
| `AmigaSharp.Tests` | The tests. |

## Run a program

The launcher translates the program, compiles it, and keeps the result in a cache. The next start uses the cache.

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- <executable> --listing <file.lst>
```

Use `--help` to see all the options. These options are the most important:

- `--drive <directory>` sets the host directory of `SYS:`.
- `--volume NAME=<directory>` and `--assign NAME=<path>` make the volumes and the assigns of the program.
- `--serial-port <port>` sets the TCP port of the serial port. Connect to it with `nc localhost <port>`.
- `--screenshot <file.png>` saves the display after `--seconds` and does not open a window.
- `--virtual-time` uses a virtual clock. Time moves at each safe point, and a wait ends at once. So a run is the same
  each time, and it is as fast as the host can run it.
- `--press <seconds>=<key>` presses a key at a time, for example `--press 8=escape`.
- `--fast-cpu` runs the 68000 as fast as the host can. By default, the CPU runs at the speed of a real 68000
  (7.16 MHz) with a real-time clock. It sleeps when it is ahead, so a program that polls in a loop does not use a full
  host core.
- `--turbo <seconds>` and `--turbo-until <label>` run the 68000 as fast as the host can at the start, and then at
  its real speed. `--turbo-until` ends the turbo when the word at a label of the listing is not 0. The script uses
  `--turbo-until _ESQ_MainLoopUiTickEnabledFlag`, so ESQ starts as fast as with `--fast-cpu`.
- `--watch <label>` writes each change of the word at a label of the listing, for example
  `--watch _Global_RefreshTickCounter`.
- `--stats` writes the speed each second: the frames made and dropped, the time to make a frame, the time that the
  program waited, and the rate of the VERTB and AUD1 interrupts.
- `--date <date>` sets the date and the time of the Amiga at the start, for example `--date 2020-11-01T16:00`.
  Prevue shows saved listing data only on the date of that data.

In the window, the keys of the host go to the Amiga keyboard. F11 is the Help key.

This command runs Prevue with a copy of the drive of the original machine. Prevue writes to its drive, so do not use
`target-source/binaries` directly:

```sh
cp -R target-source/binaries /tmp/prevue-drive
dotnet run --project AmigaSharp.Launcher -c Release -- build/target/ESQ --listing build/target/ESQ.lst \
    --drive /tmp/prevue-drive --volume DH1=/tmp/prevue-drive \
    --assign DF0=DH1: --assign ENV=DH1: --arguments GA24005 --command-name esq
```

Run `scripts/build-target.sh` first to make `build/target/ESQ` and its listing. `scripts/run-esq.sh` does all of
these steps.

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
dotnet run --project AmigaSharp.Launcher -c Release -- build/target/ESQ --listing build/target/ESQ.lst \
    --drive /tmp/prevue-drive --volume DH1=/tmp/prevue-drive \
    --assign DF0=DH1: --assign ENV=DH1: --arguments GA24005 --command-name esq --date 2020-11-01T16:00
```

## Show the listings of a Channels DVR server

`AmigaSharp.PrevueListings` reads the guide of a Channels DVR server and writes the Prevue listing files
`curday.dat` and `nxtday.dat`. The files have the HD channels of the guide, in the order of their numbers. ESQ keeps
a maximum of 200 channels. Set `CHANNELS_DVR` to use it with the script:

```sh
CHANNELS_DVR=http://192.168.0.195:8089 scripts/run-esq.sh
```

The script then writes new listing files to a separate drive copy, `build/drive-channels-dvr/`, before each run,
and the Amiga uses the time of the host. While ESQ runs, the tool reads the guide again every 10 minutes
(`CHANNELS_DVR_INTERVAL`). It sends the changes to the serial port as a Prevue data feed, so the grid stays current.
The feed has the same commands as the satellite feed of Prevue: `C` for the channel lineup and `P` for each program.
A day that ESQ does not have yet, for example the next day after the change at 5:00 AM, gets all its programs. The
launcher receives the feed at 4 times 2400 baud (`--serial-speed 4`). ESQ has no flow control, and it parses about
6 times 2400 baud, so a larger factor can fill its receive buffer.
To write the files to another drive, run the tool directly:

```sh
dotnet run --project AmigaSharp.PrevueListings -- --server http://192.168.0.195:8089 --output <drive directory>
```

Use `--insecure` for an HTTPS address with a certificate that does not match the server. Use `--feed <file>` to
also write all the listings as a feed, for `--serial-file` of the launcher.

The files use the time zone `6` in their configuration. ESQ adds (time zone - 6) hours to the times of the listings
and of the clock, so with `6`, the grid and the clock show the local time of the host. The saved 2020 files use `5`,
and that is why their clock is one hour behind.

## Stream the display as a TV channel

The launcher can stream the display as live HLS video. ffmpeg (from the PATH) encodes it to H.264 at 29.97 pictures
each second, with an AAC audio track. An HTTP server gives the stream and an M3U playlist of one channel:

```sh
CHANNELS_DVR=http://192.168.0.195:8089 scripts/run-esq.sh --headless --stream 8091 --deinterlace blend \
    --stream-name "Prevue Guide"
```

- `http://<this host>:8091/stream.m3u8` is the stream.
- `http://<this host>:8091/channels.m3u` is a playlist for a custom channel in Channels DVR. Add it as a custom
  channel source of the type M3U playlist, with the address of this host that the server can reach.
- The picture is 4:3 in a 1280 by 720 picture with black bars at the sides. `--stream-4x3` makes a 960 by 720
  picture without bars.
- `--stream-audio <path>` gives the stream a sound: an M3U playlist, a text file with one audio file on each line,
  or a directory of audio files. The files play in a loop, in the order of the playlist (or of their names in a
  directory). A path in a playlist can be relative to the directory of the playlist. Files that do not exist or do
  not play are skipped. If no file plays, the stream is silent, and the launcher tries the playlist again each
  10 seconds. Without this option, the stream is silent.
- `--headless` runs without a window until Ctrl-C. The stream also works with a window.
- macOS can ask if the launcher can accept incoming network connections. Accept it, so that the server can connect.

`--deinterlace` sets how the window, the screenshots and the stream show the interlaced display of Prevue:

- `weave` shows the two fields on their rows, as a TV does. Moving content has comb lines. This is the default.
- `bob` shows the last field with each row twice. It has no comb lines, and half the vertical detail.
- `blend` shows the average of the two fields. It has no comb lines, and moving content is a little blurred. It is
  the best mode for a stream.

## Replay a serial feed

Prevue gets its listings on the serial port. The launcher can replay a captured feed from a file:

- `--serial-file <file>` replays the bytes of the file in place of the TCP bridge. The bytes go at the baud rate of
  SERPER. The replay starts when the program enables the RBF interrupt.
- `--serial-start <seconds>` delays the replay. Prevue empties its receive buffer while it starts, so use 8 or more.
- `--serial-log <file>` writes each byte in the two directions to the file, with the time of the Amiga clock.
- `--feed-trace <file>` writes the commands that the Prevue feed parser reads, and the changes of its counters. For
  example, a change of `_DATACErrs` shows a checksum error. This option needs `--listing`.

Use `--virtual-time` with a replay. The run is then the same each time. Run the replay on a copy of the drive,
because Prevue writes the data that it receives to the drive:

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- build/target/ESQ --listing build/target/ESQ.lst \
    --drive /tmp/prevue-drive --volume DH1=/tmp/prevue-drive \
    --assign DF0=DH1: --assign ENV=DH1: --arguments GA24005 --command-name esq --virtual-time \
    --serial-file feed.bin --serial-start 8 --serial-log serial.log --feed-trace feed.log
```

## Tests

```sh
scripts/fetch-cpu-tests.sh   # The 68000 test vectors. The CPU tests skip without them.
scripts/build-target.sh      # The target program. The target tests skip without it.
dotnet test
```
