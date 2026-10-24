import numpy as np
from typing import Optional, List, Tuple, Dict


class VocalTractFilter:
    """
    Vocal tract filter class

    Supports time-varying digital filters built from LPC coefficients or formant trajectories
    """

    def __init__(self, sample_rate: int, lpc_order: int = 12):
        """
        Initialize the vocal tract filter

        Parameters:
            sample_rate: sample rate
            lpc_order: LPC order
        """
        self.sample_rate = sample_rate
        self.lpc_order = lpc_order
        self.buffer = np.zeros(lpc_order)

    def reset(self):
        """Reset the filter buffer"""
        self.buffer = np.zeros(self.lpc_order)

    def apply_lpc_filter(self, signal: np.ndarray, lpc_coefficients: np.ndarray) -> np.ndarray:
        """
        Apply the LPC filter

        Parameters:
            signal: input signal
            lpc_coefficients: LPC coefficient array (one coefficient vector per frame)

        Returns:
            filtered signal
        """
        if len(signal) == 0 or len(lpc_coefficients) == 0:
            return np.array([])

        output = np.zeros(len(signal))
        self.reset()

        frame_period = len(signal) // len(lpc_coefficients) if len(lpc_coefficients) > 0 else 512

        for i in range(len(signal)):
            frame_idx = min(i // frame_period, len(lpc_coefficients) - 1)
            a = lpc_coefficients[frame_idx]

            output[i] = signal[i]
            for j in range(1, min(len(a), self.lpc_order + 1)):
                if i >= j:
                    output[i] -= a[j] * output[i - j]

        return output

    def apply_formant_filter(self, signal: np.ndarray, formants: List[dict],
                            bandwidths: Optional[List[dict]] = None) -> np.ndarray:
        """
        Apply the formant filter

        Parameters:
            signal: input signal
            formants: formant frequency trajectory list (one dict per frame, containing F1, F2, F3...)
            bandwidths: formant bandwidth trajectory list (optional)

        Returns:
            filtered signal
        """
        if len(signal) == 0 or len(formants) == 0:
            return np.array([])

        output = signal.copy()

        frame_period = len(signal) // len(formants) if len(formants) > 0 else 512

        for i in range(len(formants)):
            start = i * frame_period
            end = min((i + 1) * frame_period, len(signal))

            if start >= end:
                continue

            segment = output[start:end]

            formant_freqs = []
            formant_bw = []

            for f in ['F1', 'F2', 'F3', 'F4']:
                if f in formants[i] and formants[i][f] > 0:
                    formant_freqs.append(formants[i][f])
                    if bandwidths and i < len(bandwidths) and f in bandwidths[i]:
                        formant_bw.append(bandwidths[i][f])
                    else:
                        formant_bw.append(50)

            filtered_segment = self._apply_single_formant_filter(segment, formant_freqs, formant_bw)
            output[start:end] = filtered_segment

        return output

    def _apply_single_formant_filter(self, signal: np.ndarray, 
                                     formant_freqs: List[float], 
                                     bandwidths: List[float]) -> np.ndarray:
        """
        Apply the formant filter for a single frame

        Parameters:
            signal: input signal segment
            formant_freqs: formant frequency list
            bandwidths: formant bandwidth list

        Returns:
            filtered signal segment
        """
        if len(formant_freqs) == 0:
            return signal.copy()

        b = np.array([1.0])
        a = np.array([1.0])

        for f, bw in zip(formant_freqs, bandwidths):
            if f <= 0 or f >= self.sample_rate / 2:
                continue

            omega = 2 * np.pi * f / self.sample_rate
            alpha = np.sin(omega) * np.sinh(np.log(2) / 2 * bw * 2 * np.pi / self.sample_rate)

            b0 = 1.0
            b1 = -2 * np.cos(omega)
            b2 = 1.0

            a0 = 1.0 + alpha
            a1 = -2 * np.cos(omega)
            a2 = 1.0 - alpha

            b = np.convolve(b, [b0, b1, b2])
            a = np.convolve(a, [a0, a1, a2])

        a = a / a[0]
        b = b / a[0]

        filtered = np.zeros(len(signal))
        max_order = max(len(a), len(b)) - 1
        delay_line = np.zeros(max_order)

        for i in range(len(signal)):
            w = signal[i]
            for j in range(1, len(a)):
                if j - 1 < len(delay_line):
                    w -= a[j] * delay_line[j - 1]

            y = 0.0
            for j in range(len(b)):
                if j == 0:
                    y += b[j] * w
                elif j - 1 < len(delay_line):
                    y += b[j] * delay_line[j - 1]

            filtered[i] = y

            for j in range(len(delay_line) - 1, 0, -1):
                delay_line[j] = delay_line[j - 1]
            if len(delay_line) > 0:
                delay_line[0] = w

        return filtered

    def get_impulse_response(self, formants: List[dict],
                             bandwidths: Optional[List[dict]] = None,
                             length: int = 4096) -> np.ndarray:
        """
        Get the impulse response of the formant filter

        Parameters:
            formants: formant frequency trajectory list
            bandwidths: formant bandwidth trajectory list (optional)
            length: impulse response length (in samples)

        Returns:
            impulse response array
        """
        impulse = np.zeros(length)
        impulse[0] = 1.0
        return self.apply_formant_filter(impulse, formants, bandwidths)

    def get_lpc_impulse_response(self, lpc_coefficients: np.ndarray,
                                 length: int = 4096) -> np.ndarray:
        """
        Get the impulse response of the LPC filter

        Parameters:
            lpc_coefficients: LPC coefficient array
            length: impulse response length

        Returns:
            impulse response array
        """
        impulse = np.zeros(length)
        impulse[0] = 1.0
        return self.apply_lpc_filter(impulse, lpc_coefficients)

    def get_frequency_response(self, formants: List[dict],
                               bandwidths: Optional[List[dict]] = None,
                               n_fft: int = 4096) -> Tuple[np.ndarray, np.ndarray]:
        """
        Get the frequency response of the formant filter

        Parameters:
            formants: formant frequency trajectory list
            bandwidths: formant bandwidth trajectory list (optional)
            n_fft: number of FFT points

        Returns:
            (frequencies, magnitude_db) frequency axis and magnitude (dB)
        """
        impulse = self.get_impulse_response(formants, bandwidths, n_fft)
        spectrum = np.fft.rfft(impulse, n=n_fft)
        magnitude = np.abs(spectrum)
        magnitude_db = 20 * np.log10(magnitude + 1e-10)
        frequencies = np.fft.rfftfreq(n_fft, 1.0 / self.sample_rate)
        return frequencies, magnitude_db

    def get_lpc_frequency_response(self, lpc_coefficients: np.ndarray,
                                   n_fft: int = 4096) -> Tuple[np.ndarray, np.ndarray]:
        """
        Get the frequency response of the LPC filter

        Parameters:
            lpc_coefficients: LPC coefficient array
            n_fft: number of FFT points

        Returns:
            (frequencies, magnitude_db) frequency axis and magnitude (dB)
        """
        impulse = self.get_lpc_impulse_response(lpc_coefficients, n_fft)
        spectrum = np.fft.rfft(impulse, n=n_fft)
        magnitude = np.abs(spectrum)
        magnitude_db = 20 * np.log10(magnitude + 1e-10)
        frequencies = np.fft.rfftfreq(n_fft, 1.0 / self.sample_rate)
        return frequencies, magnitude_db

    def get_filter_coefficients(self, formants: List[dict],
                                bandwidths: Optional[List[dict]] = None,
                                frame_index: int = 0) -> Tuple[np.ndarray, np.ndarray]:
        """
        Get the filter coefficients (b, a) of the specified frame

        Parameters:
            formants: formant frequency trajectory list
            bandwidths: formant bandwidth trajectory list (optional)
            frame_index: frame index

        Returns:
            (b, a) numerator and denominator coefficients
        """
        if frame_index >= len(formants):
            return np.array([1.0]), np.array([1.0])

        formant_dict = formants[frame_index]
        bw_dict = bandwidths[frame_index] if bandwidths and frame_index < len(bandwidths) else None

        formant_freqs = []
        formant_bw = []

        for f in ['F1', 'F2', 'F3', 'F4']:
            if f in formant_dict and formant_dict[f] > 0:
                formant_freqs.append(formant_dict[f])
                if bw_dict and f in bw_dict:
                    formant_bw.append(bw_dict[f])
                else:
                    formant_bw.append(50)

        b = np.array([1.0])
        a = np.array([1.0])

        for freq, bw in zip(formant_freqs, formant_bw):
            if freq <= 0 or freq >= self.sample_rate / 2:
                continue
            omega = 2 * np.pi * freq / self.sample_rate
            alpha = np.sin(omega) * np.sinh(np.log(2) / 2 * bw * 2 * np.pi / self.sample_rate)

            b = np.convolve(b, [1.0, -2 * np.cos(omega), 1.0])
            a = np.convolve(a, [1.0 + alpha, -2 * np.cos(omega), 1.0 - alpha])

        a = a / a[0]
        b = b / a[0] if a[0] != 0 else b

        return b, a


def compute_lpc_coefficients(signal: np.ndarray, sample_rate: int, 
                              frame_size: int = 512, hop_size: int = 256,
                              lpc_order: int = 12) -> np.ndarray:
    """
    Compute LPC coefficients

    Parameters:
        signal: input signal
        sample_rate: sample rate
        frame_size: frame size
        hop_size: hop size
        lpc_order: LPC order

    Returns:
        LPC coefficient array (one row of coefficients per frame)
    """
    n_frames = int(np.ceil((len(signal) - frame_size) / hop_size)) + 1
    lpc_coeffs = np.zeros((n_frames, lpc_order + 1))

    window = np.hanning(frame_size)

    for i in range(n_frames):
        start = i * hop_size
        end = min(start + frame_size, len(signal))

        frame = signal[start:end]
        if len(frame) < frame_size:
            frame = np.pad(frame, (0, frame_size - len(frame)))

        frame = frame * window

        autocorr = np.correlate(frame, frame, mode='full')
        autocorr = autocorr[len(autocorr) // 2:len(autocorr) // 2 + lpc_order + 1]

        a = np.zeros(lpc_order + 1)
        a[0] = 1.0
        e = autocorr[0]

        for k in range(1, lpc_order + 1):
            sum_val = np.sum(a[1:k] * autocorr[k-1:0:-1])
            alpha = (autocorr[k] + sum_val) / e
            a[1:k+1] -= alpha * a[k-1::-1]
            e *= (1 - alpha * alpha)

        lpc_coeffs[i] = a

    return lpc_coeffs


def lpc_to_formants(lpc_coefficients: np.ndarray, sample_rate: int) -> List[dict]:
    """
    Extract formants from LPC coefficients

    Parameters:
        lpc_coefficients: LPC coefficient array
        sample_rate: sample rate

    Returns:
        formant frequency list (one dict per frame)
    """
    formants_list = []

    for a in lpc_coefficients:
        roots = np.roots(a)

        formants = {}
        f_count = 1

        for root in roots:
            if np.imag(root) > 0:
                angle = np.arctan2(np.imag(root), np.real(root))
                freq = angle * sample_rate / (2 * np.pi)
                bw = -np.log(np.abs(root)) * sample_rate / np.pi

                if freq > 50 and freq < sample_rate / 2 and bw < 1000:
                    formants[f'F{f_count}'] = freq
                    f_count += 1
                    if f_count > 5:
                        break

        formants_list.append(formants)

    return formants_list


def apply_preemphasis(signal: np.ndarray, alpha: float = 0.97) -> np.ndarray:
    """
    Apply pre-emphasis filtering

    Parameters:
        signal: input signal
        alpha: pre-emphasis coefficient

    Returns:
        pre-emphasized signal
    """
    if len(signal) < 2:
        return signal.copy()

    output = np.zeros(len(signal))
    output[0] = signal[0]

    for i in range(1, len(signal)):
        output[i] = signal[i] - alpha * signal[i - 1]

    return output


def apply_deemphasis(signal: np.ndarray, alpha: float = 0.97) -> np.ndarray:
    """
    Apply de-emphasis filtering

    Parameters:
        signal: input signal
        alpha: de-emphasis coefficient

    Returns:
        de-emphasized signal
    """
    if len(signal) < 2:
        return signal.copy()

    output = np.zeros(len(signal))
    output[0] = signal[0]

    for i in range(1, len(signal)):
        output[i] = signal[i] + alpha * output[i - 1]

    return output


def synthesize_formant_filtered_voice(source_signal: np.ndarray, sample_rate: int,
                                       formants_trajectory: List[dict]) -> np.ndarray:
    """
    Synthesize speech using a formant trajectory

    Parameters:
        source_signal: glottal source signal
        sample_rate: sample rate
        formants_trajectory: formant trajectory list

    Returns:
        filtered speech signal
    """
    if len(source_signal) == 0 or len(formants_trajectory) == 0:
        return np.array([])

    source_preemph = apply_preemphasis(source_signal)

    vtf = VocalTractFilter(sample_rate)
    filtered = vtf.apply_formant_filter(source_preemph, formants_trajectory)

    voice = apply_deemphasis(filtered)

    voice = voice / np.max(np.abs(voice)) if np.max(np.abs(voice)) > 0 else voice

    return voice


def synthesize_lpc_filtered_voice(source_signal: np.ndarray, sample_rate: int,
                                   lpc_coefficients: np.ndarray) -> np.ndarray:
    """
    Synthesize speech using LPC coefficients

    Parameters:
        source_signal: glottal source signal
        sample_rate: sample rate
        lpc_coefficients: LPC coefficient array

    Returns:
        filtered speech signal
    """
    if len(source_signal) == 0 or len(lpc_coefficients) == 0:
        return np.array([])

    source_preemph = apply_preemphasis(source_signal)

    vtf = VocalTractFilter(sample_rate, lpc_order=lpc_coefficients.shape[1] - 1)
    filtered = vtf.apply_lpc_filter(source_preemph, lpc_coefficients)

    voice = apply_deemphasis(filtered)

    voice = voice / np.max(np.abs(voice)) if np.max(np.abs(voice)) > 0 else voice

    return voice
