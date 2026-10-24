"""
vocal_lib.synthesis - vocal synthesis module

Calls the synthesis functions in vocal_lib.engine and wraps
(F0, breath WAV, glottal excitation) into a synthesized WAVAudio result.
"""

import os
import numpy as np
from typing import List, Dict, Optional

from .engine import synthesize_voice_from_parameters
from .data_classes import F0, WAVAudio, GlottalExcitation


def synthesize_vocal(
    f0: F0,
    breath: WAVAudio,
    glottal: GlottalExcitation,
    *,
    match_rms_to: Optional[float] = None,
    model_type: str = 'lf',
) -> WAVAudio:
    """
    Vocal synthesis: takes (F0, breath WAV, glottal excitation) and returns the synthesized WAVAudio.

    Parameters:
        f0: F0 trajectory object
        breath: breath-component audio object
        glottal: glottal excitation object
        match_rms_to: target RMS energy (optional)
        model_type: glottal model type, 'lf' | 'rosenberg'

    Returns:
        synthesized WAVAudio object
    """
    sr = glottal.sample_rate

    f0_sequence = f0.values.copy()

    jitter = 0.319
    shimmer = 0.0
    if glottal.jitter is not None:
        jitter = float(glottal.jitter.get('jitter_local', 0.319))
    if glottal.shimmer is not None:
        shimmer = float(glottal.shimmer.get('shimmer_local', 0.0))

    sp = glottal.sp
    ap = glottal.ap

    glottal_parameters = None
    if model_type == 'lf' and glottal.oq is not None and glottal.oq > 0:
        glottal_parameters = {'rd': 0.3, 'ta': 0.2, 'tb': 0.4, 'te': glottal.oq}

    formants_raw = glottal.get_param('formants', [])
    formants_trajectory: List[Dict[str, float]] = []
    if formants_raw and len(formants_raw) > 0:
        for frame_formants in formants_raw:
            ft = {}
            for i, f in enumerate(frame_formants):
                ft[f'F{i+1}'] = float(f.get('frequency', 0))
            formants_trajectory.append(ft)

    synthesized = synthesize_voice_from_parameters(
        f0_sequence=f0_sequence,
        sample_rate=sr,
        jitter=jitter,
        shimmer=shimmer,
        formants_trajectory=formants_trajectory if formants_trajectory else None,
        model_type=model_type,
        sp=sp,
        ap=ap,
        target_rms=match_rms_to,
        glottal_parameters=glottal_parameters,
    )

    return WAVAudio(data=synthesized, sample_rate=sr)
