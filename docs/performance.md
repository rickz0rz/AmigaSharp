# Performance

This document gives the speed of the emulation, the causes of a slowdown, and work that can make it faster. The work
is not done. Prevue runs well at the speed of a real 68000, so this work has a low priority.

## Measurements

All values are for ESQ with the saved listings, on the same Apple Silicon Mac, on 2026-09-30. The launcher translates
ESQ at run time (.NET).

| Commit | ESQ at `--fast-cpu` | ESQ at the real speed |
|---|---|---|
| 1840165 (the values in the README before this document) | about 5,510 MHz | 18.8% of a core |
| cc2c194 (the chipset parts of the Amiga Test Kit) | about 3,520 MHz | not measured |
| 2f112a9 (the control line of Prevue) | about 2,980 MHz | not measured |
| a1cf89a | about 2,830 MHz | not measured |
| 02f98cc | about 2,770 MHz | 26.8% of a core |

- At `--fast-cpu`, the speed is the number of 68000 cycles in each second of host time, from `--stats`. Use the median
  of the last 15 lines. The values change by about 2% from run to run.
- At the real speed, the value is the CPU time of the launcher process between 20 s and 60 s after the start, from
  `ps -o cputime=`.
- A real 68000 runs at 7.16 MHz. Thus a slowdown at `--fast-cpu` does not change the speed of Prevue. It changes only
  the CPU time that Prevue uses.

## The cause

A profile of ESQ at `--fast-cpu` shows that 94% of the time of the emulation thread is in `Core.UpdateHardware`, not
in the code of ESQ.

- Each library call is a safe point. `Core.RunNative` calls `PollNow`, and `PollNow` updates all the hardware.
- ESQ calls library functions again and again in its loop for grid messages. So the hardware updates many times in
  each frame.
- Each new hardware part adds to the cost of each update. Commit cc2c194 added the CIA timers, the blitter and the
  display line by line. Later commits added the floppy drive, the sound and the control line.

The parts of `UpdateHardware` in the profile:

| Part | Part of the time |
|---|---|
| `CustomChips.Update` (frames and audio timers) | about 52% |
| `DiskController.Update` | about 15% |
| `BitBangedLine.Update` (the CTS and DSR lines) | about 7% |
| `PrevueState.Poll` | about 4% |

`DiskController.Update` uses time, but ESQ does not use the floppy drive. `BitBangedLine.Update` uses much of its time
to wait for a lock that the thread of the control line feed also uses.

## The map of the code that ran

Without a listing, the translator translates only the code that it can reach from the entry point. The launcher keeps
a map of the addresses where the interpreter started (see `AmigaSharp.Host/CodeMap.cs`), and the next translation also
starts at them. For the Sonic demo at `--cpu-mhz 56`:

| Run | Translated | Interpreted in 30 s | Host waits |
|---|---|---|---|
| 1 | 15 functions, 255 instructions | 115 million | 67% |
| 3 | 263 functions, 8,883 instructions | 4.0 million | 80% |
| 5 | 508 functions, 14,256 instructions | 0.6 million | 80% |

ESQ has a listing, so it does not use the map.

## Possible work

1. Do not update all the hardware at each library call if almost no Amiga time went by since the last update, for
   example less than one line of the display. The interrupts then still come at the correct time.
2. Make `DiskController.Update` return at once when no motor is on and no disk DMA runs.
3. Make `BitBangedLine.Update` read the state of its queue without the lock when the queue is empty.
4. Do not set the condition codes when no instruction reads them. The translation sets them after almost each
   instruction, for example after `MOVE.W (A1)+,D0`, also when the next instruction sets them again. The translator
   can find the instructions whose condition codes the next instructions of the function always set again before
   a read. A branch, a call, a return, a jump to an address that is known only at run time, and an interrupt can read
   them, so the translator must keep them at those points.
5. Make a call of translated code cheaper. Each `core.Call` pushes the return address, runs the function in a `try`
   block for `StackUnwindException`, and checks the return address. A call from translated code to a translated
   function with a normal return could skip the `try` block, if the unwind can find its frame in another way.

Items 4 and 5 make all the translated code faster. They do not need a profile of the hot functions: the JIT of .NET
already compiles the hot methods of a translation again with its own profile (tiered compilation and Dynamic PGO).
A profile of the program would help only the native (AOT) build, which has no JIT. .NET can give a recorded profile
to such a build.

Make sure that each change keeps the behavior:

- Run `dotnet test`. The CPU tests compare the translated code with the interpreter.
- Compare pictures of `scripts/run-esq.sh --virtual-time --screenshot <file> --seconds 200` before and after the
  change. The pictures must be the same.
- Run the Amiga Test Kit. It uses the CIA timers, the blitter, the keyboard and the floppy drive.
- Run the Sonic demo (see the README). It uses the AGA display, a fast CPU, and the map of the code that ran.

## How to measure

The speed at `--fast-cpu`:

```sh
scripts/run-esq.sh --fast-cpu --stats --headless --serial-port 5413
```

A profile, with `dotnet-trace` through `dnx` (no global installation):

```sh
dotnet build AmigaSharp.PrevueLauncher -c Release
dnx -y dotnet-trace -- collect --format speedscope --duration 00:00:00:40 -o trace.nettrace -- \
    dotnet AmigaSharp.PrevueLauncher/bin/Release/net10.0/AmigaSharp.PrevueLauncher.dll build/target/ESQ \
    --listing build/target/ESQ.lst --drive build/drive --volume DH1=build/drive --assign DF0=DH1: \
    --assign ENV=DH1: --arguments GA24005 --command-name esq --turbo-until _ESQ_MainLoopUiTickEnabledFlag \
    --date 2020-11-01T16:00 --fast-cpu --headless
```

Open `trace.speedscope.json` in https://www.speedscope.app. The sample profiler of .NET records samples only at the
safe points of the runtime. Thus it can give the time of a short method to `PollGCWorker`. Use the totals of the
methods that call it.

To find the commit that made a slowdown, use `git bisect run` in a worktree with a script that measures the speed at
`--fast-cpu`. Link `build/target` and copy `build/drive` into the worktree first.
