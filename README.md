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
- `--date <date>` sets the date and the time of the Amiga at the start, for example `--date 2020-11-01T16:00`.
  Prevue shows saved listing data only on the date of that data.

In the window, the keys of the host go to the Amiga keyboard. F11 is the Help key.

This command runs Prevue with the drive of the original machine:

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- build/target/ESQ --listing build/target/ESQ.lst \
    --drive target-source/binaries --volume DH1=target-source/binaries \
    --assign DF0=DH1: --assign ENV=DH1: --arguments GA24005 --command-name esq
```

Run `scripts/build-target.sh` first to make `build/target/ESQ` and its listing.

## Show the saved listings

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
