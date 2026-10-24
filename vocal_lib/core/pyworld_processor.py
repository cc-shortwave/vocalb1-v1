import numpy as np

# pyworld is a mandatory dependency: if it is not installed, raise ImportError here with no graceful degradation.
import pyworld as pw

MIN_F0 = 60
MAX_F0 = 500
FRAME_PERIOD = 5.0

def extract_pyworld_features(audio_data, sample_rate):
    audio_float = audio_data.astype(np.float64)

    f0, time_axis = pw.dio(audio_float, sample_rate, f0_floor=MIN_F0, f0_ceil=MAX_F0, frame_period=FRAME_PERIOD)
    f0 = pw.stonemask(audio_float, f0, time_axis, sample_rate)

    sp = pw.cheaptrick(audio_float, f0, time_axis, sample_rate)
    ap = pw.d4c(audio_float, f0, time_axis, sample_rate)

    voiced_flags = f0 > 0

    return {
        'f0': f0,
        'time_axis': time_axis,
        'sp': sp,
        'ap': ap,
        'voiced_flags': voiced_flags
    }

def compute_jitter(f0_sequence, time_axis=None):
    if f0_sequence is None or len(f0_sequence) < 2:
        return {
            'jitter_local' : 0.0,
            'jitter_rap' : 0.0,
            'jitter_ppq5' : 0.0,
            'jitter_ddp' : 0.0
        }

    voiced_f0 = f0_sequence[f0_sequence > 0]
    if len(voiced_f0) < 5:
        return {
            'jitter_local' : 0.0,
            'jitter_rap' : 0.0,
            'jitter_ppq5' : 0.0,
            'jitter_ddp' : 0.0
        }

    diffs = np.abs(np.diff(voiced_f0))
    avg_f0 = np.mean(voiced_f0)

    jitter_local = np.mean(diffs) / avg_f0 if avg_f0 > 0 else 0.0

    rap_values = []
    for i in range(1, len(voiced_f0) - 1):
        sum_abs = np.abs(voiced_f0[i] - voiced_f0[i-1]) + np.abs(voiced_f0[i+1] - voiced_f0[i])
        rap_values.append(sum_abs / (2 * avg_f0))
    jitter_rap = np.mean(rap_values) if rap_values else 0.0

    ppq5_values = []
    for i in range(2, len(voiced_f0) - 2):
        sum_abs = np.abs(voiced_f0[i-2] - voiced_f0[i]) + np.abs(voiced_f0[i-1] - voiced_f0[i]) + \
                   np.abs(voiced_f0[i+1] - voiced_f0[i]) + np.abs(voiced_f0[i+2] - voiced_f0[i])
        ppq5_values.append(sum_abs / (4 * avg_f0))
    jitter_ppq5 = np.mean(ppq5_values) if ppq5_values else 0.0

    ddp_values = []
    for i in range(2, len(voiced_f0) - 2):
        sum_abs = np.abs(voiced_f0[i-2] - 2*voiced_f0[i-1] + voiced_f0[i]) + \
                   np.abs(voiced_f0[i-1] - 2*voiced_f0[i] + voiced_f0[i+1]) + \
                   np.abs(voiced_f0[i] - 2*voiced_f0[i+1] + voiced_f0[i+2])
        ddp_values.append(sum_abs / (3 * avg_f0))
    jitter_ddp = np.mean(ddp_values) if ddp_values else 0.0

    return {
        'jitter_local' : jitter_local * 100,
        'jitter_rap' : jitter_rap * 100,
        'jitter_ppq5' : jitter_ppq5 * 100,
        'jitter_ddp' : jitter_ddp * 100
    }

def compute_shimmer(f0_sequence, audio_data, sample_rate, time_axis=None):
    if f0_sequence is None or audio_data is None or len(f0_sequence) < 2:
        return {
            'shimmer_local' : 0.0,
            'shimmer_apq3' : 0.0,
            'shimmer_apq5' : 0.0,
            'shimmer_dda' : 0.0
        }

    if time_axis is None:
        frame_period = 5.0
        time_axis = np.arange(len(f0_sequence)) * frame_period / 1000.0

    voiced_indices = np.where(f0_sequence > 0)[0]
    if len(voiced_indices) < 5:
        return {
            'shimmer_local' : 0.0,
            'shimmer_apq3' : 0.0,
            'shimmer_apq5' : 0.0,
            'shimmer_dda' : 0.0
        }

    amplitudes = []
    for idx in voiced_indices:
        if idx < len(time_axis) - 1:
            start_time = time_axis[idx]
            end_time = time_axis[idx + 1] if idx + 1 < len(time_axis) else time_axis[idx] + 0.01
            start_sample = int(start_time * sample_rate)
            end_sample = int(end_time * sample_rate)
            if end_sample > start_sample:
                frame_amp = np.max(np.abs(audio_data[start_sample:end_sample]))
                amplitudes.append(frame_amp)

    amplitudes = np.array(amplitudes)
    if len(amplitudes) < 5:
        return {
            'shimmer_local' : 0.0,
            'shimmer_apq3' : 0.0,
            'shimmer_apq5' : 0.0,
            'shimmer_dda' : 0.0
        }

    avg_amp = np.mean(amplitudes)
    diffs = np.abs(np.diff(amplitudes))

    shimmer_local = np.mean(diffs) / avg_amp if avg_amp > 0 else 0.0

    apq3_values = []
    for i in range(1, len(amplitudes) - 1):
        sum_abs = np.abs(amplitudes[i] - amplitudes[i-1]) + np.abs(amplitudes[i+1] - amplitudes[i])
        apq3_values.append(sum_abs / (2 * avg_amp))
    shimmer_apq3 = np.mean(apq3_values) if apq3_values else 0.0

    apq5_values = []
    for i in range(2, len(amplitudes) - 2):
        sum_abs = np.abs(amplitudes[i-2] - amplitudes[i]) + np.abs(amplitudes[i-1] - amplitudes[i]) + \
                   np.abs(amplitudes[i+1] - amplitudes[i]) + np.abs(amplitudes[i+2] - amplitudes[i])
        apq5_values.append(sum_abs / (4 * avg_amp))
    shimmer_apq5 = np.mean(apq5_values) if apq5_values else 0.0

    dda_values = []
    for i in range(2, len(amplitudes) - 2):
        sum_abs = np.abs(amplitudes[i-2] - 2*amplitudes[i-1] + amplitudes[i]) + \
                   np.abs(amplitudes[i-1] - 2*amplitudes[i] + amplitudes[i+1]) + \
                   np.abs(amplitudes[i] - 2*amplitudes[i+1] + amplitudes[i+2])
        dda_values.append(sum_abs / (3 * avg_amp))
    shimmer_dda = np.mean(dda_values) if dda_values else 0.0

    return {
        'shimmer_local' : shimmer_local * 100,
        'shimmer_apq3' : shimmer_apq3 * 100,
        'shimmer_apq5' : shimmer_apq5 * 100,
        'shimmer_dda' : shimmer_dda * 100
    }

def compute_oq(ap, f0, sample_rate):
    """
    Estimate the open quotient (OQ).

    Note: pyworld's ap is a frequency-domain parameter (n_frames x n_bins) that
    represents the aperiodicity ratio of each frequency bin in each frame, not a
    time-domain signal. The original implementation mistakenly treated ap as a
    time-domain signal and computed an energy ratio sliced by sample_rate/f0,
    which has no physical meaning (it produced OQ ~= 0.002).

    Correct approach: OQ is positively correlated with breathiness (the overall
    mean of ap) - a larger OQ means the glottis stays open longer, more airflow
    leaks, and breathiness is more obvious. Here the mean ap of voiced frames is
    linearly mapped onto the typical OQ range [0.40, 0.70]:
        - ap_mean ~= 0 (clean voiced) -> OQ ~= 0.50 (neutral)
        - ap_mean ~= 0.5 (strong breathiness) -> OQ ~= 0.65 (breathy)
        - ap_mean ~= 1.0 (all noise) -> OQ ~= 0.70

    Returns:
        OQ estimate (0.40~0.70), returns 0.0 when there are no voiced frames
    """
    if ap is None or f0 is None:
        return 0.0

    voiced_mask = f0 > 0
    if not np.any(voiced_mask) or ap.size == 0:
        return 0.0

    # Take the ap of voiced frames
    ap_voiced = ap[voiced_mask] if ap.ndim >= 1 else ap

    # Compute the per-frame mean ap over the low-frequency range (first 1/4 of the bins,
    # which better reflects breathiness characteristics)
    if ap_voiced.ndim == 2:
        n_bins = ap_voiced.shape[1]
        low_bins = max(1, n_bins // 4)
        ap_low_mean = np.mean(ap_voiced[:, :low_bins])
    else:
        ap_low_mean = np.mean(ap_voiced)

    # Linear mapping onto the OQ range [0.40, 0.70]
    # ap_low_mean in [0, 1] -> OQ in [0.50, 0.70]
    oq = 0.50 + np.clip(ap_low_mean, 0.0, 1.0) * 0.20

    return float(oq)

def extract_glottal_pulse(f0, time_axis, sample_rate):
    if f0 is None or time_axis is None:
        return np.array([])

    total_samples = int(time_axis[-1] * sample_rate) + 1 if len(time_axis) > 0 else 0
    glottal_pulse = np.zeros(total_samples)

    for i in range(len(f0)):
        if f0[i] > 0:
            center_time = time_axis[i]
            center_sample = int(center_time * sample_rate)
            pulse_width = int(sample_rate / f0[i])

            start = max(0, center_sample - pulse_width // 2)
            end = min(total_samples, center_sample + pulse_width // 2)

            t = np.arange(start, end) - center_sample
            envelope = np.exp(-(t ** 2) / (2 * (pulse_width // 4) ** 2))
            glottal_pulse[start:end] += envelope * 0.5

    if np.max(np.abs(glottal_pulse)) > 0:
        glottal_pulse = glottal_pulse / np.max(np.abs(glottal_pulse))

    return glottal_pulse[:int(time_axis[-1] * sample_rate) + 1] if len(time_axis) > 0 else glottal_pulse

def compute_f0_statistics(f0_sequence):
    if f0_sequence is None:
        return {
            'avg_f0' : 0.0,
            'min_f0' : 0.0,
            'max_f0' : 0.0,
            'f0_std' : 0.0,
            'voiced_ratio' : 0.0
        }

    voiced_f0 = f0_sequence[f0_sequence > 0]
    voiced_count = len(voiced_f0)
    total_count = len(f0_sequence)

    avg_f0 = np.mean(voiced_f0) if voiced_count > 0 else 0.0
    min_f0 = np.min(voiced_f0) if voiced_count > 0 else 0.0
    max_f0 = np.max(voiced_f0) if voiced_count > 0 else 0.0
    f0_std = np.std(voiced_f0) if voiced_count > 1 else 0.0
    voiced_ratio = voiced_count / total_count if total_count > 0 else 0.0

    return {
        'avg_f0' : avg_f0,
        'min_f0' : min_f0,
        'max_f0' : max_f0,
        'f0_std' : f0_std,
        'voiced_ratio' : voiced_ratio
    }


def synthesize_pyworld(f0, sp, ap, sample_rate, frame_period=FRAME_PERIOD):
    if f0 is None or sp is None or ap is None:
        return None

    f0 = f0.astype(np.float64)
    sp = sp.astype(np.float64)
    ap = ap.astype(np.float64)

    synthesized = pw.synthesize(f0, sp, ap, sample_rate, frame_period)

    return synthesized
