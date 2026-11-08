"""
vocal_lib.engine - core vocal processing engine

Provides the low-level decomposition and synthesis functions; all imports use
package-relative imports, making the package self-contained.
"""

from .core.f0_detection import smooth_f0_sequence
from .core.voice_separation import decompose_voice

from .core.pyworld_processor import (
    extract_pyworld_features, compute_jitter, compute_shimmer,
    compute_oq, extract_glottal_pulse, compute_f0_statistics,
)
from .perf import span


def decompose_vocal(audio_data, sample_rate, frame_period=5.0):
    """Vocal decomposition: takes audio data and sample rate, returns a decomposition result dict"""
    # One pyworld pass for the whole decomposition: the extracted F0 drives both the
    # smoothed f0 curve and the singer decomposition, and the same sp/ap feed the
    # synthesis parameters. Running DIO/StoneMask/CheapTrick/D4C twice was pure waste.
    with span("lib.extract_pyworld"):
        pyworld_result = extract_pyworld_features(audio_data, sample_rate, frame_period)

    with span("lib.f0_smoothing"):
        f0_sequence = smooth_f0_sequence(pyworld_result['f0'])

    with span("lib.decompose_voice"):
        separation_result = decompose_voice(audio_data, sample_rate, f0_sequence)

    result = {
        'glottal': separation_result['glottal'],
        'formant': separation_result['formant'],
        'breath': separation_result['breath'],
        'f0': f0_sequence,
        'formants': separation_result['formants'],
        'glottal_intensity': separation_result['glottal_intensity'],
        'formant_intensity': separation_result['formant_intensity'],
        'breath_intensity': separation_result['breath_intensity'],
        'voiced_flags': separation_result['voiced_flags'],
        'frequencies': separation_result['frequencies'],
        'jitter': None,
        'shimmer': None,
        'oq': 0.0,
        'f0_stats': None,
        'time_axis': None,
        'glottal_pulse': None,
        'sp': None,
        'ap': None
    }

    # pyworld is a mandatory dependency: the single extraction above is reused here,
    # so sp/ap/time axis always come from the exact same pass as the f0 curve.
    f0_pyworld = pyworld_result['f0']
    time_axis = pyworld_result['time_axis']
    sp = pyworld_result['sp']
    ap = pyworld_result['ap']
    voiced_flags_pyworld = pyworld_result['voiced_flags']

    result['time_axis'] = time_axis
    result['sp'] = sp
    result['ap'] = ap
    result['f0_pyworld'] = f0_pyworld
    result['voiced_flags_pyworld'] = voiced_flags_pyworld

    with span("lib.jitter_shimmer_oq"):
        result['jitter'] = compute_jitter(f0_pyworld, time_axis)
        result['shimmer'] = compute_shimmer(f0_pyworld, audio_data, sample_rate, time_axis)
        result['oq'] = compute_oq(ap, f0_pyworld, sample_rate)
        result['f0_stats'] = compute_f0_statistics(f0_pyworld)
        result['glottal_pulse'] = extract_glottal_pulse(f0_pyworld, time_axis, sample_rate)

    return result


def synthesize_voice_from_parameters(f0_sequence, sample_rate=44100,
                                     jitter=0.319, shimmer=0.0,
                                     formants_trajectory=None, model_type='lf',
                                     sp=None, ap=None,
                                     target_rms=None, glottal_parameters=None):
    """Synthesize speech from parameters"""
    from .core.voice_synthesizer import VoiceSynthesizer
    with span("synth.from_parameters"):
        synthesizer = VoiceSynthesizer(sample_rate, model_type)
        return synthesizer.synthesize_from_parameters(
            f0_sequence, jitter, shimmer,
            glottal_parameters=glottal_parameters,
            formants_trajectory=formants_trajectory,
            sp=sp, ap=ap, target_rms=target_rms
        )
