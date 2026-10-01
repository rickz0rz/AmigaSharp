# Build programs for other people

This document is a part of the [README](../README.md). It tells how to build the launcher and the listings tool
for people who do not have the .NET SDK.

`scripts/publish.sh` builds the launcher for Prevue (`AmigaSharp.PrevueLauncher`) and the listings tool as native
programs with Native AOT. The people who use them do not need .NET. The launcher for Prevue also runs other programs.
To publish only the generic launcher, use `dotnet publish AmigaSharp.Launcher -c Release -r <runtime identifier>
-p:PublishAot=true`.

```sh
scripts/publish.sh              # For this host, for example osx-arm64.
scripts/publish.sh osx-x64      # For a Mac with an Intel processor.
scripts/publish.sh win-x64      # For Windows, also on macOS or Linux (a self-contained .NET build).
```

The programs go to `dist/<runtime identifier>/`, with `run-prevue.sh` and `README.txt` for the users. The script
copies the drive of a user to a data directory on the first run, unpacks the saved listings, and starts the launcher
with the options of the Prevue machine. It can also start the listings of Channels DVR and the stream:

```sh
./run-prevue.sh --drive /path/to/drive --channels-dvr http://channels-dvr.local:8089 --headless --stream 8091
```

Add `--restart` for a channel that runs without a person: the script starts Prevue again if it stops, or if its
picture does not change for a minute (`--watchdog 60`). A launcher also removes the temporary folders that a launcher
that crashed or that was killed left.

Keep the files of the directory together. The launcher needs the SDL2 library next to it.

Native AOT compiles only for the operating system of the host. For another operating system, the script makes a
self-contained .NET build: a directory of about 220 files and 120 MB, with the programs, their libraries and the .NET
runtime. The users do not need .NET for it either. Its launcher translates the program when it starts, as
`dotnet run` does, so it runs programs faster than a native launcher. The first start of a program takes some seconds
longer, for the translation. Native programs for Linux and Windows still need a build on Linux or on Windows.

On Windows, use `scripts\publish.ps1` (or `scripts\publish.ps1 -RuntimeIdentifier win-arm64`). Native AOT on Windows
needs the C++ build tools of Visual Studio: install the workload "Desktop development with C++" of Visual Studio or
of the Build Tools for Visual Studio. A Windows build has `run-prevue.ps1` and `run-prevue.cmd` in place of
`run-prevue.sh`. They have the same options. `run-prevue.cmd` starts the PowerShell script, so the users do not have
to change the execution policy:

```bat
run-prevue.cmd --drive C:\path\to\drive --channels-dvr http://channels-dvr.local:8089
```

A native launcher cannot compile a translation while it runs, so it uses the interpreter. The interpreter
runs about 17 million instructions each second, and a 68000 runs less than 1 million, so the speed of a program does
not change. A native build does not need the listing of the program. Without a listing, use `--turbo <seconds>` in
place of `--turbo-until`. The users need these files, which are not part of the programs:

- The program to run and its drive, for example `ESQ` and a copy of the drive of Prevue.
- ffmpeg on the PATH, for `--stream`.

The build can also translate one program and compile the translation into the launcher:

```sh
EMBEDDED_PROGRAM=build/target/ESQ EMBEDDED_LISTING=build/target/ESQ.lst scripts/publish.sh
```

For a program without a listing, give a map of the code that ran in place of the listing, for example
`EMBEDDED_PROGRAM=aonic EMBEDDED_KNOWN_CODE=aonic.code scripts/publish.sh`. A native launcher with the Sonic demo and
its map interpreted 84 thousand instructions in 30 seconds.

The launcher then runs that program (found by its SHA-256) from the translation, and other programs in the
interpreter. The launcher is then 19 MB, not 7 MB. It contains the code of the program, so give it only to people
who can have that program. The speed of the emulated 68000 at full speed (`--fast-cpu`) for ESQ, measured at commit
1840165:

| Launcher | Speed |
|---|---|
| .NET, translation at run time | about 5,950 MHz |
| Native, translation at build time | about 4,350 MHz |
| .NET, interpreter | about 265 MHz |
| Native, interpreter | about 247 MHz |

A real 68000 runs at 7.16 MHz. At the real speed, all four use about a third of a host core, most of it for the
display and the pacing. The translation saves about 5% of a core.

The hardware parts that came after commit 1840165 made the emulation slower. On 2026-09-30, the .NET launcher with
translation ran ESQ at about 2,770 MHz at `--fast-cpu`, and ESQ used about 27% of a core at the real speed. Prevue
still runs at its real speed. [docs/performance.md](performance.md) gives the measurements, the cause, and work
that can make the emulation faster.

macOS stops programs from the internet that Apple did not check. A user can remove the mark with
`xattr -dr com.apple.quarantine <directory>`, or the programs can be signed and notarized with an Apple developer
account.
