"""
vocal_lib.pitch - pitch shifting module

Calls the core functions in vocal_lib.core.pitch_shifter,
modifies (F0, GlottalExcitation) according to (original pitch -> target pitch),
and returns new objects.
"""

import os
import re
import numpy as np
from typing import Dict, List, Optional, Tuple, Union

from .core.pitch_shifter import process_pitch_shift
from .data_classes import F0, GlottalExcitation


def _parse_semitones(pitch: Union[str, int, float]) -> float:
    """
    Flexibly parse a pitch representation into semitone offset (relative to C4=0)

    Supports:
        str:  "C4", "A4", "F#5", "Bb3"   (scientific pitch)
              "1D", "-1C", "0C#", "-2Eb" (project style)
              "440Hz", "329.63HZ"       (Hz)
        int/float: >50 treated as Hz, otherwise treated as semitones
    """
    if isinstance(pitch, (int, float)):
        if float(pitch) > 50:
            return _hz_to_semitones_c4(float(pitch))
        return float(pitch)

    if not isinstance(pitch, str):
        raise TypeError(f"Unsupported pitch type: {type(pitch).__name__}")

    s = pitch.strip()

    # Hz format
    m = re.match(r'^([+-]?\d+(?:\.\d+)?)\s*[hH][zZ]$', s)
    if m:
        return _hz_to_semitones_c4(float(m.group(1)))

    # Scientific pitch: C4, A4, F#5, Bb3
    m = re.match(r'^([A-G])([#b]?)(-?\d+)$', s.upper())
    if m:
        name, acc, octave = m.group(1), m.group(2), int(m.group(3))
        if acc == 'B':
            acc = 'b'
        midi = (octave + 1) * 12 + _NOTE_SEMITONES.get(name + acc, 0)
        return float(midi - 60)

    # Project style: 1D, -1C, 0C#, -2Eb
    m = re.match(r'^([+-]?\d+)([A-G])([#B]?)$', s.upper())
    if m:
        oct_offset, name, acc = int(m.group(1)), m.group(2), m.group(3)
        if acc == 'B':
            acc = 'b'
        note_key = name + acc
        if note_key in _NOTE_SEMITONES:
            return float(oct_offset * 12 + _NOTE_SEMITONES[note_key])

    raise ValueError(
        f"Unable to parse pitch: '{pitch}'; supported examples: C4, 440Hz, 1D, -1C, 0C#, +3, -5"
    )


_NOTE_SEMITONES = {
    'C': 0, 'D': 2, 'E': 4, 'F': 5, 'G': 7, 'A': 9, 'B': 11,
    'C#': 1, 'Db': 1, 'D#': 3, 'Eb': 3,
    'F#': 6, 'Gb': 6, 'G#': 8, 'Ab': 8, 'A#': 10, 'Bb': 10,
}

_A4_HZ = 440.0
_A4_MIDI = 69


def _hz_to_semitones_c4(hz: float) -> float:
    c4_hz = _A4_HZ * 2.0 ** ((60 - _A4_MIDI) / 12.0)
    return 12.0 * np.log2(hz / c4_hz)


def _extract_formants_trajectory(glottal: GlottalExcitation) -> Optional[List[Dict[str, float]]]:
    """
    Extract per-frame F1~F7 frequencies from glottal.parameters['formants'].

    Original format (from voice_separation.extract_formants):
        [[{'frequency': hz, 'magnitude':..., 'index':...}, ...], ...]

    Converted to:
        [{'F1': hz, 'F2': hz, ..., 'F7': hz}, ...]  # take the first 7 formants per frame

    Returns None when there is no formant data or the frame count is 0
    (the caller falls back to fixed frequency bands).
    """
    formants_raw = glottal.get_param('formants', [])
    if not formants_raw or len(formants_raw) == 0:
        return None

    trajectory: List[Dict[str, float]] = []
    for frame_formants in formants_raw:
        ft: Dict[str, float] = {}
        if frame_formants and len(frame_formants) > 0:
            for i, f in enumerate(frame_formants[:7]):
                ft[f'F{i+1}'] = float(f.get('frequency', 0))
        trajectory.append(ft)

    return trajectory if len(trajectory) > 0 else None


def pitch_shift(
    f0: F0,
    glottal: GlottalExcitation,
    original_pitch: Union[str, int, float],
    target_pitch: Union[str, int, float],
    follow_ratio: Union[float, Tuple[float, float, float]] = (1.5, 0.8, 0.3),
    breathy_threshold: float = 0.3,
    band_ratios=None,
    band_width_hz: float = 250.0,
) -> Tuple[F0, GlottalExcitation]:
    """
    Pitch shift: modify F0 and glottal parameters according to (original pitch, target pitch).

    Parameters:
        f0: original F0 object
        glottal: original glottal excitation object
        original_pitch: original pitch (C4 / 440Hz / 1D / +0, etc.)
        target_pitch: target pitch
        follow_ratio: formant follow ratio, forced to use the segmented mode (F1, F2, F3)
            default (1.5, 0.8, 0.3):
            - F1 follows 150% (driven by the jaw, moves the most)
            - F2 follows 80% (driven by tongue position, moves moderately)
            - F3 follows 30% (mostly preserves the original timbre)
            for male voice synthesis (0.4, 0.3, 0.15) is recommended to preserve low formants
        breathy_threshold: threshold for classifying a frame as breathy (default 0.3)
            0.3: recommended for male voice, only enhances genuinely breathy frames
            0.1: old default, misclassifies normal male voiced frames as breathy -> hoarse
        0.4+: barely enhances anything
        band_ratios: per-band spectrum multiplier list, 8 bands recommended
            [r_F0, r_F1, r_F2, r_F3, r_F4, r_F5, r_F6, r_F7]
            bands are centered on each frame's actual F0/F1/F2/F3/F4/F5/F6/F7
            (boundaries at the midpoints of adjacent formants)
            >1.0 boost, <1.0 attenuate, 1.0 unchanged; bands beyond the list use the last value
            None disables the processing
        band_width_hz: fallback band width (default 250Hz, used when no formant data)

    Returns:
        (modified F0, modified GlottalExcitation)

    Note:
        If glottal.parameters['formants'] exists, per-frame F1/F2/F3 frequencies are
        extracted automatically and passed to warp_spectral_envelope to enable dynamic
        band segmentation, fixing the misclassification of fixed bands for back vowels
        (/u//o/ with low F2).
    """
    src_semi = _parse_semitones(original_pitch)
    dst_semi = _parse_semitones(target_pitch)
    semitones = dst_semi - src_semi

    if abs(semitones) < 1e-6:
        return f0.copy(), glottal.copy()

    sr = glottal.sample_rate

    # Extract per-frame formant frequencies from glottal.parameters for dynamic band segmentation
    formants_trajectory = _extract_formants_trajectory(glottal)

    result = process_pitch_shift(
        f0=f0.values,
        semitones=semitones,
        sample_rate=sr,
        oq=glottal.oq,
        frame_period=f0.frame_period,
        ap=glottal.ap,
        sp=glottal.sp,
        follow_ratio=follow_ratio,
        breathy_threshold=breathy_threshold,
        formants_trajectory=formants_trajectory,
        band_ratios=band_ratios,
        band_width_hz=band_width_hz,
    )

    f0_new = F0(
        values=result['f0_shifted'],
        time_axis=f0.time_axis.copy(),
        frame_period=f0.frame_period,
        sample_rate=sr,
        voiced_flags=(result['f0_shifted'] > 0),
    )

    params_new = {k: v.copy() if hasattr(v, 'copy') else v
                  for k, v in glottal.parameters.items()}
    params_new['_pitch_shift_info'] = {
        'semitones': semitones,
        'original': str(original_pitch),
        'target': str(target_pitch),
        'pitch_factor': result['pitch_factor'],
        'max_f0': result['max_f0'],
    }
    # Update formant positions to the warped values so sp and formants metadata stay consistent
    # warped_formants has the form [{'F1':..,'F2':..,'F3':..}, ...]
    # and must be converted back to the original format [[{'frequency':.., ...}, ...], ...] for synthesis.py
    warped_formants = result.get('warped_formants')
    if warped_formants is not None:
        original_formants = glottal.get_param('formants', [])
        updated_formants = []
        for frame_idx, frame_formants in enumerate(original_formants):
            if frame_idx < len(warped_formants):
                wf = warped_formants[frame_idx]
                updated_frame = []
                for i, f in enumerate(frame_formants):
                    f_new = dict(f)  # preserve original fields such as magnitude/index
                    key = f'F{i+1}'
                    if key in wf:
                        f_new['frequency'] = wf[key]
                    updated_frame.append(f_new)
                updated_formants.append(updated_frame)
            else:
                updated_formants.append(frame_formants)
        params_new['formants'] = updated_formants

    # Prefer sp with formant following applied; keep the original sp if no warp was done
    sp_new = result['sp_warped']
    if sp_new is None:
        sp_new = glottal.sp.copy() if glottal.sp is not None else None

    glottal_new = GlottalExcitation(
        signal=glottal.signal.copy(),
        sample_rate=sr,
        sp=sp_new,
        ap=result['ap_enhanced'],
        oq=result['oq_adjusted'],
        glottal_intensity=glottal.glottal_intensity.copy() if glottal.glottal_intensity is not None else None,
        parameters=params_new,
    )

    return f0_new, glottal_new
