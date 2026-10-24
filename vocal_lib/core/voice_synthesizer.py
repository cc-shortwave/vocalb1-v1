import numpy as np
import soundfile as sf
from typing import Optional, List, Dict

from .glottal_models import LFModel, RosenbergModel, GlottalPulseGenerator, differentiate_glottal_flow
from .excitation_generator import ExcitationGenerator, generate_excitation, convolve_with_pulse
from .vocal_tract_filter import (
    VocalTractFilter, compute_lpc_coefficients, lpc_to_formants,
    apply_preemphasis, apply_deemphasis, synthesize_formant_filtered_voice
)

from .pyworld_processor import synthesize_pyworld


class VoiceSynthesizer:
    """
    Core speech synthesis class

    Reconstructs natural continuous speech from vocal fold parameters
    """

    def __init__(self, sample_rate: int = 44100, model_type: str = 'lf'):
        """
        Initialize the speech synthesizer

        Parameters:
            sample_rate: sample rate
            model_type: glottal model type ('lf' or 'rosenberg')
        """
        self.sample_rate = sample_rate
        self.model_type = model_type
        self.pulse_generator = GlottalPulseGenerator(model_type)
        self.excitation_generator = ExcitationGenerator(sample_rate)

    def synthesize_from_parameters(self, f0_sequence: np.ndarray,
                                   jitter: float = 0.319,
                                   shimmer: float = 0.0,
                                   glottal_parameters: dict = None,
                                   formants_trajectory: List[Dict] = None,
                                   sp: np.ndarray = None,
                                   ap: np.ndarray = None,
                                   target_rms: float = None,
                                   lf_trajectory: List[Dict] = None) -> np.ndarray:
        """
        Synthesize speech from parameters

        Parameters:
            f0_sequence: F0 trajectory curve (Hz)
            jitter: Jitter percentage (%), default 0.319%
            shimmer: Shimmer percentage (%), default 0%
            glottal_parameters: glottal parameter dict (optional)
            formants_trajectory: formant trajectory list (optional)
            sp: pyworld spectral envelope (optional, used preferentially)
            ap: pyworld aperiodicity parameter (optional)
            target_rms: target RMS value (optional, used to match the original audio volume)
            lf_trajectory: per-frame LF parameter trajectory list (optional, supports smooth transitions)

        Returns:
            synthesized speech signal
        """
        if len(f0_sequence) == 0:
            return np.array([])

        if sp is not None and ap is not None:
            return self._synthesize_with_pyworld(f0_sequence, sp, ap, target_rms)

        if lf_trajectory and len(lf_trajectory) > 0:
            u_g = self._synthesize_with_lf_trajectory(f0_sequence, lf_trajectory, jitter, shimmer)
        else:
            if glottal_parameters:
                if self.model_type == 'lf':
                    self.pulse_generator.model = LFModel(**glottal_parameters)
                else:
                    alpha = glottal_parameters.get('alpha', 0.25)
                    epsilon = glottal_parameters.get('epsilon', 0.7)
                    self.pulse_generator.model = RosenbergModel(alpha, epsilon)

            excitation = self.excitation_generator.generate_with_perturbations(
                f0_sequence, jitter, shimmer
            )

            avg_f0 = np.mean(f0_sequence[f0_sequence > 0]) if np.any(f0_sequence > 0) else 100
            t0 = 1.0 / avg_f0
            pulse = self.pulse_generator.generate_pulse(t0, self.sample_rate)

            u_g = convolve_with_pulse(excitation, pulse)

        source = differentiate_glottal_flow(u_g)

        if formants_trajectory and len(formants_trajectory) > 0:
            voice = synthesize_formant_filtered_voice(source, self.sample_rate, formants_trajectory)
        else:
            voice = source

        return voice

    def _synthesize_with_lf_trajectory(self, f0_sequence: np.ndarray, lf_trajectory: List[Dict],
                                       jitter: float = 0.319, shimmer: float = 0.0) -> np.ndarray:
        """
        Synthesize the glottal airflow signal using a per-frame LF parameter trajectory

        Parameters:
            f0_sequence: F0 trajectory curve (Hz)
            lf_trajectory: per-frame LF parameter trajectory list
            jitter: Jitter percentage (%)
            shimmer: Shimmer percentage (%)

        Returns:
            synthesized glottal airflow signal
        """
        frame_period = 5.0
        frame_period_sec = frame_period / 1000.0
        total_duration = len(f0_sequence) * frame_period_sec
        total_samples = int(total_duration * self.sample_rate)

        u_g = np.zeros(total_samples)
        current_sample = 0

        np.random.seed(42)

        for i, f0 in enumerate(f0_sequence):
            if f0 <= 0:
                current_sample += int(frame_period_sec * self.sample_rate)
                continue

            lf_params = lf_trajectory[i] if i < len(lf_trajectory) else lf_trajectory[-1]
            rd = lf_params.get('rd', 0.3)
            ta = lf_params.get('ta', 0.2)
            tb = lf_params.get('tb', 0.4)
            te = lf_params.get('te', 1.0)

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

                model = LFModel(rd=rd, ta=ta, tb=tb, te=te)
                pulse_samples = min(perturbed_period, total_samples - current_sample)
                pulse = model.generate_pulse(pulse_samples / self.sample_rate, self.sample_rate)
                pulse = pulse[:pulse_samples] * amplitude

                u_g[current_sample:current_sample + pulse_samples] += pulse

                current_sample += perturbed_period

        if np.max(np.abs(u_g)) > 0:
            u_g = u_g / np.max(np.abs(u_g))

        return u_g

    def _synthesize_with_pyworld(self, f0_sequence: np.ndarray, sp: np.ndarray, ap: np.ndarray,
                                target_rms: float = None) -> np.ndarray:
        """
        High-quality parametric synthesis using pyworld

        Parameters:
            f0_sequence: F0 trajectory
            sp: spectral envelope
            ap: aperiodicity parameter
            target_rms: target RMS value (optional, used to match the original audio volume)

        Returns:
            synthesized speech signal
        """
        synthesized = synthesize_pyworld(f0_sequence, sp, ap, self.sample_rate)

        if synthesized is None or len(synthesized) == 0:
            return np.array([])

        # 1. RMS matching or peak normalization (first bring it up to the target volume)
        if target_rms is not None and target_rms > 0:
            current_rms = np.sqrt(np.mean(synthesized ** 2))
            if current_rms > 0:
                synthesized = synthesized * (target_rms / current_rms)
        else:
            peak = np.max(np.abs(synthesized))
            if peak > 0:
                synthesized = synthesized / peak * 0.9

        # 2. tanh soft clip for peaks above the threshold
        # Compared with hard limiting (/ max * 0.9), soft clip only compresses the part
        # above the threshold, without pulling down the whole signal and raising the
        # relative noise floor
        threshold = 0.85
        over_mask = np.abs(synthesized) > threshold
        if np.any(over_mask):
            sign = np.sign(synthesized[over_mask])
            compressed = threshold + (1.0 - threshold) * np.tanh(
                (np.abs(synthesized[over_mask]) - threshold) / (1.0 - threshold)
            )
            synthesized[over_mask] = sign * compressed

        return synthesized

    def synthesize_from_glottal_signal(self, glottal_signal: np.ndarray,
                                       f0_sequence: np.ndarray = None,
                                       jitter: float = 0.319,
                                       shimmer: float = 0.0,
                                       formants_trajectory: List[Dict] = None) -> np.ndarray:
        """
        Synthesize speech from a glottal signal

        Parameters:
            glottal_signal: glottal signal
            f0_sequence: F0 trajectory (optional)
            jitter: Jitter percentage (%)
            shimmer: Shimmer percentage (%)
            formants_trajectory: formant trajectory list (optional)

        Returns:
            synthesized speech signal
        """
        if len(glottal_signal) == 0:
            return np.array([])

        source = differentiate_glottal_flow(glottal_signal)

        if formants_trajectory and len(formants_trajectory) > 0:
            voice = synthesize_formant_filtered_voice(source, self.sample_rate, formants_trajectory)
        else:
            voice = source

        return voice

    def synthesize_from_decomposition(self, decomposition_result: dict) -> np.ndarray:
        """
        Synthesize speech from a decomposition result

        Parameters:
            decomposition_result: decomposition result dict

        Returns:
            synthesized speech signal
        """
        glottal = decomposition_result.get('glottal', np.array([]))
        f0 = decomposition_result.get('f0', np.array([]))
        formants = decomposition_result.get('formants', [])

        jitter = 0.319
        shimmer = 0.0

        if 'jitter' in decomposition_result and decomposition_result['jitter']:
            jitter = decomposition_result['jitter'].get('jitter_local', 0.319)

        if 'shimmer' in decomposition_result and decomposition_result['shimmer']:
            shimmer = decomposition_result['shimmer'].get('shimmer_local', 0.0)

        formants_trajectory = []
        if formants and len(formants) > 0:
            for frame_formants in formants:
                ft = {}
                for i, f in enumerate(frame_formants):
                    ft[f'F{i+1}'] = f.get('frequency', 0)
                formants_trajectory.append(ft)

        return self.synthesize_from_glottal_signal(
            glottal, f0, jitter, shimmer, formants_trajectory
        )

    def export_audio(self, signal: np.ndarray, file_path: str, 
                     bit_depth: int = 24, target_sample_rate: int = 48000) -> None:
        """
        Export a high-fidelity audio file

        Parameters:
            signal: speech signal
            file_path: output file path
            bit_depth: bit depth (8, 16, 24)
            target_sample_rate: target sample rate
        """
        if len(signal) == 0:
            return

        if target_sample_rate != self.sample_rate:
            signal_resampled = self._resample(signal, self.sample_rate, target_sample_rate)
        else:
            signal_resampled = signal

        max_val = np.max(np.abs(signal_resampled))
        if max_val > 1.0:
            signal_resampled = signal_resampled / max_val

        if bit_depth == 24:
            sf.write(file_path, signal_resampled, target_sample_rate, subtype='PCM_24')
        elif bit_depth == 16:
            sf.write(file_path, signal_resampled, target_sample_rate, subtype='PCM_16')
        elif bit_depth == 8:
            sf.write(file_path, signal_resampled, target_sample_rate, subtype='PCM_U8')
        else:
            sf.write(file_path, signal_resampled.astype(np.float32), target_sample_rate)

    def _resample(self, signal: np.ndarray, src_rate: int, dst_rate: int) -> np.ndarray:
        """
        Simple resampling

        Parameters:
            signal: input signal
            src_rate: source sample rate
            dst_rate: target sample rate

        Returns:
            resampled signal
        """
        if src_rate == dst_rate:
            return signal.copy()

        ratio = dst_rate / src_rate
        output_length = int(len(signal) * ratio)

        output = np.zeros(output_length)

        for i in range(output_length):
            src_idx = i / ratio
            idx_floor = int(np.floor(src_idx))
            idx_ceil = min(idx_floor + 1, len(signal) - 1)

            if idx_floor >= len(signal) - 1:
                output[i] = signal[-1]
            else:
                frac = src_idx - idx_floor
                output[i] = signal[idx_floor] * (1 - frac) + signal[idx_ceil] * frac

        return output


def synthesize_voice(f0_sequence: np.ndarray, sample_rate: int = 44100,
                     jitter: float = 0.319, shimmer: float = 0.0,
                     formants_trajectory: List[Dict] = None,
                     model_type: str = 'lf') -> np.ndarray:
    """
    Convenience function: synthesize speech from an F0 trajectory

    Parameters:
        f0_sequence: F0 trajectory curve (Hz)
        sample_rate: sample rate
        jitter: Jitter percentage (%)
        shimmer: Shimmer percentage (%)
        formants_trajectory: formant trajectory list (optional)
        model_type: glottal model type

    Returns:
        synthesized speech signal
    """
    synthesizer = VoiceSynthesizer(sample_rate, model_type)
    return synthesizer.synthesize_from_parameters(
        f0_sequence, jitter, shimmer, formants_trajectory=formants_trajectory
    )


def synthesize_voice_from_glottal(glottal_signal: np.ndarray, sample_rate: int = 44100,
                                   f0_sequence: np.ndarray = None,
                                   jitter: float = 0.319, shimmer: float = 0.0,
                                   formants_trajectory: List[Dict] = None) -> np.ndarray:
    """
    Convenience function: synthesize speech from a glottal signal

    Parameters:
        glottal_signal: glottal signal
        sample_rate: sample rate
        f0_sequence: F0 trajectory (optional)
        jitter: Jitter percentage (%)
        shimmer: Shimmer percentage (%)
        formants_trajectory: formant trajectory list (optional)

    Returns:
        synthesized speech signal
    """
    synthesizer = VoiceSynthesizer(sample_rate)
    return synthesizer.synthesize_from_glottal_signal(
        glottal_signal, f0_sequence, jitter, shimmer, formants_trajectory
    )


def final_audio_processing(voice_signal: np.ndarray, sample_rate: int,
                           normalize: bool = True,
                           preemphasis: bool = True,
                           alpha: float = 0.97) -> np.ndarray:
    """
    Final audio post-processing

    Parameters:
        voice_signal: speech signal
        sample_rate: sample rate
        normalize: whether to normalize the volume
        preemphasis: whether to apply pre-emphasis
        alpha: pre-emphasis coefficient

    Returns:
        processed speech signal
    """
    if len(voice_signal) == 0:
        return np.array([])

    signal = voice_signal.copy()

    if preemphasis:
        signal = apply_preemphasis(signal, alpha)

    if normalize:
        max_val = np.max(np.abs(signal))
        if max_val > 0:
            signal = signal / max_val * 0.9

    return signal
