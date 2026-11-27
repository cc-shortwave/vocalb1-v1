# -*- coding: utf-8 -*-
"""Formant target warping for the lyric-driven formant positions.

The v3 object model stores F1~F7 target frequencies per syllable in the lyric (or derives them from the
vowel manager reading the voicebank). This module moves the spectral envelope of every frame so that the
measured formants land on those targets, using a piecewise-linear frequency mapping in the log-amplitude
domain (the same interpolation domain the existing spectral warp uses).

The source formants are measured on the pyworld envelope of the *same* frames that are warped
(:func:`envelope_formants`), so source and targets share the 5 ms synthesis grid frame for frame.
Within a frame the peaks are paired by frequency (never by index): the two lists may describe a
different number of peaks, and a spurious low-frequency peak would otherwise shift every later pair
by one slot. Peaks at or below the frame's f0 are discarded because a vocal-tract resonance cannot
sit below the fundamental. The voicebank's older formant list is deliberately not used as the source:
it is computed on an 11.6 ms STFT grid and keeps the lowest five spectral peaks, which for a voiced
vowel are harmonics rather than vocal-tract resonances, so pairing it by index transposed the timbre.

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


def envelope_formants(sp: np.ndarray, sample_rate: int, max_formants: int = 7,
                      min_hz=None):
    """Measures F1~F7 as the strongest peaks of the pyworld spectral envelope.

    ``min_hz`` is the lowest admissible formant: a resonance cannot sit below the fundamental, so
    callers pass the frame's f0 (scalar or per-frame array). Peaks at or below that floor are never
    reported, which keeps the low-frequency plateau of the envelope from occupying the F1 slot.

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

    floors = None
    if min_hz is not None:
        floors = np.asarray(min_hz, dtype=np.float64)
        if floors.ndim == 0:
            floors = np.full(n_frames, float(floors))
        elif floors.size < n_frames:
            floors = np.pad(floors, (0, n_frames - floors.size), mode="edge")
        else:
            floors = floors[:n_frames]
        floors = np.maximum(np.where(np.isfinite(floors), floors, _MIN_HZ), _MIN_HZ)

    high = min(n_bins - 2, int(np.searchsorted(freqs, min(_MAX_HZ, nyquist))))
    if high <= 1:
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

        low = _MIN_HZ if floors is None else max(_MIN_HZ, float(floors[i]))
        low = max(1, int(np.searchsorted(freqs, low)))
        if high <= low:
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


_PAIR_TOLERANCE_HZ = 250.0
_PAIR_TOLERANCE_RATIO = 0.2

# The graph's F1~F7 bandwidth default. Bandwidth is applied relative to this nominal width so an
# untouched graph (bandwidth = 80 Hz on every formant) leaves the envelope exactly as measured.
_DEFAULT_FORMANT_BW_HZ = 80.0


def _gain_profile(freqs, targets, gains, nyquist, default=1.0):
    """Per-bin amplitude multiplier interpolated through the formant gains (1.0 at DC/Nyquist)."""
    xs = [0.0]
    ys = [default]
    for hz, gain in zip(targets, gains):
        try:
            hz = float(hz)
            gain = float(gain)
        except (TypeError, ValueError):
            continue
        if not (np.isfinite(hz) and np.isfinite(gain)) or hz <= 0.0 or gain <= 0.0:
            continue
        xs.append(min(hz, nyquist * 0.98))
        ys.append(gain)
    xs.append(nyquist)
    ys.append(default)
    xs = np.asarray(xs, dtype=np.float64)
    ys = np.asarray(ys, dtype=np.float64)
    order = np.argsort(xs, kind="stable")
    xs, ys = xs[order], ys[order]
    keep = np.concatenate(([True], np.diff(xs) > 1e-6))
    xs, ys = xs[keep], ys[keep]
    if xs.size < 2 or np.allclose(ys, default):
        return None
    return np.interp(freqs, xs, ys, left=default, right=default)


def _bandwidth_anchors(src_pairs, tgt_pairs, pair_indices, bandwidths):
    """Extra mapping anchors that widen/narrow each formant to its target bandwidth.

    A pair maps the target formant frequency onto the source formant. Two more anchors at
    ``target ± bw/2`` → ``source ± default/2`` change the local slope of the frequency mapping, which
    is what makes the resonance wider (bw > default) or narrower (bw < default). Bandwidths equal to
    the default are skipped so the measured envelope is untouched by default.
    """
    extra_src: list[float] = []
    extra_tgt: list[float] = []
    half_default = _DEFAULT_FORMANT_BW_HZ * 0.5
    for source, target, formant_index in zip(src_pairs, tgt_pairs, pair_indices):
        if formant_index >= len(bandwidths):
            continue
        try:
            bw = float(bandwidths[formant_index])
        except (TypeError, ValueError):
            continue
        if not np.isfinite(bw) or bw <= 0.0 or abs(bw - _DEFAULT_FORMANT_BW_HZ) <= 1e-6:
            continue
        half_target = bw * 0.5
        for sign in (-1.0, 1.0):
            t = target + sign * half_target
            s = source + sign * half_default
            if t > 1.0 and s > 1.0:
                extra_tgt.append(t)
                extra_src.append(s)
    return extra_src, extra_tgt


def _as_matrix(values, n_frames):
    """Coerces per-formant data to (n_frames, 7) with edge padding; returns None when unusable."""
    if values is None:
        return None
    matrix = np.asarray(values, dtype=np.float64)
    if matrix.ndim != 2 or matrix.shape[1] < 7:
        return None
    if matrix.shape[0] < n_frames:
        matrix = np.pad(matrix, ((0, n_frames - matrix.shape[0]), (0, 0)), mode="edge")
    return matrix[:n_frames]


def warp_to_targets(sp: np.ndarray, sample_rate: int, target_hz,
                    frame_period: float = 5.0, f0=None,
                    target_gain=None, target_bw=None):
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
    f0 : array-like, optional
        Per-frame fundamental in Hz. Formants at or below it are physically impossible, so it is
        used as the per-frame floor for both the measured source peaks and the targets.
    target_gain : np.ndarray, shape (n_frames, 7), optional
        Per-formant amplitude gain (1.0 = unchanged; F1~F7 order).
    target_bw : np.ndarray, shape (n_frames, 7), optional
        Per-formant bandwidth in Hz (F1~F7 order), applied relative to 80 Hz so the default graph
        leaves the measured width untouched.

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
    count = min(n_frames, targets.shape[0])
    gains = _as_matrix(target_gain, n_frames)
    bandwidths = _as_matrix(target_bw, n_frames)

    floors = None
    if f0 is not None:
        values = np.asarray(f0, dtype=np.float64).reshape(-1)
        if values.size:
            if values.size < n_frames:
                values = np.pad(values, (0, n_frames - values.size), mode="edge")
            else:
                values = values[:n_frames]
            # A formant never sits below the fundamental; unvoiced frames keep the 200 Hz floor.
            floors = np.where(values > 0, np.maximum(values, _MIN_HZ), _MIN_HZ)

    # Source and target rows share the frame grid; the peaks inside a frame are matched by frequency.
    sources = envelope_formants(sp, sample_rate, targets.shape[1], min_hz=floors)

    fft_size = 2 * (n_bins - 1)
    freqs = np.arange(n_bins, dtype=np.float64) * sample_rate / fft_size
    nyquist = sample_rate / 2.0
    src_bins = np.arange(n_bins, dtype=np.float64)

    sp_log = np.log(sp + 1e-10)
    out = sp.copy()
    warped = 0
    skipped = 0

    for i in range(count):
        src = _valid_formants(sources[i])

        # Keep each target's formant index so bandwidth control can look up the matching BW value.
        tgt: list[float] = []
        tgt_indices: list[int] = []
        last = 0.0
        for formant_index, value in enumerate(targets[i]):
            try:
                hz = float(value)
            except (TypeError, ValueError):
                break
            if not np.isfinite(hz) or hz <= 0.0 or hz <= last:
                break
            last = hz
            if floors is not None and hz <= floors[i]:
                continue
            tgt.append(hz)
            tgt_indices.append(formant_index)
        tgt = np.asarray(tgt, dtype=np.float64)

        if floors is not None:
            src = src[src > floors[i]]

        # Peaks are matched by frequency, not by index: the measured lists and the target lists do
        # not have to describe the same number of peaks, and a plateau artefact would otherwise
        # shift every later pair by one slot.
        src_pairs: list[float] = []
        tgt_pairs: list[float] = []
        pair_indices: list[int] = []
        cursor = 0
        for target, formant_index in zip(tgt, tgt_indices):
            tolerance = max(_PAIR_TOLERANCE_HZ, _PAIR_TOLERANCE_RATIO * float(target))
            best = -1
            best_distance = None
            for index in range(cursor, src.size):
                distance = abs(float(src[index]) - float(target))
                if best_distance is None or distance < best_distance:
                    best, best_distance = index, distance
                if src[index] > target + tolerance:
                    break
            if best < 0 or best_distance is None or best_distance > tolerance:
                continue
            src_pairs.append(float(src[best]))
            tgt_pairs.append(float(target))
            pair_indices.append(formant_index)
            cursor = best + 1

        # Two matched pairs are the minimum for a stable piecewise-linear mapping.
        if len(src_pairs) < 2:
            skipped += 1
            continue

        # Anchors: DC, matched formants, Nyquist. Target anchors must stay below Nyquist.
        src_anchor = np.concatenate(([0.0], np.asarray(src_pairs), [nyquist]))
        tgt_anchor = np.concatenate(([0.0], np.minimum(np.asarray(tgt_pairs), nyquist * 0.98), [nyquist]))

        # Bandwidth control: extra anchors stretch or shrink each formant's local width.
        if bandwidths is not None:
            extra_src, extra_tgt = _bandwidth_anchors(src_pairs, tgt_pairs, pair_indices, bandwidths[i])
            if extra_tgt:
                src_anchor = np.concatenate((src_anchor, np.asarray(extra_src)))
                tgt_anchor = np.concatenate(
                    (tgt_anchor, np.minimum(np.asarray(extra_tgt), nyquist * 0.98)))
                order = np.argsort(tgt_anchor, kind="stable")
                src_anchor, tgt_anchor = src_anchor[order], tgt_anchor[order]

        tgt_anchor = np.maximum.accumulate(tgt_anchor)
        keep = np.concatenate(([True], np.diff(tgt_anchor) > 0))
        src_anchor = src_anchor[keep]
        tgt_anchor = tgt_anchor[keep]

        # For each output frequency, read the source frequency whose formants were at that position.
        source_freq = np.interp(freqs, tgt_anchor, src_anchor)
        old_bins = source_freq * fft_size / sample_rate
        warped_frame = np.exp(np.interp(old_bins, src_bins, sp_log[i]))

        # Formant gain: a smooth amplitude profile through the seven target positions.
        if gains is not None:
            profile = _gain_profile(freqs, targets[i], gains[i], nyquist)
            if profile is not None:
                warped_frame = warped_frame * profile

        out[i] = warped_frame
        warped += 1

    diagnostics = {
        "frames_warped": warped,
        "frames_skipped": skipped + max(0, n_frames - count),
        "source": "envelope",
        "reason": "ok",
        "frame_period": frame_period,
    }
    return out, diagnostics
