#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Analyze a UTAU voicebank and write formants.json for the v3 vowel manager.

The C# vowel manager reads <voicebank>/formants.json to drive F1~F7 from the singer's own
voicebank instead of the built-in vowel table. This tool produces that file by reading pyworld's
CheapTrick spectral envelope of every wav in the bank (harmonic peaks are already removed from the
envelope) and taking the median resonance positions of the voiced frames.

Usage:
    python tools/analyze_voicebank.py <voicebank-dir> [--limit N] [--frame-period 5]

Output:
    <voicebank-dir>/formants.json
    {
      "phonemes": {"_あ": [F1..F7], ...},
      "vowels":   {"a": [F1..F7], ...},
      "scale": 1.0,
      "source": "analyze_voicebank",
      "generatedAt": "..."
    }

Every failure is reported as "[<Class> @ <stage>] message" and exits non-zero; nothing is silently
skipped except individual wav files that cannot be decoded (reported on stderr and counted).
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
import sys
import traceback

import numpy as np
import soundfile as sf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)


class AnalyzerError(RuntimeError):
    def __init__(self, stage: str, message: str):
        super().__init__(f"[VoicebankAnalyzer @ {stage}] {message}")
        self.stage = stage


# Kana -> vowel for the five Japanese vowels (single kana and common voiced rows).
_VOWEL_ROWS = {
    "a": "あかさたなはまやらわがざだばぱぁゃゎアカサタナハマヤラワガザダバパァャヮ",
    "i": "いきしちにひみりぎじぢびぴぃイキシチニヒミリギジヂビピィ",
    "u": "うくすつぬふむゆるぐずづぶぷぅゔウクスツヌフムユルグズヅブプゥヴ",
    "e": "えけせてねへめれげぜでべぺぇエケセテネヘメレゲゼデベペェ",
    "o": "おこそとのほもよろをごぞどぼぽぉオコソトノホモヨロヲゴゾドボポォ",
    "n": "んン",
}


def _vowel_of(stem: str) -> str:
    text = stem.lstrip("_-")
    for vowel, chars in _VOWEL_ROWS.items():
        if any(ch in chars for ch in text):
            return vowel
    return "a"


def _formants_of_sample(path: str, frame_period: float):
    """Returns (F1..F7, voiced_frame_count) or (None, 0).

    Formants are read from pyworld's Cheaptrick spectral envelope, not from the raw magnitude
    spectrum: raw peaks are dominated by the harmonic series, while the envelope already has the
    glottal source removed, so its peaks are the actual vocal-tract resonances.
    """
    from vocal_lib.core.pyworld_processor import extract_pyworld_features

    data, sr = sf.read(path, dtype="float64", always_2d=True)
    if data.size == 0:
        return None, 0
    mono = data.mean(axis=1)
    features = extract_pyworld_features(mono, int(sr), frame_period=frame_period)
    sp = np.asarray(features["sp"], dtype=np.float64)
    voiced = np.asarray(features["voiced_flags"], dtype=bool)
    if sp.size == 0:
        return None, 0

    rows = sp[voiced] if voiced.any() else sp
    if len(rows) < 2:
        return None, len(rows)

    envelope = np.median(rows, axis=0)
    values = _pick_envelope_peaks(envelope, int(sr))
    if values is None:
        return None, len(rows)
    return values, len(rows)


def _pick_envelope_peaks(envelope: np.ndarray, sample_rate: int, count: int = 7):
    """Picks up to ``count`` increasing vocal-tract resonances from a median power envelope."""
    n_bins = len(envelope)
    if n_bins < 8:
        return None
    fft_size = 2 * (n_bins - 1)
    freqs = np.arange(n_bins, dtype=np.float64) * sample_rate / fft_size
    bin_hz = sample_rate / fft_size

    log_env = np.log(np.maximum(envelope, 1e-12))
    window = max(3, int(round(150.0 / bin_hz)) | 1)
    smooth = np.convolve(log_env, np.ones(window) / window, mode="same")

    low = max(1, int(np.searchsorted(freqs, 200.0)))
    high = min(n_bins - 2, int(np.searchsorted(freqs, 5000.0)))
    candidates = [i for i in range(low, high)
                  if smooth[i] >= smooth[i - 1] and smooth[i] > smooth[i + 1]]
    if not candidates:
        return None

    # Greedy by prominence with a minimum spacing so one broad resonance is not counted twice.
    chosen: list[int] = []
    for index in sorted(candidates, key=lambda i: smooth[i], reverse=True):
        if all(abs(freqs[index] - freqs[other]) >= 150.0 for other in chosen):
            chosen.append(index)
        if len(chosen) >= count:
            break
    chosen.sort(key=lambda i: freqs[i])

    values: list[float] = []
    last = 0.0
    for index in chosen:
        hz = float(freqs[index])
        if hz <= last:
            continue
        values.append(hz)
        last = hz

    if len(values) < 2:
        return None
    while len(values) < count:
        last = last * 1.15 if last > 0 else 1000.0
        values.append(last)
    return values[:count]


def _median_rows(rows):
    matrix = np.asarray(rows, dtype=np.float64)
    medians = np.median(matrix, axis=0)
    values = []
    last = 0.0
    for hz in medians:
        if not np.isfinite(hz) or hz <= last or hz <= 0:
            break
        values.append(float(hz))
        last = float(hz)
    return values or None


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Analyze a voicebank and write formants.json.")
    parser.add_argument("voicebank", help="voicebank directory")
    parser.add_argument("--limit", type=int, default=0, help="analyze at most N wav files (0 = all)")
    parser.add_argument("--frame-period", type=float, default=5.0)
    args = parser.parse_args(argv)

    root = os.path.abspath(args.voicebank)
    if not os.path.isdir(root):
        raise AnalyzerError("validate", f"声库目录不存在：{root}")

    files = sorted(
        os.path.join(root, name)
        for name in os.listdir(root)
        if name.lower().endswith(".wav")
    )
    if args.limit > 0:
        files = files[: args.limit]
    if not files:
        raise AnalyzerError("scan", f"声库目录中没有 wav：{root}")

    phonemes: dict[str, list[float]] = {}
    vowel_rows: dict[str, list[list[float]]] = {}
    failed: list[str] = []

    print(f"[VoicebankAnalyzer @ scan] {len(files)} 个样本，目录 {root}", flush=True)
    for index, path in enumerate(files, start=1):
        stem = os.path.splitext(os.path.basename(path))[0]
        try:
            values, frames = _formants_of_sample(path, args.frame_period)
        except Exception as exc:  # noqa: BLE001 - reported, never swallowed
            failed.append(f"{stem}: {type(exc).__name__}: {exc}")
            print(f"[VoicebankAnalyzer @ analyze] {stem} 失败：{type(exc).__name__}: {exc}",
                  file=sys.stderr, flush=True)
            continue

        if values is None:
            failed.append(f"{stem}: 没有足够的浊音帧")
            print(f"[VoicebankAnalyzer @ analyze] {stem} 跳过：没有足够的浊音帧", file=sys.stderr,
                  flush=True)
            continue

        phonemes[stem] = values
        vowel_rows.setdefault(_vowel_of(stem), []).append(values)
        print(f"[VoicebankAnalyzer @ analyze] {index}/{len(files)} {stem} "
              f"F1={values[0]:.0f} F2={values[1]:.0f} frames={frames}", flush=True)

    if not phonemes:
        raise AnalyzerError("analyze", f"没有可用的样本，失败 {len(failed)} 个。")

    vowels = {
        vowel: (median or rows[0])
        for vowel, rows in vowel_rows.items()
        if (median := _median_rows(rows)) is not None
    }

    output = {
        "phonemes": phonemes,
        "vowels": vowels,
        "scale": 1.0,
        "source": "analyze_voicebank",
        "generatedAt": _dt.datetime.now().isoformat(timespec="seconds"),
        "failed": failed,
    }
    path = os.path.join(root, "formants.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(output, handle, ensure_ascii=False, indent=2)

    print(f"[VoicebankAnalyzer @ write] 已写入 {path}（{len(phonemes)} 个音节，"
          f"{len(vowels)} 个元音，失败 {len(failed)} 个）", flush=True)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AnalyzerError as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(2)
    except Exception as exc:  # noqa: BLE001
        print(f"[VoicebankAnalyzer @ main] {type(exc).__name__}: {exc}", file=sys.stderr)
        traceback.print_exc()
        raise SystemExit(3)
