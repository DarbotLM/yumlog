"""Stub of gen_audio.cli.render_speech: same arguments and receipt, a synthetic tone instead of speech."""

import argparse
import hashlib
import json
import math
import os
import struct
import sys
import wave
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", required=True)
    parser.add_argument("--voice", required=True)
    parser.add_argument("--language", default="en-us")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    text = sys.stdin.buffer.read().decode("utf-8-sig").strip()
    mode = os.environ.get("YUMLOG_GEN_AUDIO_STUB", "")
    if mode == "fail":
        print("stub engine refused: no weights for " + args.engine, file=sys.stderr)
        return 1
    if not text:
        print("stdin must contain a nonempty utterance", file=sys.stderr)
        return 2
    rate = 24000
    count = rate // 2
    with wave.open(str(args.output), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(rate)
        audio.writeframes(b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * 220 * i / rate))) for i in range(count)))
    args.output.with_name(args.output.name + ".script.txt").write_bytes(text.encode("utf-8"))
    checksum = hashlib.sha256(args.output.read_bytes()).hexdigest()
    if mode == "badhash":
        checksum = "0" * 64
    print("stub: rendering complete")
    print(json.dumps({"engine": args.engine, "voice": args.voice, "sampleRate": rate,
                      "durationSeconds": count / rate, "audioSha256": checksum}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
