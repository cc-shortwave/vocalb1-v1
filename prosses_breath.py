"""
prosses_breath.py - breath processing script

Read splice information from the header of spliced_frames.txt (segment count is not hardcoded),
and fold each breath segment breath_seg{i}.wav into a breath track equal in length to the main signal
via per-seam crossfading.
"""
import os
import re

import numpy as np
import vocal_lib as vl

# Anchor paths to the script directory to avoid broken relative paths when the working directory differs at runtime
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
FRAMES_TXT = os.path.join(_BASE_DIR, "_output", "spliced_frames.txt")
BREATH_FMT = os.path.join(_BASE_DIR, "_output", "breath_seg{}.wav")
OUTPUT_PATH = os.path.join(_BASE_DIR, "_output", "breath_trim_concat.wav")


def read_seam_info(txt_path: str) -> dict:
    """Read key=value information from the comment header of the spliced-frames txt.

    Scalar values (frames/n_seg/sr/fp_ms) are parsed as int/float;
    comma-separated list values (seg_frames/over_frames) are parsed as list.
    """
    with open(txt_path, "r", encoding="utf-8") as fh:
        header = []
        for line in fh:
            if not line.startswith("#"):
                break              # stop at the first data line
            header.append(line)
    info = {}
    for key, val in re.findall(r"(\w+)=([0-9.,]+)", "".join(header)):
        parts = [p for p in val.split(",") if p != ""]
        if len(parts) > 1:
            info[key] = [int(p) if p.isdigit() else float(p) for p in parts]
        else:
            info[key] = int(val) if re.fullmatch(r"\d+", val) else float(val)
    return info


def load_breath_arrays(paths: list):
    """Read each breath wav segment into a list of np arrays (vocal_lib WAVAudio, 24-bit supported).

    Returns:
        (arrays, sr)  arrays is a list of 1-D float64 arrays ordered by segment index
    """
    arrays, sr = [], None
    for p in paths:
        a = vl.WAVAudio.from_file(p, mono=True)
        if sr is None:
            sr = a.sample_rate
        assert a.sample_rate == sr, f"Breath segments have inconsistent sample rates: {p}"
        arrays.append(np.asarray(a.data, dtype=np.float64))
    return arrays, sr


def _crossfade(x: np.ndarray, y: np.ndarray, ov_smp: int) -> np.ndarray:
    """Fold y into the tail of x using an equal-power cos/sin window (same algorithm as VocalB1._splice_breath).

    Rules: x is kept intact (pure leading segment + crossfade region), y is folded in starting at len(x) - ov;
    in the overlap region the x weight goes 1→0 and the y weight goes 0→1, producing a continuous output with no jumps.
    Output length = len(x) + len(y) - ov.
    """
    if ov_smp <= 0 or len(x) == 0 or len(y) == 0:
        return np.concatenate([x, y]) if len(y) else x
    ov = max(0, min(int(ov_smp), len(x), len(y)))
    if ov <= 0:
        return np.concatenate([x, y])
    out = np.zeros(len(x) + len(y) - ov, dtype=np.float64)
    s0 = len(x) - ov
    out[:s0] = x[:s0]
    t = np.linspace(0.0, np.pi / 2.0, ov, dtype=np.float64)
    out[s0:s0 + ov] = np.cos(t) * x[s0:] + np.sin(t) * y[:ov]
    if len(y) > ov:
        out[s0 + ov:] = y[ov:]
    return out


def build_concat_breath():
    """Read splice information and all breath segments, returning concatenated breath aligned with the frame-domain Bezier overlap regions of the main signal.

    Key point: segment i+1 of the main signal takes over from frame (accumulated frames of the first i segments - over_i)
    (overlap-add crossfading over over_i frames), so the breath must use the same time base —
    perform equal-power crossfading per seam instead of trimming the previous segment to the seam and hard-concatenating the next one
    (that would make the next segment late by over frames and exceed the main signal length, causing breath misalignment and missed plosive regions).

    Returns:
        (breath_out, sr)  breath_out length = n_out * fp * sr / 1000, aligned to the main signal length
    """
    seam = read_seam_info(FRAMES_TXT)
    for key in ("n_seg", "seg_frames", "over_frames"):
        if key not in seam:
            raise ValueError(f"Header is missing {key}: {FRAMES_TXT}")
    fp_ms = seam.get("fp_ms", 5.0)
    n_seg = int(seam["n_seg"])
    overs = [int(v) for v in seam["over_frames"]]
    if len(overs) != n_seg:
        raise ValueError(
            f"Header over_frames length {len(overs)} does not match n_seg={n_seg}: {FRAMES_TXT}")

    paths = [BREATH_FMT.format(i) for i in range(n_seg)]
    for p in paths:
        if not os.path.isfile(p):
            raise FileNotFoundError(f"Breath file not found: {p}")

    arrays, sr = load_breath_arrays(paths)

    out = arrays[0]
    for i in range(1, n_seg):
        # Number of overlap samples = over_i frames (exact floating-point frame period, consistent with the frame-domain overlap regions of the main signal)
        ov_smp = int(round(overs[i] * sr * fp_ms / 1000.0))
        out = _crossfade(out, arrays[i], ov_smp)
    return out, sr


if __name__ == "__main__":
    if not os.path.isfile(FRAMES_TXT):
        raise FileNotFoundError(
            f"Spliced frames file not found: {FRAMES_TXT}\nRun VocalB1.py first to generate it, then run this script.")
    seam = read_seam_info(FRAMES_TXT)
    print(f"[prosses_breath] n_seg={seam.get('n_seg')}  frames={seam.get('frames')}  "
          f"seg_frames={seam.get('seg_frames')}  over_frames={seam.get('over_frames')}  "
          f"fp_ms={seam.get('fp_ms')}")
    breath_out, sr = build_concat_breath()
    vl.WAVAudio(data=breath_out, sample_rate=sr).save(OUTPUT_PATH, bit_depth=24)
    print(f"[prosses_breath] Concatenated breath {len(breath_out)}smp "
          f"({len(breath_out) / sr:.3f}s) saved: {OUTPUT_PATH}")