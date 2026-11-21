# Studio smoke harness

`launchers/Test-YumlogStudioSmoke.ps1` builds this host, renders a synthetic 6-second fixture with FFmpeg, and then runs two checks:

- **Native** (`StudioSmoke native <workspace> <video>`): an empty library, idempotent import, FFprobe metadata and poster, clip, still frame, scene export, waveform, spectrogram, embedded captions, capability catalog, Windows voices, OCR, MDKG runbook (when timed metadata exists), card size persistence, card export, and the Paperboy bundle contents. It then reopens the workspace and checks that state is restored.
- **UI** (`StudioSmoke ui <workspace>` plus `check-ui.mjs`): hosts the real Studio WebView2 with a local DevTools port, then uses Playwright to check the nine tabs, video decode and playback, keyboard navigation, the Paperboy flip card, size modes, and the adaptive narrow layout. Add `-SkipUi` to run only the native check.

Outputs go to `dist\StudioSmoke-*` by default and are never committed.
