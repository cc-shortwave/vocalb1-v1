"""Offline reproduction + parameter audit for the VocalB1 synthesis pipeline.

Usage:
    python tools/probe_render.py <project.vbproj.json> [--wav path.wav]

It renders the first track of a project with the same bridge mapping the UI uses
(ui/bridge/vocal_bridge._build_kwargs), keeps the intermediate frame dump
(_output/spliced_frames.txt), then aggregates every time-varying parameter
(F0, F1..F7, AP, GI, band energy) around the anomaly frames and writes
_output/probe/param_report.tsv.
"""
import argparse
import importlib.util
import json
import os
import sys

import numpy as np
import scipy.signal

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, REPO_ROOT)


def _load_bridge():
    path = os.path.join(REPO_ROOT, "ui", "bridge", "vocal_bridge.py")
    spec = importlib.util.spec_from_file_location("vocal_bridge", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _load_project(path):
    with open(path, "r", encoding="utf-8") as fh:
        project = json.load(fh)
    tracks = project.get("tracks") or []
    if not tracks:
        raise SystemExit("project has no tracks")
    track = tracks[0]
    return {
        "bpm": project.get("bpm", 120),
        "beatsPerBar": project.get("beatsPerBar", 4),
        "beatUnit": project.get("beatUnit", 4),
        "segments": track.get("segments") or [],
        "pitchLine": track.get("pitchLine") or [],
        "settings": track.get("settings") or {},
    }


def _read_frames(path):
    with open(path, "r", encoding="utf-8") as fh:
        lines = fh.readlines()
    header = None
    for line in lines:
        if line.startswith("# ") and "frame" in line:
            header = line[2:].split()
    data = np.loadtxt([l for l in lines if not l.startswith("#")], delimiter="\t")
    if header is None:
        raise SystemExit("frame dump has no header")
    return {name: data[:, i] for i, name in enumerate(header)}, header


def _formants(sp_db, freqs, n=7):
    """Pick the n strongest spectral peaks (formants) per frame below 5.5 kHz."""
    peaks, props = scipy.signal.find_peaks(
        sp_db, prominence=3.0, distance=max(1, int(60 / (freqs[1] - freqs[0])))
    )
    order = np.argsort(props["prominences"])[::-1]
    keep = np.sort(peaks[order[:n]])
    out = np.full(n, np.nan)
    if keep.size:
        out[:keep.size] = freqs[keep[:n]]
    return out


def _frame_rms(samples, sr, fp, n_frames):
    hop = sr * fp / 1000.0
    rms = np.zeros(n_frames)
    for i in range(n_frames):
        a = int(round(i * hop))
        b = int(round((i + 1) * hop))
        seg = samples[a:b]
        if seg.size:
            rms[i] = float(np.sqrt(np.mean(seg ** 2)))
    return rms


def _click_events(samples, sr, fp, thresh=0.2, ratio_thresh=6.0, win=40):
    """Sample-to-sample steps that stand out from the local slope (A/B ratio to median)."""
    import scipy.ndimage
    d = np.abs(np.diff(samples))
    med = scipy.ndimage.median_filter(d, size=2 * win + 1, mode="nearest")
    score = d / np.maximum(med, 1e-6)
    idx = np.flatnonzero((d >= thresh) & (score >= ratio_thresh))
    events = [(int(i), float(d[i]), float(score[i])) for i in idx]
    return events


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("project")
    parser.add_argument("--wav", default=None)
    parser.add_argument("--out", default=os.path.join(REPO_ROOT, "_output", "probe"))
    parser.add_argument("--reuse", action="store_true", help="reuse an existing render instead of synthesizing again")
    args = parser.parse_args()

    os.makedirs(args.out, exist_ok=True)
    bridge = _load_bridge()
    project = _load_project(args.project)
    segments, kwargs = bridge._build_kwargs(project)
    kwargs["verbose"] = True
    kwargs["clean_intermediate"] = False

    wav_path = args.wav or os.path.join(args.out, "repro.wav")
    os.makedirs(os.path.dirname(wav_path), exist_ok=True)

    import VocalB1Synthesizer as vs
    if not (args.reuse and os.path.exists(wav_path)):
        print(f"[Repro] {len(segments)} segments, bpm={kwargs.get('bpm')}, -> {wav_path}")
        vs.synthesize(segments, wav_path, **kwargs)
    else:
        print(f"[Repro] reusing {wav_path}")

    import soundfile as sf
    samples, sr = sf.read(wav_path, dtype="float64")
    if samples.ndim > 1:
        samples = samples[:, 0]
    fp = float(kwargs.get("frame_period", 5.0))

    frames_path = os.path.join(os.path.dirname(wav_path), "spliced_frames.txt")
    frames, header = _read_frames(frames_path)
    n = len(frames["f0"])
    rms = _frame_rms(samples, sr, fp, n)
    rms_db = 20 * np.log10(np.maximum(rms, 1e-12))

    sp_keys = [k for k in header if k.startswith("sp")]
    ap_keys = [k for k in header if k.startswith("ap")]
    sp = np.column_stack([frames[k] for k in sp_keys])
    ap = np.column_stack([frames[k] for k in ap_keys])
    freqs = np.arange(sp.shape[1]) * (sr / 2.0) / (sp.shape[1] - 1)
    sp_db = 10 * np.log10(np.maximum(sp, 1e-12))
    band = freqs <= 5500

    fmt = np.array([_formants(sp_db[i, band], freqs[band]) for i in range(n)])
    f0 = frames["f0"]
    gi = frames.get("gi", np.full(n, np.nan))
    ap_mean = ap.mean(axis=1)
    ap_peak = ap.max(axis=1)
    sp_max_db = sp_db.max(axis=1)

    flags = {i: [] for i in range(n)}
    for name, arr in (("f0", f0), ("gi", gi), ("ap", ap_mean), ("sp", sp_max_db), ("rms", rms_db)):
        bad = ~np.isfinite(arr)
        for i in np.flatnonzero(bad):
            flags[i].append(f"{name}:non-finite")
    for i in range(1, n):
        if np.isfinite(f0[i]) and np.isfinite(f0[i - 1]) and f0[i - 1] > 0 and f0[i] > 0:
            if abs(f0[i] - f0[i - 1]) > max(10.0, 0.15 * f0[i - 1]):
                flags[i].append(f"f0:{f0[i - 1]:.1f}->{f0[i]:.1f}Hz")
        if abs(rms_db[i] - rms_db[i - 1]) > 12.0:
            flags[i].append(f"rms:{rms_db[i - 1]:.1f}->{rms_db[i]:.1f}dB")
        if abs(ap_mean[i] - ap_mean[i - 1]) > 0.25:
            flags[i].append(f"ap:{ap_mean[i - 1]:.2f}->{ap_mean[i]:.2f}")
        if np.isfinite(gi[i]) and np.isfinite(gi[i - 1]) and gi[i - 1] > 0 and gi[i] / max(gi[i - 1], 1e-9) > 3:
            flags[i].append(f"gi:{gi[i - 1]:.3f}->{gi[i]:.3f}")
        for k in range(fmt.shape[1]):
            a, b = fmt[i - 1, k], fmt[i, k]
            if np.isfinite(a) and np.isfinite(b) and abs(b - a) > 300:
                flags[i].append(f"F{k + 1}:{a:.0f}->{b:.0f}Hz")

    hop = sr * fp / 1000.0
    clicks = _click_events(samples, sr, fp)
    click_frames = {}
    for i, d, score in clicks:
        f = int(round(i / hop))
        click_frames.setdefault(f, []).append((i, d, score))
    print(f"[Repro] {n} frames ({n * fp / 1000:.3f}s); click candidates: {len(clicks)} "
          f"in {len(click_frames)} frames")
    top = sorted(clicks, key=lambda e: -e[1])[:10]
    for i, d, score in top:
        f = int(round(i / hop))
        print(f"   #{i}@{i / sr:.4f}s frame {f} step={d:.3f} ×{score:.1f} of local median")

    anomalous = sorted(i for i in flags if flags[i])
    print(f"[Repro] frames with parameter anomalies: {len(anomalous)}")
    rows = []
    touched = set()
    for i in anomalous:
        touched.update(range(max(0, i - 2), min(n, i + 3)))
    for f in click_frames:
        touched.update(range(max(0, f - 2), min(n, f + 3)))
    for i in sorted(touched):
        rows.append([i, i * fp / 1000.0, f0[i], gi[i], rms_db[i], sp_max_db[i], ap_mean[i], ap_peak[i]]
                    + list(fmt[i, :7]) + [";".join(flags[i])])

    out_path = os.path.join(args.out, "param_report.tsv")
    with open(out_path, "w", encoding="utf-8") as fh:
        fh.write("frame\ttime_s\tf0_hz\tgi\trms_db\tsp_max_db\tap_mean\tap_peak\t"
                 "F1\tF2\tF3\tF4\tF5\tF6\tF7\tflags\n")
        for r in rows:
            fh.write("\t".join(f"{v:.4g}" if isinstance(v, float) else str(v) for v in r) + "\n")
    print(f"[Repro] report: {out_path}  ({len(rows)} rows)")

    problematic = [i for i in anomalous if any(not f.startswith("rms") for f in flags[i])]
    print("\n[Summary] frames with parameter anomalies (excluding plain rms steps):")
    for i in problematic[:40]:
        print(f"  f{i} @{i * fp / 1000:.3f}s f0={f0[i]:.1f}Hz gi={gi[i]:.3f} "
              f"AP={ap_mean[i]:.2f}/{ap_peak[i]:.2f} F="
              + " ".join(f"{v:.0f}" if np.isfinite(v) else "-" for v in fmt[i, :7])
              + "  [" + ";".join(flags[i]) + "]")


if __name__ == "__main__":
    main()
