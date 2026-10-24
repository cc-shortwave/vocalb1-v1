import numpy as np
def calculate_pitch_factor(semitones: float) -> float:
    """
    Step 1: set the target
    Compute the frequency scaling factor k = 2^(semitones/12)

    Parameters:
        semitones: target semitone count (positive raises pitch, negative lowers it)

    Returns:
        frequency scaling factor k
    """
    return np.power(2.0, semitones / 12.0)


def shift_f0_logarithmic(f0: np.ndarray, semitones: float) -> np.ndarray:
    """
    Step 2: shift F0 (core step) - logarithmic scaling
    Key point 1: logarithmic vs linear scaling of F0

    Taking the log of F0 and adding/subtracting a constant keeps the semitone span
    of glides unchanged, which sounds the most natural. Pure linear multiplication
    would amplify the glide range proportionally and sound exaggerated.

    Parameters:
        f0: original F0 curve (Hz)
        semitones: target semitone count

    Returns:
        scaled F0 curve
    """
    if len(f0) == 0:
        return np.array([])

    k = calculate_pitch_factor(semitones)
    log_k = np.log(k)

    f0_shifted = np.zeros_like(f0, dtype=np.float64)

    voiced_mask = f0 > 0
    f0_shifted[voiced_mask] = np.exp(np.log(f0[voiced_mask]) + log_k)

    return f0_shifted


def smooth_f0_boundaries(f0: np.ndarray, sample_rate: int,
                         frame_period: float = 5.0) -> np.ndarray:
    """
    Boundary artifact handling: apply a tapered median smoothing to the first and
    last few frames of the F0 curve.

    Note: do not multiply F0 by a 0->1 ramp, that would turn voiced frames into
    unvoiced frames (F0=0 means unvoiced in pyworld), producing "half-semitone
    slide + muted" bubble artifacts at note onsets/offsets.

    Improvement: use a raised-cosine taper window to smoothly blend the "boundary
    median" with the "original F0", avoiding the step discontinuity that hard
    median replacement creates at the junction between the median region and the
    original region (a step causes both pitch tremor and crackling artifacts
    during pyworld synthesis).
    The voiced/unvoiced state is preserved (frames with F0=0 are not modified).

    Parameters:
        f0: F0 curve
        sample_rate: sample rate (kept for signature compatibility, unused here)
        frame_period: frame period (milliseconds)

    Returns:
        boundary-smoothed F0 curve (voiced frames remain voiced)
    """
    if len(f0) == 0:
        return np.array([])

    f0_smooth = f0.copy().astype(np.float64)

    # Median-smooth the first and last 20ms (4 frames @5ms)
    frame_period_sec = frame_period / 1000.0
    n_edge = max(1, int(0.020 / frame_period_sec))

    num_frames = len(f0_smooth)
    n_edge = min(n_edge, num_frames // 2)

    if n_edge < 2:
        return f0_smooth

    def tapered_median_blend(arr, start, end, reverse=False):
        """
        Perform a tapered median blend over the voiced frames in [start, end):
        - the boundary end (start when reverse=False, end-1 when reverse=True) uses the median entirely
        - the inner end uses the original values entirely
        the middle transitions smoothly with a raised cosine, removing the step discontinuity.
        Only voiced frames are modified; unvoiced frames (F0=0) stay unchanged.
        """
        segment = arr[start:end]
        voiced_mask = segment > 0
        voiced = segment[voiced_mask]
        if len(voiced) < 2:
            return

        med = np.median(voiced)
        n = len(segment)
        t = np.arange(n, dtype=np.float64)
        if n > 1:
            # head: t=0 -> 1.0 (strong median), t=n-1 -> 0.0 (original value)
            fade = 0.5 * (1.0 + np.cos(np.pi * t / (n - 1)))
            if reverse:
                # tail: t=0 -> 0.0 (original value), t=n-1 -> 1.0 (strong median)
                fade = 1.0 - fade
        else:
            fade = np.array([1.0])

        blended = segment * (1.0 - fade) + med * fade
        arr[start:end] = np.where(voiced_mask, blended, segment)

    # Head: boundary end = median, inner end = original value
    tapered_median_blend(f0_smooth, 0, n_edge, reverse=False)
    # Tail: inner end = original value, boundary end = median
    tapered_median_blend(f0_smooth, num_frames - n_edge, num_frames, reverse=True)

    return f0_smooth


def adjust_oq(oq: float, semitones: float) -> float:
    """
    Key point 2: the open quotient (OQ) must be corrected manually with pitch

    Raising pitch -> decrease OQ (let the vocal folds close faster, giving high notes "core")
    Lowering pitch -> increase OQ (let the airflow be gentler)

    Parameters:
        oq: original OQ value
        semitones: target semitone count

    Returns:
        corrected OQ value
    """
    if oq is None or oq <= 0:
        oq = 0.60

    if semitones > 0:
        oq_adjusted = oq - semitones * 0.01
        oq_adjusted = max(0.45, oq_adjusted)
    else:
        oq_adjusted = oq + abs(semitones) * 0.008
        oq_adjusted = min(0.70, oq_adjusted)

    return oq_adjusted


def enhance_breathiness(ap: np.ndarray, semitones: float,
                        breathy_threshold: float = 0.3) -> np.ndarray:
    """
    Enhance the breathy component (only affects frames that are already breathy)

    In pyworld, a larger ap (aperiodicity) value means stronger breathiness.
    Breathiness weakens after pitch shifting and needs appropriate enhancement -
    but pure voiced frames must never be polluted, otherwise stable voiced
    segments get broadband noise injected and sound hoarse.

    Strategy:
        - voiced frames (ap < breathy_threshold): unchanged
        - breathy frames (ap >= breathy_threshold): enhanced linearly with semitones, capped at 1.5x

    Parameters:
        ap: original aperiodicity parameter, shape (n_frames, n_bins)
        semitones: target semitone count
        breathy_threshold: threshold for classifying a frame as breathy
            0.3 (default, recommended for male voice): only captures genuinely breathy frames (/h/, breathy onsets)
            0.1 (old default): misclassifies normal male voiced frames as breathy -> hoarse
            0.4+: barely enhances, suitable for extremely clean timbre

    Returns:
        enhanced ap parameter
    """
    if ap is None:
        return None

    # Only enhance frames that are already breathy
    BREATHY_THRESHOLD = breathy_threshold

    # Compute the mean ap per frame to decide whether it is a breathy frame
    frame_mean_ap = np.mean(ap, axis=1) if ap.ndim > 1 else ap
    breathy_mask = frame_mean_ap >= BREATHY_THRESHOLD  # shape (n_frames,)

    ap_enhanced = ap.copy()

    # Compute the breathiness enhancement factor
    if semitones > 0:
        breath_factor = 1.0 + semitones * 0.02   # raising pitch: breathiness weakens, compensate moderately
    else:
        breath_factor = 1.0 + abs(semitones) * 0.015  # lowering pitch: gentle compensation
    breath_factor = min(breath_factor, 1.5)

    # Apply the enhancement only to breathy frames
    ap_enhanced[breathy_mask] = np.clip(
        ap_enhanced[breathy_mask] * breath_factor, 0.0, 1.0
    )

    return ap_enhanced


def warp_spectral_envelope(sp: np.ndarray, semitones: float, sample_rate: int,
                            follow_ratio=0.3,
                            formants_trajectory=None) -> np.ndarray:
    """
    Spectral envelope frequency-axis mapping (formant following)

    After pitch shifting the harmonic positions all move, but the formant positions
    in sp stay unchanged, which misaligns harmonics and formants and sounds buzzy/hoarse.
    This function moves the formants along with the pitch by follow_ratio.

    Parameters:
        sp: spectral envelope, shape (n_frames, n_bins)
        semitones: semitone count
        sample_rate: sample rate
        follow_ratio: formant follow ratio, supports two forms:
            - float: uniformly scale all formants (0.0~1.0+)
            - tuple/list (f1_ratio, f2_ratio, f3_ratio): per-segment scaling
        formants_trajectory: per-frame formant frequency trajectory, list of dict
            [{'F1': hz, 'F2': hz, 'F3': hz}, ...]
            when provided, enables [dynamic band] mode: bands are segmented by each
            frame's actual F1/F2/F3 positions, fixing the misclassification of fixed
            bands for back vowels (/u//o/ with low F2).
            missing or invalid frames automatically fall back to fixed bands.

    Returns:
        sp after frequency-axis mapping, same shape as input
    """
    if sp is None or semitones == 0:
        return sp.copy() if sp is not None else None

    if sp.ndim != 2:
        return sp.copy()

    n_frames, n_bins = sp.shape
    # pyworld cheaptrick fft_size = 2 * (n_bins - 1)
    fft_size = 2 * (n_bins - 1)
    freqs = np.arange(n_bins) * sample_rate / fft_size  # frequency of each bin

    # Formant follow coefficient k = 2^(semitones/12)
    k = calculate_pitch_factor(semitones)

    # Parse follow_ratio into a list, supporting 3~7 segments
    if isinstance(follow_ratio, (tuple, list, np.ndarray)):
        follow_ratios = [float(r) for r in follow_ratio]
    else:
        follow_ratios = [float(follow_ratio)]
    # Pad to 7 segments with the last value
    while len(follow_ratios) < 7:
        follow_ratios.append(follow_ratios[-1])

    # Fixed-band fallback (used when there is no formant data or a frame's data is invalid)
    fallback_bp_freq = [0, 900, 1100, 2400, 2600, sample_rate // 2]
    fallback_bp_ratio = [follow_ratios[0], follow_ratios[0],
                         follow_ratios[1], follow_ratios[1],
                         follow_ratios[2], follow_ratios[2]]
    fallback_per_bin_ratio = np.interp(freqs, fallback_bp_freq, fallback_bp_ratio)

    # Uniform mode (follow_ratio is a float and there is no formant data): use fixed warp_factors directly
    use_uniform = (not isinstance(follow_ratio, (tuple, list, np.ndarray))
                   and (formants_trajectory is None or len(formants_trajectory) == 0))

    # Precompute old_bins (identical for all frames in uniform mode)
    new_bins = np.arange(n_bins, dtype=float)
    x_orig = np.arange(n_bins)

    # Interpolate in the log domain: pyworld sp is a linear power spectrum and linear
    # interpolation smears formant peaks, causing inter-frame energy discontinuities
    # (heard as crackling artifacts). Log-domain interpolation preserves peak shapes
    # and makes the peak transition between adjacent frames smoother.
    sp_log = np.log(sp + 1e-10)
    sp_warped = np.zeros_like(sp)

    if use_uniform:
        warp_factors = np.full(n_bins, 1.0 + (k - 1.0) * follow_ratios[0])
        old_bins = new_bins / warp_factors
        for i in range(n_frames):
            sp_warped[i] = np.exp(np.interp(old_bins, x_orig, sp_log[i]))
        return sp_warped

    # Segmented mode: compute per_bin_ratio frame by frame
    has_dynamic = (formants_trajectory is not None and len(formants_trajectory) > 0)

    for i in range(n_frames):
        per_bin_ratio = None

        # Try dynamic bands: locate band boundaries with this frame's actual F1~F7 frequencies
        if has_dynamic and i < len(formants_trajectory):
            ft = formants_trajectory[i]
            # Collect all available formant frequencies
            formant_freqs = []
            for j in range(1, 8):  # F1~F7
                fv = float(ft.get(f'F{j}', 0))
                if fv > 0:
                    formant_freqs.append(fv)
                else:
                    break

            # Need at least 2 formants, strictly increasing, with sufficient spacing (>100Hz)
            if len(formant_freqs) >= 2 and formant_freqs[0] > 50:
                valid = all(formant_freqs[j+1] > formant_freqs[j] + 100
                           for j in range(len(formant_freqs) - 1))
                if valid:
                    # Build band breakpoints: boundaries at the midpoints of adjacent formants
                    bp_freq = [0.0]
                    bp_ratio = [follow_ratios[0]]
                    for j in range(len(formant_freqs) - 1):
                        mid = (formant_freqs[j] + formant_freqs[j + 1]) / 2.0
                        trans = min(100.0, (formant_freqs[j + 1] - formant_freqs[j]) / 3.0)
                        r_cur = follow_ratios[j] if j < len(follow_ratios) else follow_ratios[-1]
                        r_next = follow_ratios[j + 1] if j + 1 < len(follow_ratios) else follow_ratios[-1]
                        bp_freq.append(mid - trans)
                        bp_ratio.append(r_cur)
                        bp_freq.append(mid + trans)
                        bp_ratio.append(r_next)
                    bp_freq.append(sample_rate / 2.0)
                    bp_ratio.append(follow_ratios[len(formant_freqs) - 1]
                                   if len(formant_freqs) - 1 < len(follow_ratios)
                                   else follow_ratios[-1])
                    per_bin_ratio = np.interp(freqs, bp_freq, bp_ratio)

        if per_bin_ratio is None:
            per_bin_ratio = fallback_per_bin_ratio

        warp_factors = 1.0 + (k - 1.0) * per_bin_ratio
        old_bins = new_bins / warp_factors
        sp_warped[i] = np.exp(np.interp(old_bins, x_orig, sp_log[i]))

    return sp_warped


def apply_band_ratios(sp: np.ndarray, sample_rate: int,
                      band_ratios, f0_values=None,
                      formants_trajectory=None,
                      transition_hz: float = 50.0,
                      fallback_band_width_hz: float = 250.0) -> np.ndarray:
    """
    Per-band spectrum multipliers centered on F0/F1/F2/.../F7

    For each frame, band boundaries are derived from that frame's actual F0 and
    available formant (F1~F7) frequencies (midpoints of adjacent formants), each band
    gets its own multiplier, and raised-cosine transitions are used between bands.

    Band layout (dynamic per frame, with 4 formants as an example):
        band 0 (F0 region): [0,          (F0+F1)/2]  -> band_ratios[0]
        band 1 (F1 region): [(F0+F1)/2,  (F1+F2)/2]  -> band_ratios[1]
        band 2 (F2 region): [(F1+F2)/2,  (F2+F3)/2]  -> band_ratios[2]
        ...
        band N (FN region): [(F_{N-1}+FN)/2, Nyquist] -> band_ratios[N]
    Each formant lies exactly at the center of its corresponding band.

    Parameters:
        sp: spectral envelope, shape (n_frames, n_bins)
        sample_rate: sample rate
        band_ratios: multiplier list for each band, 8 bands recommended [r_F0,r_F1,...,r_F7]
            >1.0 boost, <1.0 attenuate, 1.0 unchanged
            bands beyond the list length use the last value
        f0_values: per-frame F0 (n_frames,), used to locate the F0 band center
        formants_trajectory: per-frame formants [{'F1':hz,...,'F7':hz}, ...]
            None, or a frame with invalid data, falls back to fixed bands for that frame
        transition_hz: transition width between bands (default 50Hz)
        fallback_band_width_hz: fallback band width when there is no formant data (default 250Hz)

    Returns:
        sp after applying the per-band multipliers, same shape as input
    """
    if sp is None or band_ratios is None or len(band_ratios) == 0:
        return sp.copy() if sp is not None else None

    if all(abs(r - 1.0) < 1e-6 for r in band_ratios):
        return sp.copy() if sp is not None else None

    if sp.ndim != 2:
        return sp.copy()

    n_frames, n_bins = sp.shape
    fft_size = 2 * (n_bins - 1)
    freqs = np.arange(n_bins) * sample_rate / fft_size
    nyquist = sample_rate / 2.0
    n_bands = len(band_ratios)

    out = sp.astype(np.float64).copy()

    for frame_idx in range(n_frames):
        # Get this frame's F0 and all available formants
        f0 = float(f0_values[frame_idx]) if f0_values is not None else 0.0

        formant_freqs = []
        if formants_trajectory is not None and frame_idx < len(formants_trajectory):
            fmt = formants_trajectory[frame_idx]
            for j in range(1, 8):  # F1~F7
                fv = float(fmt.get(f'F{j}', 0))
                if fv > 0:
                    formant_freqs.append(fv)
                else:
                    break

        # Check whether formant-based segmentation is usable: requires F0>0, at least 2
        # formants, strictly increasing, spacing >100Hz
        use_formant = False
        if f0 > 0 and len(formant_freqs) >= 2 and formant_freqs[0] > f0:
            use_formant = all(formant_freqs[j] > formant_freqs[j - 1] + 100
                             for j in range(1, len(formant_freqs)))

        if use_formant:
            # F0 + formant midpoints as band boundaries
            edges = [0.0, (f0 + formant_freqs[0]) / 2.0]
            for j in range(len(formant_freqs) - 1):
                edges.append((formant_freqs[j] + formant_freqs[j + 1]) / 2.0)
            edges.append(nyquist)
        else:
            # Fallback: fixed-width bands
            edges = [min(i * fallback_band_width_hz, nyquist)
                     for i in range(n_bands + 1)]
            edges[-1] = nyquist

        # Build interpolation breakpoints: flat at each band center, raised-cosine transitions between bands
        bp_freq = [0.0]
        bp_ratio = [float(band_ratios[0])]

        for i in range(1, min(n_bands, len(edges) - 1)):
            edge = edges[i]
            if edge - transition_hz >= nyquist or edge <= 0:
                break
            r_prev = float(band_ratios[i - 1])
            r_curr = float(band_ratios[i])
            lo = max(0.0, edge - transition_hz)
            hi = min(nyquist, edge + transition_hz)
            if hi <= lo:
                hi = lo + 1.0
            bp_freq.append(lo)
            bp_ratio.append(r_prev)
            bp_freq.append(hi)
            bp_ratio.append(r_curr)

        bp_freq.append(nyquist)
        bp_ratio.append(float(band_ratios[-1]))

        gain = np.interp(freqs, bp_freq, bp_ratio)
        out[frame_idx] = sp[frame_idx] * gain

    return out


def process_pitch_shift(f0: np.ndarray, semitones: float,
                        sample_rate: int, oq: float = None,
                        frame_period: float = 5.0, ap: np.ndarray = None,
                        sp: np.ndarray = None, follow_ratio: float = 0.3,
                        breathy_threshold: float = 0.3,
                        formants_trajectory=None,
                        band_ratios=None,
                        band_width_hz: float = 250.0) -> dict:
    """
    One-shot processing of all parameters for a pitch shift

    Parameters:
        f0: original F0 curve (Hz)
        semitones: target semitone count
        sample_rate: sample rate
        oq: original OQ value (optional, metadata only, not used by pyworld synthesis)
        frame_period: frame period (milliseconds)
        ap: pyworld aperiodicity parameter (optional, only enhances breathy frames)
        sp: pyworld spectral envelope (optional, used for formant-following warp)
        follow_ratio: formant follow ratio (float or (F1,F2,F3) tuple)
        breathy_threshold: threshold for classifying a frame as breathy (default 0.3, recommended for male voice)
        formants_trajectory: per-frame formant frequencies [{'F1':hz,'F2':hz,'F3':hz},...]
            when provided, warp_spectral_envelope enables dynamic band segmentation
        band_ratios: per-band spectrum multiplier list, 4 bands recommended [r_F0, r_F1, r_F2, r_F3]
            bands are centered on each frame's actual F0/F1/F2/F3
            (boundaries at the midpoints of adjacent formants)
            >1.0 boost, <1.0 attenuate, 1.0 unchanged; bands beyond the list use the last value
            None or all 1.0 disables the processing
        band_width_hz: fallback band width (default 250Hz, used when no formant data)

    Returns:
        processed parameter dictionary:
        {
            'f0_shifted': processed F0 curve,
            'oq_adjusted': corrected OQ value (metadata only),
            'ap_enhanced': enhanced ap parameter (breathy frames only),
            'sp_warped': sp after frequency-axis mapping (formant following + per-band multipliers),
            'pitch_factor': frequency scaling factor k,
            'max_f0': maximum F0 value (used for anti-aliasing decisions)
        }
    """
    k = calculate_pitch_factor(semitones)

    f0_shifted = shift_f0_logarithmic(f0, semitones)

    f0_shifted = smooth_f0_boundaries(f0_shifted, sample_rate, frame_period)

    oq_adjusted = adjust_oq(oq, semitones)

    ap_enhanced = enhance_breathiness(ap, semitones, breathy_threshold)

    if sp is not None:
        sp_warped = warp_spectral_envelope(sp, semitones, sample_rate,
                                           follow_ratio, formants_trajectory)
        # Compute the warped formant positions for aligning the band_ratios centers
        # warp_spectral_envelope mapping: f_warped = f * (1 + (k-1) * ratio)
        warped_formants = None
        if formants_trajectory is not None and len(formants_trajectory) > 0:
            if isinstance(follow_ratio, (tuple, list, np.ndarray)):
                follow_ratios_list = [float(r) for r in follow_ratio]
            else:
                follow_ratios_list = [float(follow_ratio)]
            warped_formants = []
            for ft in formants_trajectory:
                wf = {}
                for j in range(1, 8):  # F1~F7
                    f_key = f'F{j}'
                    f_val = float(ft.get(f_key, 0))
                    if f_val > 0:
                        r_idx = min(j - 1, len(follow_ratios_list) - 1)
                        ratio = follow_ratios_list[r_idx]
                        wf[f_key] = f_val * (1.0 + (k - 1.0) * ratio)
                        wf[f'{f_key}_orig'] = f_val
                    else:
                        break
                warped_formants.append(wf)
        # Per-band spectrum multipliers (applied after warp, using the warped formant positions as band centers)
        if band_ratios is not None and len(band_ratios) > 0:
            sp_warped = apply_band_ratios(sp_warped, sample_rate,
                                          band_ratios,
                                          f0_values=f0_shifted,
                                          formants_trajectory=warped_formants,
                                          fallback_band_width_hz=band_width_hz)
    else:
        sp_warped = None
        warped_formants = None

    max_f0 = np.max(f0_shifted[f0_shifted > 0]) if np.any(f0_shifted > 0) else 0

    return {
        'f0_shifted': f0_shifted,
        'oq_adjusted': oq_adjusted,
        'ap_enhanced': ap_enhanced,
        'sp_warped': sp_warped,
        'pitch_factor': k,
        'max_f0': max_f0,
        'warped_formants': warped_formants,
    }
