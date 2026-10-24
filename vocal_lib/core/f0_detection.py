import numpy as np

from .pyworld_processor import extract_pyworld_features

MIN_F0 = 60
MAX_F0 = 500
HOP_SIZE = 512


def viterbi_smooth(f0_sequence, transition_cost=50, silence_cost=100):
    n_frames = len(f0_sequence)
    if n_frames == 0:
        return f0_sequence

    smoothed = np.copy(f0_sequence)

    consecutive_zero = 0

    for i in range(1, n_frames):
        prev_f0 = smoothed[i - 1]

        if f0_sequence[i] == 0:
            consecutive_zero += 1
            if consecutive_zero >= 3:
                smoothed[i] = 0.0
            else:
                if prev_f0 > 0:
                    smoothed[i] = prev_f0 * 0.5
        else:
            consecutive_zero = 0
            if prev_f0 > 0:
                if abs(f0_sequence[i] - prev_f0) < transition_cost:
                    smoothed[i] = 0.7 * f0_sequence[i] + 0.3 * prev_f0
                else:
                    smoothed[i] = prev_f0
            else:
                smoothed[i] = f0_sequence[i]

    return smoothed

def smooth_f0(f0_sequence, window_size=5):
    smoothed = np.copy(f0_sequence)
    pad = window_size // 2

    for i in range(len(f0_sequence)):
        start = max(0, i - pad)
        end = min(len(f0_sequence), i + pad + 1)
        window = f0_sequence[start:end]
        valid = window[window > 0]

        if len(valid) > 0:
            smoothed[i] = np.median(valid)
        else:
            smoothed[i] = 0.0

    return smoothed

def detect_f0_pyworld(audio_data, sample_rate):
    result = extract_pyworld_features(audio_data, sample_rate)
    return result['f0'], result['time_axis']

def detect_f0_sequence(audio_data, sample_rate):
    """
    Detect the F0 trajectory using pyworld (DIO + Stonemask) exclusively.

    There is no autocorrelation/cepstrum/YIN fusion fallback:
    if pyworld is unavailable or detection fails, an exception is raised directly.
    """
    f0_pyworld, time_axis = detect_f0_pyworld(audio_data, sample_rate)
    if f0_pyworld is None:
        raise RuntimeError("pyworld F0 detection failed")

    f0_sequence = smooth_f0(f0_pyworld)
    f0_sequence = viterbi_smooth(f0_sequence)

    return f0_sequence
