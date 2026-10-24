"""
vocal_lib.decomposition - vocal decomposition module

Calls the core functions in vocal_lib.engine,
and wraps a WAV input into a (F0, breath WAV, glottal excitation) tuple.
"""

import os
import numpy as np
from typing import Tuple, Union

from .engine import decompose_vocal as _engine_decompose
from .data_classes import F0, WAVAudio, GlottalExcitation


def _normalize_input(input_audio: Union[str, WAVAudio, np.ndarray],
                     sample_rate: int) -> WAVAudio:
    """Normalize the input to WAVAudio"""
    if isinstance(input_audio, WAVAudio):
        return input_audio
    if isinstance(input_audio, str):
        if not os.path.isfile(input_audio):
            raise FileNotFoundError(f"Audio file not found: {input_audio}")
        return WAVAudio.from_file(input_audio, mono=True)
    if isinstance(input_audio, np.ndarray):
        return WAVAudio(data=input_audio, sample_rate=sample_rate)
    raise TypeError(f"Unsupported input_audio type: {type(input_audio).__name__}")


def decompose_vocal(
    input_audio: Union[str, WAVAudio, np.ndarray],
    sample_rate: int = 44100,
    frame_period: float = 5.0,
) -> Tuple[F0, WAVAudio, GlottalExcitation]:
    """
    Vocal decomposition: takes a WAV input and returns (F0, breath WAV, glottal excitation)

    Parameters:
        input_audio: input audio, accepts str / WAVAudio / np.ndarray
        sample_rate: sample rate used when input_audio is an np.ndarray
        frame_period: F0 analysis frame period (milliseconds)

    Returns:
        (F0, WAVAudio_breath, GlottalExcitation) tuple
    """
    audio = _normalize_input(input_audio, sample_rate)
    data_mono = audio.to_mono().data
    sr = audio.sample_rate

    result = _engine_decompose(data_mono, sr)

    # Wrap F0 (pyworld is mandatory, no fallback)
    f0_values = result['f0_pyworld']
    time_axis = result.get('time_axis')
    voiced_pyworld = result['voiced_flags_pyworld']

    f0_obj = F0(
        values=f0_values,
        time_axis=time_axis,
        frame_period=frame_period,
        sample_rate=sr,
        voiced_flags=voiced_pyworld,
    )

    # Wrap breath
    breath_wav = WAVAudio(data=result['breath'], sample_rate=sr)

    # Wrap glottal excitation
    extra_params = {}
    if result.get('jitter'):
        extra_params['jitter'] = result['jitter']
    if result.get('shimmer'):
        extra_params['shimmer'] = result['shimmer']
    if result.get('formants'):
        extra_params['formants'] = result['formants']
    if result.get('f0_stats'):
        extra_params['f0_stats'] = result['f0_stats']
    if result.get('glottal_pulse') is not None:
        extra_params['glottal_pulse'] = result['glottal_pulse']
    if result.get('formant_intensity') is not None:
        extra_params['formant_intensity'] = result['formant_intensity']
    if result.get('breath_intensity') is not None:
        extra_params['breath_intensity'] = result['breath_intensity']

    glottal_obj = GlottalExcitation(
        signal=result['glottal'],
        sample_rate=sr,
        sp=result.get('sp'),
        ap=result.get('ap'),
        oq=result.get('oq', 0.6),
        glottal_intensity=result.get('glottal_intensity'),
        parameters=extra_params,
    )

    return f0_obj, breath_wav, glottal_obj
