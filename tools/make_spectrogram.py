"""Render spectrogram PNGs for a synthesized WAV (diagnostic helper).

Usage:
    python tools/make_spectrogram.py <wav> [--frames spliced_frames.txt] [--out out.png]

Panels: full view, seam zoom, onset zoom; f0 from the frame dump is overlaid
so the harmonic tracks can be matched against the f0 curve.
"""
import argparse
import os

import numpy as np
import soundfile as sf
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from scipy import signal


def read_f0(frames_path):
    if not frames_path or not os.path.isfile(frames_path):
        return None
    f0 = []
    with open(frames_path, encoding="utf-8") as fh:
        for line in fh:
            if line.startswith("#") or not line.strip():
                continue
            parts = line.split("\t")
            f0.append(float(parts[1]))
    return np.array(f0)


def stft_db(x, sr, nper=1024, hop=128):
    f, t, z = signal.stft(x, fs=sr, window="hann", nperseg=nper,
                          noverlap=nper - hop, boundary=None, padded=False)
    mag = np.abs(z)
    db = 20.0 * np.log10(np.maximum(mag, 1e-12))
    db -= db.max()
    return f, t, db


def draw(ax, f, t, db, sr, f0, fp_ms, vmin=-70, fmax=10000):
    ax.pcolormesh(t, f, db, shading="nearest", cmap="magma", vmin=vmin, vmax=0,
                  rasterized=True)
    if f0 is not None:
        tt = (np.arange(len(f0)) * fp_ms) / 1000.0
        m = f0 > 0
        ax.plot(tt[m], f0[m], color="black", lw=3.0, zorder=5)
        ax.plot(tt[m], f0[m], color="#00e5ff", lw=1.4, zorder=6, label="f0")
    ax.set_ylim(0, fmax)
    ax.set_facecolor("#111111")
    ax.tick_params(labelsize=8)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("wav")
    ap.add_argument("--frames", default=None)
    ap.add_argument("--out", default=None)
    ap.add_argument("--zoom", nargs=2, type=float, action="append",
                    metavar=("LO", "HI"), help="extra zoom panel, repeatable")
    ap.add_argument("--fmax", type=float, default=10000)
    a = ap.parse_args()

    x, sr = sf.read(a.wav, dtype="float64", always_2d=True)
    x = x[:, 0]
    frames = a.frames or os.path.join(os.path.dirname(a.wav), "spliced_frames.txt")
    f0 = read_f0(frames)
    fp_ms = 5.0

    f, t, db = stft_db(x, sr)

    if a.zoom:
        fig, axes = plt.subplots(len(a.zoom), 1, figsize=(14, 3.6 * len(a.zoom)))
    else:
        fig, axes = plt.subplots(4, 1, figsize=(14, 13.0), gridspec_kw={"height_ratios": [1.5, 1, 1, 1]})
    fig.patch.set_facecolor("white")

    if a.zoom:
        views = [(lo, hi, "zoom %.3f-%.3fs" % (lo, hi)) for lo, hi in a.zoom]
    else:
        views = [
            (0.0, t[-1], "full 0.00-%.3fs" % t[-1]),
            (1.48, 1.78, "seam 1.48-1.78s (overlap 1.500-1.625, pitch ramp 1.605-1.625)"),
            (1.06, 1.30, "onset 1.06-1.30s (head-silence mute ends 1.125)"),
            (1.585, 1.660, "burst zoom 1.585-1.660s"),
        ]

    for ax, (lo, hi, title) in zip(axes, views):
        draw(ax, f, t, db, sr, f0, fp_ms, fmax=a.fmax)
        ax.set_xlim(lo, hi)
        ax.set_title(title, fontsize=9, loc="left")
        ax.set_ylabel("Hz", fontsize=9)
        for xv, txt, col in ((1.125, "f225 mute end", "#ffcc00"),
                             (1.500, "f300 fade in", "#ffffff"),
                             (1.625, "f325 seam", "#ff4444")):
            if lo <= xv <= hi:
                ax.axvline(xv, color=col, lw=0.7, ls=":", alpha=0.9)
                ax.text(xv, a.fmax * 0.97, " " + txt, color=col, fontsize=7,
                        va="top", ha="left")
        for step in np.arange(np.ceil(lo / 0.1) * 0.1, hi, 0.1):
            ax.axvline(step, color="#888888", lw=0.4, alpha=0.5)
    axes[-1].set_xlabel("time (s)", fontsize=9)

    axes[0].legend(loc="upper left", fontsize=7, framealpha=0.35)

    sm = plt.cm.ScalarMappable(cmap="magma", norm=plt.Normalize(-70, 0))
    cb = fig.colorbar(sm, ax=axes, fraction=0.025, pad=0.01)
    cb.set_label("dB (rel. peak)", fontsize=8)
    cb.ax.tick_params(labelsize=7)

    fig.suptitle(os.path.basename(a.wav), fontsize=10)
    out = a.out or os.path.join(os.path.dirname(a.wav), "spectrogram.png")
    fig.savefig(out, dpi=150, bbox_inches="tight")
    print("[Spectrogram] %s" % out)
    print("[Spectrogram] sr=%d  duration=%.3fs  nper=1024(23ms) hop=128(2.9ms)" % (sr, len(x) / sr))


if __name__ == "__main__":
    main()
