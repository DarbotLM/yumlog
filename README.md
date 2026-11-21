# darbot.yumlog

## Screen Capture & Recording

darbot.yumlog provides local Windows tools for desktop screen capture, recording, and lightweight screen analysis. The legacy PowerShell tools remain available, and the native `Yumlog.Native.Cli` F# CLI is the no-PowerShell path for capture, recording orchestration, analysis manifests, follow-me step extraction, verifiable runbooks, and on-device reasoning about a recording.

Two reference documents cover the native path in depth:

- [`docs/NATIVE-CLI.md`](docs/NATIVE-CLI.md) — full command reference, flags, config, and known rough edges
- [`docs/ON-DEVICE-REASONING.md`](docs/ON-DEVICE-REASONING.md) — how grounding works, frame-rate accuracy, and measured Edge Prompt API behaviour

Paperboy is Yumlog's lightweight courier layer: it can sample recordings with
half-step navigation and can pack files into small manifest-backed bundles that
are easy to copy, attach, or "toss" to another workspace.

Yumlog also includes a local APIM service foundation under
`apps\Yumlog.APIM.Service`. The service exposes loopback health endpoints,
ACP resource/tool/prompt metadata, authenticated local envelope routes, and
APIM route invocation scaffolding for the Paperboy APIM app, MCP bridge,
Widget Studio, LiveTile, OCR, and MDKG workflows.

### Repository ownership

Yumlog is the source of truth for capture, recording, Windows AI OCR, native
analysis/follow manifests, and MDKG metadata contracts. The standalone Paperboy
application owns delivery/bundling, orchestration, its client SDK, APIM execution,
and widget authoring/runtime development.

The Paperboy-related components in this checkout remain available with the
behavior documented here. They are not automatically synchronized with the
standalone application: in particular, this checkout's APIM service is still
the documented foundation, not a fully connected execution backend. No existing
commands are removed by this ownership split.

Integrations should exchange versioned manifests and bundles, not assume a
shared working directory or copy unreviewed implementation changes between
repositories. See [the ownership and compatibility policy](Tasklist.md#ownership-and-compatibility)
before changing shared contracts.

### Components and prerequisites

Run the examples below from the repository root on Windows.

| Component | Purpose | Documentation |
|---|---|---|
| `apps\Yumlog.Native` | F# capture, recording, OCR, MDKG, and extension catalog | [Extension contract](apps/Yumlog.Native/EXTENSIBILITY.md) and [MDKG format](apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md) |
| `apps\Paperboy.Desktop` | WPF bundle UI, model manager, live tile, tray, and capability catalog | [Paperboy guide](apps/Paperboy.Desktop/README.md) |
| `apps\Paperboy.Mcp` | Paperboy stdio MCP host | [MCP configuration](apps/Paperboy.Desktop/README.md) |
| `apps\Yumlog.APIM.Service` | Local HTTP/ACP service foundation | [Service guide](apps/Yumlog.APIM.Service/README.md) |
| `apps\Yumlog.WidgetStudio` | Widget manifest validation, HTML preview, and package export | [Widget Studio guide](apps/Yumlog.WidgetStudio/README.md) |
| `Skills`, `launchers`, `snap` | PowerShell utilities, packaging/service launchers, and fast screenshot executable | [Development guide](Tasklist.md) |

Use the .NET SDK versions required by the component projects: Paperboy, APIM,
and Widget Studio target .NET 11, while Yumlog.Native targets .NET 10 with
Windows 11 24H2 projections. Install matching runtimes for framework-dependent
execution, including the Windows Desktop runtime for Paperboy Desktop and
Yumlog.Native.
PowerShell is required for launcher scripts; FFmpeg is required for recording.
`launchers\install.ps1` can download FFmpeg and build the optional `snap` helper.
Windows AI OCR has additional hardware, runtime, and package-identity requirements
described below. Foundry Local is optional and must be installed/configured
separately for model workflows.

The tools default to local operation, but dependency downloads, model acquisition,
and explicitly configured HTTP/connectors can use the network. Do not commit
API keys, signing certificates, recordings, or generated reports.

### Web Interface

For a visual interface to manage, configure, and use Yumlog, open `yumlog-manager.html` in your web browser. The HTML interface provides:

- Easy command generation for recording and capturing
- Commands for retrieving statistics
- Configuration editor with manual load/save instructions
- PowerShell command reference

Simply double-click `yumlog-manager.html` to launch the interface in your default browser.
It generates commands and instructions; it does not execute them or automatically
read local recordings/configuration.

### Usage Examples

#### Portable analysis and MDKG handoff

```powershell
# Analyze existing images, copy sources, and emit a portable verified handoff.
# No desktop capture or OCR model provisioning with --ocr none.
dotnet run --project .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release -- handoff --input .\screenshots --out-dir .\yumlogs\handoff-one --ocr none

# Reuse only if the original inputs/options and every completed output match.
dotnet run --project .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release -- handoff --input .\screenshots --out-dir .\yumlogs\handoff-one --ocr none --resume
```

The versioned `handoff.json` identifies copied frames, `analysis.json`, and
`session.mdkg` using relative paths, lengths, and SHA-256 hashes. `handoff.sha256`
protects native resume against an accidentally modified manifest. Output is
promoted from unique staging only on completion; existing directories are not
overwritten. Downstream consumers can package this directory without depending
on the producer's original absolute image paths. Checksums detect corruption,
not malicious replacement of both content and checksums.

This writes a real standalone MDKG sidecar; it does not mux metadata into MP4.
See the [native guide](apps/Yumlog.Native/EXTENSIBILITY.md) for the full handoff
contract and explicit OCR side effects.

`handoff` OCR never provisions models. Use `readiness --ocr windows-ai` for a
read-only `{ready,code,message}` probe; existing `analyze`/`follow` behavior is
unchanged and may provision when explicitly invoked with OCR.

#### Using Yumlog Studio

Launch the native desktop UI on Windows with the .NET 10 SDK and Microsoft Edge
WebView2 Runtime.

```powershell
dotnet run --project .\apps\Yumlog.Native\Yumlog.Native.fsproj -c Release
```

Open a local video or record the desktop to create an AgentCard. Use the bottom
tabs to work with clips, frames, audio, captions, agents, and analysis. Flip to
**Paperboy** to copy the JSON Schema or export the card. **Harvest** can bundle
the card, original recording, and derived files as a `.paperboy.zip`.

FFmpeg and FFprobe enable video inspection and exports. Configure an optional
`whisper-cli` executable and model under **Settings** for local speech
transcription. **Voices** uses the installed Windows speech voices.

#### Build and install the production package

The release flow publishes self-contained `win-x64` Native, Paperboy, MCP, APIM,
and Widget Studio payloads, bundles the local FFmpeg runtime, and writes per
component payload digests.

```powershell
.\launchers\Build-YumlogProduction.ps1
.\launchers\Test-YumlogProductionPackage.ps1
.\launchers\Install-YumlogProduction.ps1
```

The installer creates versioned user-local binaries and Start Menu shortcuts.
It does not modify system-wide certificate or service state.

#### Agent client protocol and Agent Plugin 1.0

Paperboy exposes the same MCP tools over standard stdio NDJSON (with legacy
`Content-Length` compatibility) and a loopback HTTP host:

```powershell
.\apps\Paperboy.Mcp\bin\Release\net11.0-windows\Paperboy.Mcp.exe --http `
  --url http://127.0.0.1:5117
```

The installed Paperboy UI can start and stop this host from **APIM Surface**.
It publishes:

| Surface | URL |
|---|---|
| Stateless / streamable MCP | `http://127.0.0.1:5117/mcp` |
| Legacy MCP SSE | `http://127.0.0.1:5117/sse` |
| Legacy POST endpoint | `http://127.0.0.1:5117/message?sessionId=...` |
| Agent Plugin 1.0 | `http://127.0.0.1:5117/.well-known/ai-plugin.json` |
| OpenAPI | `http://127.0.0.1:5117/openapi.json` |
| Read-only tagging pipeline | `http://127.0.0.1:5117/api/tag` |

The HTTP catalog is intentionally read-only: it exposes cards, discovery,
bundle inspection, and Foundry analysis, but not model downloads, model
mutation, bundle writes, or arbitrary extension execution. Tag requests accept
only paths under Yumlog/Studio storage or roots listed in
`YUMLOG_AGENT_ALLOWED_ROOTS`, and only a loopback HTTP Foundry endpoint.

The installed app connects selected payload paths by passing its allowed roots
to the child host. The current pipeline computes hashes and Foundry tags,
classifications, and summaries. Image bytes are supported; video uses one
sampled frame. Audio, speech transcription, and document-body grounding remain
explicit capability/model requirements rather than silently inferred output.

#### Using the native F# CLI

The runnable binary is built from `apps\Yumlog.Native.Cli`. The `apps\Yumlog.Native` project is the implementation library.

```powershell
# Build the native CLI
dotnet build .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release
$exe = ".\apps\Yumlog.Native.Cli\bin\Release\net10.0-windows10.0.26100.0\win-x64\Yumlog.Native.Cli.exe"

# Capture a fast all-monitor screenshot sequence
& $exe capture --out-dir .\screenshots --duration 1

# Record HEVC video AND the .mdkg metadata sidecar that powers runbook/reason
& $exe record --out-file .\yumlogs\native.mp4 --timed-metadata-out .\yumlogs\native.mdkg `
              --duration 10 --fps 30 --backend auto

# Analyze image files and emit JSON metadata
& $exe analyze --input .\screenshots --ocr auto

# Capture follow-me frames and write a follow manifest
& $exe follow --out-dir .\yumlogs\follow --duration 10 --ocr auto

# Turn a recording into a runbook where every claim carries a resolvable citation
& $exe runbook --input .\yumlogs\native.mdkg --markdown-out .\runbook.md

# Ask an on-device model a question about the recording
& $exe reason --input .\yumlogs\native.mdkg --provider cpu `
              --question "At what frame rate was this captured, and were any frames dropped?"

# Watch the live screen and narrate on an interval
& $exe watch --duration 30 --fps 2 --narrate-every 10

# List installed on-device models, and run the built-in correctness suite
& $exe models
& $exe selftest

# Show or initialize native config
& $exe config show
& $exe config init --force

# Build an unsigned MSIX without installing or changing certificate trust
.\launchers\Build-YumlogNativeMsix.ps1 `
  -ProjectPath .\apps\Yumlog.Native\Yumlog.Native.fsproj `
  -OutputRoot .\dist\Yumlog.Native -Configuration Release -NoSign
```

> `--timed-metadata-out` is what makes a recording answerable. Without it you get
> video only, and `runbook` and `reason` have nothing to read.

> Unknown arguments are currently ignored silently. A typo such as `--output`
> instead of `--out-file` exits 0 and writes nothing.

#### Verifiable runbooks

`runbook` converts an `.mdkg` sidecar into ordered steps, each carrying proofs —
claim / path / value triples where the path resolves back into the file:

```json
{ "claim": "frame index at this step",
  "path":  "mdkg://timing@0#/frameIndex",
  "value": "0" }
```

Because every proof re-resolves against the source, a runbook can be checked
rather than believed. Measured on `hevc-swarm-out\baseline\artifacts\r2-churn2.mdkg`:
155 steps, 310 proofs (two per step).

#### On-device reasoning

`reason` answers questions about a recording using a local model through ONNX
Runtime GenAI. Nothing leaves the machine, and Yumlog loads model weights
directly rather than calling Edge's model service.

Frame-rate reporting is verified across three independent rates — 12 fps, 15 fps,
and 30 fps each produce the correct answer with zero coverage overlaps. Answer
phrasing varies between runs, so assert on content rather than exact wording. See
[`docs/ON-DEVICE-REASONING.md`](docs/ON-DEVICE-REASONING.md) for how grounding is
assembled and why runbook proofs are *not* visible to the model.

#### Built-in correctness suite

`selftest` runs 16 checks across the MDKG container, the intelligence layer, and
hardware probes, and returns JSON with keys `passed`, `passedCount`,
`failedCount`, and `cases`. `mdkg.timing.coverageNoOverlap` ships with a negative
control so it cannot pass vacuously.

Note that `bin\` is committed in this repository, so a stale binary can report an
old pass count. Rebuild before treating `selftest` output as current.

`Yumlog.Native` is local-only and uses `config\yumlog.native.json` by default. Capture uses Windows Graphics Capture (WGC) with a Direct3D path; recording encodes HEVC through NVENC, falls back to x265, then to FFmpeg (`--backend auto|nvenc|x265|ffmpeg`). `selftest` reports NVENC availability via `probe.nvenc.driverDll`. OCR uses the Windows AI Text Recognition stack: `--ocr auto`, `--ocr windows-ai`, and `--ocr raw-com` (alias of `windows-ai`) run the full recognition pipeline and return lines, words, polygon bounds, and confidence values. The pipeline is implemented in F# (`WindowsAiOcr.fs`) on the managed `Microsoft.WindowsAppSDK.AI` projections: it bootstraps the Windows App Runtime, checks `TextRecognizer.GetReadyState`, provisions the on-device model via `TextRecognizer.EnsureReadyAsync` when `NotReady`, creates a recognizer with `CreateAsync`, decodes the image to a BGRA8 `SoftwareBitmap`/`ImageBuffer`, and projects `RecognizeTextFromImage` results into the native data model. Windows AI OCR requires Windows App SDK runtime registration, package/sparse-package identity with `systemAIModels` (run the packaged build so `EnsureReadyAsync` can deploy the `Windows.Workload.ContentExtraction` model), and Copilot+/NPU-class hardware; the unpackaged exe reports `CapabilityMissing` by design. `apps\Yumlog.Native\Package.appxmanifest` declares the `systemAIModels` capability and an `appExecutionAlias` so the packaged CLI is invocable as `Yumlog.Native.Cli.exe`.

`Mdkg.fs` implements the `.mdkg` binary container format (Magic: `MDKG\r\n\x1A\n`, version 1.0). MDKG is a layered metadata track format designed to carry frame-accurate metadata alongside HEVC/MP4 recordings. Layer samples align to the `MovieTimescale` (10,000,000 ticks/sec), and unregistered layer IDs are skipped rather than treated as corruption, so the format is forward-compatible.

| Layer | ID | Carries |
|---|---|---|
| `Timing` | 1 | Frame index, PTS, duration, drop accounting |
| `OcrText` | 2 | Recognized text, lines, words, confidence |
| `UiAffordance` | 3 | Interactive UI elements and their bounds |
| `SemanticGraph` | 4 | Entities and relations reconstructed across frames |
| `InputEvent` | 5 | Input events correlated to frames |

The format is independent of MP4 and can be embedded as a `mett` timed-metadata track or stored as a standalone `.mdkg` sidecar. See `apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md` for the full byte-layout specification.

#### NPU Emulator Container app

`apps\Yumlog.NpuEmulator` is the Rust/Tauri host for Yumlog's adaptive NPU-emulation runtime. It models the hard parts of Windows AI `systemAIModels`-style execution directly: feature readiness, model provisioning, container worker state, CPU-native emulated NPU execution targets, and trace capture for repeatable ground-truth analysis.

```powershell
cd apps\Yumlog.NpuEmulator
npm install
npm run tauri dev

# Rust-only validation
cd src-tauri
cargo test
cargo check
```

The first implementation exposes Tauri commands for `TextRecognizer`-style state, provisioning, model inventory, trace journal, container runtime probing, and recognition request acceptance. The next integration step is a local HTTP or named-pipe bridge so `Yumlog.Native.Cli` can call the emulator through a first-class `--ocr npu-emulator` mode.
The MSIX launcher requires Windows SDK `makeappx.exe` and rebuilds the package
layout under the specified output root. **Omitting `-NoSign` creates/reuses a
signing certificate and modifies CurrentUser certificate trust.** Installation
is a separate explicit action. The older project `PackageMsix` target references
two missing Paperboy image assets; use the documented native launcher instead.
See the [packaging details](apps/Yumlog.Native/EXTENSIBILITY.md#package-identity-and-ocr)
before running install or OCR helpers.

#### Using the local APIM service

```powershell
# Build the APIM service
dotnet build .\apps\Yumlog.APIM.Service\Yumlog.APIM.Service.fsproj -c Release

# Run a temporary local round-trip validation on http://127.0.0.1:5119
.\launchers\Test-YumlogAPIMRoundTrip.ps1

# Install/start/stop as a Windows Service from an elevated PowerShell session
.\launchers\Install-YumlogAPIMService.ps1
.\launchers\Start-YumlogAPIMService.ps1
.\launchers\Stop-YumlogAPIMService.ps1
.\launchers\Uninstall-YumlogAPIMService.ps1
```

APIM service endpoints include `/health`, `/ready`, `/version`,
`/capabilities`, `/acp/resources`, `/acp/resources/read`, `/acp/tools`,
`/acp/tools/call`, `/acp/prompts`, `/acp/prompts/get`, `/apim/routes`, and
`/apim/routes/invoke`. If `YUMLOG_APIM_API_KEY` or `Auth:ApiKey` is set,
protected requests must provide the key in `X-Yumlog-APIM-Key`,
`Authorization: Bearer <key>`, or `auth.token` in the JSON envelope. The service
is a foundation: catalog/validation and envelope responses do not imply that
OCR, bundling, model inference, or arbitrary route backends are executed.
See the service guide for public endpoints and implementation limits.


`Mdkg.fs` implements the `.mdkg` binary container format (Magic: `MDKG\r\n\x1A\n`, version 1.0). MDKG carries timestamped metadata alongside recordings as a standalone sidecar. The typed layer contracts cover timing, OCR, change scores, navigation, embeddings, semantic and integration metadata, and the extension catalog. The binary format and its proposed MP4 integration are documented in [MDKG-SPEC.md](apps/Yumlog.Native/hevc-swarm-out/MDKG-SPEC.md); writing a sidecar does not itself mux metadata into an MP4.

#### Native extension catalog

```powershell
dotnet run --project .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release -- extensions list
dotnet run --project .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release -- extensions validate --dir .\apps\Yumlog.Native\Samples
dotnet run --project .\apps\Yumlog.Native.Cli\Yumlog.Native.Cli.fsproj -c Release -- extensions mcp
```

The catalog combines built-in capability descriptors with optional JSON extension
manifests. The MCP command exports a manifest; it does not start a tool server or
execute the declared extensions. See the [extension guide](apps/Yumlog.Native/EXTENSIBILITY.md).

#### Widget Studio

```powershell
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- validate .\apps\Yumlog.WidgetStudio\Samples\paperboy-capability-mesh.widget.json
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- preview .\apps\Yumlog.WidgetStudio\Samples\paperboy-livetile.widget.json .\dist\widgetstudio\paperboy.preview.html
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- self-test
```

Widget Studio supports schema versions `1.0` and `1.1`, semantic validation,
capability indexes, static HTML previews, and `.widget.zip` exports. It is an
authoring CLI, not an always-running widget host.

#### Using the Unified CLI

```powershell
# Record the desktop at 30 FPS for 15 seconds
.\launchers\yumlog.ps1 start -Fps 30 -DurationSec 15

# Get the latest recording
.\launchers\yumlog.ps1 get

# Check recording count and total size
.\launchers\yumlog.ps1 count
.\launchers\yumlog.ps1 size

# View configuration
.\launchers\yumlog.ps1 config
```

#### Recording terminal windows with Paperboy frames

```powershell
# Record visible terminal windows, extract binary-search frame samples,
# and write a manifest for the HTML viewer.
.\launchers\Record-Terminals.ps1 -DurationSec 10 -Depth 4 -Fps 15
```

The terminal recorder uses the `Paperboy` half-step navigation engine to choose
representative timestamps across a recording. The generated manifest can be
loaded by the bundled terminal viewer for quick inspection of frame changes.

#### Packing files into a Paperboy bundle

```powershell
# Pack files/folders into a manifest-backed .paperboy.zip archive.
.\launchers\paperboy.ps1 pack .\Skills, .\README.md -OutFile .\dist\yumlog.paperboy.zip

# Inspect the bundle manifest without unpacking the payload.
.\launchers\paperboy.ps1 list .\dist\yumlog.paperboy.zip

# Expand the bundle later.
.\launchers\paperboy.ps1 unpack .\dist\yumlog.paperboy.zip -Destination .\restored -Force

# Toss an existing bundle to another directory or final file path.
.\launchers\paperboy.ps1 toss .\dist\yumlog.paperboy.zip -Destination .\outbox -Force
```

Paperboy bundles store payload files under `payload/` and include
`paperboy-manifest.json` with source paths, archive paths, byte counts,
timestamps, and SHA-256 hashes. They are ordinary ZIP archives with a
`.paperboy.zip` convention so they remain easy to inspect with standard tools.

#### Using the native Paperboy desktop app

```powershell
# Build the Windows-native .NET 11 desktop app
dotnet build .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj -c Release
dotnet build .\apps\Paperboy.Mcp\Paperboy.Mcp.csproj -c Release

# Publish a self-contained win-x64 app folder
dotnet publish .\apps\Paperboy.Desktop\Paperboy.Desktop.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

The desktop app provides a modern drag-and-drop UI for selecting files and
folders, choosing compression, creating `.paperboy.zip` bundles, inspecting
manifests, unpacking bundles, and tossing bundles to an outbox folder.
It uses WPF for its showcase shell and ships
composable agent-card metadata for A2A/livetile surfaces plus a companion
`Paperboy.Mcp` stdio host for agent tool calls.

#### Offline metadata tagging with Foundry Local

Paperboy can enrich payloads with offline model metadata using a running
Foundry Local OpenAI-compatible endpoint:

```powershell
# Example endpoint started by a Foundry Local web-server workflow:
# http://127.0.0.1:52495/v1
```

In the desktop app, use **Foundry Local offline tagging** to choose:

- endpoint, for example `http://127.0.0.1:52495/v1`
- model alias/id, for example `qwen3.5-0.8b` for vision-capable responses
- whether to send image content
- whether to sample a video frame with FFmpeg

The pipeline produces a `paperboy-foundry-metadata-*.json` sidecar containing
file hashes, media kind, tags, classifications, summaries, and raw model output.
The sidecar is automatically added to the payload list so it can travel inside
the same `.paperboy.zip` bundle.

Recommended offline model roles:

| Pipeline | Foundry Local model role |
|---|---|
| Image classification/tagging | Vision-capable Responses API model |
| Video classification/tagging | Vision-capable model over sampled frame plus file metadata |
| Audio metadata tagging | Chat model over file metadata, or Whisper externally for transcription |
| Document/log tagging | Small chat model over text/file metadata |

The MCP host also exposes `paperboy.foundryLocal.analyze` for agent-driven
offline tagging.

#### Delivering Foundry Local language models with Paperboy

Paperboy includes a full-page **Foundry Local Model Delivery Cards** layer. It
does not redistribute model weights. Instead, it packages model aliases, roles,
recommended prompts/config, and MCP tool hints so an offline Foundry Local
workspace can pull/load the right local model:

| Model card | Delivery role |
|---|---|
| `qwen2.5-0.5b` | Fast language model for chat, metadata, and routing |
| `qwen3.5-0.8b` | Vision-capable responses model for image/video-frame tagging |
| `phi-3.5-mini` | General local reasoning and document summarization |
| `whisper-tiny` | Audio transcription workflow |
| `qwen3-0.6b-embedding` | Semantic search and similarity fingerprints |

Use **Foundry Models** in the desktop app to export or bundle the model-card
catalog as a `.paperboy.zip`. The MCP host exposes the same catalog through
`paperboy.foundryLocal.modelCards`.

The **Paperboy Model Manager (PMM)** controls in that layer call the local
`foundry` CLI to manage the model lifecycle:

- `Download` caches the selected model locally.
- `Load` loads the selected model into the Foundry Local service.
- `Info` displays the model's Foundry catalog metadata.
- `Download all` hydrates the Paperboy model-card set.
- `Refresh cache` shows which model cards are already cached.

PMM settings include the CLI path (`foundry` by default) and target device
(`Auto`, `GPU`, `CPU`, or `NPU`). The same operations are exposed through MCP as
`paperboy.pmm.info`, `paperboy.pmm.download`, `paperboy.pmm.load`,
`paperboy.pmm.cacheList`, and `paperboy.pmm.run`.

#### Floating Paperboy 9bit widget

The Windows desktop app launches a small always-on-top **Paperboy 9bit** live
tile alongside the main window. Click the tile to expand a compact model chat
surface backed by PMM:

- choose a local model (`qwen3-0.6b`, `qwen2.5-0.5b`, or `phi-3.5-mini`)
- send a natural-language prompt through `foundry model run`
- view the real model response in the widget
- ask the widget to propose the next offline pipeline step
- inspect local orchestration endpoints from `dayourbot` or `dlm-lib` when those
  repos are present on the machine

The widget is intentionally a live tile: small by default, expandable on demand,
and model-backed rather than a static status badge.

### Testing

Use the existing component suites rather than `Simple.Tests.ps1`, which is only
an always-passing placeholder:

```powershell
Invoke-Pester -Path .\Skills\Test-PaperboyBundle.Tests.ps1, .\Skills\Test-PaperboyExtensions.Tests.ps1 -Output Detailed
.\apps\Yumlog.Native\Test-YumlogNativeExtensibility.ps1
.\launchers\Test-YumlogNativeMdkgRoundTrip.ps1
.\launchers\Test-YumlogNativeHandoff.ps1
.\launchers\Test-YumlogAPIMRoundTrip.ps1
dotnet run --project .\apps\Yumlog.WidgetStudio\Yumlog.WidgetStudio.csproj -c Release -- self-test
```

The Paperboy suites use modern Pester syntax; see the component guide for the
supported invocation. The older `Test-ScreenSkills.Tests.ps1` uses Pester 3
syntax and mocked files, not actual screen capture. OCR and MSIX installation
are separate, machine-dependent checks; do not run installation scripts as
ordinary unit tests. APIM round-trip reports and `dist` output stay local.

CI builds the maintained desktop/MCP, APIM, and Widget Studio components and
runs their meaningful contract suites plus native extension/MDKG round trips.
It does not download FFmpeg, capture a desktop, provision OCR, sign packages,
or install a service. The always-passing/mock screen tests are not evidence of
capture readiness and are not release gates.

---

### Command Reference

#### Native CLI (`Yumlog.Native.Cli`)

| Command | Description |
|---|---|
| `capture` | Fast screenshot sequence to a directory |
| `record` | HEVC desktop video plus optional `.mdkg` metadata sidecar |
| `analyze` | Image metadata, hashes, dimensions, OCR |
| `follow` | Change-gated frame sequence plus follow manifest |
| `orchestrate` | Run a multi-step plan file |
| `runbook` | Emit a runbook whose every claim carries a resolvable citation |
| `reason` | Answer a question about a recording with an on-device model |
| `watch` | Observe the live screen and narrate on an interval |
| `models` | List installed on-device models |
| `selftest` | Run the 16-check built-in correctness suite |
| `config show` / `config init` | Inspect or scaffold native config |

Full flags in [`docs/NATIVE-CLI.md`](docs/NATIVE-CLI.md).

#### PowerShell tooling

| Command                | Description                                      |
|------------------------|--------------------------------------------------|
| `.\launchers\yumlog.ps1 start` | Record with optional `-Fps`, `-DurationSec`, and `-OutFile` |
| `.\launchers\yumlog.ps1 capture` | Capture with optional `-Fps`, `-DurationSec`, and `-OutDir` |
| `.\launchers\yumlog.ps1 pause` / `stop` | Placeholders; process control is not implemented |
| `.\launchers\yumlog.ps1 get` | Get the latest recording path |
| `.\launchers\yumlog.ps1 count` / `size` | Recording count / total size |
| `.\launchers\yumlog.ps1 config` | Print `config\tools.json` |
| Record-Terminals.ps1   | Record terminal windows and extract Paperboy frames |
| paperboy.ps1 pack      | Create a `.paperboy.zip` bundle with manifest    |
| paperboy.ps1 list      | Print a Paperboy bundle manifest                 |
| paperboy.ps1 unpack    | Expand a Paperboy bundle                         |
| paperboy.ps1 toss      | Copy a bundle to a destination                   |
| Paperboy.exe           | Native desktop UI for pack/list/unpack/toss workflows |

---

## Project Tree (excerpt)

```text
darbot.yumlog/
├── Skills/
│   ├── Capture-Screens.ps1
│   ├── Paperboy.Bundle.ps1
│   ├── Paperboy.ps1
│   ├── paperboy.js
│   ├── Record-Screen.ps1
│   └── Run-FFmpeg.ps1
├── launchers/
│   ├── install.ps1
│   ├── paperboy.ps1
│   ├── Record-Terminals.ps1
│   └── yumlog.ps1
├── apps/
│   ├── Paperboy.Desktop/
│   ├── Paperboy.Mcp/
│   ├── Yumlog.APIM.Service/
│   ├── Yumlog.WidgetStudio/
│   ├── Yumlog.Native/            # F# implementation library
│   │   ├── WgcCapture.fs         # Windows Graphics Capture
│   │   ├── NvencHevc.fs          # NVENC HEVC encoder
│   │   ├── X265Encoder.fs        # x265 software fallback
│   │   ├── HevcMux.fs            # MP4/HEVC muxer
│   │   ├── Mdkg.fs               # .mdkg layered metadata container
│   │   ├── MdkgIntel.fs          # timeline, citations, runbooks
│   │   ├── OnDeviceLlm.fs        # local model reasoning + watch
│   │   ├── SelfTest.fs           # built-in correctness suite
│   │   └── hevc-swarm-out/       # MDKG-SPEC.md, challenge reviews (see its README)
├── hevc-swarm-out/               # CDP harnesses + recording artifacts (see its README)
│   ├── Yumlog.Native.Cli/        # runnable CLI entry point
│   ├── Yumlog.NpuEmulator/       # Rust/Tauri NPU-emulation runtime
│   ├── Paperboy.Desktop/
│   └── Paperboy.Mcp/
├── docs/
│   ├── NATIVE-CLI.md
│   ├── ON-DEVICE-REASONING.md
│   └── NPU-EMULATOR-CONTAINER-SPEC.md
├── bisect-test/
├── config/
│   ├── tools.json
│   └── yumlog.native.json
├── .github/
│   └── copilot-instructions.md
├── yumlog-manager.html
└── ...
```

---

For more details, see `Tasklist.md` and inline comments in each script.

Local media under `archive\51PercentCrew` and generated integration reports under
`Scratchpad\apim-roundtrip` are not application source and are excluded from new
commits. Historical research notes under `hevc-swarm-out` describe design work;
the component guides distinguish implemented behavior from planned integration.


### Studio semantic Library

Yumlog Studio can index an entire recording into a searchable Library of atomic
flipcards. Open a video, configure FFmpeg and local Whisper in Settings, then use
**Insights → Run full decimation**. Studio reuses the latest attached timestamped
SRT/VTT transcript; otherwise it transcribes the audio locally. Plain text
transcripts can be viewed, but need timestamps for decimation.

**Library** supports modality filters, text search, frame previews, source-time
navigation, JSON/evidence flips, and JSON export. Each card records source and
evidence SHA-256 hashes, time ranges, and measured/inferred/unknown attributes.
Use **Attach document**, then run decimation again to include text, Markdown,
JSON, or CSV files up to 8 MB. Repeated runs deduplicate identical document
content within each extension. Document schema extraction covers the first JSON
object, CSV header, or Markdown headings and requires review before reuse.

The video cards retain scene-change frames, sampled color palettes, classic
Windows OCR text, and lexical tags. Audio cards group timestamped speech into
roughly 30-second segments with transcript-derived word counts, speaking rate,
and heuristic intent. Exact fonts, avatar or speaker identity, diagram structure,
icons, acoustic tone, calibrated confidence, signal quality, and perspective are
explicitly unknown. PDF/DOCX parsing and automatic speaker diarization are not
implemented by semantic decimation. The separate **Agent Voices** panel is
under the local processing pipeline in **Agents** and also appears in **Voices**.
Choose **Detect agent voices** to group speech from the selected recording's
audio track using local Voice Tools. Each group has a fictional alias, a
downloadable WAV of its source excerpts, and a flipcard with playable intervals
in the original video. Short groups can reflect overlap or model splits.
Script narration uses the installed Windows text-to-speech voices.

On Windows x64, install the pinned local speaker detector and models by running
`.\launchers\Install-YumlogVoiceTools.ps1` from this repository. Once installed,
speaker detection runs on the computer without a network connection.

The versioned `urn:yumlog:bitdot-layers:v1` output uses one-second occupancy bins
with bits for visual changes (1), speech (2), lexical tags (4), and document
schemas (8), compressed into contiguous runs. This is Yumlog's local sparse
index definition. Full-duration coverage describes processing and indexing;
object recognition remains limited to the extractors listed above. MDKG graph
snapshots track both card starts and ends over the recording's half-open time
interval. Its proof checks establish structural inclusion and integrity.

**Insights → Import verified analysis** accepts a `source-sha256.json` whose
`Hash` matches the current recording, with supported transcript and analysis
sidecars beside it. The hash checks source identity; transcript alignment remains
caller-supplied. Whisper cue endpoints that exceed duration by at most 250 ms
are clamped in the index only; the original transcript bytes are retained.

**Harvest** exports a Paperboy card or ZIP bundle containing the original and
derived files. When moving a bundle, resolve embedded original paths through
`paperboy-manifest.json`'s `sourcePath` → `bundlePath` mapping and verify its
SHA-256 hashes. Settings and recording artifacts persist between launches.

Run the real-media integration suite with an explicit short speech clip:

```powershell
.\launchers\Test-YumlogStudio.ps1 -InputVideo 'C:\recordings\speech.mp4'
```

The runner expects the repository-local FFmpeg and Whisper tools/model under
`.tools`, builds the native app, and retains its JSON test report in `dist`.
It exercises settings, transcription, capability loading, narration, documents,
Library, repeated decimation, MDKG timing, exports, and bundle integrity.
