# Samples

Each sample has the assembly source, the executable that vasm made from it, and the vasm listing. The tests use
the executable and the listing, so they do not need vasm.

| Sample | What it tests |
|---|---|
| `HelloWorld` | Library calls through `exec.library` and `dos.library`. `AmigaSharp/Generated/HelloWorld.cs` is the translation. |
| `ControlFlow` | A jump table into local labels, interpreted code that calls translated code, a stack frame, a data hunk, and an exit through a saved stack pointer. |

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
