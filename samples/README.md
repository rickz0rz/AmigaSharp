# Samples

Each sample has the assembly source, the executable that vasm made from it, and the vasm listing. The tests use
the executable and the listing, so they do not need vasm.

| Sample | What it tests |
|---|---|
| `HelloWorld` | Library calls through `exec.library` and `dos.library`. `AmigaSharp/Generated/HelloWorld.cs` is the translation. |
| `ControlFlow` | A jump table into local labels, interpreted code that calls translated code, a stack frame, a data hunk, and an exit through a saved stack pointer. |
| `FileIO` | File access through `dos.library`, `AllocMem`, `RawDoFmt` with a 68000 PutChProc, `IoErr`, and a BSS hunk. |
| `Graphics` | Drawing with `graphics.library`: `RectFill`, `Draw`, `Text` with JAM1, COMPLEMENT mode, and `BltBitMapRastPort`. |
| `Serial` | Interrupts and the serial port: `serial.device`, an RBF handler that `SetIntVector` installs, and `Wait` for a signal from the handler. |
| `Tasks` | A second process from `CreateProc` with a segment list in memory, as SAS/C programs make it. The tasks wait for each other in busy loops, so the scheduler must switch them. |
| `Fonts` | A disk font for the tests: `test.font` size 9, with proportional characters and a kern. It has no listing, because the tests only load it. Build it with `vasmm68k_mot -Fhunkexe -nosym -o test/9 test9.s`. The contents file `test.font` is binary data. |

## Rebuild a sample

Run these commands in the directory of the sample. The example is for `HelloWorld`.

```sh
vasmm68k_mot -Fhunkexe -nosym -L hello.lst -o hello hello.s
```

vasm writes the full path of the source file in the listing. Change the `Source:` line to the file name only, so
that the listing is the same on each computer.

## Translate HelloWorld again

Run this command in the root of the repository:

```sh
dotnet run --project AmigaSharp.Translator -- samples/HelloWorld/hello \
    --listing samples/HelloWorld/hello.lst --class HelloWorld --output AmigaSharp/Generated/HelloWorld.cs
```

A test fails if `AmigaSharp/Generated/HelloWorld.cs` is different from the output of the translator.
