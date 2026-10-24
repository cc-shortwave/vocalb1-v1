import numpy as np
from typing import Optional, Tuple


class ExcitationGenerator:
    """
    Excitation pulse train generator

    Uses a phase accumulator to generate a precise impulse train, and supports
    injecting natural perturbation characteristics
    """

    def __init__(self, sample_rate: int):
        """
        Initialize the excitation generator

        Parameters:
            sample_rate: sample rate
        """
        self.sample_rate = sample_rate
        self.phase = 0.0

    def reset(self):
        """Reset the phase accumulator"""
        self.phase = 0.0

    def generate_impulse_train(self, f0_sequence: np.ndarray, 
                              frame_period: float = 5.0) -> np.ndarray:
        """
        Generate an impulse train

        Parameters:
            f0_sequence: F0 trajectory (Hz)
            frame_period: frame period (milliseconds)

        Returns:
            impulse train
        """
        frame_period_sec = frame_period / 1000.0
        total_duration = len(f0_sequence) * frame_period_sec
        total_samples = int(total_duration * self.sample_rate)

        impulse_train = np.zeros(total_samples)
        self.phase = 0.0

        current_sample = 0

        for i, f0 in enumerate(f0_sequence):
            if f0 <= 0:
                current_sample += int(frame_period_sec * self.sample_rate)
                continue

            t0 = 1.0 / f0
            period_samples = int(t0 * self.sample_rate)

            num_periods_in_frame = int(frame_period_sec / t0)
            if num_periods_in_frame == 0:
                num_periods_in_frame = 1

            for _ in range(num_periods_in_frame):
                if current_sample < total_samples:
                    impulse_train[current_sample] = 1.0

                self.phase += 2 * np.pi
                current_sample += period_samples

        return impulse_train

    def generate_with_perturbations(self, f0_sequence: np.ndarray,
                                    jitter: float = 0.319,
                                    shimmer: float = 0.0,
                                    frame_period: float = 5.0) -> np.ndarray:
        """
        Generate an excitation signal with natural perturbations

        Parameters:
            f0_sequence: F0 trajectory (Hz)
            jitter: Jitter percentage (%), default 0.319%
            shimmer: Shimmer percentage (%), default 0%
            frame_period: frame period (milliseconds)

        Returns:
            excitation signal with perturbations
        """
        frame_period_sec = frame_period / 1000.0
        total_duration = len(f0_sequence) * frame_period_sec
        total_samples = int(total_duration * self.sample_rate)

        excitation = np.zeros(total_samples)
        self.phase = 0.0

        np.random.seed(42)

        current_sample = 0

        for i, f0 in enumerate(f0_sequence):
            if f0 <= 0:
                current_sample += int(frame_period_sec * self.sample_rate)
                continue

            t0 = 1.0 / f0
            base_period_samples = int(t0 * self.sample_rate)

            num_periods_in_frame = int(frame_period_sec / t0)
            if num_periods_in_frame == 0:
                num_periods_in_frame = 1

            for _ in range(num_periods_in_frame):
                if current_sample >= total_samples:
                    break

                jitter_noise = np.random.normal(0, jitter / 100.0)
                perturbed_period = int(base_period_samples * (1 + jitter_noise))
                perturbed_period = max(int(base_period_samples * 0.9), 
                                      min(int(base_period_samples * 1.1), perturbed_period))

                shimmer_noise = np.random.normal(0, shimmer / 100.0)
                amplitude = 1.0 + shimmer_noise
                amplitude = max(0.5, min(1.5, amplitude))

                if current_sample < total_samples:
                    excitation[current_sample] = amplitude

                self.phase += 2 * np.pi
                current_sample += perturbed_period

        return excitation


def apply_jitter(f0_sequence: np.ndarray, jitter_percent: float = 0.319, 
                 rate: float = 1.0) -> np.ndarray:
    """
    Apply Jitter perturbation to an F0 trajectory

    Parameters:
        f0_sequence: original F0 trajectory
        jitter_percent: Jitter percentage (%)
        rate: perturbation rate (default 1.0)

    Returns:
        F0 trajectory with Jitter perturbation
    """
    if len(f0_sequence) == 0:
        return np.array([])

    # Compute a pitch-dependent scaling factor
    reference_f0 = 260.0
    f0_scaling = (f0_sequence / reference_f0) ** rate

    np.random.seed(42)
    jitter_noise = np.random.normal(0, jitter_percent / 100.0, len(f0_sequence))

    perturbed_f0 = f0_sequence.copy()
    voiced_mask = f0_sequence > 0
    perturbed_f0[voiced_mask] *= (1 + jitter_noise[voiced_mask] * f0_scaling[voiced_mask])

    min_f0, max_f0 = 60, 500
    perturbed_f0 = np.clip(perturbed_f0, min_f0, max_f0)

    return perturbed_f0


def apply_shimmer(amplitude_sequence: np.ndarray, shimmer_percent: float = 0.0,
                  rate: float = 1.0) -> np.ndarray:
    """
    Apply Shimmer perturbation to an amplitude sequence

    Parameters:
        amplitude_sequence: original amplitude sequence
        shimmer_percent: Shimmer percentage (%)
        rate: perturbation rate (default 1.0)

    Returns:
        amplitude sequence with Shimmer perturbation
    """
    if len(amplitude_sequence) == 0:
        return np.array([])
    # Compute a pitch-dependent scaling factor
    reference_f0 = 260.0
    f0_scaling = (amplitude_sequence / reference_f0) ** rate

    np.random.seed(42)
    shimmer_noise = np.random.normal(0, shimmer_percent / 100.0, len(amplitude_sequence))

    perturbed_amplitude = amplitude_sequence * (1 + shimmer_noise * f0_scaling)
    perturbed_amplitude = np.clip(perturbed_amplitude, 0.1, 2.0)

    return perturbed_amplitude


def generate_white_noise(duration: float, sample_rate: int, 
                         amplitude: float = 1.0) -> np.ndarray:
    """
    Generate a white noise signal

    Parameters:
        duration: duration (seconds)
        sample_rate: sample rate
        amplitude: noise amplitude

    Returns:
        white noise signal
    """
    num_samples = int(duration * sample_rate)
    noise = np.random.randn(num_samples) * amplitude
    noise = noise / np.max(np.abs(noise)) * amplitude

    return noise


def generate_pink_noise(duration: float, sample_rate: int, 
                        amplitude: float = 1.0) -> np.ndarray:
    """
    Generate a pink noise signal (1/f noise)

    Parameters:
        duration: duration (seconds)
        sample_rate: sample rate
        amplitude: noise amplitude

    Returns:
        pink noise signal
    """
    num_samples = int(duration * sample_rate)

    white_noise = np.random.randn(num_samples)

    fft_result = np.fft.fft(white_noise)
    frequencies = np.fft.fftfreq(num_samples, 1.0 / sample_rate)

    pink_filter = 1.0 / np.sqrt(np.abs(frequencies) + 1e-10)
    pink_filter[0] = 0

    pink_fft = fft_result * pink_filter
    pink_noise = np.real(np.fft.ifft(pink_fft))

    pink_noise = pink_noise / np.max(np.abs(pink_noise)) * amplitude

    return pink_noise


def generate_excitation(f0_sequence: np.ndarray, sample_rate: int,
                        jitter: float = 0.319, shimmer: float = 0.0,
                        noise_level: float = 0.01) -> np.ndarray:
    """
    Generate the complete excitation signal (pulse train + noise)

    Parameters:
        f0_sequence: F0 trajectory (Hz)
        sample_rate: sample rate
        jitter: Jitter percentage (%)
        shimmer: Shimmer percentage (%)
        noise_level: noise level

    Returns:
        complete excitation signal
    """
    generator = ExcitationGenerator(sample_rate)
    impulse_train = generator.generate_with_perturbations(f0_sequence, jitter, shimmer)

    total_samples = len(impulse_train)
    noise = generate_pink_noise(total_samples / sample_rate, sample_rate, noise_level)

    excitation = impulse_train + noise

    excitation = excitation / np.max(np.abs(excitation))

    return excitation


def convolve_with_pulse(excitation: np.ndarray, pulse: np.ndarray) -> np.ndarray:
    """
    Convolve the excitation signal with a glottal pulse

    Parameters:
        excitation: excitation signal (impulse train)
        pulse: single-period glottal pulse

    Returns:
        convolved glottal airflow signal
    """
    if len(excitation) == 0 or len(pulse) == 0:
        return np.array([])

    result = np.convolve(excitation, pulse, mode='same')
    result = result / np.max(np.abs(result)) if np.max(np.abs(result)) > 0 else result

    return result
