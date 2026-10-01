# AGENTS.md

Notes for agents working in this repository. The README covers what the project is, the projects, the build and the
tests; read it first. This file covers what the README does not: how to check your work, conventions, and traps.

## Build and test

- `dotnet build -c Release` and `dotnet test` (see README "Tests" for the CPU vectors and the target program; tests
  that need them skip without them).
- `build/`, `target-source/` and `screenshots/` are untracked. `build/target/ESQ` and `build/target/ESQ.lst` come from
  `scripts/build-target.sh`.

## Check changes visually

Most display, genlock and Prevue work can only be verified by looking at the screen.

- `scripts/run-esq.sh [launcher options]` runs Prevue on a copy of the drive in `build/drive/` with the saved
  2020-11-01 listings. Without `target-source/asm` (and no `build/target/ESQ`), it runs the drive's ESQ (byte-identical)
  without a listing and with `--turbo 3`. Keep docs generic: any 68000 AmigaOS executable can run, a vasm listing is
  an optional development aid, and ESQ and ATK are only the tested programs. Useful launcher options:
  - `--virtual-time`: the run is deterministic and repeatable. Prefer it for comparisons.
  - `--screenshot <file.png> --seconds <n>` and `--screenshot-every <n>`: pictures without a window.
  - `--watch <label>`: logs each change of a 16-bit word at a listing label (needs the listing, which run-esq.sh
    passes). A 32-bit variable cannot be watched this way.
  - `--serial-port <port>`: the default serial bridge port is 5400. Give each parallel run its own port.
- Put screenshots for the user in `screenshots/` (untracked). Contact sheets:
  `ffmpeg -framerate 1 -pattern_type glob -i 'dir/p-*.png' -vf "scale=384:240,tile=5x2" -frames:v 1 sheet.png`.
- Stop background launcher runs with `pkill -TERM -f 'AmigaSharp\..*Launcher .*--stream <port>'`. Background jobs
  ignore SIGINT, and SIGTERM lets the launcher stop ffmpeg.
- The stream's HTTP server is .NET HttpListener: a POST without a body needs `curl -d ''`, or it answers 411.

## Sources for ESQ (Prevue)

- `build/target/ESQ.lst` (the vasm listing) is the authority for ESQ behavior. Code hunk at $200008, chip data hunk at
  $100008.
- https://github.com/rickz0rz/esq-decomp has readable C for most functions. It is a good map, but check details
  against the listing: for example, its letters for the type 1 CTRL sub-commands are wrong.
- `docs/ctrl-line.md` documents the 110 baud control line (promos, logos) and what is verified versus only read
  from code. `docs/orchestration.md` is the user-facing guide to using videos, music, promos and logos together;
  keep it in step when these features change. `scripts/examples/prevue-coordinator.py` is its tested example.

## Conventions

- Human-readable text (README, docs, code comments, commit messages, PR text) uses ASD-STE100 Simplified Technical
  English: active voice, short sentences (20 words max), no contractions, no semicolons, one term per concept. Match
  the style of the surrounding text. Agent-instruction files like this one are exempt.
- No AI attribution anywhere in the repository: no Co-Authored-By trailers, no "Generated with" footers.
- Commit only when asked, and push only when asked.
- `scripts/dist/run-prevue.sh` and `run-prevue.ps1` must stay in parity (same options, same help text). Both print
  their help from the header comment by line count (`sed -n '2,Np'` and `Select-Object -First N`): update the count
  when the header changes. Keep `scripts/dist/README.txt` in step with user-visible options.
- Keep the runtime and `AmigaSharp.Host` generic: they must run any Amiga program. `AmigaSharp.Host` is the launcher
  as a library (`Launcher.Run`, `LauncherOptions`, `ILauncherExtension`, `IStreamRequests`). A launcher for one
  program is a small exe that calls `Launcher.Run` with an extension: `AmigaSharp.Launcher` (none) and
  `AmigaSharp.PrevueLauncher` (`PrevueExtension`: control line, `/prevue/ctrl`, feed trace, Prevue defaults). Put
  Prevue code in `AmigaSharp.PrevueLauncher`. Plug HTTP routes into the stream through `IStreamRequests`, not by
  editing `VideoStream`. Prevue is a good example in comments, but name things for the general mechanism
  (`CtsLine`, not "ControlLine").
- `/prevue/state` and `/prevue/logos` read ESQ variables through `EsqVariables`: from `--listing`, else from a table
  of offsets for the known ESQ build (SHA-256 checked). If you need a new variable, add it to the table from
  `build/target/ESQ.lst`; `PrevueStateTests.KnownOffsets_AreTheOffsetsOfTheListing` verifies the table.
- Schedules (`--schedule`, `AmigaSharp.Host/Schedule.cs`) drive the genlock queue; extensions add segment keys
  through `IScheduleExtension` (Prevue adds "top" in `PrevueSchedule`). A segment starts when its genlock item
  becomes current; the runner queues the next segment's item at that moment so the 5 s preload works.
- Channel logos: `AmigaSharp.PrevueListings/Logos/` (PNG reader, ILBM writer, 640x240 16-color high-res composer, navy
  background as color 0 (the key, like the drive's logos) and never color 0 on the card, `LOGO.LST` writer). High-res matters: ESQ draws a logo and its channel text in the picture's
  display mode, and in low-res the text comes out twice as wide as on real hardware. The logo file name must equal ESQ's source name of the channel
  (`PrevueFeed.SourceNames`), not the displayed call letters.
- `AmigaSharp.Translator` is an exe that the launchers also use as a library. Its `ProjectReference`s carry
  `GlobalPropertiesToRemove="RuntimeIdentifier;SelfContained;PublishReadyToRun;PublishSingleFile;PublishAot"`, and the
  referencing projects set `ValidateExecutableReferencesMatchSelfContained=false`. Without them, a publish for another
  OS (`scripts/publish.sh win-x64` on macOS) builds the translator for the host RID and fails. `publish.sh`/`.ps1` use
  Native AOT for the host OS and a self-contained ReadyToRun folder (runtime translation works) for another OS. Do not
  make that folder single-file: `ProgramCompiler` needs `Assembly.Location`.
- Both launcher exes import `AmigaSharp.Host/EmbeddedProgram.targets` for build-time translation; the generated code
  registers itself with a module initializer in `EmbeddedPrograms`.
- Launcher options and HTTP endpoints that only make sense for Prevue get a `prevue` prefix (`--prevue-ctrl-port`,
  `/prevue/ctrl`, `--prevue-feed-trace`). Generic ones do not (`/genlock`, `/music`, `/mixer`).
- HTTP API style (see `VideoStream`): JSON via `JsonDocument`/`Utf8JsonWriter` (AOT-safe, no reflection
  serialization), errors as `{"error": "..."}` with 400/404, each POST answers with the current state.

## Traps

- Performance: every library call runs `Core.UpdateHardware` (all devices). New per-update work in a device costs
  throughput everywhere. `docs/performance.md` has the measurements, the profile method and the deferred
  optimizations; re-measure there if you touch `UpdateHardware`, `RunNative` or a device `Update`.

- Timing: in real-time mode the runtime jumps the emulation clock forward when the host falls behind, and interrupts
  can then come in a burst. Anything a program samples per interrupt must follow the interrupt's due time, not the
  wall clock. See `CustomChips.AudioSampleTime` and `BitBangedLine.Time` for the control line.
- ESQ resets its serial and control line state while it starts: bytes sent too early are lost. The launcher holds
  control line bytes until one second after ESQ enables AUD1, and holds replayed serial bytes with `--serial-start`.
- The stream's HTTP server is up before ESQ reads its control line, so early `/prevue/ctrl` requests wait in the
  queue. The line sends 11 bytes per second, and ESQ defers parsing while its display is busy: when sequencing
  commands, wait for `queued` to reach 0 and then a few seconds more.
- ESQ's logo timing (first logo, rotation) is not deterministic even with `--virtual-time`: the IFF loader runs as
  a separate task. To time a CTRL command against a logo, watch the screenshots live (for example the mean of the
  top 200 rows) and send the command over `--prevue-ctrl-port` when the logo shows.
