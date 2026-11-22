# Yumlog Native CLI Reference

`Yumlog.Native.Cli` is the no-PowerShell entry point for capture, recording,
metadata, and on-device reasoning.

> **Entry point.** The shipping executable is built from
> `apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj`. `apps\Yumlog.Native` is the
> library project that holds the implementation modules. Build the `.Cli`
> project to get a runnable binary.

```powershell
dotnet build .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release
$exe = ".\apps\Yumlog.Native.Cli\bin\Release\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.Cli.exe"
& $exe --help
```

## Commands

| Command | Purpose |
|---|---|
| `capture` | Fast screenshot sequence to a directory |
| `record` | Desktop video via WGC plus a hardware or software HEVC encoder |
| `analyze` | Image metadata, hashes, dimensions, OCR |
| `follow` | Change-gated frame sequence plus a follow manifest |
| `orchestrate` | Run a multi-step plan file |
| `config show` / `config init` | Inspect or scaffold `config\yumlog.native.json` |
| `selftest` | Built-in correctness suite (17 checks) |
| `runbook` | Turn an `.mdkg` sidecar into a cited, replayable runbook |
| `reason` | Answer a question about a recording using an on-device LLM |
| `watch` | Live screen observation with periodic narration |
| `models` | List installed on-device models |
| `speak` | Render narration through gen-audio (`render_speech`) and verify its receipt |
| `transcribe` | Transcribe a WAV with gen-audio VibeVoice ASR into a receipt and optional SRT |

### speak and transcribe (gen-audio)

```
speak (--text text | --text-file path) --out-file path.wav [--engine kokoro_onnx] [--voice id]
      [--language en-us] [--python path] [--gen-audio-root path] [--json-out path]
transcribe --input path.wav [--model dir] [--device cpu|cuda] [--srt-out path]
           [--json-out path] [--python path] [--gen-audio-root path]
```

Yumlog calls [darbotlabs/gen-audio](https://github.com/darbotlabs/gen-audio) only through its two
CLI entry points, `python -m gen_audio.cli.render_speech` and `python -m gen_audio.cli.transcribe_vibevoice`.
The script goes to gen-audio on stdin as UTF-8. Yumlog accepts a WAV only when its SHA-256, sample
rate, and duration match the JSON receipt gen-audio prints. A non-zero gen-audio exit fails the
command with the tail of gen-audio's stderr.

Defaults come from the environment when a flag is omitted: `GEN_AUDIO_PYTHON` (else `python`),
`GEN_AUDIO_ROOT` (its `src` is prepended to `PYTHONPATH`), `YUMLOG_GEN_AUDIO_ENGINE` (else `kokoro_onnx`),
`YUMLOG_GEN_AUDIO_VOICE`, `YUMLOG_GEN_AUDIO_LANGUAGE` (else `en-us`), `GEN_AUDIO_VIBEVOICE_ASR_MODEL`,
and `YUMLOG_GEN_AUDIO_DEVICE` (else `cpu`). Studio exposes the same settings, plus
"Transcribe with gen-audio VibeVoice" on the Transcript tab and "Render with gen-audio" on the Voices tab.

`launchers\Test-YumlogGenAudio.ps1` checks this boundary against a stdlib-only stub in
`qa\gen-audio-stub` (no models needed). Pass `-LiveRoot <gen-audio checkout> -LiveVoice <id>` to render real speech too.

### record

```
record [--config path] [--out-file path] [--fps n] [--duration n]
       [--backend auto|nvenc|x265|ffmpeg] [--lossless true|false] [--preset name]
       [--container mp4] [--x265 path] [--capture-backend wgc]
       [--monitor-index n] [--disable-capture-border true|false]
       [--timed-metadata-out path]
```

`--timed-metadata-out` writes the `.mdkg` metadata sidecar that `runbook` and
`reason` consume. Without it you get video only and the reasoning commands have
nothing to read.

Backends fall back in order when `--backend auto`: NVENC, then x265, then
FFmpeg. `probe.nvenc.driverDll` in `selftest` reports whether
`nvEncodeAPI64.dll` is present on the machine.

```powershell
& $exe record --out-file .\out.mp4 --timed-metadata-out .\out.mdkg `
              --duration 4 --fps 15 --backend nvenc
```

### runbook

```
runbook --input path.mdkg [--json-out path] [--markdown-out path]
```

Emits an ordered list of steps, each carrying **proofs**: claim / path / value
triples where the path is a resolvable `mdkg://` citation.

```json
{ "claim": "frame index at this step",
  "path":  "mdkg://timing@0#/frameIndex",
  "value": "0" }
```

Every proof re-resolves against the source file, so a runbook can be verified
rather than trusted. `intel.runbook.proofsVerify` in `selftest` asserts this.
Measured on `hevc-swarm-out\baseline\artifacts\r2-churn2.mdkg`: **155 steps,
310 proofs** (two per step).

### reason

```
reason --input path.mdkg [--question text] [--model dir]
       [--provider cpu|auto|webgpu|dml|cuda] [--high-water 0..1]
       [--max-tokens n] [--max-segments n] [--json-out path]
```

See [ON-DEVICE-REASONING.md](ON-DEVICE-REASONING.md) for how grounding works and
what the model can and cannot see.

### watch

```
watch [--duration n] [--fps n] [--change-threshold n] [--ocr none|auto]
      [--narrate-every seconds] [--question text] [--out dir]
      [--model dir] [--provider cpu|auto] [--high-water 0..1]
      [--max-tokens n] [--json-out path]
```

Observes the live screen and narrates on an interval. Frames below
`--change-threshold` are skipped, so a static screen costs almost nothing.

> **Testing note.** Do not validate `watch` by generating synthetic full-screen
> windows. Fabricated input proves only that the tool can see the fabrication.
> Drive it with real application activity, with the operator's consent.

### selftest

```powershell
& $exe selftest
```

Returns JSON with top-level keys `passed`, `passedCount`, `failedCount`, and
**`cases`** (not `results` or `checks`).

| Group | Checks |
|---|---|
| `mdkg.*` | `layerTable.roundTrip`, `header.fixed128`, `timing.roundTrip`, `timing.coverageNoOverlap`, `semanticGraph.reconstruct`, `carrier.symmetry`, `unknownLayer.skip` |
| `intel.*` | `path.parseFormat`, `path.resolve`, `path.missingIsNone`, `timeline.ordered`, `timeline.window`, `runbook.deterministic`, `runbook.proofsVerify`, `runbook.markdown` |
| `probe.*` | `nvenc.driverDll` |

`mdkg.timing.coverageNoOverlap` ships with a negative control: it asserts that a
deliberately overlapping timeline *fails*, so the check cannot pass vacuously.

> **Build before you trust it.** Build output is not source. Rebuild before
> reading `selftest` as current.

## Configuration

Defaults live in `config\yumlog.native.json`; every value has a command-line
override. `config init --force` rewrites the file.

## Known rough edges

- **Unknown options fail fast.** A typo such as `--output` instead of
  `--out-file` exits 1 with the unsupported option name.
- **CLI JSON has trailing non-JSON text.** Slice between the first `{` and the
  last `}` before parsing.
- The unpackaged executable reports OCR `CapabilityMissing` by design; Windows AI
  OCR needs package identity with `systemAIModels`.

## update-check and specs (0.7.0)

```powershell
Yumlog.Native.Cli update-check [--repo owner/name] [--api url] [--timeout-ms n] [--json-out path]
Yumlog.Native.Cli specs [--out-dir path] [--capture-method auto|printwindow|screen] [--collect-only true] [--json-out path]
Yumlog.Native.exe --capture-specs   # Studio: capture Settings > System > About into the library after launch
```

Both read the `update` and `specs` sections of `config\yumlog.native.json`, then
`%LOCALAPPDATA%\DarbotLabs\Yumlog\yumlog.settings.json`, then `YUMLOG_UPDATE_*` / `YUMLOG_SPECS_*`.
Studio runs the update check on every launch; results go to `<data root>\logs\update-check.log`.
