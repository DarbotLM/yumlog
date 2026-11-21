# Yumlog review tools

Optional helpers for reviewing rendered videos with Yumlog's native MDKG and runbook pipeline. They are not part of the shipped package and never bundle any media.

Pipeline for a review folder that holds `yumlog-native-results.json` (films with sha256 and optional captions) and sampled frames with SHA-256 receipts:

1. `read-frames-winrt.ps1 -Manifest <frames-manifest.json> -Output <folder>\winrt-ocr-results.json` runs classic `Windows.Media.Ocr` over hash-verified frames.
2. `dotnet run --project MdkgImport -- <folder>` writes `<film>-evidence.mdkg` with the native Yumlog MDKG writer (OCR text plus authored caption cues).
3. `python make_runbooks.py <folder>` runs `Yumlog.Native.Cli runbook` on each `*-evidence.mdkg`.
4. `dotnet run --project MdkgImport -- <folder> verify` re-resolves every runbook proof against its MDKG.

The tools find the Yumlog CLI through `YUMLOG_CLI_ROOT`, or through the installed release recorded in `%LOCALAPPDATA%\DarbotLabs\Yumlog\current.txt`.
