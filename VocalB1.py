# -*- coding: utf-8 -*-
"""
VocalB1.py -- synthesis entry point (all input parameters given explicitly, no library defaults relied on)

Run:
    C:\\Python312\\python.exe VocalB1.py
"""

import os

from VocalB1Synthesizer import synthesize

_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
_OUTPUT_DIR = os.path.join(_BASE_DIR, "_output")

# ======================================================================
# [1] Input audio and pitch (SEGMENTS list, N>=2 segments, concatenated in list order)
#     Required per segment: audio / src_pitch / dst_pitch / beat_start / beat_end
#     Optional per segment (written out explicitly here, no fallback to library defaults):
#       transition         Bézier overlap frames with the previous segment (ignored for the first segment, 0 = hard splice)
#       keep_unvoiced_head keep the unvoiced head frames of the segment (True recommended for the first segment)
#       follow_ratio / band_ratios / breathy_thr  override values that apply to this segment only
# ======================================================================
def _default_library_root():
    """Voicebank directory: $VOCALB1_LIB, else the first subdirectory of ./voicebank."""
    env = os.environ.get("VOCALB1_LIB")
    if env and os.path.isdir(env):
        return env
    base = os.path.join(os.path.dirname(os.path.abspath(__file__)), "voicebank")
    if os.path.isdir(base):
        subs = sorted(d for d in os.listdir(base) if os.path.isdir(os.path.join(base, d)))
        if subs:
            return os.path.join(base, subs[0])
    return base


_LIB = _default_library_root()

SEGMENTS = [
    dict(audio=os.path.join(_LIB, "_で.wav"),
         src_pitch="D4", dst_pitch="G4",
         beat_start=1, beat_end=2.5,
         transition=0,
         keep_unvoiced_head=True,
         follow_ratio=(0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0),
         band_ratios=[1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0],
         breathy_thr=0.3),
    dict(audio=os.path.join(_LIB, "_い.wav"),
         src_pitch="D4", dst_pitch="G4",
         beat_start=2, beat_end=4,
         transition=0,
         keep_unvoiced_head=False,
         follow_ratio=(0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0),
         band_ratios=[1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0],
         breathy_thr=0.3),
    dict(audio=os.path.join(_LIB, "_じ.wav"),
         src_pitch="D4", dst_pitch="E4",
         beat_start=4, beat_end=7,
         transition=0,
         keep_unvoiced_head=False,
         follow_ratio=(0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0),
         band_ratios=[1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0],
         breathy_thr=0.3),
]

# ======================================================================
# [1.1] Per-frame pitch line (None = disabled)
#   [(beat, pitch), ...], sampled per frame with a cubic Bézier between adjacent endpoints (portamento);
#   None = unvoiced frames from that endpoint on; uncovered frames keep the spliced result.
# ======================================================================
PITCH_LINE = [
    (1, "G4"),    # segments 1+2 (beat1~4) = G4
    (4, "G4"),    # two endpoints at the same beat = frame-300 zero-width hard cut (seam 2, 0 overlap frames)
    (4, "E4"),    # segment 3 (beat4~7) = E4
    (7, "E4"),
]

# ======================================================================
# [2] Pitch transformation (global defaults; values here match the per-segment overrides)
# ======================================================================
FOLLOW_RATIO  = (0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0)   # F0~F6 formant follow ratios
BAND_RATIOS   = [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0]  # F0~F7 band energy multipliers
BREATHY_THR   = 0.3          # breathiness threshold
BAND_WIDTH_HZ = 250.0        # band width (Hz)

# ======================================================================
# [3] AP (aperiodicity) correction
# ======================================================================
AP_SCALE = 0.25              # global AP scale factor
AP_MAX   = 0.20              # AP upper limit

# ======================================================================
# [4] Splicing / tempo / duration
# ======================================================================
SAMPLE_RATE           = 44100    # sample rate
FRAME_PERIOD          = 5.0      # frame period (ms)
JOIN_NORMALIZE_SP     = False    # align each segment's SP energy to the first segment
BPM                   = 120      # tempo
TIME_SIGNATURE        = (3, 4)   # time signature (beats per bar, beat unit)
DUR_EDGE_FRAMES       = 25       # segment tail template frames
DUR_JITTER_SMOOTH     = 5        # jitter smoothing window in the extension region (frames)
DUR_JITTER_DECAY_MS   = 120.0    # jitter decay time constant towards the tail (ms)
DUR_TAIL_JITTER_RATIO = 0.3      # fraction of jitter kept at the far tail

# ======================================================================
# [5] Synthesis
# ======================================================================
MATCH_RMS_TO = 0.3           # target RMS of the main signal
MODEL_TYPE   = "lf"          # glottal model (only used by the fallback branch when sp/ap are missing)
SP_FLOOR     = 1e-12         # sp floor (log magnitude domain, prevents pyworld NaN)

# ======================================================================
# [6] Breath
# ======================================================================
ADD_BREATH             = True     # add breath noise (False = pure vocal-fold signal)
BREATH_GAIN            = 1.0      # breath overlay gain
BREATH_FOLLOW_PITCH    = True     # breath follows the main signal pitch (granular OLA)
BREATH_PV_FRAME_MS     = 60.0     # pitch-shift frame length (ms)
BREATH_PV_HOP_MS       = 15.0     # pitch-shift hop (ms)
BREATH_SHAPE_TO_MAIN   = True     # couple the breath spectral envelope to the main signal sp
BREATH_SHAPE_BANDS     = 48       # number of log-spaced bands
BREATH_SHAPE_GAIN_MIN  = 0.03     # bin gain lower bound
BREATH_SHAPE_GAIN_MAX  = 30.0     # bin gain upper bound

# ======================================================================
# [7] Plosives
# ======================================================================
PLOSIVE_RATIO           = 5.0    # breath RMS > voiced median x this ratio
PLOSIVE_MIN_GRAD_DB     = 12.0   # adjacent-frame energy jump threshold (dB/frame)
PLOSIVE_MIN_CREST       = 2.0    # within-frame peak/RMS threshold
PLOSIVE_MAX_VALLEY      = 0.3    # lowest energy in the preceding 40ms < current frame x this ratio
PLOSIVE_GAIN            = 0.3    # vocal-fold energy gain at the plosive
PLOSIVE_SMOOTH          = 5      # gain smoothing window (frames)
PLOSIVE_MARGIN_HEAD     = 0      # plosive region extension frames forwards
PLOSIVE_MARGIN_TAIL     = 6      # plosive region extension frames backwards
PLOSIVE_KEEP_BURST      = True   # keep burst frames from being attenuated
PLOSIVE_ENV_TRANSPLANT  = True   # transient envelope transplant
PLOSIVE_ENV_HEAD        = 6      # anchor frames applied forwards
PLOSIVE_ENV_TAIL        = 6      # anchor frames applied backwards
PLOSIVE_ENV_GAIN_MIN    = 0.15   # envelope gain lower bound
PLOSIVE_ENV_GAIN_MAX    = 2.0    # envelope gain upper bound

# ======================================================================
# [8] Output
# ======================================================================
OUTPUT_WAV         = os.path.join(_OUTPUT_DIR, "e2e_song.wav")
EDGE_FADE_MS       = 12.0   # fade in/out at the start and end of the phrase (ms)
BIT_DEPTH          = 24     # output bit depth 16/24
CLEAN_INTERMEDIATE = True   # delete breath_seg*.wav / spliced_frames.txt when done
VERBOSE            = True   # print pipeline logs


def main():
    return synthesize(
        SEGMENTS, OUTPUT_WAV,
        # [1.1] per-frame pitch line
        pitch_line=PITCH_LINE,
        # [2] pitch transformation
        follow_ratio=FOLLOW_RATIO,
        band_ratios=BAND_RATIOS,
        breathy_thr=BREATHY_THR,
        band_width_hz=BAND_WIDTH_HZ,
        # [3] AP correction
        ap_scale=AP_SCALE,
        ap_max=AP_MAX,
        # [4] splicing / tempo / duration
        sample_rate=SAMPLE_RATE,
        frame_period=FRAME_PERIOD,
        join_normalize_sp=JOIN_NORMALIZE_SP,
        bpm=BPM,
        time_signature=TIME_SIGNATURE,
        dur_edge_frames=DUR_EDGE_FRAMES,
        dur_jitter_smooth=DUR_JITTER_SMOOTH,
        dur_jitter_decay_ms=DUR_JITTER_DECAY_MS,
        dur_tail_jitter_ratio=DUR_TAIL_JITTER_RATIO,
        # [5] synthesis
        match_rms_to=MATCH_RMS_TO,
        model_type=MODEL_TYPE,
        sp_floor=SP_FLOOR,
        # [6] breath
        add_breath=ADD_BREATH,
        breath_gain=BREATH_GAIN,
        breath_follow_pitch=BREATH_FOLLOW_PITCH,
        breath_pv_frame_ms=BREATH_PV_FRAME_MS,
        breath_pv_hop_ms=BREATH_PV_HOP_MS,
        breath_shape_to_main=BREATH_SHAPE_TO_MAIN,
        breath_shape_bands=BREATH_SHAPE_BANDS,
        breath_shape_gain_min=BREATH_SHAPE_GAIN_MIN,
        breath_shape_gain_max=BREATH_SHAPE_GAIN_MAX,
        # [7] plosives
        plosive_ratio=PLOSIVE_RATIO,
        plosive_min_grad_db=PLOSIVE_MIN_GRAD_DB,
        plosive_min_crest=PLOSIVE_MIN_CREST,
        plosive_max_valley=PLOSIVE_MAX_VALLEY,
        plosive_gain=PLOSIVE_GAIN,
        plosive_smooth=PLOSIVE_SMOOTH,
        plosive_margin_head=PLOSIVE_MARGIN_HEAD,
        plosive_margin_tail=PLOSIVE_MARGIN_TAIL,
        plosive_keep_burst=PLOSIVE_KEEP_BURST,
        plosive_env_transplant=PLOSIVE_ENV_TRANSPLANT,
        plosive_env_head=PLOSIVE_ENV_HEAD,
        plosive_env_tail=PLOSIVE_ENV_TAIL,
        plosive_env_gain_min=PLOSIVE_ENV_GAIN_MIN,
        plosive_env_gain_max=PLOSIVE_ENV_GAIN_MAX,
        # [8] output
        edge_fade_ms=EDGE_FADE_MS,
        bit_depth=BIT_DEPTH,
        clean_intermediate=CLEAN_INTERMEDIATE,
        verbose=VERBOSE,
    )


if __name__ == "__main__":
    main()
