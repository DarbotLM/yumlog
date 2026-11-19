# Yumlog 0.5.0

## Highlights

- Split native desktop and console hosts with preserved HEVC, MDKG, runbook, reasoning, NPU, handoff, and extension surfaces.
- Native Agent Card Studio with the requested bottom modes: Video, Screenshot, Audio, Transcript, Agents, Voices, Insights, Harvest, and Settings.
- Full Paperboy video Agent Card schema with 4:3 adaptive flip-card shells and SHA-256/MDKG provenance.
- Paperboy APIM UI connected to the real loopback service and an in-app A2SWE project generator.
- Strict CLI option validation, `WarningsAsErrors`, fail-fast Pester coverage, and a warning-clean self-contained production publish.
- Versioned production build/install launchers and Start Menu shortcuts.

## Production build

```powershell
.\launchers\Build-YumlogProduction.ps1 -DotnetPath dotnet
.\launchers\Install-YumlogProduction.ps1
```

## Production acceptance

The release package contains self-contained `win-x64` binaries for the Native
desktop, Native CLI, Paperboy, Paperboy MCP, APIM service, and Widget Studio.
Native MDKG/self-test, Pester, APIM round-trip, Widget Studio, and MCP contracts
must pass. OCR model provisioning and actual screen capture remain explicit
machine-dependent actions.
