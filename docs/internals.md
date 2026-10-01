# How it works

This document is a part of the [README](../README.md). It tells how AmigaSharp runs a program: the translator,
the interpreter, the libraries and the chipset.

AmigaSharp does not emulate a full Amiga. It runs the code of the program, and C# code does the work of the
operating system.

## Translation

The translator reads the hunk executable and the vasm listing of the program. The listing tells which bytes are
instructions, and it gives the labels and the source lines. Without a listing, the translator follows the code from
the entry point. Each function of the program becomes a C# method, and each instruction becomes a block of C#. A
comment above each block shows the source line. This is a part of the translation of `samples/HelloWorld/hello.s`:

```csharp
// hello.s:14: JSR	OpenLibrary(A6)
// $20000A  JSR -552(A6)
{
    var target = cpu.A[6] - 552u;
    core.CallAddress(0x20000Eu, target);
}
// hello.s:15: TST.L	D0			;zero if OpenLibrary() failed
// $20000E  TST.L D0
{
    cpu.Cycles += 18;
    Ops.Logic(cpu, Size.Long, cpu.D[0]);
}
// hello.s:16: BEQ.S	NoDos			;if failed, skip to exit
// $200010  BEQ $200028
{
    if (cpu.Z) { NoDos(); return; }
}
```

The program loads at fixed addresses, so the translated code contains the addresses as constants. The launcher
compiles the C# code with Roslyn and keeps the assembly in a cache. The name of the cache file is a hash of the
executable, the listing and the translator.

## The interpreter

Some code has no translated method. For example, a program can copy code into memory, or the translator can miss a
function. The interpreter runs this code one instruction at a time. At each call, the runtime looks for a translated
method at the address. If there is no method, the interpreter runs the code. So translated code and interpreted code
can call each other.

Without a listing, the translator finds only the code that it can reach from the entry point: code that the program
reaches through a table of addresses or an interrupt vector runs in the interpreter. So the launcher keeps a map of
the addresses where the interpreter started, in the translation cache, and the next translation also starts there.
Each run then translates more of the program. For the Sonic demo, the interpreter ran 115 million instructions in 30
seconds in the first run, and 0.6 million in the fifth run. The map keeps only code of the file: code that a program
unpacks or writes while it runs stays in the interpreter. `--no-code-map` turns the map off.

A run in the interpreter only (`--interpret`, or a native launcher) also makes a map: the interpreter keeps the
target of each JSR, BSR and JMP that it runs. One such run of the Sonic demo found 441 addresses. A translation with
that map then interpreted 62 thousand instructions in 30 seconds. The map costs no measurable time: the interpreter
ran at about 275 MHz with and without it. A larger map makes the next start translate and compile again, once.

A map is a text file with one hexadecimal address on each line. The launcher writes its path after a run that adds to
it. To keep the map with the program, give the file with `--code-map <file>`. To merge the maps of runs on other
computers:

```sh
dotnet run --project AmigaSharp.Launcher -c Release -- merge-code-maps aonic.code computer1.code computer2.code
```

The translator reads a map with `--known-code <file>`, and a build that compiles a translation into the launcher reads
it from `EMBEDDED_KNOWN_CODE` (see [Build programs for other people](distribution.md)). A native
launcher then runs the translated code from its first start, also without a listing.

To see the code that the translator finds, write a disassembly with the translator:

```sh
dotnet run --project AmigaSharp.Translator -c Release -- aonic --disassemble aonic.s --known-code aonic.code
```

The disassembly has each hunk, the functions as labels, and each instruction with its address and its words. The
other bytes show as data, so a larger map shows more of the program as code. For the Sonic demo and its map, the
disassembly has 596 functions and 16,082 instructions. It is for inspection: an assembler does not read it.

A program can write new code over its own code, for example the decruncher of a packed program. The runtime keeps the
first 8 bytes of each translated method. If these bytes change, the runtime removes the method, and the interpreter
runs the new code. Some code jumps with an RTS: it puts the address on the stack, and does RTS. If the return address
of the call is still on the stack after the RTS, the runtime continues at the address of the RTS.

The tests compare the translated code with the interpreter. The SingleStepTests 68000 test vectors check the
interpreter. A native launcher cannot compile C# code while it runs, so it uses only the interpreter.

## The libraries

The runtime has no Kickstart ROM. Each library is a C# class, and each library function is a method of that class.
The library base and its jump table are in the emulated memory, as on a real Amiga. Each vector is a `JMP` to a stub
address in the ROM area. When the code jumps to a stub, the runtime calls the C# method. So a program can read the
jump table or change a vector with `SetFunction`.

The runtime has these libraries, devices and resources:

- `exec.library`, `dos.library`, `graphics.library`, `intuition.library`, `diskfont.library` and `utility.library`.
- `serial.device`, `input.device`, `console.device` and `trackdisk.device`.
- `battclock.resource`.

Each library has only the functions that ESQ and the samples use. A call to another function stops the program. The
error message gives the name of the library and the offset of the function. `dos.library` maps the AmigaDOS volumes
and assigns to host directories. `PROGDIR:` is the directory of the program, as in AmigaDOS 2.0 and later.

## Tasks and interrupts

Each Amiga task runs on its own host thread, because the translated code of a task keeps its calls on the C# stack.
Only one task runs at a time. A task gives the CPU to another task when it waits, or at a safe point when its time
slice ends.

Translated code cannot stop at each instruction for an interrupt. So the runtime delivers the interrupts at safe
points: in each library call, in each wait, in the interpreter, and at each backward branch of translated code. A
loop that waits for an interrupt has a backward branch, so the loop can end.

## Timing

The runtime adds an estimate of the clock cycles of each instruction. It uses the estimate to run the CPU at the
speed of a 7.16 MHz 68000. When the CPU is ahead of the real time, the runtime sleeps. The estimate uses the main
rules of the 68000 timing tables, so it is near the real time, but not equal to it.

## The chipset

The runtime emulates the parts of an Amiga 2000 (ECS, NTSC) that ESQ and the Amiga Test Kit use:

- The display: the bitplanes, the copper, sprites, the display window, the scroll, dual playfield, extra half-brite
  and interlace. It makes each line when the beam passes it. It does not show HAM. The pixels of the genlock key
  (color 0, or the ECS key of BPLCON2) have alpha 0 in the picture.
- The blitter: area, fill and line blits. A blit ends at once.
- The interrupts of the custom chips and the CIAs. A program can use the handlers of exec, or write its own handlers
  to the exception vectors.
- The audio channels play their samples with DMA, with the low-pass filter of the power LED. The window and the
  stream play the sound, and `--audio-file <file.wav>` writes it to a file. Modulation and the play of AUDxDAT without DMA are not
  emulated.
- The serial port. A TCP port or a file on the host is the other end of the cable.
- The two CIAs: the ports, the timers, the time-of-day counters and the keyboard on the serial port of CIA-A.
- The mouse in port 1 and the joystick in port 2. In the window, the host mouse is the mouse, and a game controller
  of the host is the joystick.
- The battery-backed clock at $DC0000.
- The internal floppy drive DF0: the drive signals on the CIAs, the index pulse and the disk DMA. When the program
  runs from an ADF, the disk is in DF0 as AmigaDOS MFM tracks. A write changes only a copy in memory.

The addresses where an A2000 has nothing are an open bus: a write does nothing, and a read gives 0.
