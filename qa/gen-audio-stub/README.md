# gen-audio boundary stub

Stdlib-only stand-ins for `gen_audio.cli.render_speech` and `gen_audio.cli.transcribe_vibevoice` with the same arguments and JSON receipts as darbotlabs/gen-audio. `launchers/Test-YumlogGenAudio.ps1` points Yumlog at this folder through `--gen-audio-root`, so the gate checks the boundary without models. `YUMLOG_GEN_AUDIO_STUB=fail` or `badhash` makes the stub fail or return a wrong hash. Real gen-audio is used with `-LiveRoot`.
