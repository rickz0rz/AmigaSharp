# AmigaSharp

AmigaSharp translates AmigaOS executables for the 68000 to C#, and runs them on a runtime that emulates the Amiga
libraries and a part of the chipset.

## Status

AmigaSharp is a hobby project. It runs AmigaOS executables for the 68000. These programs have been tested:

- Prevue Guide (ESQ), the Amiga program of the Prevue Channel. It is the main target. See [Prevue Guide](#prevue-guide).
- The Amiga Test Kit, a program that takes over the machine and tests the hardware directly.
- Aonic, the demo of the A1200 port of Sonic the Hedgehog, with PAL, AGA and the speed of a 68020.
- Sneak Prevue, the program of another channel of the Prevue company. It shows its first screens.

Other programs can also run. A program runs if it uses only the library calls and the hardware that the runtime
emulates:

- It emulates the 68000 CPU only. It does not emulate the 68020 or later CPUs. `--unaligned-access` adds one
  behavior of the 68020: words and longs at odd addresses.
- It emulates an NTSC Amiga with the ECS chipset. `--pal` makes a PAL Amiga, and `--chipset aga` adds a part of
  AGA.
- It uses high-level emulation (HLE) of the Amiga libraries. It does not use a Kickstart ROM. C# code does the work
  of each library call.
- It emulates only the parts of the chipset that the tested programs and the samples use.

A program needs only its executable. Its assembly source is optional. See
[The listing of a program](#the-listing-of-a-program).

## Prevue Guide

![Prevue Guide in AmigaSharp, with the saved listings of November 1, 2020](docs/prevue-guide.png)

Prevue Guide (ESQ) was the program of the Prevue Channel: the grid of listings in the bottom half of the screen, and
promos and logos over a video in the top half. AmigaSharp runs it with its saved listings, or with the listings of a
Channels DVR server, and can stream it as a TV channel. With a copy of the drive of a Prevue machine in
`target-source/binaries/`:

```sh
scripts/run-esq.sh                                              # The saved listings of 2020.
CHANNELS_DVR=http://channels-dvr.local:8089 scripts/run-esq.sh  # The listings of a Channels DVR server.
```

[docs/prevue.md](docs/prevue.md) tells all the steps: the files of Prevue, the listings, the stream, the promos and
the logos of the control line, and the programs for other people.

## Requirements

- The .NET 10 SDK.
- vasm (`vasmm68k_mot`), only to rebuild the samples or to make the listing of a program from its assembly source.
- ffmpeg on the PATH, only for `--stream`.

The launcher gets SDL2 from the Silk.NET.SDL package. You do not install SDL2.

## Windows

AmigaSharp runs on macOS, Linux and Windows. Each shell script in `scripts/` has a PowerShell version, with the same
name and the extension `.ps1`. The PowerShell scripts work in Windows PowerShell 5.1, which Windows 10 and 11 contain,
and in PowerShell 7. The examples in this document use the shell scripts. On Windows, use the PowerShell script with
the same name.

Windows does not run PowerShell scripts by default. Let PowerShell run the scripts on your computer, one time:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

The shell scripts get their settings from environment variables. The PowerShell scripts get them from parameters, and
they also read the same environment variables. For example, `EMBEDDED_PROGRAM` of `publish.sh` is `-EmbeddedProgram`
of `publish.ps1`. [docs/prevue.md](docs/prevue.md) has the settings of the scripts for Prevue. Use
`Get-Help scripts\publish.ps1 -Detailed` to see the parameters of a script.

- vasm has Windows programs on [its web site](http://sun.hasenbraten.de/vasm/). Put `vasmm68k_mot.exe` on the PATH,
  or give its path with `-Vasm`.
- Install ffmpeg with `winget install Gyan.FFmpeg`.
- Windows has no `nc`. To connect to the serial port, use another TCP client, for example `ncat` from Nmap.
- For `--stream`, Windows needs more steps. See [docs/streaming.md](docs/streaming.md).

## Files that the repository does not contain

This repository contains no Amiga programs, only the samples in `samples/`. The tested programs and their data are not
public. Their steps use these files in `target-source/`:

- `binaries/` and `asm/`: the drive of a Prevue machine, and the optional assembly source of ESQ. See
  [The files of Prevue](docs/prevue.md#the-files-of-prevue). The tests of the target program skip without them.
- `AmigaTestKit.adf`, `Aonic-TheGreenHillZoneDemo.adf`, and the two disks of Sneak Prevue.

The samples, the translator, the runtime and the other tests do not need these files.

This project is not related to the owners of Amiga, Prevue, Channels DVR or Sonic the Hedgehog, and they do not
support it. These names are trademarks of their owners.

## Projects

| Project | Purpose |
|---|---|
| `AmigaSharp.Runtime` | The 68000 CPU and its interpreter, the memory, the HLE libraries, and the chipset model. |
| `AmigaSharp.Translator` | Translates an executable and its vasm listing to a C# class. |
| `AmigaSharp.Host` | The launcher as a library: the options, the window, the stream, the genlock and the sound. |
| `AmigaSharp.Launcher` | Translates, compiles and runs any executable, and shows its display in a window. |
| `AmigaSharp.PrevueLauncher` | The launcher with the parts for Prevue: its control line, `/prevue/ctrl`, and its defaults. |
| `AmigaSharp.PrevueListings` | Writes Prevue listing files and a Prevue data feed from the guide of a Channels DVR server. |
| `AmigaSharp` | Runs the translated Hello World sample. |
| `AmigaSharp.Tests` | The tests. |

The folders of the repository:

```text
AmigaSharp.Runtime/          The emulated Amiga: CPU, memory, libraries, chipset. No code for one program.
AmigaSharp.Translator/       The translator from 68000 code to C#.
AmigaSharp.Host/             The launcher as a library, with its extension points (ILauncherExtension).
AmigaSharp.Launcher/         The generic launcher program: one file.
AmigaSharp.PrevueLauncher/   The launcher program for Prevue: the extension and the code for Prevue.
AmigaSharp.PrevueListings/   The listings tool for Prevue.
AmigaSharp.Tests/            The tests of all the projects.
docs/                        The documents: streaming.md, distribution.md, internals.md and performance.md, and
                             prevue.md, ctrl-line.md and orchestration.md about Prevue.
scripts/                     The scripts to build and run. run-esq and scripts/dist/ are for Prevue.
```

The two launchers use `AmigaSharp.Host`. A launcher for a program is a small project that calls `Launcher.Run` with
an `ILauncherExtension`. The extension adds the options, the parts and the defaults of the program.
`AmigaSharp.PrevueLauncher` is the example. `AmigaSharp.Launcher` has no extension, and it runs any program as a plain
Amiga.

## Build

Run this command in the root of the repository:

```sh
dotnet build -c Release
```

You do not have to build first to use the examples in this document. `dotnet run` builds the project before it runs
it. To make native programs that do not need .NET, see
[Build programs for other people](#build-programs-for-other-people).

## Run a program

The launcher translates the program, compiles it, and keeps the result in a cache. The next start uses the cache.

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- <executable> [--listing <file.lst>]
```

For example, the Hello World sample writes its text to the console:

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- samples/HelloWorld/hello --listing samples/HelloWorld/hello.lst
```

Use `--help` to see all the options. These options are the most important:

- `--drive <directory>` sets the host directory of `SYS:`.
- `--volume NAME=<directory>` and `--assign NAME=<path>` make the volumes and the assigns of the program.
- `--disk DF1=<file.adf>` puts a disk image in a drive (DF0 to DF3), for a program on more than one disk. The program
  finds the files in `DF1:` and in the volume with the name of the disk. A file that the disk image cannot give, for
  example because of a damaged block, is left out with a warning.
- `--serial-port <port>` opens the serial bridge on a TCP port. The bridge connects the serial port of the Amiga to
  a TCP client, for example `nc localhost <port>`. Without this option, there is no bridge. The launcher for Prevue
  opens it on port 5400.
- `--screenshot <file.png>` saves the display after `--seconds` and does not open a window.
- `--virtual-time` uses a virtual clock. Time moves at each safe point, and a wait ends at once. So a run is the same
  each time, and it is as fast as the host can run it. The screenshots, the key presses of `--press` and the copper
  dump happen at their Amiga time, so they are the same in each run too.
- `--pal` makes a PAL Amiga: 312 lines and 50 frames each second. The default is NTSC.
- `--chipset aga` makes an AGA Amiga (A1200, A4000): the chip IDs, graphics.library, and the display parts of AGA
  that the A1200 port of Sonic the Hedgehog uses. These are the fetch modes of the bitplanes (FMODE), up to 8 planes,
  the palette of 256 colors with its banks (BPLCON3, BPLCON4), the scroll of up to 64 pixels, and sprites of 32 and 64
  pixels. HAM8, the second palette of dual playfield, other resolutions of the sprites and scan doubling are not
  emulated.
- `--unaligned-access` lets the program read and write words and longs at odd addresses, as a 68020 does. Programs
  for the A1200 can need it.
- `--cpu-mhz <n>` runs the CPU faster than the 7.16 MHz of the 68000. The runtime counts the cycles of the 68000, and
  a 68020 needs fewer cycles for an instruction. So a program of the A1200 needs more than its 14 MHz: the Sonic
  demo gets about 45 of its 50 frames each second at 7.16 MHz, and 49 at 56 MHz.
- `--press <seconds>=<key>` presses a key at a time, for example `--press 8=escape`.
- `--fast-cpu` runs the 68000 as fast as the host can. By default, the CPU runs at the speed of a real 68000
  (7.16 MHz) with a real-time clock. It sleeps when it is ahead, so a program that polls in a loop does not use a full
  host core.
- `--turbo <seconds>` and `--turbo-until <label>` run the 68000 as fast as the host can at the start, and then at
  its real speed. `--turbo-until` ends the turbo when the word at a label of the listing is not 0.
- `--watch <label>` writes each change of the word at a label of the listing, for example
  `--watch _Global_RefreshTickCounter`.
- `--stats` writes the speed each second: the frames made and dropped, the time to make a frame, the time that the
  program waited, and the rate of the VERTB and AUD1 interrupts.
- `--date <date>` sets the date and the time of the Amiga at the start, for example `--date 2020-11-01T16:00`.
- `--serial-file <file>` replays a captured feed on the serial port in place of the TCP bridge, after
  `--serial-start <seconds>`. `--serial-log <file>` writes each byte of the serial port with the time.

In the window, the keys of the host go to the Amiga keyboard. F11 is the Help key.

### The listing of a program

A program runs from its executable only. If you have the assembly source of a program, you can also give the launcher
the vasm listing of the program with `--listing`. This is true for each program, not only for ESQ. Make the listing
when you assemble the program:

```sh
vasmm68k_mot -Fhunkexe -nosym -L program.lst -o program program.s
```

The program runs the same with and without the listing. The listing adds these features for development:

- The translator knows which bytes are code. It translates more of the program, and the interpreter runs less of it.
  The translation has the names of the labels and the source lines in its comments.
- `--turbo-until <label>` and `--watch <label>` use the labels of the listing.
- The launcher for Prevue reads the variables of ESQ by their labels. Without the listing, it uses a table of the
  addresses of the known build of ESQ.

Without a listing, the launcher keeps a map of the code that ran, and each run translates more of the program. See
[The interpreter](docs/internals.md#the-interpreter).

The executable must be the same file that vasm made with the listing. The translator does not check all of it, and
with another file the translation is wrong.

## Stream the display as a TV channel

`--stream <port>` streams the display as live HLS video (H.264 and AAC, with ffmpeg from the PATH), with an M3U
playlist for a custom channel of Channels DVR. The stream can also show a video behind the display, as the genlock of
a Prevue machine, play a queue of videos, and mix music with the sound. An HTTP server controls them.
[docs/streaming.md](docs/streaming.md) tells how, and gives the extra steps for Windows.

## Build programs for other people

`scripts/publish.sh` (`scripts\publish.ps1` on Windows) builds the launcher for Prevue and the listings tool for
people who do not have the .NET SDK: native programs for the operating system of the host, and a self-contained .NET
build for another operating system. `run-prevue.sh` starts Prevue from such a build. See
[docs/distribution.md](docs/distribution.md).

## How it works

[docs/internals.md](docs/internals.md) tells how AmigaSharp runs a program: the translation to C#, the interpreter
and the map of the code that ran, the libraries, the tasks and the timing, and the chipset.
[docs/performance.md](docs/performance.md) gives the speed of the emulation.

## Programs that take over the machine

The Amiga Test Kit is on an ADF disk image. Run it from the disk:

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- AmigaTestKit.adf:AmigaTestKit
```

The program unpacks itself into memory when it starts, so the translator sees only the code that unpacks it. The
interpreter runs the unpacked code. `--interpret` gives the same result.

Aonic, the demo of the A1200 port of Sonic the Hedgehog, needs a PAL Amiga with AGA and the speed of a 68020:

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- Aonic-TheGreenHillZoneDemo.adf:aonic \
    --pal --chipset aga --unaligned-access --cpu-mhz 56
```

The keys are the keys of the demo: the cursor keys, Z, X or C to jump, V to start or to pause, and Escape to stop.

## Tests

```sh
scripts/fetch-cpu-tests.sh   # The 68000 test vectors. The CPU tests skip without them.
scripts/build-target.sh      # The target program. The target tests skip without it.
dotnet test
```

On Windows:

```powershell
scripts\fetch-cpu-tests.ps1
scripts\build-target.ps1
dotnet test
```

## License

AmigaSharp uses the MIT license. See `LICENSE`.
