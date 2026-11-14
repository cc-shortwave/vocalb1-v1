# -*- coding: utf-8 -*-
"""Formant target warping for the lyric-driven formant positions.

The v3 object model stores F1~F7 target frequencies per syllable in the lyric (or derives them from the
vowel manager reading the voicebank). This module moves the spectral envelope of every frame so that the
measured formants land on those targets, using a piecewise-linear frequency mapping in the log-amplitude
domain (the same interpolation domain the existing spectral warp uses).

The source formants are measured on the pyworld envelope of the *same* frames that are warped
(:func:`envelope_formants`), so source and targets share the 5 ms synthesis grid and pair
index-for-index. The voicebank's older formant list is deliberately not used as the source: it is
computed on an 11.6 ms STFT grid and keeps the lowest five spectral peaks, which for a voiced vowel are
harmonics rather than vocal-tract resonances, so pairing it by index transposed the timbre.

Errors carry the class and stage explicitly: ``[FormantWarp @ ...]``. Frames whose measured or target
formants are structurally unusable (e.g. silence) are documented in the returned diagnostics.
"""

from __future__ import annotations

import numpy as np

# Envelope peak picking defaults, mirrored from tools/analyze_voicebank.py so the measured targets in
# formants.json and the source formants measured here describe the same kind of peak: local maxima of
# the smoothed log envelope, at least 150 Hz apart, strongest first.
_MIN_HZ = 200.0
_MAX_HZ = 5000.0
_SMOOTH_HZ = 150.0
_MIN_PEAK_SPACING_HZ = 150.0
_FLOOR_DB = -70.0


class FormantWarpError(RuntimeError):
    """Raised when the formant warp input is structurally invalid."""

    def __init__(self, stage: str, message: str):
        super().__init__(f"[FormantWarp @ {stage}] {message}")
        self.stage = stage


def _valid_formants(values) -> np.ndarray:
    """Returns a strictly increasing positive formant array (may be shorter than 7)."""
    out = []
    last = 0.0
    for value in values:
        try:
            hz = float(value)
        except (TypeError, ValueError):
            break
        if not np.isfinite(hz) or hz <= 0:
            break
        if hz <= last:
            break
        out.append(hz)
        last = hz
    return np.asarray(out, dtype=np.float64)


def envelope_formants(sp: np.ndarray, sample_rate: int, max_formants: int = 7):
    """Measures F1~F7 as the strongest peaks of the pyworld spectral envelope.

    Returns an ``(n_frames, max_formants)`` array on the same 5 ms grid as ``sp``; frames without
    usable peaks are filled with NaN so the caller can skip them explicitly.
    """
    sp = np.asarray(sp, dtype=np.float64)
    if sp.ndim != 2:
        raise FormantWarpError("validate", f"sp 必须是二维数组，实际 {sp.ndim} 维。")

    n_frames, n_bins = sp.shape
    out = np.full((n_frames, max_formants), np.nan)
    if n_frames == 0 or n_bins < 8:
        return out

    fft_size = 2 * (n_bins - 1)
    bin_hz = sample_rate / float(fft_size)
    freqs = np.arange(n_bins, dtype=np.float64) * bin_hz
    nyquist = sample_rate / 2.0

    low = max(1, int(np.searchsorted(freqs, _MIN_HZ)))
    high = min(n_bins - 2, int(np.searchsorted(freqs, min(_MAX_HZ, nyquist))))
    if high <= low:
        return out

    log_env = np.log(np.maximum(sp, 1e-12))
    width = int(round(_SMOOTH_HZ / bin_hz))
    width = max(3, width | 1)
    kernel = np.ones(width) / float(width)
    spacing = _MIN_PEAK_SPACING_HZ

    for i in range(n_frames):
        if not np.isfinite(log_env[i]).any():
            continue
        if 10.0 * np.log10(max(float(np.max(sp[i])), 1e-20)) < _FLOOR_DB:
            continue

        smooth = np.convolve(log_env[i], kernel, mode="same")
        band = smooth[low:high]
        if band.size < 3:
            continue

        peaks = [j for j in range(1, band.size - 1)
                 if band[j] >= band[j - 1] and band[j] > band[j + 1]]
        if not peaks:
            continue

        # Greedy by height with a minimum spacing, so one broad resonance is not counted twice.
        chosen: list[int] = []
        for j in sorted(peaks, key=lambda k: band[k], reverse=True):
            hz = freqs[low + j]
            if all(abs(hz - freqs[low + other]) >= spacing for other in chosen):
                chosen.append(j)
            if len(chosen) >= max_formants:
                break

        values = np.sort(np.asarray([freqs[low + j] for j in chosen], dtype=np.float64))
        for k in range(1, values.size):
            if values[k] <= values[k - 1]:
                values = values[:k]
                break
        out[i, :values.size] = values

    return out


def warp_to_targets(sp: np.ndarray, sample_rate: int, target_hz,
                    frame_period: float = 5.0):
    """Warps ``sp`` so its measured formants move onto ``target_hz``.

    Parameters
    ----------
    sp : np.ndarray, shape (n_frames, n_bins)
        Spectral envelope (power spectrum from pyworld).
    sample_rate : int
    target_hz : np.ndarray, shape (n_frames, 7)
        Absolute target positions in Hz. Invalid rows (non-finite / non-increasing) leave the frame
        untouched.
    frame_period : float
        Only used for diagnostics.

    Returns
    -------
    (warped_sp, diagnostics)
        diagnostics = {'frames_warped': int, 'frames_skipped': int, 'source': str, ...}
    """
    if sp is None:
        return None, {"frames_warped": 0, "frames_skipped": 0, "source": "none", "reason": "no sp"}

    sp = np.asarray(sp, dtype=np.float64)
    if sp.ndim != 2:
        raise FormantWarpError("validate", f"sp 必须是二维数组，实际 {sp.ndim} 维。")

    targets = np.asarray(target_hz, dtype=np.float64) if target_hz is not None else None
    if targets is None:
        return sp.copy(), {"frames_warped": 0, "frames_skipped": len(sp),
                           "source": "none", "reason": "no targets"}
    if targets.ndim != 2 or targets.shape[1] < 7:
        raise FormantWarpError("validate",
                               f"formantTargets 必须是 (帧数, 7)，实际 {targets.shape}。")

    n_frames, n_bins = sp.shape
    # Source and targets are both measured on this frame grid, so pairing is index-for-index.
    sources = envelope_formants(sp, sample_rate, targets.shape[1])

    fft_size = 2 * (n_bins - 1)
    freqs = np.arange(n_bins, dtype=np.float64) * sample_rate / fft_size
    nyquist = sample_rate / 2.0
    src_bins = np.arange(n_bins, dtype=np.float64)

    sp_log = np.log(sp + 1e-10)
    out = sp.copy()
    warped = 0
    skipped = 0

    count = min(n_frames, targets.shape[0])
    for i in range(count):
        src = _valid_formants(sources[i])
        tgt = _valid_formants(targets[i])

        # Two formants on both sides are the minimum for a stable piecewise-linear mapping.
        pairs = min(len(src), len(tgt))
        if pairs < 2:
            skipped += 1
            continue

        src = src[:pairs]
        tgt = tgt[:pairs]

        # Anchors: DC, formants, Nyquist. Target anchors must stay below Nyquist.
        src_anchor = np.concatenate(([0.0], src, [nyquist]))
        tgt_anchor = np.concatenate(([0.0], np.minimum(tgt, nyquist * 0.98), [nyquist]))
        tgt_anchor = np.maximum.accumulate(tgt_anchor)

        # For each output frequency, read the source frequency whose formants were at that position.
        source_freq = np.interp(freqs, tgt_anchor, src_anchor)
        old_bins = source_freq * fft_size / sample_rate
        out[i] = np.exp(np.interp(old_bins, src_bins, sp_log[i]))
        warped += 1

    diagnostics = {
        "frames_warped": warped,
        "frames_skipped": skipped + max(0, n_frames - count),
        "source": "envelope",
        "reason": "ok",
        "frame_period": frame_period,
    }
    return out, diagnostics
