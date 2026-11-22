"""Stub of gen_audio.cli.transcribe_vibevoice: same arguments and receipt shape as the VibeVoice ASR checkpoint."""

import argparse
import json
import sys
import wave
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audio", type=Path)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--context")
    parser.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    parser.add_argument("--max-new-tokens", type=int)
    parser.add_argument("--streaming", action="store_true")
    args = parser.parse_args()
    if not args.model.is_dir():
        parser.error("provide a local checkpoint directory")
    with wave.open(str(args.audio), "rb") as audio:
        seconds = audio.getnframes() / audio.getframerate()
    if seconds <= 0:
        print("audio file is empty", file=sys.stderr)
        return 2
    half = round(seconds / 2, 3)
    segments = [{"Start": 0.0, "End": half, "Speaker": 0, "Content": "Open the review workspace."},
                {"Start": half, "End": round(seconds, 3), "Speaker": 1, "Content": "Confirm step two."}]
    print(json.dumps({"text": "Open the review workspace. Confirm step two.", "segments": segments,
                      "token_limit_reached": False, "model": args.model.name, "device": args.device,
                      "audioSeconds": seconds, "loadSeconds": 0.0, "inferenceSeconds": 0.0}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
