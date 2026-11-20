---
title: Shared media presets
description: Consistent duration, exact frame-rate, resolution and audio targets across a2swe, Gen-Audio and Yumlog.
---

This shared guide uses a2swe-relative paths in its first command block; Gen-Audio and Yumlog commands appear below.

The authoritative catalog in a2swe is `library/media-presets.json`. Identical copies live in Gen-Audio's Python package and desktop source, and in Yumlog's `config/media-presets.json`.

Durations: **5s, 15s, 30s, 1m, 2m, 3m, 5m, 10m, 15m, 30m, 45m, 60m, 90m, 120m**. The first two are labeled “5 second clip” and “15 second statement”.

Frame rates: **23.976, 24, 25, 29.97, 30, 50, 59.94, 60, 120**. Fractional rates use exact rational values: 24000/1001, 30000/1001 and 60000/1001.

Resolutions: **480p, 720p, 1080p, 1440p, 2160p**, portrait 720×1280 and 1080×1920, and square 1080×1080. Exports fit and pad; they do not silently crop.

Audio targets: 16 kHz mono, 24 kHz mono, 44.1 kHz stereo, 48 kHz stereo and 96 kHz stereo. These describe delivery samples, not the native inference rate of a speech model.

```powershell
node packages/core/src/cli.ts media-presets
node packages/core/src/cli.ts media-plan --duration 2m --frame-rate 29.97 --resolution 720p
pwsh scripts/export-media-preset.ps1 -InputFile movie.mp4 -OutputFile clip.mp4 -Duration 5s -FrameRate 29.97 -Resolution 720p
pwsh scripts/sync-media-presets.ps1 -GenAudioRoot G:/gen-audio -YumlogRoot G:/Yumlog
```

The exporter requires a new MP4 destination and enough source duration. It writes a receipt containing source/output hashes, exact target settings, actual FFprobe measurements and a full-decode result. `-PlanOnly` resolves and validates without encoding. Export is an explicit clip operation; speech synthesis never silently stretches, truncates or pads speech to meet a target.

| Adapter | Applied settings | Boundary |
| --- | --- | --- |
| a2swe project form | All 14 duration targets; custom 5–7200 seconds | Targets do not guarantee measured speech length |
| a2swe native worker | Theme-bound 1080p/30 production | Other delivery geometries/rational rates use explicit export |
| Gen-Audio desktop | Same duration picker | Duration is a request, not evidence of synthesized audio |
| Gen-Audio `synth` | `--duration-preset`; actual duration and drift in sidecar | Does not alter speech to force the duration |
| Yumlog native CLI | `--duration-preset`, integer `--frame-rate-preset` | Captures monitor geometry; fractional delivery rates use export |
| Shared exporter | All catalog durations/rates/resolutions/audio targets | Source must contain the requested time window |

Gen-Audio exposes `python -m gen_audio.media_presets --duration 120m --frame-rate 59.94`. Yumlog exposes `pwsh launchers/media-presets.ps1 -Duration 120m -FrameRate 29.97`. Both repositories include the same export script. The sync command checks by default; `-Write` explicitly updates catalog copies.

Long-duration configurations are supported as targets; the Ignite acceptance run does not certify every two-hour, 4K/120 combination on every machine.
