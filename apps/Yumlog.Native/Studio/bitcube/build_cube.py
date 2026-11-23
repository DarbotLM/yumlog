#!/usr/bin/env python3
"""Build a library_r3 Inverse-HDR bitcube for one WAV.

Reuses gen-audio (cube_layers, identity.mint) when importable so the cube uid,
revision, covered range and four layers match the gen-audio schema. Falls back
to a vendored port of the library_r3 formulas and an RFC 8785-style identity
mint when gen-audio is not on PYTHONPATH.
"""
from __future__ import annotations

import argparse, base64, hashlib, json, math, sys, wave
from pathlib import Path

LAYER_NAMES = ("signal", "tonality", "confidence", "quality")
LAYER_COLORS = ((0.20, 0.45, 0.95), (0.15, 0.75, 0.40), (0.95, 0.55, 0.10), (0.95, 0.40, 0.75))
LEGEND = {"signal": "blue", "tonality": "green", "confidence": "orange", "quality": "pink"}
LIBRARY_REVISION, LAYER_METHOD = 3, "library_r3"
N_FFT, HOP, MAX_F, MAX_T, THRESH, PER_LAYER = 1024, 256, 128, 400, 0.12, 900

def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()

def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()

def normalized_sha256(data: bytes) -> str:
    return sha256_bytes(data.replace(b"\r\n", b"\n"))

def round_half_up(value: float) -> int:
    if not math.isfinite(value) or value < 0:
        raise ValueError(f"bad rounding input: {value!r}")
    return int(math.floor(value + 0.5))

def read_wav_mono(path: Path):
    import numpy as np
    with wave.open(str(path), "rb") as handle:
        channels, width, rate, frames = handle.getnchannels(), handle.getsampwidth(), handle.getframerate(), handle.getnframes()
        raw = handle.readframes(frames)
    if width == 2:
        data = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0
    elif width == 4:
        data = np.frombuffer(raw, dtype="<i4").astype(np.float64) / 2147483648.0
    elif width == 1:
        data = (np.frombuffer(raw, dtype="u1").astype(np.float64) - 128.0) / 128.0
    else:
        raise ValueError(f"unsupported WAV sample width: {width}")
    if channels > 1:
        data = data.reshape(-1, channels).mean(axis=1)
    return data, int(rate)

def stft_mag(y, n_fft=N_FFT, hop=HOP):
    import numpy as np
    pad = n_fft // 2
    padded = np.pad(y, pad, mode="reflect")
    n = n_fft
    window = 0.5 - 0.5 * np.cos(2.0 * np.pi * np.arange(n, dtype=np.float64) / max(n - 1, 1)) if n > 1 else np.ones(n)
    frames = 1 + max(0, (len(padded) - n_fft) // hop)
    mag = np.empty((n_fft // 2 + 1, frames), dtype=np.float64)
    for i in range(frames):
        start = i * hop
        mag[:, i] = np.abs(np.fft.rfft(padded[start:start + n_fft] * window, n=n_fft))
    return mag

def compute_layers(mag, sample_rate: int, n_fft: int) -> dict:
    import numpy as np
    eps = 1e-12
    logm = np.log10(mag + eps)
    lo, hi = np.percentile(logm, 5), np.percentile(logm, 99)
    signal = np.power(np.clip((logm - lo) / max(hi - lo, eps), 0, 1), 0.65)
    geo = np.exp(np.mean(np.log(mag + eps), axis=0))
    arith = np.mean(mag, axis=0)
    tonality_t = 1.0 - np.clip(geo / (arith + eps), 0, 1)
    padded = np.pad(mag, ((1, 1), (0, 0)), mode="edge")
    smooth = 0.15 * padded[:-2] + 0.7 * padded[1:-1] + 0.15 * padded[2:]
    peakiness = np.clip((np.clip(mag / (smooth + eps), 0, 1) - 1.0) / 3.0 + 0.5, 0, 1)
    tonality = np.clip(0.55 * tonality_t[None, :] + 0.45 * peakiness, 0, 1) * (signal > 0.08).astype(np.float64)
    noise_floor = np.percentile(mag, 20, axis=0, keepdims=True) + eps
    confidence = np.clip(np.log1p(mag / noise_floor) / np.log1p(40.0), 0, 1) * (signal > 0.05).astype(np.float64)
    freqs = np.linspace(0, sample_rate / 2.0, mag.shape[0])
    speech = ((freqs >= 300) & (freqs <= 3400)).astype(np.float64)[:, None]
    formant = speech * signal
    clarity = np.clip(confidence * signal, 0, 1)
    mud = np.clip(1.0 - signal, 0, 1) * (freqs <= 250)[:, None]
    q_t = np.clip(0.5 * formant.mean(axis=0) + 0.4 * clarity.mean(axis=0) - 0.2 * mud.mean(axis=0), 0, 1)
    quality = np.clip(0.5 * q_t[None, :] + 0.5 * np.clip(speech + 0.25, 0, 1) * signal, 0, 1)
    return {name: arr.astype(np.float32) for name, arr in (("signal", signal), ("tonality", tonality), ("confidence", confidence), ("quality", quality))}

def downsample_cube(layers: dict, max_f: int, max_t: int):
    f, t = next(iter(layers.values())).shape
    sf, st = max(1, math.ceil(f / max_f)), max(1, math.ceil(t / max_t))
    nf, nt = f // sf, t // st
    out = {k: v[: nf * sf, : nt * st].reshape(nf, sf, nt, st).mean(axis=(1, 3)) for k, v in layers.items()}
    return out, int(sf), int(st)

def layers_to_points(layers: dict, thresh: float = THRESH):
    import numpy as np
    buckets = []
    for li, name in enumerate(LAYER_NAMES):
        mat = layers[name]
        fy, tx = np.nonzero(mat >= thresh)
        if fy.size == 0:
            continue
        v = mat[fy, tx]
        rgba = np.zeros((fy.size, 4), dtype=np.float32)
        rgba[:, :3] = LAYER_COLORS[li]
        rgba[:, 3] = np.clip(0.25 + 0.75 * v, 0, 1)
        buckets.append((tx.astype(np.float32), fy.astype(np.float32), np.full(fy.shape, li / 3.0, dtype=np.float32), rgba, v.astype(np.float32), np.full(fy.shape, li, dtype=np.int16)))
    if not buckets:
        z = np.zeros((0,), dtype=np.float32)
        return z, z, z, np.zeros((0, 4), dtype=np.float32), z, np.zeros((0,), dtype=np.int16)
    return tuple(np.concatenate(items) for items in zip(*buckets))

def preview_points(cloud, shape_f_t, per_layer: int = PER_LAYER):
    import numpy as np
    x, y, z, rgba, vals, lids = cloud
    nf, nt = shape_f_t
    points, counts = [], {}
    for li, name in enumerate(LAYER_NAMES):
        mask = lids == li
        counts[name] = int(mask.sum())
        if not np.any(mask):
            continue
        vv = vals[mask]
        order = np.argsort(-vv, kind="stable")[:per_layer]
        xx, yy, zz, cc, vm = x[mask][order], y[mask][order], z[mask][order], rgba[mask][order], vv[order]
        for i in range(len(order)):
            points.append({"t": float(xx[i] / max(nt - 1, 1)), "f": float(yy[i] / max(nf - 1, 1)), "z": float(zz[i]), "v": float(vm[i]), "r": float(cc[i, 0]), "g": float(cc[i, 1]), "b": float(cc[i, 2]), "a": float(cc[i, 3]), "layer": name})
    return points, counts

def layer_score(layers: dict) -> float:
    return float(0.35 * layers["signal"].mean() + 0.25 * layers["tonality"].mean() + 0.20 * layers["confidence"].mean() + 0.20 * layers["quality"].mean())

def measure_inv_hdr(audio) -> float:
    import numpy as np
    peak = float(np.max(np.abs(audio)))
    if peak <= 0:
        return 0.0
    return float(np.sqrt(np.mean(np.square(audio))) / peak)
def vendored_library_cube(audio, sample_rate, *, stem, engine, source_sha256, label, generator_sha256):
    import numpy as np
    y = np.asarray(audio, dtype=np.float64)
    mag = stft_mag(y)
    full = compute_layers(mag, sample_rate, N_FFT)
    layers, sf, st = downsample_cube(full, MAX_F, MAX_T)
    cloud = layers_to_points(layers)
    nf, nt = next(iter(layers.values())).shape
    bin_s = st * HOP / sample_rate
    points, counts = preview_points(cloud, (nf, nt))
    stats = {}
    for name in LAYER_NAMES:
        mat = full[name]
        stats[name] = {"mean": float(mat.mean()), "std": float(mat.std()), "p50": float(np.percentile(mat, 50)), "p90": float(np.percentile(mat, 90)), "active_frac": float((mat > 0.2).mean()), "shape": [int(nf), int(nt)]}
    doc = {
        "source_wav": f"artifacts/library/{stem}.wav",
        "source_sha256": source_sha256,
        "engine": engine,
        "title": f"Inverse-HDR cube — {label or stem}",
        "cube_revision": LIBRARY_REVISION,
        "sample_rate": int(sample_rate),
        "duration_s": float(len(y) / sample_rate),
        "n_fft": N_FFT,
        "hop": HOP,
        "inv_hdr": measure_inv_hdr(y),
        "layer_score": layer_score(full),
        "n_points": len(points),
        "n_points_source": int(len(cloud[0])),
        "n_points_source_per_layer": counts,
        "cube_shape_f_t": [int(nf), int(nt)],
        "downsample_sf_st": [int(sf), int(st)],
        "bin_seconds": bin_s,
        "cube_covers_s": nt * bin_s,
        "stft_frames": int(mag.shape[1]),
        "axes": {"x": "time bin", "y": "freq bin", "z": "layer (+value)"},
        "layers": stats,
        "points_preview": points,
        "pngUrl": f"/library/{stem}_cube3d.png",
        "wavUrl": f"/library/{stem}.wav",
        "legend": dict(LEGEND),
        "provenance": {"generator": "apps/Yumlog.Native/Studio/bitcube/build_cube.py", "generator_sha256": generator_sha256, "layer_method": LAYER_METHOD, "params": {"n_fft": N_FFT, "hop": HOP, "max_f": MAX_F, "max_t": MAX_T, "thresh": THRESH, "per_layer": PER_LAYER}},
    }
    return doc, cloud

def jcs(value) -> str:
    def dump(item) -> str:
        if item is None:
            raise ValueError("null is not allowed in identity")
        if isinstance(item, bool):
            return "true" if item else "false"
        if isinstance(item, int) and not isinstance(item, bool):
            return str(item)
        if isinstance(item, float):
            raise ValueError("float_in_identity")
        if isinstance(item, str):
            return json.dumps(item, ensure_ascii=False)
        if isinstance(item, list):
            return "[" + ",".join(dump(v) for v in item) + "]"
        if isinstance(item, dict):
            keys = sorted(item.keys())
            return "{" + ",".join(json.dumps(k, ensure_ascii=False) + ":" + dump(item[k]) for k in keys) + "}"
        raise ValueError(f"unsupported identity type: {type(item)!r}")
    return dump(value)

def mint(kind: str, fields: dict, media: list, src: list) -> str:
    try:
        from gen_audio.identity import mint as ga_mint
        return ga_mint(kind, fields, media, src)
    except Exception:
        identity = {"fields": fields, "kind": kind, "media": sorted(media, key=lambda item: (item["role"], item["sha256"])), "schema_major": 1, "src": sorted(src)}
        digest = hashlib.sha256(b"ga-asset-v1\x00" + jcs(identity).encode("utf-8")).digest()
        return f"ga:{kind}:{base64.b32encode(digest[:16]).decode('ascii').rstrip('=').lower()}"

def cube_fields(doc: dict, generator_sha256: str) -> dict:
    hop = int(doc.get("hop") or HOP)
    down = doc.get("downsample_sf_st") or [1, 1]
    return {
        "source_sha256": doc["source_sha256"],
        "sample_rate_hz": int(doc["sample_rate"]),
        "bin_frames": int(down[1]) * hop,
        "time_bins": int(doc["cube_shape_f_t"][1]),
        "freq_bins": int(doc["cube_shape_f_t"][0]),
        "duration_ms": round_half_up(float(doc["duration_s"]) * 1000.0),
        "covers_ms": round_half_up(float(doc["cube_covers_s"]) * 1000.0),
        "inv_hdr_ppm": round_half_up(float(doc["inv_hdr"]) * 1_000_000.0),
        "cube_revision": int(doc["cube_revision"]),
        "n_points": int(doc["n_points"]),
        "n_fft": int(doc.get("n_fft") or N_FFT),
        "hop_frames": hop,
        "generator_sha256": generator_sha256,
        "layer_method": (doc.get("provenance") or {}).get("layer_method") or LAYER_METHOD,
    }

def try_gen_audio_cube(audio, sample_rate, *, stem, engine, source_sha256, label):
    try:
        from gen_audio.cube_layers import library_cube
        return library_cube(audio, sample_rate, stem=stem, engine=engine, source_sha256=source_sha256, label=label)
    except Exception as exc:
        print(f"gen-audio library_cube unavailable, using vendored formulas: {exc}", file=sys.stderr)
        return None

def maybe_png(path: Path, cloud, title: str) -> bool:
    if cloud is None or path is None:
        return False
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        import numpy as np
        x, y, z, rgba, _vals, _lids = cloud
        n = len(x)
        if n > 80000:
            idx = np.random.default_rng(42).choice(n, size=80000, replace=False)
            x, y, z, rgba = x[idx], y[idx], z[idx], rgba[idx]
        fig = plt.figure(figsize=(7.2, 5.6), dpi=110)
        ax = fig.add_subplot(111, projection="3d")
        ax.scatter(x, y, z, c=rgba, s=2, linewidths=0)
        ax.set_xlabel("time bin"); ax.set_ylabel("freq bin"); ax.set_zlabel("layer (+ value)")
        ax.set_zticks([0, 1 / 3, 2 / 3, 1]); ax.set_zticklabels(list(LAYER_NAMES)); ax.set_title(title)
        fig.tight_layout(); path.parent.mkdir(parents=True, exist_ok=True); fig.savefig(path); plt.close(fig)
        return path.is_file()
    except Exception as exc:
        print(f"cube png skipped: {exc}", file=sys.stderr)
        return False

def envelope(doc, cube_json: Path, png, wav: Path, generator_sha256: str) -> dict:
    fields = cube_fields(doc, generator_sha256)
    media = [{"role": "cube_json", "sha256": sha256_file(cube_json)}]
    if png is not None and Path(png).is_file():
        media.append({"role": "cube_png", "sha256": sha256_file(Path(png))})
    wav_fields = {"engine": doc.get("engine") or "yumlog", "sample_rate_hz": int(doc["sample_rate"]), "duration_ms": fields["duration_ms"]}
    wav_media = [{"role": "wav", "sha256": sha256_file(wav)}]
    clip_uid = mint("audio_clip", wav_fields, wav_media, [])
    uid = mint("cube_ihdr", fields, media, [clip_uid])
    layer_assets = []
    for index, name in enumerate(LAYER_NAMES):
        stats = (doc.get("layers") or {}).get(name) or {}
        layer_fields = {"name": name, "index": index}
        layer_media = [{"role": "layer_stats", "sha256": sha256_bytes(json.dumps(stats, sort_keys=True).encode("utf-8"))}]
        layer_uid = mint("layer", layer_fields, layer_media, [uid])
        layer_assets.append({"uid_scheme": "ga1", "kind": "layer", "uid": layer_uid, "fields": layer_fields, "body": {"mean": stats.get("mean"), "std": stats.get("std"), "p50": stats.get("p50"), "p90": stats.get("p90"), "active_frac": stats.get("active_frac"), "shape": stats.get("shape")}, "src": [uid]})
    body = {"inv_hdr": doc.get("inv_hdr"), "layer_score": doc.get("layer_score"), "duration_s": doc.get("duration_s"), "bin_seconds": doc.get("bin_seconds"), "cube_covers_s": doc.get("cube_covers_s"), "cube_shape_f_t": doc.get("cube_shape_f_t"), "json_url": cube_json.name}
    if png is not None and Path(png).is_file():
        body["png_url"] = Path(png).name
    return {"uid_scheme": "ga1", "schema": "https://github.com/darbotlabs/gen-audio/schemas/asset-object.schema.json", "schema_version": "1.0.0", "kind": "cube_ihdr", "uid": uid, "fields": fields, "body": body, "media": media, "src": [clip_uid], "layers": layer_assets, "cube": doc, "audio_clip": {"uid": clip_uid, "fields": wav_fields, "media": wav_media, "path": wav.name}}

def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Build a library_r3 Inverse-HDR bitcube for one WAV.")
    parser.add_argument("wav", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--cube-json", type=Path)
    parser.add_argument("--png", type=Path)
    parser.add_argument("--stem", default="")
    parser.add_argument("--engine", default="yumlog")
    parser.add_argument("--label", default="")
    args = parser.parse_args(argv)
    wav = args.wav.resolve()
    if not wav.is_file():
        print(f"error: wav not found: {wav}", file=sys.stderr)
        return 1
    source_sha256 = sha256_file(wav)
    stem = args.stem or wav.stem
    generator_sha256 = normalized_sha256(Path(__file__).read_bytes())
    audio, sample_rate = read_wav_mono(wav)
    imported = try_gen_audio_cube(audio, sample_rate, stem=stem, engine=args.engine, source_sha256=source_sha256, label=args.label or stem)
    if imported is not None:
        doc, cloud = imported
        method = "gen_audio.cube_layers"
    else:
        doc, cloud = vendored_library_cube(audio, sample_rate, stem=stem, engine=args.engine, source_sha256=source_sha256, label=args.label or stem, generator_sha256=generator_sha256)
        method = "vendored_library_r3"
    cube_json = args.cube_json or args.out.with_name(args.out.stem + ".cube.json")
    cube_json.parent.mkdir(parents=True, exist_ok=True)
    cube_json.write_text(json.dumps(doc, indent=2), encoding="utf-8")
    png_written = maybe_png(args.png, cloud, doc.get("title") or stem) if args.png else False
    asset = envelope(doc, cube_json, args.png if png_written else None, wav, (doc.get("provenance") or {}).get("generator_sha256") or generator_sha256)
    asset["builder"] = method
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(asset, indent=2), encoding="utf-8")
    print(json.dumps({"ok": True, "uid": asset["uid"], "out": str(args.out), "cubeJson": str(cube_json), "png": str(args.png) if png_written else "", "builder": method, "covers_ms": asset["fields"]["covers_ms"], "cube_revision": asset["fields"]["cube_revision"]}))
    return 0

if __name__ == "__main__":
    sys.exit(main())
