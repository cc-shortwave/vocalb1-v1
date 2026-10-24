import numpy as np
from typing import Optional, Tuple


class LFModel:
    """
    LF glottal model (Liljencrants-Fant model)

    The LF model is a physically plausible glottal airflow model consisting of four phases:
    1. Closing phase (ab): the vocal folds start from full closure
    2. Opening phase (bc): the vocal folds open and airflow rises to its peak
    3. Deceleration phase (cd): airflow starts to decrease from the peak
    4. Return phase (de): the vocal folds close and airflow returns to zero
    """

    def __init__(self, rd: float = 0.3, ta: float = 0.2, tb: float = 0.4, te: float = 1.0):
        """
        Initialize LF model parameters

        Parameters:
            rd: deceleration coefficient (0.0-1.0), controls the slope of the cd phase
            ta: normalized rise time (0.0-1.0)
            tb: normalized peak time (0.0-1.0)
            te: normalized total period (usually 1.0)
        """
        self.rd = rd
        self.ta = ta
        self.tb = tb
        self.te = te

    def generate_pulse(self, t0: float, sample_rate: int) -> np.ndarray:
        """
        Generate a single-period LF model glottal waveform

        Parameters:
            t0: fundamental period (seconds)
            sample_rate: sample rate

        Returns:
            single-period glottal pulse waveform
        """
        num_samples = int(t0 * sample_rate)
        if num_samples < 10:
            num_samples = 10

        t = np.linspace(0, t0, num_samples, endpoint=False)
        normalized_t = t / t0

        u = np.zeros(num_samples)

        for i in range(num_samples):
            tn = normalized_t[i]

            if tn < self.ta:
                u[i] = self._phase_ab(tn)
            elif tn < self.tb:
                u[i] = self._phase_bc(tn)
            elif tn < self.te:
                u[i] = self._phase_cd(tn)
            else:
                u[i] = self._phase_de(tn)

        u = u / np.max(np.abs(u)) if np.max(np.abs(u)) > 0 else u

        return u

    def _phase_ab(self, tn: float) -> float:
        """Closing phase: t from 0 to ta"""
        return 0.0

    def _phase_bc(self, tn: float) -> float:
        """Opening phase: t from ta to tb"""
        t1 = (tn - self.ta) / (self.tb - self.ta + 1e-10)
        return np.sin(np.pi * t1)

    def _phase_cd(self, tn: float) -> float:
        """Deceleration phase: t from tb to te"""
        t2 = (tn - self.tb) / (self.te - self.tb + 1e-10)
        return np.cos(np.pi * t2 / 2) ** 2 * (1 - self.rd * t2)

    def _phase_de(self, tn: float) -> float:
        """Return phase: t from te to 1"""
        return 0.0

    def generate_continuous(self, f0_sequence: np.ndarray, sample_rate: int) -> np.ndarray:
        """
        Generate a continuous glottal airflow signal from an F0 trajectory

        Parameters:
            f0_sequence: F0 trajectory (Hz)
            sample_rate: sample rate

        Returns:
            continuous glottal airflow signal
        """
        total_samples = 0
        pulses = []

        for f0 in f0_sequence:
            if f0 > 0:
                t0 = 1.0 / f0
                pulse = self.generate_pulse(t0, sample_rate)
                pulses.append(pulse)
                total_samples += len(pulse)

        if not pulses:
            return np.array([])

        signal = np.zeros(total_samples)
        current_pos = 0
        for pulse in pulses:
            signal[current_pos:current_pos + len(pulse)] = pulse
            current_pos += len(pulse)

        return signal


class RosenbergModel:
    """
    Rosenberg glottal pulse model

    The classic Rosenberg model consists of two polynomials:
    - rising part: quadratic polynomial
    - falling part: cubic polynomial
    """

    def __init__(self, alpha: float = 0.25, epsilon: float = 0.7):
        """
        Initialize Rosenberg model parameters

        Parameters:
            alpha: normalized rise time (0.0-1.0), controls the pulse rise time
            epsilon: normalized peak position (0.0-1.0), controls when the peak occurs
        """
        self.alpha = alpha
        self.epsilon = epsilon

    def generate_pulse(self, t0: float, sample_rate: int) -> np.ndarray:
        """
        Generate a single-period Rosenberg glottal pulse

        Parameters:
            t0: fundamental period (seconds)
            sample_rate: sample rate

        Returns:
            single-period glottal pulse waveform
        """
        num_samples = int(t0 * sample_rate)
        if num_samples < 10:
            num_samples = 10

        t = np.linspace(0, t0, num_samples, endpoint=False)
        normalized_t = t / t0

        u = np.zeros(num_samples)

        for i in range(num_samples):
            tn = normalized_t[i]

            if tn < self.epsilon:
                u[i] = self._rising_part(tn)
            else:
                u[i] = self._falling_part(tn)

        u = u / np.max(np.abs(u)) if np.max(np.abs(u)) > 0 else u

        return u

    def _rising_part(self, tn: float) -> float:
        """Rising part: quadratic polynomial"""
        return (2 * tn / self.epsilon) - (tn / self.epsilon) ** 2

    def _falling_part(self, tn: float) -> float:
        """Falling part: cubic polynomial"""
        t_prime = (tn - self.epsilon) / (1 - self.epsilon + 1e-10)
        return (1 - self.alpha) * (1 - 3 * t_prime ** 2 + 2 * t_prime ** 3) + \
               self.alpha * (1 - t_prime)

    def generate_continuous(self, f0_sequence: np.ndarray, sample_rate: int) -> np.ndarray:
        """
        Generate a continuous glottal airflow signal from an F0 trajectory

        Parameters:
            f0_sequence: F0 trajectory (Hz)
            sample_rate: sample rate

        Returns:
            continuous glottal airflow signal
        """
        total_samples = 0
        pulses = []

        for f0 in f0_sequence:
            if f0 > 0:
                t0 = 1.0 / f0
                pulse = self.generate_pulse(t0, sample_rate)
                pulses.append(pulse)
                total_samples += len(pulse)

        if not pulses:
            return np.array([])

        signal = np.zeros(total_samples)
        current_pos = 0
        for pulse in pulses:
            signal[current_pos:current_pos + len(pulse)] = pulse
            current_pos += len(pulse)

        return signal


class GlottalPulseGenerator:
    """
    Glottal pulse generator, a unified interface that dispatches to different glottal models
    """

    def __init__(self, model_type: str = 'lf', **kwargs):
        """
        Initialize the glottal pulse generator

        Parameters:
            model_type: model type, 'lf' or 'rosenberg'
            **kwargs: model parameters
        """
        if model_type.lower() == 'lf':
            self.model = LFModel(**kwargs)
        elif model_type.lower() == 'rosenberg':
            self.model = RosenbergModel(**kwargs)
        else:
            raise ValueError(f"Unknown model type: {model_type}")

    def generate_pulse(self, t0: float, sample_rate: int) -> np.ndarray:
        """
        Generate a single-period glottal pulse

        Parameters:
            t0: fundamental period (seconds)
            sample_rate: sample rate

        Returns:
            single-period glottal pulse waveform
        """
        return self.model.generate_pulse(t0, sample_rate)

    def generate_continuous(self, f0_sequence: np.ndarray, sample_rate: int) -> np.ndarray:
        """
        Generate a continuous glottal airflow signal from an F0 trajectory

        Parameters:
            f0_sequence: F0 trajectory (Hz)
            sample_rate: sample rate

        Returns:
            continuous glottal airflow signal
        """
        return self.model.generate_continuous(f0_sequence, sample_rate)


def differentiate_glottal_flow(u_g: np.ndarray) -> np.ndarray:
    """
    Differentiate the glottal airflow signal to obtain the glottal source signal

    Principle: Source(t) = dU_g(t)/dt

    Parameters:
        u_g: glottal airflow signal

    Returns:
        glottal source signal (the differentiated signal)
    """
    if len(u_g) < 2:
        return np.array([])

    source = np.diff(u_g)
    source = np.concatenate([[0], source])

    source = source / np.max(np.abs(source)) if np.max(np.abs(source)) > 0 else source

    return source


def compute_glottal_parameters(glottal_signal: np.ndarray, sample_rate: int) -> dict:
    """
    Extract LF model parameters from a glottal signal

    Parameters:
        glottal_signal: glottal signal
        sample_rate: sample rate

    Returns:
        dictionary of LF model parameters
    """
    if len(glottal_signal) < 100:
        return {
            'rd': 0.3,
            'ta': 0.2,
            'tb': 0.4,
            'te': 1.0,
            'peak_amplitude': 0.0,
            'rise_time': 0.0,
            'fall_time': 0.0
        }

    peak_idx = np.argmax(glottal_signal)
    peak_amplitude = glottal_signal[peak_idx]

    zero_crossings = np.where(np.diff(np.sign(glottal_signal)))[0]

    rise_time = 0.0
    fall_time = 0.0

    if len(zero_crossings) >= 2:
        start_idx = zero_crossings[0]
        end_idx = zero_crossings[-1]

        rise_time = (peak_idx - start_idx) / sample_rate
        fall_time = (end_idx - peak_idx) / sample_rate

    t0 = (zero_crossings[-1] - zero_crossings[0]) / sample_rate if len(zero_crossings) >= 2 else 0.01

    ta = rise_time / (t0 + 1e-10)
    tb = (peak_idx - zero_crossings[0]) / (t0 * sample_rate + 1e-10) if len(zero_crossings) > 0 else 0.4

    return {
        'rd': 0.3,
        'ta': np.clip(ta, 0.05, 0.5),
        'tb': np.clip(tb, 0.1, 0.9),
        'te': 1.0,
        'peak_amplitude': peak_amplitude,
        'rise_time': rise_time,
        'fall_time': fall_time
    }
