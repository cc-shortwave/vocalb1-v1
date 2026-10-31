"""
VocalB1Synthesizer - multi-audio voiced-frame concatenation synthesis (Bézier interpolation join, N>=2 segments)

Principle:
    Given N input audios (each with its own pitch shift), for every audio:
      1. Keep only the per-frame parameters of voiced frames (F0/SP/AP/glottal_intensity)
         and the matching breath sample segments; drop unvoiced frames (removes silent gaps).
      2. Concatenate the N segments in list order: segment 1 first, segment N last.
      3. In the transition region where two adjacent segments overlap, smooth-join
         F0/SP/AP/GI with a cubic Bézier (smoothstep, zero endpoint tangents)
         weight curve, while the breath is cross-faded in the same region with
         overlap-add, eliminating F0 jumps and spectral/energy discontinuities at
         the seams. N segments yield N-1 transition regions, produced by repeatedly
         folding a pairwise join function along the segment order.
      4. Synthesize with VocalSynthesizer (pyworld: f0+sp+ap) and mix in the breath.

Library usage (recommended):
    from VocalB1Synthesizer import synthesize
    synthesize(
        segments=[
            dict(audio=r"voicebank\\_で.wav", src_pitch="D4", dst_pitch="G4",
                 keep_unvoiced_head=True, beat_start=1, beat_end=2.5),
            dict(audio=r"voicebank\\_い.wav", src_pitch="D4", dst_pitch="G4",
                 beat_start=2, beat_end=4),
        ],
        output_path=r"out\\song.wav",
        bpm=120, time_signature=(4, 4),
        verbose=True,
    )

Run directly:
    Runs the DEFAULT_SEGMENTS example below once (equivalent to the old script form).
"""

import contextlib
import io
import os
import shutil
import tempfile
import numpy as np

import vocal_lib as vl
import prosses_breath   # used to mix the concatenated breath directly into the final output
from VocalDecomposer import VocalDecomposer
from VocalSynthesizer import VocalSynthesizer
from vocal_lib.pitch import _parse_semitones

# Output directory is anchored to the script directory (same as prosses_breath),
# so that a different working directory does not write artifacts into CWD or
# leave prosses_breath reading stale data
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
_OUTPUT_DIR = os.path.join(_BASE_DIR, "_output")

# ======================================================================
# Default parameter section (overridden item by item by synthesize() keyword args)
#   [1] Input audio & pitch  [2] Pitch shift   [3] AP correction   [4] Splicing
#   [5] Synthesis            [6] Breath        [7] Plosives        [8] Output
# Note: the uppercase constants below are the defaults for synthesize() parameters
#       and the config used when running this file directly; they are temporarily
#       rewritten during a synthesize() call and restored afterwards.
# ======================================================================

# ----------------------------------------------------------------------
# [1] Input audio & pitch (SEGMENTS list, segment count N>=2, joined in list order)
#     Required per segment: audio / src_pitch / dst_pitch / beat_start / beat_end
#               (src_pitch equal to dst_pitch = no pitch shift)
#     Optional per segment:
#       transition        = Bézier overlap frames with the PREVIOUS segment (ignored
#                           for the first segment; default 0 = hard join. When beat
#                           positions do not overlap it only widens the fade and
#                           does not shift the note onset)
#       keep_unvoiced_head= True keeps the unvoiced frames of the first half of that
#                           segment (recommended True for the first segment)
#       follow_ratio / band_ratios / breathy_thr
#                         = override the global defaults of [2], for this segment only
#     Beat semantics: beat_start/beat_end are 1-based start/end beats of the note on
#       the beat grid (fractional beats allowed; global BPM required); "bar:beat"
#       strings (e.g. "2:3" = bar 2, beat 3) are converted via TIME_SIGNATURE.
#       The onset lands exactly on beat_start and the tail stops on beat_end:
#         overlapping adjacent note ranges -> the later note enters early and the
#         overlap is Bézier cross-faded
#         gapped adjacent note ranges -> silence (rest) is padded after the previous
#         note tail
# ----------------------------------------------------------------------
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

DEFAULT_SEGMENTS = [
    dict(audio=os.path.join(_LIB, "_で.wav"),
         src_pitch="D4", dst_pitch="G4",
         keep_unvoiced_head=True,             # seg 1: keep first-half unvoiced frames (incl. onset)
         beat_start=1, beat_end=2.5),           # beat 1 -> beat 3
    dict(audio=os.path.join(_LIB, "_い.wav"),
         src_pitch="D4", dst_pitch="G4",
         beat_start=2, beat_end=4),           # beat 3 -> beat 5
    dict(audio=os.path.join(_LIB, "_じ.wav"),
         src_pitch="D4", dst_pitch="E4",
         beat_start=4, beat_end=7),           # beat 5 -> beat 7
]

# Module-level current config: used when running this file directly; temporarily
# overridden by synthesize() during library calls
SEGMENTS = DEFAULT_SEGMENTS

# ----------------------------------------------------------------------
# [1.1] Per-frame pitch line PITCH_LINE (None = disabled)
#   Splicing proceeds as usual, but every spliced frame's pitch is STRICTLY
#   overridden by this curve (only f0 changes; sp/ap still come from each
#   segment's dst_pitch pitch shift).
#   Syntax: [(beat position, pitch), ...]
#     beat position : 1-based, same coordinate system as SEGMENTS
#                     beat_start/beat_end (converted to frame index via global
#                     BPM/TIME_SIGNATURE; accepts "2:3" and fractional beats)
#     pitch : note name ("G4") / Hz value / None (None = silent frames from that
#             point on)
#   Interval semantics (the value of interval [t_i, t_{i+1}) is decided by its
#   LEFT endpoint):
#     v_i = None                  -> interval is silent (f0=0 and forced mute,
#                                    including breath)
#     v_i value, v_{i+1} value    -> cubic Bézier (horizontal endpoint tangents =
#                                    smoothstep) sampled per frame, gliding from
#                                    v_i to v_{i+1} (portamento)
#     v_i value, v_{i+1} = None   -> interval holds v_i (silence starts at the
#                                    next endpoint)
#   Pitch is interpolated in the SEMITONE domain (perceptually linear) and then
#   converted back to Hz.
#   Frames before the first endpoint / after the last endpoint = uncovered -> keep
#   the spliced result.
#   Example (hold G4 -> glide to A4 -> silent):
#     PITCH_LINE = [(1, "G4"), (2.5, "G4"), (4, "A4"), (5, None), (7, "G4")]
# ----------------------------------------------------------------------
PITCH_LINE = None

# ----------------------------------------------------------------------
# [2] Pitch-shift parameters (global defaults, overridable per segment by the
#     same-named keys in SEGMENTS)
# ----------------------------------------------------------------------
FOLLOW_RATIO  = (0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0)  # fr: formant follow ratio for F0~F6
BAND_RATIOS   = [1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0]  # br: band energy multiplier for F0~F7
BREATHY_THR   = 0.3           # bt: breathiness threshold
BAND_WIDTH_HZ = 250.0         # band width (fixed)

# ----------------------------------------------------------------------
# [3] AP (aperiodicity) correction -- pyworld d4c fails systematically on this
#     voice library:
#   Measured on _い.wav the true harmonic energy ratio is 98.6% (highly periodic),
#   yet d4c reports 0~1kHz AP=0.465 and ~0.5 across the full band, a "half-noise"
#   estimate that marks even the low band of voiced vowels as aperiodic.
#   pw.synthesize therefore renders 2~8kHz as a diffuse noise cloud (the "huge
#   artifact" visible in the spectrogram). Scaling + clamping AP restores
#   periodicity: synthesized harmonic energy ratio 71.4% -> 97.7%.
# ----------------------------------------------------------------------
AP_SCALE      = 0.25          # overall AP scale factor (1.0 = no correction)
AP_MAX        = 0.20          # AP upper limit (clipped above this)

# ----------------------------------------------------------------------
# [4] Splicing (transition frames between segments are set per segment via
#     SEGMENTS[i]["transition"])
# ----------------------------------------------------------------------
SAMPLE_RATE      = 44100      # sample rate
FRAME_PERIOD     = 5.0        # frame period (ms)
JOIN_NORMALIZE_SP = False     # before splicing, align each segment's overall SP energy to the FIRST segment

# Click/pop suppression (seam continuity)
MIN_SEAM_OVER_FRAMES = 3      # 相邻音符首尾相接（无重叠、无休止）时强制的最小交叉淡化帧数。
                              # 完全不重叠时 sp/ap/gi 与气声会硬拼，接缝处产生咔哒/爆音。
                              # 交叉淡化只加宽融合区、不改变拍位与总时长。
PITCH_JUMP_RAMP_MS = 20.0     # pitch line 同拍端点（零长度区间）的过渡窗（ms）：
                              # 前一个音在段尾、后一个音在段首时会落在同一帧，f0 必须在该窗口内平滑过去，
                              # 否则 1 帧内跳变会让声码器相位重排，听起来是咔哒。
SILENCE_FADE_MS = 15.0        # pitch line 静音端点的样本域淡入/淡出窗（ms），消除静音区的硬切

# 起音保护（同一音符连击时第二个音的爆破/起音不被接缝淡化吞掉）
# 问题：接缝交叉淡化让"进入段"从权重 0 淡入，而进入段的前几帧正是它的起音/爆破
#       （样本头部静音被裁掉后，第一个保留帧就是爆破峰）。淡化宽度越长，起音被压得越低，
#       两个カ音连击时第二个的爆破就完全消失了。
# 判据：进入段头部 RMS 明显低于它自身的持续电平（< ATTACK_QUIET_RATIO）→ 这是一次
#       从闭合/静音起步的起音（爆破/塞音），而不是延续中的元音。此时：
#       · 进入段从接缝第一帧起就是全权重（爆破完整保留）
#       · 前一段的尾巴在 ATTACK_FADE_FRAMES 内快速退出（它的电平低于进入段持续电平，
#         叠加不会出现音量鼓包），整个过渡只占约 30ms
ATTACK_HEAD_FRAMES  = 2       # 起音峰所在的帧数（从静音头之后算起）
ATTACK_DECAY_FRAMES = 3       # 起音峰之后检查衰减的帧数
ATTACK_DECAY_RATIO  = 0.60    # 峰后电平 ≤ 峰值 × 该比例 → 是瞬间峰（爆破），而不是元音渐入
ATTACK_QUIET_RATIO  = 0.60    # 起音峰 ≤ 本段中位电平 × 该比例（爆破电平低于持续元音）
ATTACK_LEAD_FRAMES  = 6       # 起音保护接缝上给进入段补的头部静音（闭合段）帧数（6帧=30ms）
                              # 前一段的尾音会在这一段静音里退出，爆破才有"闭合→爆破"的落差
ATTACK_FADE_FRAMES  = 6       # 起音保护接缝上，前一段尾巴退出所占的帧数（6帧=30ms）
ATTACK_CLIP_FRAMES  = 3       # 前一段尾巴真正衰减到 0 的帧数（3帧=15ms）；之后到爆破之间是真静音

# Beat layout (each segment gives its start/end beat on the beat grid via
# beat_start/beat_end)
#   The onset lands exactly on beat_start and the tail stops on beat_end:
#     overlapping adjacent note ranges -> the later note enters early and the
#     overlap is Bézier cross-faded
#     gapped adjacent note ranges -> silence (rest) is padded after the previous
#     note tail
#   Total output length = duration of the beat position given by the last
#   segment's beat_end.
#   Material shorter than the note's beat duration -> trim the tail (keep the
#   front); surplus material -> extend using the segment tail as a template --
#   F0 follows the segment-tail baseline plus a smoothed random walk scaled by
#   measured jitter; SP/AP take the segment-tail mean with overall amplitude
#   driven by measured shimmer; breath loops the segment-tail excerpt (loop
#   seams are equal-power cross-faded).
BPM               = 120       # global tempo (beats/minute), required
TIME_SIGNATURE    = (3, 4)    # time signature (beats per bar, beat value). The numerator is used for "bar:beat" position conversion;
                              # the denominator is informational only (BPM's "1 beat" is the beat denoted by the numerator)
DUR_EDGE_FRAMES   = 25        # how many tail frames serve as F0 baseline / SP/AP mean / breath loop template
DUR_JITTER_SMOOTH = 5         # smoothing window for the jitter noise (frames), larger = slower jitter
DUR_JITTER_DECAY_MS = 120.0   # time constant (ms) over which extension-region jitter decays from the original tail toward the end;
                              # sustained notes get steadier the longer they are held; measured jitter/shimmer includes onset
                              # fluctuation, and applying it at full depth over a long tail creates an audible loudness/pitch wave
DUR_TAIL_JITTER_RATIO = 0.3  # jitter retention ratio at the far end of the tail (0 = fully steady, 1 = no decay)
JITTER_EXTRA_PCT  = 0.0       # extra pitch-jitter depth added to measured jitter_local (percentage points, 0~1)
SHIMMER_EXTRA_PCT = 0.0       # extra amplitude-shimmer depth added to measured shimmer_local (percentage points, 0~1)

# ----------------------------------------------------------------------
# [5] Synthesis
# ----------------------------------------------------------------------
MATCH_RMS_TO  = 0.3           # target RMS of the main signal
MODEL_TYPE    = "lf"          # glottal model "lf" | "rosenberg"
                              # (only used by the fallback branch when sp/ap are missing; has no effect on the normal pyworld path)
SP_FLOOR      = 1e-12         # lower bound for sp (log magnitude domain). Only prevents pyworld synthesis
                              # from producing NaN when sp==0; it must never be raised to the 1e-3 range: cheaptrick's
                              # true spectral floor is about 3e-11, and sp of an "energy-less band" is exactly that floor.
                              # If raised to 1e-3, pyworld renders broadband content with magnitude e^1e-3≈1.0 in those bins
                              # -> a hissing noise floor across the whole segment (measured HF flatness 0.003->0.175, worse with larger AP)

# ----------------------------------------------------------------------
# [6] Breath
# ----------------------------------------------------------------------
ADD_BREATH          = True    # mix breath into the final output (False = glottal source only)
BREATH_GAIN         = 1.0     # breath mix gain (broadband noise; higher values make the noise floor more audible in the high range)
BREATH_FOLLOW_PITCH = True    # breath follows the segment's main-signal pitch (granular OLA pitch shift)
BREATH_PV_FRAME_MS  = 60.0    # pitch-shift frame length (ms)
BREATH_PV_HOP_MS    = 15.0    # pitch-shift hop (ms)

# [6.1] Breath spectral coupling (removes the "disjointed" feel) -- the breath is noise cut straight
#       from the source library and carries the SOURCE's formants; when segment A is shifted up it is
#       also moved up 1.5x as a whole, while the main signal's formants only follow partially according
#       to [2] FOLLOW_RATIO -> the two formant sets misalign and sound like "two independent tracks".
#       Measured per-frame log-spectral band correlation: 0.601 unshaped (A 0.553 / B 0.725)
#       -> 0.782 after coupling (A 0.767 / B 0.821).
#       Method: in the STFT domain, shape the breath's spectral envelope to the main signal's sp; only the
#       formant shape changes, while the breath's own time-domain energy envelope and noise phase stay
#       untouched (loudness and breath timing are unaffected).
BREATH_SHAPE_TO_MAIN  = True
BREATH_SHAPE_BANDS    = 48    # number of log bands (spectral envelope smoothing granularity; larger = more detail)
BREATH_SHAPE_GAIN_MIN = 0.03  # bin gain lower bound (prevents a band from being over-suppressed)
BREATH_SHAPE_GAIN_MAX = 30.0  # bin gain upper bound

# ----------------------------------------------------------------------
# [7] Plosives (detected automatically; without seed frames the whole block is
#     inactive and does not affect other audio)
# ----------------------------------------------------------------------
# [7.1] Detection and ducking: when a plosive is detected, reduce the glottal
#       source (main signal) energy there.
#       Three features combined with AND; with no seed frames the audio is
#       automatically judged plosive-free and nothing is ducked:
#         1) breath RMS clearly above the segment median (energy burst)
#         2) steep rising edge (burst transient attack; sustained aspiration is gradual)
#         3) high crest factor + preceding closure valley (the closure->burst shape of p/t/k)
PLOSIVE_RATIO       = 5.0     # feature 1: frame breath RMS > voiced-frame median × this factor
PLOSIVE_MIN_GRAD_DB = 12.0    # feature 2: energy jump between adjacent frames ≥ this dB/frame
PLOSIVE_MIN_CREST   = 2.0     # feature 3a: in-frame peak/RMS ≥ this value (a burst is noise, crest≈3; this gate only filters near-DC anomalous frames)
PLOSIVE_MAX_VALLEY  = 0.3     # feature 3b: minimum energy in the preceding 40ms < current frame × this ratio (closure segment)
PLOSIVE_GAIN        = 0.3     # glottal source energy at the plosive segment is lowered to this factor (0 = fully muted)
PLOSIVE_SMOOTH      = 5       # gain smoothing window (frames), avoids abrupt clicks
PLOSIVE_MARGIN_HEAD = 0       # number of frames to extend the plosive segment FORWARD (0 = do not duck the closure/burst before the burst)
PLOSIVE_MARGIN_TAIL = 6       # number of frames to extend the plosive segment BACKWARD (covers the aspiration/vowel after the burst)
PLOSIVE_KEEP_BURST  = True    # keep the detected burst frames themselves unducked (the burst transient is key to intelligibility)

# [7.2] Transient envelope transplant (makes the plosive audible) -- pyworld's per-frame (5ms)
#       spectral reconstruction smears the burst transient into a gradual rise: in _ば the source rises
#       10%->90% in only 4ms, but after synthesis it takes 18ms (t90 23ms->36ms); the energy is preserved
#       but the "click" impact is gone, so the plosive becomes inaudible.
#       Method: inside the detected plosive window, anchored at the burst frame, transplant the relative
#       shape of the SOURCE audio's per-frame energy envelope onto the synthesized main signal (the burst
#       frame level is unchanged, closure/gradual-rise parts are darkened according to the source,
#       -> restoring the "silence->burst" contrast; harmonics are untouched, so the pitch is still the target pitch).
PLOSIVE_ENV_TRANSPLANT = True
PLOSIVE_ENV_HEAD       = 6    # frames the seed frame's influence extends FORWARD (incl. the closure merge segment)
PLOSIVE_ENV_TAIL       = 6    # frames the seed frame's influence extends BACKWARD (incl. aspiration/vowel onset)
PLOSIVE_ENV_GAIN_MIN   = 0.15 # envelope gain lower bound (darkening the closure segment -> increases silence->burst contrast)
PLOSIVE_ENV_GAIN_MAX   = 2.0  # envelope gain upper bound (prevents clipping)

# ----------------------------------------------------------------------
# [8] Output (paths anchored to the script directory, independent of the runtime
#     working directory)
# ----------------------------------------------------------------------
EDGE_FADE_MS      = 12.0      # fade in/out at the start and end of the whole phrase (ms)
BIT_DEPTH         = 24        # output bit depth 16/24
OUTPUT_WAV        = os.path.join(_OUTPUT_DIR, "multi_voiced_bezier.wav")  # final synthesis result
OUTPUT_BREATH_FMT = os.path.join(_OUTPUT_DIR, "breath_seg{}.wav")  # intermediate artifact: per-segment source breath
OUTPUT_FRAMES_TXT = os.path.join(_OUTPUT_DIR, "spliced_frames.txt")  # intermediate artifact: spliced frame text
CLEAN_INTERMEDIATE = True     # delete the intermediate artifacts above after the run (keep only the final OUTPUT_WAV)
# ======================================================================


# ----------------------------------------------------------------------
# Utility functions
# ----------------------------------------------------------------------
def _frame_bound(i: int, sr: int, fp: float) -> int:
    """Sample boundary of frame i (exact floating-point frame period, avoids cumulative drift)."""
    return int(round(i * sr * fp / 1000.0))


def _align_samples(arr: np.ndarray, target_len: int) -> np.ndarray:
    """Strictly align a sample-domain array to target_len (trim or zero-pad the tail)."""
    n = len(arr)
    if n == target_len:
        return arr
    if n > target_len:
        return arr[:target_len].copy()
    out = np.zeros(target_len, dtype=arr.dtype)
    out[:n] = arr
    return out


def _bezier_weights(n: int) -> np.ndarray:
    """Cubic Bézier rising weights (0->1) for n transition frames.

    Uses a cubic Bézier with horizontal endpoint tangents (P1y=P0y=0, P2y=P3y=1),
    i.e. smoothstep: w = 3t²-2t³; the first derivative is 0 at both ends and the
    curve is monotonic and smooth.
    """
    if n <= 1:
        return np.ones(max(n, 0), dtype=np.float64)
    t = np.linspace(0.0, 1.0, n, dtype=np.float64)
    return 3.0 * t ** 2 - 2.0 * t ** 3


def _resample_1d(arr: np.ndarray, target_len: int) -> np.ndarray:
    """Linearly resample a 1D array to target_len."""
    arr = np.asarray(arr, dtype=np.float64)
    if len(arr) == 0:
        return np.zeros(target_len, dtype=np.float64)
    if len(arr) == target_len:
        return arr.copy()
    src = np.linspace(0.0, len(arr) - 1, target_len)
    lo = np.floor(src).astype(int)
    hi = np.minimum(lo + 1, len(arr) - 1)
    f = src - lo
    return arr[lo] * (1.0 - f) + arr[hi] * f


def _smooth_random_walk(n: int, win: int, seed: int = 20260927) -> np.ndarray:
    """Generate n zero-mean smooth random walks (low-passed white noise) normalized to unit RMS.

    Per-frame white noise would produce harsh frame-to-frame jumps when used for
    duration-extension jitter, so the signal is first Hann-window smoothed and then
    normalized to RMS=1; the caller multiplies by a percentage to obtain the jitter
    depth. A fixed random seed keeps results reproducible (same output on every run).
    """
    if n <= 0:
        return np.zeros(0, dtype=np.float64)
    rng = np.random.default_rng(seed)
    r = rng.normal(0.0, 1.0, n)
    win = min(max(1, int(win)), n)
    # win must stay >= 3: np.hanning(2) is exactly [0, 0], so kernel/kernel.sum() is 0/0 -> the
    # whole noise (and every parameter derived from it) becomes NaN. A 2-frame duration
    # extension used to hit this, poisoning sp/ap/gi and producing a 5~80ms burst at the
    # note tail. Two frames need no smoothing anyway.
    if win > 2:
        kernel = np.hanning(win)
        kernel = kernel / kernel.sum()
        r = np.convolve(np.pad(r, win // 2, mode="edge"), kernel, mode="valid")[:n]
    rms = float(np.sqrt(np.mean(r ** 2)))
    return r / rms if rms > 1e-12 else r


def _loop_tile(tmpl: np.ndarray, n_out: int, sr: int, fade_ms: float = 8.0) -> np.ndarray:
    """Loop-tile template tmpl to n_out samples, with an equal-power cross-fade at the loop seam.

    Blocks are added with hop = len(tmpl) - fade: each block fades in with sin at
    its head and out with cos at its tail; sin²+cos²=1 keeps the overlap energy
    constant, avoiding clicks or energy dips at the loop point.
    """
    L = len(tmpl)
    if L <= 0 or n_out <= 0:
        return np.zeros(max(n_out, 0), dtype=np.float64)
    fd = int(round(sr * fade_ms / 1000.0))
    fd = max(1, min(fd, L // 2))
    hop = L - fd
    n_rep = int(np.ceil(n_out / hop)) + 1
    buf = np.zeros((n_rep - 1) * hop + L, dtype=np.float64)
    w = np.ones(L, dtype=np.float64)
    t = np.linspace(0.0, np.pi / 2.0, fd, endpoint=False, dtype=np.float64)
    w[:fd] = np.sin(t)        # head: overlaps the previous block's tail -> fade in
    w[-fd:] = np.cos(t)       # tail: overlaps the next block's head -> fade out
    for r in range(n_rep):
        p = r * hop
        buf[p:p + L] += tmpl * w
    return buf[:n_out]


def _fit_duration(seg: dict, m_target: int, sr: int, fp: float) -> dict:
    """Trim or extend a segment's voiced-frame sequence to m_target frames (matching the note's beat duration).

    Trim (m_target < original length): keep only the FRONT frames and cut the tail
    (the front carries the most complete information).
    Extend (m_target > original length): take the last DUR_EDGE_FRAMES frames as a
    template and continue every parameter --
        F0 : anchor on the segment tail's voiced-frame BASELINE (median) and add a
             smoothed random walk scaled by the segment's measured jitter_local;
             blend the first DUR_EDGE_FRAMES frames with the final value via
             smoothstep so the join with the existing voiced part is seamless;
        SP/AP : segment-tail mean (formants are stable, avoiding spectral breaks in
             the extension);
        amplitude : a smoothed random walk scaled by the segment's measured
             shimmer_local modulates overall SP/GI energy (shimmer is amplitude
             jitter, which shows up directly as natural loudness fluctuation);
        breath : loop the segment-tail noise excerpt (equal-power cross-fade, see
             _loop_tile);
        src_rms: carry on the segment-tail mean (the extension has no plosive
             transient; used only as the reference level for envelope transplant).
    """
    m = len(seg['f0'])
    m_target = int(m_target)
    if m_target == m:
        return seg

    out = dict(seg)

    # ── trim: keep the front ──
    if m_target < m:
        keep = max(1, m_target)
        out['f0'] = seg['f0'][:keep]
        for key in ('sp', 'ap', 'gi', 'src_rms'):
            v = seg.get(key)
            if v is not None:
                out[key] = v[:keep]
        n_smp = min(len(seg['breath']), int(round(keep * sr * fp / 1000.0)))
        out['breath'] = seg['breath'][:n_smp].copy()
        return out

    # ── extend: continue using the segment tail as template ──
    k = m_target - m
    W = min(DUR_EDGE_FRAMES, m)
    # Pitch jitter and amplitude jitter must use MUTUALLY INDEPENDENT noise: in a real
    # voice jitter/shimmer are uncorrelated; sharing one sequence would make "loud
    # moments also higher in pitch", producing an unnatural synchronized wobble.
    noise_f0 = _smooth_random_walk(k, DUR_JITTER_SMOOTH, seed=20260927)
    noise_amp = _smooth_random_walk(k, DUR_JITTER_SMOOTH, seed=20260928)
    # Jitter envelope: 1.0 at the extension start (matching the original tail's
    # fluctuation, seamless), decaying exponentially toward the tail end down to
    # DUR_TAIL_JITTER_RATIO. Measured jitter/shimmer contains larger fluctuation near
    # onsets/consonants; applying it at full depth over a long tail creates 30~50ms
    # periodic loudness/pitch waves (a "wobbly" tail).
    _ratio = float(np.clip(DUR_TAIL_JITTER_RATIO, 0.0, 1.0))
    _tau = max(1e-9, float(DUR_JITTER_DECAY_MS) / fp)
    jenv = _ratio + (1.0 - _ratio) * np.exp(
        -np.arange(k, dtype=np.float64) / _tau)

    f0 = seg['f0']
    voiced_tail = f0[-W:][f0[-W:] > 0.0]
    base = float(np.median(voiced_tail)) if len(voiced_tail) else 0.0
    if base > 0.0:
        j_amp = (float(seg.get('jitter_local', 0.0)) + JITTER_EXTRA_PCT) / 100.0
        f0_ext = base * (1.0 + j_amp * noise_f0 * jenv)
        ramp_n = min(k, W)
        if ramp_n > 0:
            t = np.linspace(0.0, 1.0, ramp_n, dtype=np.float64)
            w = 3.0 * t ** 2 - 2.0 * t ** 3
            f0_last = float(f0[-1]) if f0[-1] > 0.0 else base
            f0_ext[:ramp_n] = (1.0 - w) * f0_last + w * f0_ext[:ramp_n]
    else:
        f0_ext = np.zeros(k, dtype=np.float64)
    out['f0'] = np.concatenate([f0, f0_ext])

    s_amp = (float(seg.get('shimmer_local', 0.0)) + SHIMMER_EXTRA_PCT) / 100.0
    amp = np.clip(1.0 + s_amp * noise_amp * jenv, 0.2, 3.0)

    if seg.get('sp') is not None:
        sp_mean = seg['sp'][-W:].mean(axis=0)
        # sp is pyworld cheaptrick's LINEAR POWER spectrum (not the log domain!), so the
        # overall amplitude gain amp must be MULTIPLIED in the linear domain. A previous
        # version mistakenly used +log(amp): the true value of silent bins is about 1e-12,
        # so adding a ±0.05 log(amp) turned frames with amp>1 into ~0.05 directly (bright
        # vertical stripes from a noise floor 80dB higher) and pushed frames with amp<1
        # negative, where the floor lifted them back (dark stripes), slowly waving with the
        # random walk to form a 25~50ms periodic grid. Multiplying weak bins by amp, even
        # if slightly below SP_FLOOR, is uniformly raised by the clip in run(), so no
        # frame-to-frame steps are produced anymore.
        out['sp'] = np.concatenate(
            [seg['sp'], np.tile(sp_mean, (k, 1)) * amp[:, None]])
    if seg.get('ap') is not None:
        ap_mean = seg['ap'][-W:].mean(axis=0)
        out['ap'] = np.concatenate([seg['ap'], np.tile(ap_mean, (k, 1))])
    if seg.get('gi') is not None:
        gi_mean = float(np.mean(seg['gi'][-W:]))
        out['gi'] = np.concatenate([seg['gi'], np.full(k, gi_mean) * amp])
    if seg.get('src_rms') is not None:
        r_mean = float(np.mean(seg['src_rms'][-W:]))
        out['src_rms'] = np.concatenate([seg['src_rms'], np.full(k, r_mean)])

    w_smp = int(round(W * sr * fp / 1000.0))
    tmpl = seg['breath'][max(0, len(seg['breath']) - w_smp):]
    k_smp = int(round(k * sr * fp / 1000.0))
    out['breath'] = np.concatenate([seg['breath'], _loop_tile(tmpl, k_smp, sr)])

    return out


def _parse_beat_pos(v, beats_per_bar: float) -> float:
    """Parse a beat position into a 1-based float beat number. Accepts a number or a "bar:beat" string (e.g. "2:3")."""
    if isinstance(v, str):
        s = v.strip()
        if ':' in s:
            bar_s, beat_s = s.split(':', 1)
            return (float(bar_s) - 1.0) * beats_per_bar + float(beat_s)
        return float(s)
    return float(v)


def _bar_beat(b: float, beats_per_bar: float) -> str:
    """Format a 1-based beat position as "bar:beat" (handy for comparing against the score)."""
    bar = int((b - 1.0) // beats_per_bar) + 1
    beat = (b - 1.0) - (bar - 1) * beats_per_bar + 1.0
    return f"{bar}:{beat:g}"


def _pad_silence(seg: dict, head_n: int, tail_n: int, sr: int, fp: float) -> dict:
    """Pad silent frames (rests) at the head/tail of a segment.

    Silent frame = f0 0 (main signal is zeroed in the time domain after synthesis)
    + sp at SP_FLOOR (avoids pyworld NaN) + ap 1.0 (fully aperiodic)
    + gi/src_rms 0 + 0 breath samples.
    """
    head_n, tail_n = int(head_n), int(tail_n)
    if head_n <= 0 and tail_n <= 0:
        return seg
    out = dict(seg)
    n_freq = seg['sp'].shape[1] if seg.get('sp') is not None else 0

    def _blk(n, key):
        if key in ('f0', 'gi', 'src_rms'):
            return np.zeros(n, dtype=np.float64)
        if key == 'sp':
            return np.full((n, n_freq), SP_FLOOR, dtype=np.float64)
        return np.ones((n, n_freq), dtype=np.float64)          # ap

    for key in ('f0', 'gi', 'src_rms', 'sp', 'ap'):
        v = out.get(key)
        if v is None:
            continue
        parts = ([_blk(head_n, key)] if head_n > 0 else []) + [v] \
            + ([_blk(tail_n, key)] if tail_n > 0 else [])
        out[key] = np.concatenate(parts, axis=0)
    if out.get('breath') is not None:
        parts = []
        if head_n > 0:
            parts.append(np.zeros(int(round(head_n * sr * fp / 1000.0))))
        parts.append(out['breath'])
        if tail_n > 0:
            parts.append(np.zeros(int(round(tail_n * sr * fp / 1000.0))))
        out['breath'] = np.concatenate(parts)
    return out


def _attack_head_index(seg: dict):
    """Frame index (into the segment's kept frames) where the attack starts, skipping a near-silent lead.

    Returns None when the level is unusable, else the index of the first frame whose source
    RMS rises above 2% of the segment's median level (at most ATTACK_LEAD_FRAMES frames in).
    0 = the segment starts directly with the attack.
    """
    rms = np.asarray(seg.get('src_rms', np.zeros(0)), dtype=np.float64)
    if rms.size < ATTACK_HEAD_FRAMES + ATTACK_DECAY_FRAMES:
        return None
    level = float(np.median(rms))
    if level <= 0.0:
        return None
    quiet = rms <= level * 0.02
    s = 0
    while s < min(len(rms), ATTACK_LEAD_FRAMES) and quiet[s]:
        s += 1
    return s


def _is_attack_head(seg: dict) -> bool:
    """True when the segment starts with a stop-consonant burst instead of a sustained vowel.

    The seam cross-fade gives the incoming segment weight 0 at the seam and ramps it up
    over the whole overlap. The incoming segment's first frames ARE its attack (the
    source's leading silence is dropped by the decomposer, so the first kept frame is
    the burst peak). The fade therefore erases it and two consecutive カ lose the
    second one's burst. Criteria (all must hold):
      · the attack peak is well below the note's own level (a burst, not a vowel),
      · the level falls right after the peak (a 1~2 frame transient, not a gradual fade-in).
    """
    s = _attack_head_index(seg)
    if s is None:
        return False
    rms = np.asarray(seg['src_rms'], dtype=np.float64)
    if s + ATTACK_HEAD_FRAMES + ATTACK_DECAY_FRAMES > len(rms):
        return False
    level = float(np.median(rms))
    head = float(np.max(rms[s:s + ATTACK_HEAD_FRAMES]))
    if head <= level * 1e-3 or head > level * ATTACK_QUIET_RATIO:
        return False
    decay = float(np.min(rms[s + ATTACK_HEAD_FRAMES:
                            s + ATTACK_HEAD_FRAMES + ATTACK_DECAY_FRAMES]))
    return decay <= head * ATTACK_DECAY_RATIO


def _attack_head_pad(seg: dict) -> int:
    """Silent frames to insert before an attack-protected note's head.

    The burst must start from a closure, otherwise the previous note's (louder) tail
    masks it: the seam cross-fade alone cannot remove that tail at the seam frame.
    The source's own lead already provides part of it, so only the missing part is padded.
    """
    s = _attack_head_index(seg)
    if s is None:
        return 0
    return max(0, ATTACK_LEAD_FRAMES - s)


def _attack_weights(over: int) -> tuple:
    """Weights (w_outgoing, w_incoming) for an attack-protected seam.

    The incoming note is at full weight from its first frame (its burst survives),
    while the outgoing note's tail is cut within ATTACK_CLIP_FRAMES frames. The rest
    of the overlap (up to ATTACK_LEAD_FRAMES) is then a true closure before the burst.
    """
    w_in = np.ones(over, dtype=np.float64)
    w_out = np.zeros(over, dtype=np.float64)
    k = min(over, ATTACK_CLIP_FRAMES)
    if k > 0:
        w_out[:k] = 1.0 - _bezier_weights(k)
    return w_out, w_in


def _apply_beat_timeline(segs: list, seg_cfgs: list, sr: int, fp: float) -> tuple:
    """Anchor each segment's onset to the beat grid per beat_start/beat_end.

    Returns (overs, protects): per-seam overlap frame counts and whether the seam must
    protect the incoming note's attack (see _is_attack_head / _attack_weights).

    Let spb = frames/beat, P(b) = start frame position, L = the note's sounding frame
    count, D = the gap to the next segment's start:
        S_{i+1} - S_i = M_i - ov_{i+1} = D_i     (M_i = frames fed into the splice)
    So ov_{i+1} only sets the fade width and never shifts the beat position; take
        ov_{i+1} = max( beat-range overlap max(0, L_i - D_i) , transition_{i+1} )
        M_i = D_i + ov_{i+1} (the first segment also adds the head silence before beat_start)
    M_i - L_i > 0 means a rest after that note (silence padded at the tail); the last
    segment has M = L. Since every onset is anchored to a beat position, the total
    output length is exactly the duration corresponding to the final beat position.
    """
    n_seg = len(segs)
    if n_seg < 2:
        raise ValueError("Beat layout requires at least 2 segments")
    if not BPM:
        raise ValueError("beat_start/beat_end given but BPM is not set")
    bpb = float(TIME_SIGNATURE[0])
    spb = 60000.0 / (float(BPM) * float(fp))                   # frames/beat

    bs = [_parse_beat_pos(c['beat_start'], bpb) for c in seg_cfgs]
    be = [_parse_beat_pos(c['beat_end'], bpb) for c in seg_cfgs]
    for i in range(n_seg):
        if be[i] <= bs[i]:
            raise ValueError(f"SEGMENTS[{i}] beat_end({be[i]:g}) must be greater than "
                             f"beat_start({bs[i]:g})")

    P = [int(round((b - 1.0) * spb)) for b in bs]              # start frame position of each segment
    L = [max(1, int(round((be[i] - bs[i]) * spb))) for i in range(n_seg)]
    D = [P[i + 1] - P[i] for i in range(n_seg - 1)]

    overs = [0] * n_seg
    protects = [False] * n_seg
    M = [0] * n_seg
    for i in range(n_seg - 1):
        tr = int(seg_cfgs[i + 1].get('transition', 0) or 0)
        ov = max(max(0, L[i] - D[i]), tr)
        # 音符首尾相接（D<=L，没有真实休止）时给一个最小交叉淡化宽度：
        # 完全没有重叠会让 sp/ap/gi 与气声在接缝处硬拼，产生咔哒/爆音。
        # 有真实休止（D>L）时不补，否则会把静音尾巴与下一段混在一起。
        if D[i] <= L[i]:
            ov = max(ov, MIN_SEAM_OVER_FRAMES)
        # 进入段以"静音/闭合 → 爆破"起步时，交叉淡化会把它的起音按 0→1 淡入抹掉
        # （两个カ音连击时第二个的爆破消失）。这类接缝改走起音保护权重：进入段首帧即全权重，
        # 前一段尾巴在 ATTACK_FADE_FRAMES 内退出，过渡宽度也就收缩到该值。
        if _is_attack_head(segs[i + 1]):
            protects[i + 1] = True
            ov = min(ov, ATTACK_FADE_FRAMES)
        ov = min(ov, L[i], L[i + 1])                           # not longer than either side's note
        overs[i + 1] = ov
        M[i] = D[i] + ov
    M[0] += P[0]                                               # blank before the first segment -> head silence
    M[n_seg - 1] = L[n_seg - 1]

    print(f"[Beat] BPM={BPM}  time signature={TIME_SIGNATURE[0]}/{TIME_SIGNATURE[1]}  "
          f"{spb:.4g}frames/beat({60000.0 / float(BPM):.0f}ms/beat)")
    for i in range(n_seg):
        if i == 0:
            head_n = P[0]
        elif protects[i] and overs[i] > 0:
            # 起音保护接缝：给进入段补一小段静音，让它的爆破从"闭合"起步；
            # 这段静音正好落在交叉淡化区内，前一段的尾音在这里退出。
            # 有真实休止（ov=0）时不需要补：音符本来就从静音开始。
            head_n = min(_attack_head_pad(segs[i]), max(0, M[i] - 1))
        else:
            head_n = 0
        # True rest = gap between the beat ranges (the D > L portion) -> pad silence
        # at the tail; when D <= L, frames beyond L come from transition's extra fade
        # width -> go through sounding extension (true cross-fade). Otherwise the
        # overlap would become "previous segment silent, next segment fading in",
        # creating an energy dip (perceived as a gap).
        gap_i = max(0, D[i] - L[i]) if i < n_seg - 1 else 0
        tail_n = min(gap_i, max(0, M[i] - head_n))
        body_n = M[i] - head_n - tail_n
        before = len(segs[i]['f0'])
        segs[i] = _fit_duration(segs[i], body_n, sr, fp)
        segs[i] = _pad_silence(segs[i], head_n, tail_n, sr, fp)
        act = "trimmed" if body_n < before else ("extended" if body_n > before else "unchanged")
        extra = []
        if head_n > 0:
            extra.append(f"head silence {head_n} frames({head_n * fp:.0f}ms)")
        if tail_n > 0:
            extra.append(f"rest {tail_n} frames({tail_n * fp:.0f}ms)")
        if overs[i] > 0:
            extra.append(f"overlap with previous note {overs[i]} frames({overs[i] * fp:.0f}ms)"
                         + (" attack-protected cross-fade" if protects[i] else " cross-fade"))
        print(f"[Beat] segment {i + 1}: beat {bs[i]:g}({_bar_beat(bs[i], bpb)}) → "
              f"beat {be[i]:g}({_bar_beat(be[i], bpb)})  "
              f"beat length {L[i]} frames({L[i] * fp:.0f}ms) → material {body_n} frames({act}) "
              f"→ fed {M[i]} frames"
              + ("  " + " + ".join(extra) if extra else ""))
    total = sum(M) - sum(overs)
    print(f"[Beat] output {total} frames({total * fp:.0f}ms), end beat "
          f"beat {be[-1]:g}({_bar_beat(be[-1], bpb)})")
    return overs, protects


def _plosive_gain(breath: np.ndarray, f0: np.ndarray, sr: int, fp: float,
                  ratio: float, duck_gain: float, smooth: int,
                  margin_head: int = 0, margin_tail: int = 6,
                  keep_burst: bool = True, min_grad_db: float = 12.0,
                  min_crest: float = 5.0, max_valley: float = 0.3) -> tuple:
    """Auto-detect breath plosive frames (three features AND); returns (per-frame glottal gain, plosive seed mask).

    A real plosive (p/t/k) has the shape closure -> release transient -> aspiration
    tail, quite unlike a sustained aspiration plateau. A single RMS threshold
    misjudges aspiration as a plosive (measured: ei/bie contain no plosive yet
    triggered 275 candidate frames), so three features are combined and all must
    pass before a frame is a plosive seed:
      feature 1 energy burst   : frame breath RMS > voiced-frame median × ratio
      feature 2 steep attack   : energy jump from the adjacent frame ≥ min_grad_db dB (transient, not gradual)
      feature 3 transient shape: in-frame crest factor peak/RMS ≥ min_crest (sharp peak)
                                 and minimum energy in the preceding 40ms < current frame × max_valley (a closure valley)
    No seeds at all -> the audio is automatically judged plosive-free and an all-1
    gain is returned (no ducking).

    Dilation direction is controlled separately: seed frames extend FORWARD by
    margin_head frames (closure/burst lead-in) and BACKWARD by margin_tail frames
    (aspiration/vowel onset). With keep_burst=True the ducked region excludes the
    seed frames themselves -- the burst transient (the frame with the steepest
    energy rise) is exactly what makes a plosive clear, and ducking it too would
    smear the plosive, so it is kept by default.
    """
    n = len(f0)
    gain = np.ones(n, dtype=np.float64)
    no_seed = np.zeros(n, dtype=bool)
    voiced = f0 > 0
    if not np.any(voiced):
        return gain, no_seed
    # Per-frame breath RMS + crest factor (exact frame boundaries)
    rms = np.zeros(n, dtype=np.float64)
    crest = np.zeros(n, dtype=np.float64)
    for i in range(n):
        s = _frame_bound(i, sr, fp)
        e = min(_frame_bound(i + 1, sr, fp), len(breath))
        if e > s:
            seg = breath[s:e]
            r = np.sqrt(np.mean(seg ** 2))
            rms[i] = r
            crest[i] = np.max(np.abs(seg)) / r if r > 1e-12 else 0.0
    med = np.median(rms[voiced])
    if med <= 0:
        return gain, no_seed
    # Feature 1: energy burst candidates
    cand = voiced & (rms > med * ratio)
    seed = np.zeros(n, dtype=bool)
    lead = max(1, int(round(40.0 / fp)))            # 40ms lead-in window
    for i in np.where(cand)[0]:
        # Feature 2: steep rising edge (energy jump in dB vs. previous frame)
        if i < 1 or rms[i - 1] <= 0:
            continue
        grad_db = 20.0 * np.log10(rms[i] / rms[i - 1])
        if grad_db < min_grad_db:
            continue
        # Feature 3a: transient peak (crest factor)
        if crest[i] < min_crest:
            continue
        # Feature 3b: preceding closure valley
        w = rms[max(0, i - lead):i]
        if len(w) == 0 or w.min() >= rms[i] * max_valley:
            continue
        seed[i] = True
    if not np.any(seed):
        print(f"[Plosive detect] {int(cand.sum())} candidate frames show no transient shape "
              f"(attack≥{min_grad_db}dB & crest≥{min_crest} & closure valley<{max_valley}) "
              f"→ no plosive in this audio, no ducking")
        return gain, no_seed
    # Dilation by direction: forward margin_head frames (closure->burst), backward margin_tail frames (aspiration->vowel)
    burst = seed.copy()
    mh = max(0, int(margin_head))
    for _ in range(mh):
        burst[1:] |= burst[:-1]
    mt = max(0, int(margin_tail))
    for _ in range(mt):
        burst[:-1] |= burst[1:]
    duck = (burst & ~seed) if keep_burst else burst      # keep burst transient frames unducked
    gain[duck] = duck_gain
    # Smoothing (moving average) makes both edges of the ducked region gradual
    # instead of hard-cut; edges use replicate padding so np.convolve's zero padding
    # does not wrongly duck the first/last frames
    k = max(1, int(smooth))
    if k % 2 == 0:
        k += 1
    if k > 1:
        half = k // 2
        padded = np.pad(gain, half, mode="edge")
        gain = np.convolve(padded, np.ones(k) / k, mode="valid")
        gain = np.clip(gain, duck_gain, 1.0)
    # Smoothing spreads the attenuation of adjacent ducked frames onto the burst
    # frames (a single seed frame would be spread to ~0.44×); restore the burst
    # frames' gain to 1.0 here so the transient energy is not weakened
    if keep_burst and np.any(seed):
        gain[seed] = 1.0
    seed_frames = np.where(seed)[0].tolist()
    print(f"[Plosive detect] {int(seed.sum())} seed frames {seed_frames[:8]}"
          f"{'...' if len(seed_frames) > 8 else ''}, "
          f"dilate head {mh} frames/tail {mt} frames{' (burst kept)' if keep_burst else ''} "
          f"→ duck {int(duck.sum())} frames to {duck_gain}×")
    return gain, seed


def _transient_env_gain(main_rms: np.ndarray, src_rms: np.ndarray, seed: np.ndarray,
                        head: int, tail: int, g_min: float, g_max: float,
                        ramp: int = 3) -> np.ndarray:
    """Per-frame energy envelope gain inside the plosive window (transplants the source audio's transient shape onto the synthesized main signal).

    pyworld's per-frame (5ms) spectral reconstruction flattens the plosive's steep
    rise (source 10%->90% in only 4ms -> 18ms after synthesis); the energy remains
    but the impact is gone.

    The source envelope's relative shape is transplanted with the BURST frame as
    anchor:
        gain[i] = (source[i]/source[burst frame]) / (synthesized[i]/synthesized[burst frame])
    The burst frame gain is always 1 (the plosive's own level is unchanged); other
    frames are scaled by the relation between the source envelope and the burst
    peak -- closure segments are darkened and gradual-rise segments flattened,
    restoring the source audio's "silence->burst" contrast.

    Note that "window energy normalization" must not be used: the synthesized
    per-frame envelope is much flatter than the source (which is itself the
    smearing), and normalizing by the window mean would lift the whole window and
    actually reduce the contrast.

    The gain is clamped to [g_min, g_max], is 1.0 outside the window, and linear
    ramps over ramp frames at both ends avoid hard-cut clicks.
    """
    n = len(main_rms)
    g = np.ones(n, dtype=np.float64)
    if not np.any(seed):
        return g
    mw = seed.copy()
    for _ in range(max(0, int(head))):
        mw[1:] |= mw[:-1]
    for _ in range(max(0, int(tail))):
        mw[:-1] |= mw[1:]
    idx = np.where(mw)[0]
    a, b = int(idx[0]), int(idx[-1]) + 1
    m = np.asarray(main_rms[a:b], dtype=np.float64)
    s = np.asarray(src_rms[a:b], dtype=np.float64)
    # Anchor = strongest burst frame (the seed frame with the largest source RMS)
    sf = np.where(seed)[0]
    sf = sf[(sf >= a) & (sf < b)]
    if len(sf) == 0:
        return g
    si = int(sf[int(np.argmax(src_rms[sf]))])
    s_ref, m_ref = float(src_rms[si]), float(main_rms[si])
    if s_ref <= 1e-9 or m_ref <= 1e-9:
        return g
    ratio = (s / s_ref) / np.maximum(m / m_ref, 1e-9)
    g[a:b] = np.clip(ratio, g_min, g_max)
    # Linear transition to 1.0 outside the window
    r = max(1, int(ramp))
    for j in range(r):
        w = (j + 1.0) / (r + 1.0)
        if a - 1 - j >= 0:
            g[a - 1 - j] = 1.0 - w * (1.0 - g[a])
        if b + j < n:
            g[b + j] = 1.0 - w * (1.0 - g[b - 1])
    print(f"[Plosive transient transplant] window {a}~{b - 1} frames ({a * 5}~{b * 5}ms), anchor frame {si} "
          f"({si * 5}ms), gain {g[a:b].min():.2f}~{g[a:b].max():.2f}×"
          f" (transplanting the source envelope's relative shape, burst frame gain=1)")
    return g


def _frame_gain_to_samples(g: np.ndarray, sr: int, fp: float,
                           n_samples: int) -> np.ndarray:
    """Per-frame gain -> sample-domain gain (linear interpolation sampled at FRAME CENTERS).

    Interpolating per-frame values directly into the sample domain pulls each
    frame's samples toward the neighboring frames' gains (the frame start takes the
    current value, the frame end the next value), averaging away roughly 20% of a
    burst frame with the ducking values on both sides. Interpolating aligned to
    frame centers makes the gain at each frame center exactly equal that frame's
    value, so transient frame levels are not diluted.
    """
    nf = len(g)
    if nf == 0:
        return np.ones(n_samples, dtype=np.float64)
    pos = (np.arange(nf, dtype=np.float64) + 0.5) * sr * fp / 1000.0
    x = np.arange(n_samples, dtype=np.float64)
    return np.interp(x, pos, g, left=g[0], right=g[-1])


def _refine_voiced_mask(f0: np.ndarray, sp: np.ndarray, breath_all: np.ndarray,
                        sr: int, fp: float,
                        min_keep: int = 16,
                        max_trim: int = 40,
                        max_trim_frac: float = 0.05) -> tuple:
    """Breath localization + multi-way validation: trim "near-silent fake voiced frames" at voiced-frame edges.

    Identification criteria (three independent features that cross-check each other):
      feature 1 F0 voiced frame : f0 > 0 (pyworld dio/stonemask flag)
      feature 2 SP spectral energy : per-frame sp row sum ≥ voiced-frame median × 1%
      feature 3 breath RMS      : per-frame breath window RMS ≥ voiced-frame peak × 1%
    Rules:
      - A voiced frame counts as fake only if it has NEITHER SP energy NOR breath energy;
      - Trimming happens only at the head/tail of each voiced run (so internal silent
        frames are not removed by mistake), capped at max_trim frames per side;
      - If the trimmed amount > run length × max_trim_frac or the remaining frames <
        min_keep, validation is considered failed and everything falls back to the
        original f0 voiced mask (conservative, no trimming).

    Returns:
        (mask, diag)
        mask: refined voiced mask with the same length as f0
        diag: diagnostic dict (feature positions / trim amounts / internal holes /
              verdict); None when there are no voiced frames
    """
    vf = np.asarray(f0, dtype=np.float64) > 0.0
    n = len(vf)
    if not np.any(vf):
        return vf, None

    # ── frame-level feature extraction (same frame grid as f0) ──
    n_br = min(n, int(len(breath_all) * 1000.0 / (sr * fp)))
    ebr = np.zeros(n, dtype=np.float64)
    for i in range(n_br):
        s = _frame_bound(i, sr, fp)
        e = _frame_bound(i + 1, sr, fp)
        seg = breath_all[s:e]
        ebr[i] = float(np.sqrt(np.mean(seg ** 2))) if e > s else 0.0
    if sp is not None and sp.ndim == 2 and sp.shape[0] == n:
        esp = np.asarray(sp, dtype=np.float64).sum(axis=1)
        has_sp = True
    else:
        esp = np.ones(n, dtype=np.float64)   # without SP, decide by breath RMS alone
        has_sp = False

    idxv = np.where(vf)[0]
    esp_med = float(np.median(esp[idxv])) if has_sp else 1e-9
    ebr_max = float(np.max(ebr[idxv])) if len(idxv) else 0.0
    low_esp = max(0.01 * esp_med, 1e-9) if has_sp else -1.0   # without SP this criterion is ignored
    low_ebr = max(0.01 * ebr_max, 1e-9)

    # ── head/tail trimming per voiced run ──
    mask = vf.copy()
    d = np.diff(np.concatenate(([0], vf.astype(np.int8), [0])))
    starts = np.where(d == 1)[0]
    ends = np.where(d == -1)[0]
    trim_head = trim_tail = 0
    for lo, hi in zip(starts, ends):
        run_len = int(hi - lo)
        margin = min(max_trim, max(2, run_len // 6))
        nl = lo
        while nl < hi and (nl - lo) < margin and esp[nl] < low_esp and ebr[nl] < low_ebr:
            nl += 1
        nh = hi
        while nh > nl and (hi - nh) < margin and esp[nh - 1] < low_esp and ebr[nh - 1] < low_ebr:
            nh -= 1
        mask[lo:nl] = False
        mask[nh:hi] = False
        trim_head += int(nl - lo)
        trim_tail += int(hi - nh)

    total_voiced = int(vf.sum())
    kept = int(mask.sum())
    # ── fallback protection: excessive trimming counts as validation failure ──
    fallback = False
    if kept < max(min_keep, int((1.0 - max_trim_frac) * total_voiced)):
        mask = vf.copy()
        kept = total_voiced
        trim_head = trim_tail = 0
        fallback = True

    keep_idx = np.where(mask)[0]
    # Feature-valid frames (at least one energy path) -- record original frame indices, not run-local ones
    esp_ok = (esp >= low_esp) if has_sp else np.zeros(n, dtype=bool)
    ebr_ok = ebr >= low_ebr
    any_ok = esp_ok | ebr_ok
    holes = [int(i) for i in keep_idx if not any_ok[i]]
    sp_frames = keep_idx[esp_ok[keep_idx]] if has_sp else np.array([], dtype=int)
    br_frames = keep_idx[ebr_ok[keep_idx]]

    diag = {
        'total_voiced': total_voiced, 'kept': kept,
        'trim_head': trim_head, 'trim_tail': trim_tail,
        'trim_total': trim_head + trim_tail,
        'first_voiced': int(idxv[0]), 'last_voiced': int(idxv[-1]),
        'first_kept': int(keep_idx[0]) if len(keep_idx) else -1,
        'last_kept': int(keep_idx[-1]) if len(keep_idx) else -1,
        'sp_first': int(sp_frames[0]) if len(sp_frames) else -1,
        'sp_last': int(sp_frames[-1]) if len(sp_frames) else -1,
        'br_first': int(br_frames[0]) if len(br_frames) else -1,
        'br_last': int(br_frames[-1]) if len(br_frames) else -1,
        'holes': holes,
        'has_sp': has_sp,
        'fallback': fallback,
    }
    return mask, diag


def _phase_vocoder_pitch_shift(data: np.ndarray, sr: int, ratio: float,
                               frame_ms: float, hop_ms: float) -> np.ndarray:
    """Breath pitch shift (length-preserving): granular OLA time stretch (ratio) -> resample back to the original length -> RMS normalize.

    Note: an FFT phase vocoder must not be used for time stretching -- breath is
    essentially noise with random phase in every band, and phase unwrapping/
    accumulation creates phase discontinuities that show up as formant "shattering"
    (comb-like modulation artifacts) in the spectrogram. Granular OLA (Hann-window
    overlap-add, a zero-phase operation) is safe for noise.

    Steps:
      1. OLA stretch: analysis hop=Ha, synthesis hop=Ha*ratio (shift up -> stretch, pitch unchanged)
      2. resample_poly back to the original length n -> time compression -> frequency ×ratio overall
      3. RMS normalization back to the input level
    When ratio==1, a copy of the input array is returned directly.
    """
    n = len(data)
    if n < 4 or abs(ratio - 1.0) < 1e-4:
        return data.copy()
    from math import gcd
    from scipy.signal import get_window, resample_poly

    frame_len = int(sr * frame_ms / 1000.0)
    hop = int(sr * hop_ms / 1000.0)
    if frame_len < 4 or hop < 1:
        return data.copy()
    win = get_window('hann', frame_len)

    # ── 1) granular OLA time stretch, factor = ratio ──
    # reflect-pad both ends to avoid edge holes
    x = np.concatenate([data[:frame_len][::-1], data, data[-frame_len:][::-1]])
    n_frames = 1 + (len(x) - frame_len) // hop
    out_len = int((n_frames - 1) * hop * ratio) + frame_len
    y = np.zeros(out_len, dtype=np.float64)
    win_sum = np.zeros(out_len, dtype=np.float64)
    for i in range(n_frames):
        s_ana = i * hop
        s_syn = int(round(i * hop * ratio))
        if s_syn + frame_len > out_len:
            break
        y[s_syn:s_syn + frame_len] += x[s_ana:s_ana + frame_len] * win
        win_sum[s_syn:s_syn + frame_len] += win
    nz = win_sum > 1e-8
    y[nz] /= win_sum[nz]
    # cut out the valid data region (the stretched version of the original data, length ≈ n*ratio)
    start = int(round(frame_len * ratio)) - frame_len // 2
    stretched = y[max(0, start): max(0, start) + int(round(n * ratio))]
    if len(stretched) < 2:
        return data.copy()

    # ── 2) resample back to the original length n -> frequency ×ratio ──
    g = gcd(len(stretched), n) or 1
    up, down = n // g, len(stretched) // g
    if down < 100000 and up < 100000:
        pitched = resample_poly(stretched, up, down)
    else:
        pitched = np.interp(np.linspace(0, len(stretched) - 1, n),
                            np.arange(len(stretched)), stretched)
    pitched = _align_samples(np.asarray(pitched, dtype=np.float64), n)

    # ── 3) RMS normalization back to the input level ──
    rms_in = np.sqrt(np.mean(data ** 2))
    rms_out = np.sqrt(np.mean(pitched ** 2))
    if rms_out > 1e-12:
        pitched *= rms_in / rms_out
    return pitched


def _match_breath_envelope(breath: np.ndarray, sp: np.ndarray, sr: int, fp: float,
                           fft_size: int, n_bands: int,
                           g_min: float, g_max: float) -> np.ndarray:
    """Shape the breath's spectral envelope to that of the main signal sp (formant shape only).

    Why: the breath is noise cut from the source library and carries the SOURCE's
    formants, whereas the main signal's formants have already moved according to
    the target pitch/FOLLOW_RATIO. The mismatched formants cause a disjointed
    perception.

    Implementation notes:
      1. Per frame (5ms), map both the breath power spectrum and sp onto LOG BANDS
         (80Hz->Nyquist, geometric division) -- a noise frame's magnitude spectrum
         fluctuates strongly bin by bin, so it must be averaged within a band
         before the two can be compared.
      2. For each STFT frame (window 8 frames/40ms, hop 2 frames/10ms, Hann), use
         the mean sp band envelope over the covered range as the target; the bin
         gain is sqrt(target band energy / breath band energy), clamped before it
         is applied.
      3. Hann window + hop=window length/4 -> constant window sum, so overlap-add
         introduces no periodic amplitude modulation.

    Key point: only the spectral shape changes; the breath's own time-domain
    energy envelope (breath timing/loudness fluctuation) is untouched, and the
    result is finally normalized back to the original breath RMS. Gain clamping
    keeps individual bands from being over-suppressed/lifted.
    """
    n = min(len(breath), int(round(len(sp) * sr * fp / 1000.0)))
    if n <= 0 or sp is None:
        return breath
    n_frames, nb = sp.shape
    if nb == fft_size // 2 + 1:
        freqs = np.fft.rfftfreq(fft_size, 1.0 / sr)
    else:
        freqs = np.linspace(0.0, sr / 2.0, nb)
    x = np.asarray(breath[:n], dtype=np.float64)

    # ── log band edges ──
    edges = np.geomspace(80.0, sr / 2.0, n_bands + 1)
    s = np.searchsorted(freqs, edges)
    s[0] = 0
    for j in range(n_bands):
        s[j + 1] = max(int(s[j + 1]), int(s[j]) + 1)
    s = np.clip(s, 0, nb)

    # ── target band envelope E_t[i, j] (computed from each sp frame) ──
    E_t = np.zeros((n_frames, n_bands), dtype=np.float64)
    for i in range(n_frames):
        row = sp[i]
        for j in range(n_bands):
            lo, hi = int(s[j]), min(int(s[j + 1]), nb)
            E_t[i, j] = np.mean(row[lo:hi]) if hi > lo else row[min(lo, nb - 1)]

    # ── STFT / OLA shaping (window length 8 frames, hop 2 frames -> 4x overlap, constant Hann window sum) ──
    win_len = int(round(8 * sr * fp / 1000.0))
    hop = max(1, win_len // 4)
    win = np.hanning(win_len)
    pad = win_len
    xp = np.concatenate([x[0] * np.ones(pad), x, x[-1] * np.ones(pad)])
    n_stft = 1 + max(0, (len(xp) - win_len) // hop)
    shaped = np.zeros(len(xp), dtype=np.float64)
    wsum = np.zeros(len(xp), dtype=np.float64)

    for k in range(n_stft):
        a = k * hop
        seg = xp[a:a + win_len]
        X = np.fft.rfft(seg * win, fft_size)
        P = np.abs(X) ** 2
        # Covered pyworld frame range (breath sample position -> frame index)
        c0 = a - pad
        f0i = int(np.floor(c0 / (sr * fp / 1000.0)))
        f1i = int(np.ceil((c0 + win_len) / (sr * fp / 1000.0)))
        f0i = max(0, min(f0i, n_frames - 1))
        f1i = max(f0i + 1, min(f1i, n_frames))
        tgt = E_t[f0i:f1i].mean(axis=0)
        for j in range(n_bands):
            lo, hi = int(s[j]), min(int(s[j + 1]), nb)
            if hi <= lo:
                continue
            gb = float(np.mean(P[lo:hi]))
            g = np.sqrt(float(tgt[j]) / gb) if gb > 1e-20 else 1.0
            P[lo:hi] *= float(np.clip(g, g_min, g_max))
        y = np.fft.irfft(np.sqrt(P) * np.exp(1j * np.angle(X)), fft_size)[:win_len]
        shaped[a:a + win_len] += y * win
        wsum[a:a + win_len] += win ** 2

    nz = wsum > 1e-8
    shaped[nz] /= wsum[nz]
    shaped = shaped[pad:pad + n]
    # Spectral-shape shaping only, overall energy unchanged: normalize back to the original breath RMS
    r_in = np.sqrt(np.mean(x ** 2))
    r_out = np.sqrt(np.mean(shaped ** 2))
    if r_out > 1e-12:
        shaped *= r_in / r_out
    shaped = _edge_fade(shaped, sr, 2.0)       # remove OLA edge jumps
    return shaped


def _blend_overlap(a: np.ndarray, b: np.ndarray, over: int,
                   protect_head: bool = False) -> np.ndarray:
    """Splice two parameter segments along the frame axis with a Bézier overlap.

    a: segment A (ma, ...)    b: segment B (mb, ...)   over: overlap frames
    Output length = ma + mb - over; the overlap region is mixed with cubic Bézier
    weights. Supports 1D (frames,) and 2D (frames, features) arrays.
    protect_head=True uses the attack weights instead (see _attack_weights): B is at
    full weight from its first frame so its burst/onset survives the seam.
    """
    ma, mb = a.shape[0], b.shape[0]
    over = max(0, min(int(over), ma, mb))
    out_len = ma + mb - over
    if over == 0:
        return np.concatenate([a, b], axis=0)

    shape = (out_len,) + a.shape[1:]
    out = np.zeros(shape, dtype=np.float64)
    start_b = ma - over
    # Pure A prefix
    out[:start_b] = a[:start_b]
    # Pure B suffix (skipping B's first over frames already used in the overlap)
    out[start_b + over:] = b[over:]
    # Overlap region: B's first over frames mixed with A's last over frames via Bézier
    if protect_head:
        wA, wB = _attack_weights(over)
    else:
        wB = _bezier_weights(over)
        wA = 1.0 - wB
    for j in range(over):
        idx = start_b + j
        out[idx] = wA[j] * a[start_b + j] + wB[j] * b[j]
    return out


def _blend_overlap_f0(a: np.ndarray, b: np.ndarray, over: int) -> np.ndarray:
    """Frame-axis f0 splice for two segments.

    f0 must NOT be blended linearly: mixing an unvoiced frame (f0=0) with a voiced one
    (e.g. 392 Hz) linearly creates a false fundamental (0.7*392 ≈ 274 Hz, 0.3*392 ≈ 118 Hz)
    inside the overlap, and the vocoder renders it as a loud periodic burst.
    Rules used here:
      - both sides voiced  -> interpolate in the semitone domain (perceptually linear)
      - one side unvoiced -> keep the voiced side (the overlap stays voiced; the real
                             unvoiced part appears after the overlap, as in speech)
      - both unvoiced     -> 0
    """
    ma, mb = a.shape[0], b.shape[0]
    over = max(0, min(int(over), ma, mb))
    out_len = ma + mb - over
    if over == 0:
        return np.concatenate([a, b], axis=0)

    out = np.zeros(out_len, dtype=np.float64)
    start_b = ma - over
    out[:start_b] = a[:start_b]
    out[start_b + over:] = b[over:]

    a_ov = np.asarray(a[start_b:], dtype=np.float64)
    b_ov = np.asarray(b[:over], dtype=np.float64)
    a_ok = np.isfinite(a_ov) & (a_ov > 0.0)
    b_ok = np.isfinite(b_ov) & (b_ov > 0.0)
    both = a_ok & b_ok
    ov = np.where(a_ok, a_ov, np.where(b_ok, b_ov, 0.0))   # unvoiced/invalid side yields to the voiced side
    if both.any():
        c4_hz = 440.0 * 2.0 ** ((60 - 69) / 12.0)
        w = _bezier_weights(over)
        sa = 12.0 * np.log2(a_ov[both] / c4_hz)
        sb = 12.0 * np.log2(b_ov[both] / c4_hz)
        ov[both] = c4_hz * 2.0 ** ((sa + (sb - sa) * w[both]) / 12.0)
    out[start_b:start_b + over] = ov
    return out


def _apply_pitch_line(f0: np.ndarray, n_out: int, sr: int, fp: float,
                      pitch_line: list):
    """Override f0 frame by frame according to the waypoint pitch line (splicing is unchanged; only pitch is modified).

    PITCH_LINE: [(beat position, pitch), ...]
      beat position : 1-based, same coordinate system as SEGMENTS (accepts "2:3";
                     converted to frame index via BPM/TIME_SIGNATURE); endpoint
                     frame position = round((beat position-1) * frames/beat)
      pitch : note name ("G4") / Hz value / None / "KEEP"

    Interval semantics (the value of interval [t_i, t_{i+1}) is decided by its
    LEFT endpoint):
      v_i = None               -> interval is silent (f0=0, and marked in the returned mask)
      v_i value, v_{i+1} value -> cubic Bézier (horizontal endpoint tangents, i.e. smoothstep)
                                  sampled per frame, gliding from v_i to v_{i+1} (portamento)
      v_i value, v_{i+1} = None -> interval holds v_i (silence starts at the next endpoint)
      v_{i+1} = "KEEP"         -> this interval is UNCOVERED; keep the spliced original f0
                                 (the pitch line cuts a gap); a "KEEP" endpoint usually comes
                                 paired at the same beat position with the next normal endpoint,
                                 e.g. [(1,"G4"),(4,"KEEP"),(4,"E4"),(7,"E4")]
                                 means beat1~4 is G4 and beat4~7 returns to the spliced result
    Pitch is interpolated in the SEMITONE domain (perceptually linear) and then
    converted back to Hz.
    Frames before the first endpoint / after the last endpoint = uncovered -> keep
    the spliced original values.

    Returns (new f0, boolean silent-frame mask).
    """
    silent = np.zeros(n_out, dtype=bool)
    if not pitch_line:
        return f0, silent

    bpb = float(TIME_SIGNATURE[0])
    spb = 60000.0 / (float(BPM) * float(fp))                   # frames/beat
    pts = []
    for item in pitch_line:
        pos, val = item[0], item[1]
        f = int(round((_parse_beat_pos(pos, bpb) - 1.0) * spb))
        pts.append((max(0, min(n_out, f)), val))
    # Stable sort: at the same beat position the paired endpoints (... ,"KEEP"),(...,note name) must keep their order
    pts.sort(key=lambda t: t[0])
    KEEP = "KEEP"

    f0_new = np.array(f0, dtype=np.float64, copy=True)
    covered = np.zeros(n_out, dtype=bool)
    c4_hz = 440.0 * 2.0 ** ((60 - 69) / 12.0)                  # C4 frequency
    # 同拍端点 = 零长度区间（前一个音的段尾锚点与后一个音的段首锚点落在同一帧）。
    # 这种区间原本会被跳过，f0 于是直接在 1 帧内跳到后一个音的音高，
    # 声码器在下一帧重排相位 -> 咔哒/爆音。这里先记录，循环结束后在边界前插入短过渡。
    jumps = {}
    for i in range(len(pts) - 1):
        f_a, v_a = pts[i]
        f_b, v_b = pts[i + 1]
        if f_b <= f_a:
            if v_a is not None and v_b is not None and v_a != KEEP and v_b != KEEP:
                jumps[f_b] = v_b      # 重复帧按排序顺序保留最后一个值
            continue
        # Pitch-line cut gap: interval is not covered and not silenced; f0 keeps the spliced original value
        if v_a == KEEP or v_b == KEEP:
            continue
        seg = slice(f_a, f_b)
        covered[seg] = True
        if v_a is None:
            silent[seg] = True
            f0_new[seg] = 0.0
            continue
        st_a = _parse_semitones(v_a)
        if v_b is None:                                        # hold until the next endpoint
            f0_new[seg] = c4_hz * 2.0 ** (st_a / 12.0)
            continue
        st_b = _parse_semitones(v_b)
        u = np.linspace(0.0, 1.0, f_b - f_a, endpoint=False, dtype=np.float64)
        w = 3.0 * u ** 2 - 2.0 * u ** 3                       # cubic Bézier weights
        f0_new[seg] = c4_hz * 2.0 ** ((st_a + (st_b - st_a) * w) / 12.0)

    # 同拍端点：把音高跳变摊到边界前的短窗内（半音域 smoothstep），
    # 让 f0 在边界帧之前就到达后一个音的音高，避免单帧跳变。
    if jumps:
        k_max = max(1, int(round(PITCH_JUMP_RAMP_MS / fp)))
        for f_b, v_b in jumps.items():
            if f_b <= 0 or f_b > n_out:
                continue
            k = min(k_max, f_b)
            base = float(f0_new[f_b - k])
            if not np.isfinite(base) or base <= 0.0:
                continue          # 边界前是静音/未覆盖/异常帧，不做音高滑音
            st_a = 12.0 * np.log2(base / c4_hz)
            st_b = _parse_semitones(v_b)
            u = np.linspace(0.0, 1.0, k, endpoint=False, dtype=np.float64)
            w = 3.0 * u ** 2 - 2.0 * u ** 3
            f0_new[f_b - k:f_b] = c4_hz * 2.0 ** ((st_a + (st_b - st_a) * w) / 12.0)
            covered[f_b - k:f_b] = True
            print(f"[Pitch line] 同拍端点 frame {f_b}：插入 {k} 帧({k * fp:.0f}ms)音高过渡 "
                  f"({st_a:.2f}→{st_b:.2f} 半音)")

    n_cov = int(covered.sum())
    print(f"[Pitch line] {len(pts)} endpoints → covering {n_cov}/{n_out} frames"
          f" ({n_out - n_cov} frames uncovered keep the spliced result), {int(silent.sum())} silent frames")

    # Sanitize: a NaN/Inf/negative value anywhere here reaches pyworld and produces extreme
    # samples (a burst).  Report the frame positions so the source of the bad value is visible.
    bad = ~np.isfinite(f0_new) | (f0_new < 0.0)
    n_bad = int(bad.sum())
    if n_bad:
        where = np.flatnonzero(bad)
        head = ", ".join(str(int(v)) for v in where[:12])
        print(f"[Pitch line·sanitize] {n_bad} non-finite/negative f0 frames → 0"
              f" (frames {head}{' …' if n_bad > 12 else ''})")
        f0_new[bad] = 0.0
    return f0_new, silent


def _splice_breath(br_a: np.ndarray, br_b: np.ndarray,
                   ma: int, mb: int, over: int, sr: int, fp: float,
                   protect_head: bool = False) -> np.ndarray:
    """Breath sample-level splicing: overlap-add cross-fade matching the frame-domain overlap region.

    Rules (same time base as the main-signal frame-domain splice, preventing breath
    position drift):
      - segment A's breath is kept in full (pure A part + entry into the cross-fade region)
      - segment B's breath is mixed in starting at len_a - ov
      - the overlap uses an equal-power cos/sin window: A's weight goes 1->0 and B's 0->1,
        producing a continuous output without jumps (A is no longer added twice)
    protect_head=True keeps B's breath at full weight from the start and instead fades
    A out over ATTACK_FADE_FRAMES (the incoming note's burst/aspiration onset must not
    be faded in from zero, same as the frame-domain attack protection).
    Output length = len(br_a) + len(br_b) - overlap_samples
    """
    len_a = len(br_a)
    len_b = len(br_b)
    if over <= 0 or len_a == 0:
        return np.concatenate([br_a, br_b]) if len_b else br_a
    ov_smp = _frame_bound(over, sr, fp)
    ov_smp = max(0, min(ov_smp, len_a, len_b))
    if ov_smp <= 0:
        return np.concatenate([br_a, br_b])

    out = np.zeros(len_a + len_b - ov_smp, dtype=np.float64)
    s0 = len_a - ov_smp              # sample where B starts mixing in (i.e. cross-fade start)
    out[:s0] = br_a[:s0]             # pure A part (written only once)
    if protect_head:
        k = min(ov_smp, _frame_bound(ATTACK_CLIP_FRAMES, sr, fp))
        wA = np.zeros(ov_smp, dtype=np.float64)
        if k > 0:
            u = np.linspace(0.0, 1.0, k, dtype=np.float64)
            wA[:k] = 1.0 - (3.0 * u ** 2 - 2.0 * u ** 3)
        wB = np.ones(ov_smp, dtype=np.float64)
    else:
        # Equal-power cross-fade in the overlap (cos/sin window: A 1->0, B 0->1)
        t = np.linspace(0.0, np.pi / 2.0, ov_smp, dtype=np.float64)
        wA = np.cos(t)
        wB = np.sin(t)
    out[s0:s0 + ov_smp] = wA * br_a[s0:] + wB * br_b[:ov_smp]
    if len_b > ov_smp:
        out[s0 + ov_smp:] = br_b[ov_smp:]      # pure B tail
    return out


def _edge_fade(data: np.ndarray, sr: int, fade_ms: float) -> np.ndarray:
    """Raised-cosine fade in/out at the start and end of the whole phrase, removing hard start/end jumps."""
    if fade_ms <= 0 or len(data) < 4:
        return data
    out = np.asarray(data, dtype=np.float64).copy()
    n = len(out)
    nf = min(int(fade_ms * sr / 1000.0), n // 2)
    if nf < 2:
        return out
    t = np.linspace(0.0, np.pi / 2.0, nf)
    fade_in = np.sin(t) ** 2
    fade_out = np.sin(t)[::-1] ** 2
    out[:nf] *= fade_in
    out[-nf:] *= fade_out
    return out


def _print_breath_position_report(name: str, fp: float, diag: dict) -> None:
    """Print the breath position multi-way validation report (F0/SP/breath RMS features cross-check each other)."""
    tv = diag['total_voiced']
    trim = diag['trim_total']
    trim_frac = trim / tv if tv else 0.0

    print(f"  [Breath position check·{name}] F0 voiced {tv} frames → kept {diag['kept']} frames "
          f"(head trim {diag['trim_head']}/tail trim {diag['trim_tail']} frames = {trim * fp:.0f}ms, "
          f"{trim_frac * 100:.1f}%)")
    print(f"      feature 1 F0 voiced frames : {diag['first_voiced']}..{diag['last_voiced']} frames "
          f"({diag['first_voiced'] * fp:.0f}~{diag['last_voiced'] * fp:.0f}ms)")
    if diag['has_sp']:
        print(f"      feature 2 SP energy valid : {diag['sp_first']}..{diag['sp_last']} frames "
              f"({diag['sp_first'] * fp:.0f}~{diag['sp_last'] * fp:.0f}ms)")
    print(f"      feature 3 breath RMS valid: {diag['br_first']}..{diag['br_last']} frames "
          f"({diag['br_first'] * fp:.0f}~{diag['br_last'] * fp:.0f}ms)")

    issues = []
    if diag.get('fallback'):
        issues.append("feature conflict caused a full fallback without trimming")
    if diag['holes']:
        issues.append(f"voiced region contains {len(diag['holes'])} frames with neither SP nor breath")
    if diag['has_sp']:
        lead = max(abs(diag['sp_first'] - diag['first_kept']),
                   abs(diag['sp_last'] - diag['last_kept']),
                   abs(diag['br_first'] - diag['first_kept']),
                   abs(diag['br_last'] - diag['last_kept']))
    else:
        lead = max(abs(diag['br_first'] - diag['first_kept']),
                   abs(diag['br_last'] - diag['last_kept']))
    if lead > 2:
        issues.append(f"feature boundaries deviate from the kept boundary by {lead} frames")
    if issues:
        print(f"      [Check failed] " + "; ".join(issues))
    else:
        print(f"      [Check passed] three features agree, breath position aligns with the voiced-frame boundary, max deviation ≤2 frames")


# ----------------------------------------------------------------------
# Diagnostic probe: NaN / Inf / abnormal zeroing in any parameter array
# ----------------------------------------------------------------------
PROBE = True        # set False to silence all probe output


def _probe_runs(mask: np.ndarray) -> list:
    """Compress a boolean mask into (start, end) inclusive runs."""
    runs = []
    idx = np.flatnonzero(np.asarray(mask, dtype=bool))
    if idx.size == 0:
        return runs
    start = prev = int(idx[0])
    for i in idx[1:]:
        i = int(i)
        if i == prev + 1:
            prev = i
            continue
        runs.append((start, prev))
        start = prev = i
    runs.append((start, prev))
    return runs


def _probe_runs_text(runs, limit: int = 6) -> str:
    txt = ", ".join(f"{s}~{e}" if e > s else f"{s}" for s, e in runs[:limit])
    return txt + (f" …(+{len(runs) - limit})" if len(runs) > limit else "")


def _sample_to_frame(i: int, sr: float, fp: float) -> int:
    spb = max(1e-9, float(sr) * float(fp) / 1000.0)
    return int(round(i / spb))


def _probe(name: str, arr, fp: float = 0.0) -> None:
    """Report NaN/Inf counts, zeros and abnormal all-zero runs of one frame-domain parameter.

    An abnormal run = consecutive frames where a 1-D parameter is exactly 0 (a parameter
    collapsing mid-utterance is the classic burst source), or where a whole sp/ap row
    (all bands of one frame) is 0.
    """
    if not PROBE or arr is None:
        return
    a = np.asarray(arr, dtype=np.float64)
    if a.size == 0:
        print(f"[Probe·{name}] empty")
        return

    fin = np.isfinite(a)
    nan = int(np.count_nonzero(np.isnan(a)))
    pinf = int(np.count_nonzero(np.isposinf(a)))
    ninf = int(np.count_nonzero(np.isneginf(a)))

    if a.ndim == 1:
        zero_mask = (a == 0.0) & fin
        parts = [f"n={a.size}", f"nan={nan}", f"+inf={pinf}", f"-inf={ninf}",
                 f"zero={int(zero_mask.sum())}"]
        runs = _probe_runs(zero_mask)
        if runs:
            parts.append(f"zero-runs[{_probe_runs_text(runs)}]")
        if nan or pinf or ninf:
            parts.append(f"bad[{_probe_runs_text(_probe_runs(~fin))}]")
    else:
        parts = [f"{a.shape[0]}×{a.shape[1]}", f"nan={nan}", f"+inf={pinf}", f"-inf={ninf}"]
        bad_rows = ~fin.all(axis=1)
        if bad_rows.any():
            parts.append(f"bad-rows[{_probe_runs_text(_probe_runs(bad_rows))}]")
        zero_rows = (a == 0.0).all(axis=1)
        if zero_rows.any():
            parts.append(f"zero-rows[{_probe_runs_text(_probe_runs(zero_rows))}]")

    fin_vals = a[fin]
    if fin_vals.size:
        parts.append(f"range={fin_vals.min():.4g}~{fin_vals.max():.4g}")
    print(f"[Probe·{name}] " + "  ".join(parts))


def _probe_samples(name: str, arr, sr: float, fp: float = 0.0,
                   jump_thresh: float = 0.25) -> None:
    """Report non-finite samples and the largest sample-to-sample jumps (burst positions)."""
    if not PROBE or arr is None:
        return
    a = np.asarray(arr, dtype=np.float64)
    if a.size == 0:
        print(f"[Probe·{name}] empty")
        return

    fin = np.isfinite(a)
    nan = int(np.count_nonzero(np.isnan(a)))
    pinf = int(np.count_nonzero(np.isposinf(a)))
    ninf = int(np.count_nonzero(np.isneginf(a)))
    af = np.where(fin, a, 0.0)
    parts = [f"n={a.size}", f"nan={nan}", f"+inf={pinf}", f"-inf={ninf}",
             f"peak={float(np.max(np.abs(af))):.3f}"]
    if nan or pinf or ninf:
        parts.append(f"bad[{_probe_runs_text(_probe_runs(~fin))}]smp")

    jumps = np.abs(np.diff(af))
    idx = np.flatnonzero(jumps >= jump_thresh)
    if idx.size:
        top = idx[np.argsort(jumps[idx])[::-1][:5]]
        parts.append(f"jumps≥{jump_thresh:g}[{idx.size}] top: " + ", ".join(
            f"#{int(i)}@{i / sr:.3f}s/f{_sample_to_frame(int(i), sr, fp)}Δ{jumps[i]:.2f}"
            for i in top))
    print(f"[Probe·{name}] " + "  ".join(parts))


def _probe_params() -> None:
    """Report non-finite or non-positive values among the numeric synthesis parameters."""
    if not PROBE:
        return
    names = ("FOLLOW_RATIO", "AP_SCALE", "AP_MAX", "BREATH_GAIN", "BREATH_SHAPE_GAIN_MIN",
             "BREATH_SHAPE_GAIN_MAX", "PLOSIVE_GAIN", "PLOSIVE_ENV_GAIN_MIN",
             "PLOSIVE_ENV_GAIN_MAX", "SP_FLOOR", "EDGE_FADE_MS", "MATCH_RMS_TO")
    g = globals()
    shown, bad = [], []
    for name in names:
        value = g.get(name)
        if value is None or isinstance(value, (bool, str)):
            continue
        try:
            f = float(value)
        except (TypeError, ValueError):
            continue
        shown.append(f"{name}={f:g}")
        if not np.isfinite(f) or f <= 0.0:
            bad.append(name)
    print("[Probe·params] " + ", ".join(shown))
    if bad:
        print("[Probe·params] abnormal (non-finite or ≤0): " + ", ".join(bad))


# ----------------------------------------------------------------------
# 1. Decomposition + pitch shift
# ----------------------------------------------------------------------
def _decompose_and_shift(audio_path: str, src_pitch: str, dst_pitch: str,
                         keep_first_half_unvoiced: bool = False,
                         follow_ratio=None, band_ratios=None, breathy_thr=None):
    """Decompose a single audio segment -> pitch shift -> breath position validation -> keep only voiced frames; returns the segment data.

    With keep_first_half_unvoiced=True (recommended for the first segment) the
    unvoiced frames of the first half are kept as well instead of being dropped
    (f0=0 frames are synthesized by pyworld as aperiodic noise).

    follow_ratio / band_ratios / breathy_thr are per-segment overrides; None falls
    back to the global defaults of parameter section [2]
    (FOLLOW_RATIO / BAND_RATIOS / BREATHY_THR).

    Returns:
        dict(f0, sp, ap, gi, breath, src_rms, sr, fp, semitones, name)
        breath/src_rms are sample-level/frame-level arrays sliced precisely at the
        voiced frames and then concatenated
    """
    if not os.path.isfile(audio_path):
        raise FileNotFoundError(f"audio file not found: {audio_path}")

    vd = VocalDecomposer(sample_rate=SAMPLE_RATE, frame_period=FRAME_PERIOD)
    print(f"[Decompose] {os.path.basename(audio_path)}  {src_pitch}→{dst_pitch}")
    p = vd.decompose(audio_path)
    sr = int(p['sample_rate'])
    fp = float(p['f0_frame_period'])

    f0_all = np.asarray(p['f0_values'], dtype=np.float64)
    sp_all = p['ge_sp']
    ap_all = p['ge_ap']
    gi_all = p['ge_glottal_intensity']
    breath_all = np.asarray(p['breath_data'], dtype=np.float64)

    f0_obj = vl.F0(values=f0_all, time_axis=p['f0_time_axis'],
                   frame_period=fp, sample_rate=sr,
                   voiced_flags=p['f0_voiced_flags'])
    gp = dict(p.get('ge_parameters', {}) or {})
    gl_obj = vl.GlottalExcitation(
        signal=p['ge_signal'], sample_rate=sr,
        sp=sp_all, ap=ap_all, oq=float(p['ge_oq']),
        glottal_intensity=gi_all,
        parameters=gp,
    )

    # Pitch shift (returns an original copy when src==dst); per-segment overrides take priority, None falls back to the global default
    f0_s, gl_s = vl.pitch_shift(
        f0_obj, gl_obj, src_pitch, dst_pitch,
        follow_ratio=FOLLOW_RATIO if follow_ratio is None else follow_ratio,
        breathy_threshold=BREATHY_THR if breathy_thr is None else breathy_thr,
        band_ratios=BAND_RATIOS if band_ratios is None else band_ratios,
        band_width_hz=BAND_WIDTH_HZ,
    )
    semitones = _parse_semitones(dst_pitch) - _parse_semitones(src_pitch)

    f0 = np.asarray(f0_s.values, dtype=np.float64)
    sp = np.asarray(gl_s.sp, dtype=np.float64) if gl_s.sp is not None else None
    ap = np.asarray(gl_s.ap, dtype=np.float64) if gl_s.ap is not None else None
    gi = gl_s.glottal_intensity

    # Align glottal_intensity frame by frame to the f0 frame count
    if gi is not None and len(gi) > 0:
        gi = _resample_1d(np.asarray(gi, dtype=np.float64), len(f0))
    else:
        gi = None

    # Source audio per-frame RMS: used both for the plosive transient envelope
    # transplant (the source's attack shape) and to identify unvoiced frames with
    # notable energy among f0=0 frames (real consonants such as fricatives/affricates/plosives)
    src_audio = vl.WAVAudio.from_file(audio_path, mono=True)
    xs = np.asarray(src_audio.data, dtype=np.float64)
    if src_audio.sample_rate != sr:
        xs = _resample_1d(xs, int(round(len(xs) * sr / src_audio.sample_rate)))
    src_rms = np.zeros(len(f0), dtype=np.float64)
    for i in range(len(f0)):
        s = _frame_bound(i, sr, fp)
        e = min(_frame_bound(i + 1, sr, fp), len(xs))
        if e > s:
            src_rms[i] = float(np.sqrt(np.mean(xs[s:e] ** 2)))

    # Frame selection = voiced frames + unvoiced frames with energy:
    #   · first trim "f0 voiced but no breath/no spectral energy" near-silent fake
    #     voiced frames (breath position validation);
    #   · f0=0 frames must not all be dropped -- real consonants such as fricatives
    #     have no fundamental frequency and their energy is concentrated in a few
    #     frames (criterion: source per-frame RMS ≥ 1% of the voiced median RMS) and
    #     must be kept, otherwise consonants (especially mid/final ones) would be
    #     erased entirely.
    #   · with keep_unvoiced_head=True the first segment's first-half unvoiced frames
    #     are kept too (including onset and blank parts).
    vf_base = f0 > 0.0
    mask_keep, diag = _refine_voiced_mask(f0, sp, breath_all, sr, fp)
    vf = vf_base & mask_keep
    _vmed = float(np.median(src_rms[vf_base])) if np.any(vf_base) else 0.0
    if _vmed > 0.0:
        _fric = (~vf_base) & (src_rms >= _vmed * 0.01)
        if np.any(_fric):
            print(f"    [Keep unvoiced] f0=0 but source energy ≥ 1% of voiced ({_vmed * 0.01:.5f}) frames "
                  f"{int(_fric.sum())} frames (fricatives/plosives etc.)")
        vf = vf | _fric
    if keep_first_half_unvoiced:
        half = len(f0) // 2
        vf[:half] = True           # keep the first segment's first-half unvoiced frames as well (no longer dropped)
        print(f"    [Keep unvoiced] first half {half} frames force-kept (including unvoiced/onset parts)")
    idx = np.where(vf)[0]
    if len(idx) == 0:
        raise ValueError(f"{os.path.basename(audio_path)} has no voiced frames")

    # ── breath position multi-way validation report ──
    if diag is not None:
        _print_breath_position_report(os.path.basename(audio_path), fp, diag)

    f0_v = f0[vf]
    sp_v = sp[vf] if sp is not None else None
    ap_v = ap[vf] if ap is not None else None
    gi_v = gi[vf] if gi is not None else None
    src_rms_v = src_rms[vf]

    # ── AP correction: d4c systematically overestimates -> scale+clamp restores periodicity (removes the synthesized noise-cloud artifact) ──
    if ap_v is not None:
        ap_raw_mean = float(ap_v.mean())
        ap_v = np.minimum(ap_v * AP_SCALE, AP_MAX)
        print(f"    [AP correction] mean aperiodicity {ap_raw_mean:.3f} → {float(ap_v.mean()):.3f} "
              f"(scale={AP_SCALE}, max={AP_MAX})")

    # Breath: sliced precisely at the voiced-frame sample boundaries and concatenated (length = voiced frame count × frame period)
    pieces = []
    for i in idx:
        s = _frame_bound(i, sr, fp)
        e = _frame_bound(i + 1, sr, fp)
        pieces.append(breath_all[s:e])
    breath_v = np.concatenate(pieces) if pieces else np.zeros(0, dtype=np.float64)

    print(f"    {len(f0)} frames → {len(f0_v)} voiced frames "
          f"({len(f0_v) * fp:.0f}ms), breath {len(breath_v)} samples")

    # Breath follows this segment's pitch
    if BREATH_FOLLOW_PITCH and len(breath_v) > 4 and abs(semitones) > 1e-6:
        ratio = 2.0 ** (semitones / 12.0)
        breath_v = _phase_vocoder_pitch_shift(
            breath_v, sr, ratio,
            frame_ms=BREATH_PV_FRAME_MS, hop_ms=BREATH_PV_HOP_MS)
        print(f"    [Breath pitch follow] semitones={semitones:.2f}, ratio={ratio:.4f}")

    # Jitter characteristics measured on this segment's source audio (when extending duration, continue the period/amplitude fluctuation using the real jitter)
    jitter_local = float((gp.get('jitter') or {}).get('jitter_local', 0.0))
    shimmer_local = float((gp.get('shimmer') or {}).get('shimmer_local', 0.0))
    print(f"    [Jitter features] jitter_local={jitter_local:.3f}%  "
          f"shimmer_local={shimmer_local:.3f}%")

    # Probe: any parameter collapsing to 0 / NaN / Inf right after decomposition + pitch shift
    _seg_name = os.path.basename(audio_path)
    _probe(f"seg·{_seg_name}·f0", f0_v, fp)
    _probe(f"seg·{_seg_name}·sp", sp_v, fp)
    _probe(f"seg·{_seg_name}·ap", ap_v, fp)
    _probe(f"seg·{_seg_name}·gi", gi_v, fp)
    _probe(f"seg·{_seg_name}·breath", breath_v, fp)
    _probe(f"seg·{_seg_name}·src_rms", src_rms_v, fp)

    return dict(f0=f0_v, sp=sp_v, ap=ap_v, gi=gi_v, breath=breath_v,
                src_rms=src_rms_v,
                jitter_local=jitter_local, shimmer_local=shimmer_local,
                sr=sr, fp=fp, semitones=semitones, name=os.path.basename(audio_path))


def synthesize(segments,
               output_path,
               *,
               # [1.1] per-frame pitch line (None = disabled)
               pitch_line=PITCH_LINE,
               # [2] pitch shift (global defaults, overridable by same-named keys inside a segment)
               follow_ratio=FOLLOW_RATIO,
               band_ratios=BAND_RATIOS,
               breathy_thr=BREATHY_THR,
               band_width_hz=BAND_WIDTH_HZ,
               # [3] AP correction
               ap_scale=AP_SCALE,
               ap_max=AP_MAX,
               # [4] splicing / beat / duration
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
               # [8] jitter / shimmer intensity (0~100, added on top of the voicebank's measured value)
               jitter=0.0,
               shimmer=0.0,
               # [9] output
               edge_fade_ms=EDGE_FADE_MS,
               bit_depth=BIT_DEPTH,
               clean_intermediate=CLEAN_INTERMEDIATE,
               verbose=True):
    """Run the full synthesis pipeline with the given parameters and save the resulting wav to output_path.

    Parameters
    ----
    segments : list[dict]
        Segment list (>=2 segments, spliced in order). Each segment requires
        audio / src_pitch / dst_pitch / beat_start / beat_end and optionally
        transition / keep_unvoiced_head / follow_ratio / band_ratios / breathy_thr.
    output_path : str
        Save path of the resulting wav (the directory is created if missing).
        When clean_intermediate=True, intermediate artifacts (breath_seg*.wav /
        spliced_frames.txt) are written to a temporary directory and removed
        automatically, so the output directory only receives the final wav.
        When clean_intermediate=False they stay in the output directory.
    jitter, shimmer : float
        Extra jitter/shimmer depth (0~100), added on top of the voicebank's
        measured jitter_local / shimmer_local.
    verbose : bool
        True prints pipeline logs; False runs silently.

    Returns
    ----
    str : the output_path actually saved.

    Notes
    ----
    This function temporarily injects the parameters into module globals for the
    existing pipeline functions to read and restores them afterwards (including on
    exceptions), so repeated calls do not pollute each other; it also syncs
    prosses_breath's intermediate file paths so breath splicing still reads the
    right files under a custom output directory.
    """
    if not segments or len(segments) < 2:
        raise ValueError("segments requires at least 2 entries")
    output_path = os.path.abspath(output_path)
    out_dir = os.path.dirname(output_path)
    os.makedirs(out_dir, exist_ok=True)

    # Intermediate artifacts must never leak into the user's output directory. When cleanup is
    # enabled they live in a private temp directory; when disabled they stay next to the output
    # so prosses_breath.py can still be run separately on them.
    work_dir = out_dir
    temp_work_dir = None
    if clean_intermediate:
        temp_work_dir = tempfile.mkdtemp(prefix="vocalb1-")
        work_dir = temp_work_dir
    breath_fmt = os.path.join(work_dir, "breath_seg{}.wav")
    frames_txt = os.path.join(work_dir, "spliced_frames.txt")

    overrides = dict(
        SEGMENTS=list(segments),
        OUTPUT_WAV=output_path,
        OUTPUT_BREATH_FMT=breath_fmt,
        OUTPUT_FRAMES_TXT=frames_txt,
        # [1.1]
        PITCH_LINE=pitch_line,
        # [2]
        FOLLOW_RATIO=follow_ratio,
        BAND_RATIOS=band_ratios,
        BREATHY_THR=breathy_thr,
        BAND_WIDTH_HZ=band_width_hz,
        # [3]
        AP_SCALE=ap_scale,
        AP_MAX=ap_max,
        # [4]
        SAMPLE_RATE=sample_rate,
        FRAME_PERIOD=frame_period,
        JOIN_NORMALIZE_SP=join_normalize_sp,
        BPM=bpm,
        TIME_SIGNATURE=time_signature,
        DUR_EDGE_FRAMES=dur_edge_frames,
        DUR_JITTER_SMOOTH=dur_jitter_smooth,
        DUR_JITTER_DECAY_MS=dur_jitter_decay_ms,
        DUR_TAIL_JITTER_RATIO=dur_tail_jitter_ratio,
        # [5]
        MATCH_RMS_TO=match_rms_to,
        MODEL_TYPE=model_type,
        SP_FLOOR=sp_floor,
        # [6]
        ADD_BREATH=add_breath,
        BREATH_GAIN=breath_gain,
        BREATH_FOLLOW_PITCH=breath_follow_pitch,
        BREATH_PV_FRAME_MS=breath_pv_frame_ms,
        BREATH_PV_HOP_MS=breath_pv_hop_ms,
        BREATH_SHAPE_TO_MAIN=breath_shape_to_main,
        BREATH_SHAPE_BANDS=breath_shape_bands,
        BREATH_SHAPE_GAIN_MIN=breath_shape_gain_min,
        BREATH_SHAPE_GAIN_MAX=breath_shape_gain_max,
        # [7]
        PLOSIVE_RATIO=plosive_ratio,
        PLOSIVE_MIN_GRAD_DB=plosive_min_grad_db,
        PLOSIVE_MIN_CREST=plosive_min_crest,
        PLOSIVE_MAX_VALLEY=plosive_max_valley,
        PLOSIVE_GAIN=plosive_gain,
        PLOSIVE_SMOOTH=plosive_smooth,
        PLOSIVE_MARGIN_HEAD=plosive_margin_head,
        PLOSIVE_MARGIN_TAIL=plosive_margin_tail,
        PLOSIVE_KEEP_BURST=plosive_keep_burst,
        PLOSIVE_ENV_TRANSPLANT=plosive_env_transplant,
        PLOSIVE_ENV_HEAD=plosive_env_head,
        PLOSIVE_ENV_TAIL=plosive_env_tail,
        PLOSIVE_ENV_GAIN_MIN=plosive_env_gain_min,
        PLOSIVE_ENV_GAIN_MAX=plosive_env_gain_max,
        # [8] jitter / shimmer (0~100 -> 0~1 percentage point added to the measured value)
        JITTER_EXTRA_PCT=float(jitter) / 100.0,
        SHIMMER_EXTRA_PCT=float(shimmer) / 100.0,
        # [9]
        EDGE_FADE_MS=edge_fade_ms,
        BIT_DEPTH=bit_depth,
        CLEAN_INTERMEDIATE=clean_intermediate,
    )

    g = globals()
    saved = {k: g.get(k) for k in overrides}
    pb_saved = (prosses_breath.FRAMES_TXT, prosses_breath.BREATH_FMT)
    try:
        g.update(overrides)
        prosses_breath.FRAMES_TXT = frames_txt
        prosses_breath.BREATH_FMT = breath_fmt
        if verbose:
            run()
        else:
            with contextlib.redirect_stdout(io.StringIO()):
                run()
    finally:
        g.update(saved)
        prosses_breath.FRAMES_TXT, prosses_breath.BREATH_FMT = pb_saved
        if temp_work_dir is not None:
            shutil.rmtree(temp_work_dir, ignore_errors=True)
    return output_path


def run() -> vl.WAVAudio:
    """Main flow: N-segment voiced-frame extraction -> per-segment Bézier fold splicing -> synthesis."""
    os.makedirs(os.path.dirname(os.path.abspath(OUTPUT_WAV)), exist_ok=True)
    _probe_params()

    def _guard_samples(arr: np.ndarray, stage: str) -> np.ndarray:
        """Replace non-finite samples with 0 and report the affected frame range.

        soundfile writes NaN into a 24-bit PCM file as a full-scale -1.0 constant block
        (measured), which is exactly the 80ms "burst" that can appear in the output.
        Checking every sample-domain array at the boundary keeps such a block out of the
        wav even if some upstream factor produces NaN; the frame range tells where it came from.
        """
        bad = ~np.isfinite(arr)
        n_bad = int(bad.sum())
        if not n_bad:
            return arr
        idx = np.flatnonzero(bad)
        spb = max(1e-9, float(sr) * float(fp) / 1000.0)
        print(f"[Output guard·{stage}] {n_bad} non-finite samples → 0 "
              f"(frame {int(round(idx[0] / spb))}~{int(round(idx[-1] / spb))}, "
              f"t={idx[0] / sr:.3f}~{idx[-1] / sr:.3f}s)")
        return np.where(bad, 0.0, arr)

    if len(SEGMENTS) < 2:
        raise ValueError(f"SEGMENTS requires at least 2 entries, currently {len(SEGMENTS)}")

    # 1) Per-segment processing: decomposition -> pitch shift -> voiced-frame extraction
    segs = []
    for i, seg in enumerate(SEGMENTS):
        for req in ('audio', 'src_pitch', 'dst_pitch', 'beat_start', 'beat_end'):
            if req not in seg:
                raise ValueError(f"SEGMENTS[{i}] is missing required key '{req}'"
                                 f" (strict beat mode: every segment must give its start/end beats beat_start/beat_end)")
        print(f"\n===== segment {i + 1}/{len(SEGMENTS)} =====")
        segs.append(_decompose_and_shift(
            seg['audio'], seg['src_pitch'], seg['dst_pitch'],
            keep_first_half_unvoiced=bool(seg.get('keep_unvoiced_head', False)),
            follow_ratio=seg.get('follow_ratio'),
            band_ratios=seg.get('band_ratios'),
            breathy_thr=seg.get('breathy_thr'),
        ))
    n_seg = len(segs)

    # 1.5) Beat layout: anchor each segment's onset to the beat grid; overlaps/rests are
    #      derived automatically from the beat ranges (see _apply_beat_timeline)
    _sr0, _fp0 = segs[0]['sr'], segs[0]['fp']
    beat_overs, beat_protects = _apply_beat_timeline(segs, SEGMENTS, _sr0, _fp0)

    # 2) Cross-segment consistency checks
    sr, fp = segs[0]['sr'], segs[0]['fp']
    assert all(s['sr'] == sr for s in segs), "inconsistent sample rate across segments"
    assert all(s['fp'] == fp for s in segs), "inconsistent frame period across segments"
    for key in ('sp', 'ap'):
        dims = {s[key].shape[1] for s in segs if s[key] is not None}
        assert len(dims) <= 1, f"inconsistent number of {key} bands across segments: {sorted(dims)}"

    # 3) Per-seam overlap frame count (overs[i] = overlap of segment i with segment i-1, derived from the beat ranges)
    m = [len(s['f0']) for s in segs]
    overs = [0] + [int(beat_overs[i]) for i in range(1, n_seg)]
    protects = [False] + [bool(beat_protects[i]) for i in range(1, n_seg)]
    n_out = sum(m) - sum(overs)
    print("\n[Concat] " + " + ".join(f"#{i + 1}={m[i]}frames({m[i] * fp:.0f}ms)"
                                     for i in range(n_seg)))
    for i in range(1, n_seg):
        print(f"        seam {i}: Bézier overlap region={overs[i]} frames({overs[i] * fp:.0f}ms)"
              f"{' (attack-protected)' if protects[i] else ''}"
              f" → segment {i + 1} start frame {sum(m[:i]) - sum(overs[1:i + 1])}")
    print(f"        → output {n_out} frames({n_out * fp:.0f}ms)")

    # 4) Align each segment's overall energy to the first segment (removes volume steps after splicing)
    if JOIN_NORMALIZE_SP and segs[0]['sp'] is not None:
        med0 = float(np.median(segs[0]['sp'].sum(axis=1)))
        for i in range(1, n_seg):
            if segs[i]['sp'] is None:
                continue
            medi = float(np.median(segs[i]['sp'].sum(axis=1)))
            if medi > 1e-12 and med0 > 1e-12:
                k = float(np.clip(med0 / medi, 0.3, 3.0))
                segs[i]['sp'] = segs[i]['sp'] * k
                print(f"[Energy align] segment {i + 1} median energy={medi:.2f} → scale {k:.3f}×"
                      f" (aligned to the first segment {med0:.2f})")
            else:
                print(f"[Energy align] segment {i + 1} skipped (energy too small)")

    # 5) Frame-domain Bézier fold splicing: the pairwise join function is folded repeatedly along the segment order; N=2 matches the old behavior
    out = {}
    for key in ('f0', 'gi', 'sp', 'ap'):
        if any(s[key] is None for s in segs):
            if key == 'gi':
                print("[Warn] some segments lack glottal_intensity → gi is omitted this run")
            out[key] = None
            continue
        acc = segs[0][key]
        for i in range(1, n_seg):
            # f0 needs a dedicated splice: a linear 0↔voiced blend invents a false fundamental
            # inside the overlap and the vocoder turns it into a burst.
            acc = (_blend_overlap_f0 if key == 'f0' else _blend_overlap)(
                acc, segs[i][key], overs[i],
                **({} if key == 'f0' else {'protect_head': protects[i]}))
        out[key] = acc
    f0_out, gi_out, sp_out, ap_out = out['f0'], out['gi'], out['sp'], out['ap']
    # Probe: value sanity right after the multi-segment Bézier fold (before any sanitizing)
    _probe("spliced·f0", f0_out, fp)
    _probe("spliced·gi", gi_out, fp)
    _probe("spliced·sp", sp_out, fp)
    _probe("spliced·ap", ap_out, fp)

    # Inter-frame energy/value safety clamp.
    # NOTE: pyworld's synthesize() turns a single NaN sp/ap frame into NaN samples across
    # roughly ±6 neighbouring frames (measured: one bad frame poisoned 12~16 frames), and
    # the 24-bit writer stores NaN as a full-scale -1.0 constant block -> the audible
    # "burst". f0 NaN is harmless by comparison (WORLD just treats it as unvoiced).
    # So sp/ap must be finite BEFORE synthesis, and the offending frame positions are
    # printed here to expose where the bad value was created.
    spb = max(1e-9, float(sr) * float(fp) / 1000.0)     # samples per frame
    f0_out = np.nan_to_num(f0_out, nan=0.0, posinf=0.0, neginf=0.0)
    f0_out = np.clip(f0_out, 0.0, None)
    if ap_out is not None:
        _bad_ap = ~np.isfinite(ap_out)
        n_bad_ap = int(_bad_ap.sum())
        if n_bad_ap:
            _fr = np.flatnonzero(_bad_ap.any(axis=1)).tolist()
            print(f"[AP sanitize] {n_bad_ap} non-finite ap elements in {len(_fr)} frames "
                  f"({', '.join(map(str, _fr[:12]))}{' …' if len(_fr) > 12 else ''}) → clamped")
        ap_out = np.nan_to_num(ap_out, nan=1.0, posinf=1.0, neginf=0.0)
        ap_out = np.clip(ap_out, 0.0, 1.0)
    if sp_out is not None:
        _bad_sp = ~np.isfinite(sp_out)
        n_bad_sp = int(_bad_sp.sum())
        if n_bad_sp:
            _fr = np.flatnonzero(_bad_sp.any(axis=1)).tolist()
            print(f"[Spectrum floor] {n_bad_sp} non-finite sp elements in {len(_fr)} frames "
                  f"({', '.join(map(str, _fr[:12]))}{' …' if len(_fr) > 12 else ''}) → {SP_FLOOR}")
        # +inf must also fall back to the floor: 1e30 in a log-magnitude bin makes WORLD
        # emit ~1e15 full-scale samples (another burst source), never a NaN fix.
        sp_out = np.nan_to_num(sp_out, nan=SP_FLOOR, posinf=SP_FLOOR, neginf=SP_FLOOR)
        n_zero = int((sp_out <= 0).sum())
        if n_zero:
            print(f"[Spectrum floor] {n_zero} sp elements ≤0 → raised to {SP_FLOOR} (avoid NaN in synthesis)")
        sp_out = np.clip(sp_out, SP_FLOOR, None)

    _probe("sanitized·f0", f0_out, fp)
    _probe("sanitized·gi", gi_out, fp)
    _probe("sanitized·sp", sp_out, fp)
    _probe("sanitized·ap", ap_out, fp)

    # 5.5) Per-frame pitch line: the spliced result stays as-is, but every frame's pitch
    #      is strictly overridden by the curve; frames marked None = silent frames
    #      (forced mute later, including breath)
    f0_out, pl_silent = _apply_pitch_line(f0_out, n_out, sr, fp, PITCH_LINE)

    # 5.6) f0 sanity audit before synthesis: an isolated 0 inside a voiced run makes the
    #      vocoder switch to unvoiced for a single frame and back -> audible pop.  Repair it.
    n_nan = int(np.count_nonzero(~np.isfinite(f0_out)))
    if n_nan:
        f0_out = np.where(np.isfinite(f0_out), f0_out, 0.0)
        print(f"[f0 audit] {n_nan} non-finite f0 frames → 0")
    voiced_run = f0_out > 0.0
    if voiced_run.any():
        isolated = (~voiced_run) & np.roll(voiced_run, 1) & np.roll(voiced_run, -1)
        isolated[0] = False
        isolated[-1] = False
        n_iso = int(isolated.sum())
        if n_iso:
            prev = np.roll(f0_out, 1)
            nxt = np.roll(f0_out, -1)
            f0_out[isolated] = 0.5 * (prev[isolated] + nxt[isolated])
            print(f"[f0 audit] {n_iso} isolated zero frames inside voiced runs → interpolated"
                  f" (positions {', '.join(str(int(v)) for v in np.flatnonzero(isolated)[:12])})")
    print(f"[f0 audit] zero={int(np.count_nonzero(f0_out <= 0))}/{n_out} "
          f"range={float(np.min(f0_out)):.2f}~{float(np.max(f0_out)):.2f}Hz")
    # Probe: pitch line application must not introduce NaN/Inf or an abnormal zero run
    _probe("pitch-line·f0", f0_out, fp)

    # Main-signal per-frame gain: f0=0 frames must not all be muted -- unvoiced frames
    # such as fricatives have no fundamental frequency (f0=0) yet carry real content;
    # once muted, the whole fricative disappears.
    # Only TRUE SILENT frames with very low source energy are faded out (criterion:
    # 1% of the voiced median RMS, consistent with the voiced criterion in
    # _refine_voiced_mask); unvoiced frames take part in synthesis as usual.
    src_rms_out = segs[0]['src_rms']
    for i in range(1, n_seg):
        src_rms_out = np.concatenate([src_rms_out, segs[i]['src_rms'][overs[i]:]])
    src_rms_out = _align_samples(src_rms_out, n_out)
    _vmed = float(np.median(src_rms_out[f0_out > 0])) if np.any(f0_out > 0) else 0.0
    keep = ((f0_out > 0.0) | (src_rms_out >= _vmed * 0.01)) & ~pl_silent
    unvoiced_gain = keep.astype(np.float64)
    ramp = max(3, int(round(30.0 / fp)))            # ~30ms smoothing window
    if ramp % 2 == 0:
        ramp += 1
    unvoiced_gain = np.convolve(np.pad(unvoiced_gain, ramp // 2, mode="edge"),
                                np.ones(ramp) / ramp, mode="valid")
    # The smoothing window leaks gain toward the silent side -> hard-zero on true silent frames so rest regions have strictly no residue.
    unvoiced_gain = np.where(keep, unvoiced_gain, 0.0)
    n_unv = int((~keep).sum())
    _probe("gain·unvoiced", unvoiced_gain, fp)

    # 6) Breath sample-level fold splicing (same time base as the frame-domain fold)
    breath_out = segs[0]['breath']
    frames_acc = m[0]
    seam_info = []                     # (seam index, accumulator length in samples before folding)
    for i in range(1, n_seg):
        seam_info.append((i, len(breath_out)))
        breath_out = _splice_breath(breath_out, segs[i]['breath'],
                                    frames_acc, m[i], overs[i], sr, fp,
                                    protect_head=protects[i])
        frames_acc += m[i] - overs[i]
    target_samples = int(n_out * fp / 1000.0 * sr)
    breath_out = _align_samples(breath_out, target_samples)

    # 6.1) Automatic plosive detection: duck only when the three features indicate a plosive; no plosive means no ducking
    plosive_gain, plosive_seed = _plosive_gain(
        breath_out, f0_out, sr, fp,
        PLOSIVE_RATIO, PLOSIVE_GAIN, PLOSIVE_SMOOTH,
        PLOSIVE_MARGIN_HEAD, PLOSIVE_MARGIN_TAIL,
        PLOSIVE_KEEP_BURST, PLOSIVE_MIN_GRAD_DB,
        PLOSIVE_MIN_CREST, PLOSIVE_MAX_VALLEY)
    unvoiced_gain = unvoiced_gain * plosive_gain
    _probe("gain·plosive", plosive_gain, fp)
    _probe("breath·spliced", breath_out, fp)

    print("[Breath] " + " + ".join(f"#{i + 1}={len(segs[i]['breath'])}smp"
                                 for i in range(n_seg))
          + f" → {len(breath_out)}smp (target {target_samples}smp, "
            f"{target_samples / sr:.3f}s)")

    # ── per-seam alignment check: breath mix-in sample position vs. frame-domain Bézier region start sample position ──
    for i, acc_len in seam_info:
        if overs[i] <= 0:
            continue
        seam_frame = sum(m[:i]) - sum(overs[1:i + 1])
        seam_smp = _frame_bound(seam_frame, sr, fp)
        # Overlap sample count consistent with the internals of _splice_breath (including clamping)
        ov_smp = max(0, min(_frame_bound(overs[i], sr, fp),
                            acc_len, len(segs[i]['breath'])))
        br_s0 = acc_len - ov_smp
        dev = abs(br_s0 - seam_smp)
        print(f"[Seam check·seam {i}] frame-domain seam={seam_frame} frames({seam_frame * fp:.0f}ms)"
              f"@sample {seam_smp} vs breath mix-in@{br_s0}smp, deviation {dev}smp"
              f"({'match OK' if dev <= 3 else 'out of tolerance (>3 samples)'})")

    # 6.5) Save each segment's source breath + the spliced frame text
    saved = []
    for i, s in enumerate(segs):
        path = OUTPUT_BREATH_FMT.format(i)
        vl.WAVAudio(data=s['breath'], sample_rate=sr).save(path, bit_depth=BIT_DEPTH)
        saved.append(f"#{i + 1}:{path} ({len(s['breath'])}smp)")
    print("[Save] source breath " + ", ".join(saved))

    cols = [np.arange(n_out, dtype=np.float64), f0_out]
    legend = ["frame", "f0"]
    if gi_out is not None:
        cols.append(np.asarray(gi_out, dtype=np.float64))
        legend.append("gi")
    if sp_out is not None:
        cols.extend(np.asarray(sp_out, dtype=np.float64).T)
        legend.extend(f"sp{i}" for i in range(sp_out.shape[1]))
    if ap_out is not None:
        cols.extend(np.asarray(ap_out, dtype=np.float64).T)
        legend.extend(f"ap{i}" for i in range(ap_out.shape[1]))
    X = np.column_stack(cols)
    with open(OUTPUT_FRAMES_TXT, "w", encoding="utf-8") as fh:
        fh.write("# VocalB1 spliced frames (after multi-segment Bézier overlap join)\n")
        fh.write(f"# frames={n_out}  n_seg={n_seg}  "
                 f"seg_frames={','.join(str(v) for v in m)}  "
                 f"over_frames={','.join(str(v) for v in overs)}  "
                 f"sr={sr}  fp_ms={fp}\n")
        fh.write("# " + "\t".join(legend) + "\n")
        np.savetxt(fh, X, fmt="%.6g", delimiter="\t")
    print(f"[Save] spliced frames: {OUTPUT_FRAMES_TXT}  ({X.shape[0]} rows × {X.shape[1]} cols)")

    # 6) Build the synthesis objects
    f0_obj = vl.F0(values=f0_out, frame_period=fp, sample_rate=sr,
                   voiced_flags=np.ones(n_out, dtype=bool))
    sig_dummy = np.zeros(target_samples, dtype=np.float64)
    gl_obj = vl.GlottalExcitation(
        signal=sig_dummy, sample_rate=sr,
        sp=sp_out, ap=ap_out,
        oq=0.6,
        glottal_intensity=gi_out,
    )

    print(f"[Synth] {n_out} frames / {n_out*fp:.0f}ms, "
          f"pyworld f0 range={np.min(f0_out):.1f}~{np.max(f0_out):.1f}Hz")

    # Main-signal synthesis: bypass the breath path (pass zero breath); breath is mixed in once in the final output
    breath_zero = vl.WAVAudio(data=np.zeros(target_samples, dtype=np.float64),
                              sample_rate=sr)
    vs = VocalSynthesizer(model_type=MODEL_TYPE)
    output = vs.synthesize(
        f0=f0_obj,
        glottal=gl_obj,
        breath=breath_zero,
        match_rms_to=MATCH_RMS_TO,
        add_breath=False,
    )

    # Time-domain fade-out for true silent frames: interpolate the per-frame gain to
    # the sample domain and multiply it onto the synthesized main signal, removing
    # residue in rest regions and the hard jump from 0 to voiced (unvoiced/fricative
    # frame gains are always 1 and are unaffected).
    out_data = np.asarray(output.data, dtype=np.float64)
    out_data = _guard_samples(out_data, "synthesized")
    _probe_samples("synthesized", out_data, sr, fp)
    ug_smp = _resample_1d(unvoiced_gain, len(out_data))
    out_data = out_data * ug_smp
    _probe_samples("after-mute", out_data, sr, fp)
    print(f"[Mute] {n_unv} true silent frames zeroed in the time domain with a smooth fade-in"
          f" (unvoiced/fricative frames kept) (ramp≈{ramp} frames/{ramp*fp:.0f}ms)")

    # 6.5) Plosive transient envelope transplant: pyworld's per-frame spectral
    #      reconstruction flattens the plosive's steep rise; use the source audio's
    #      per-frame energy envelope to move the real attack shape onto the synthesized main signal
    if PLOSIVE_ENV_TRANSPLANT and np.any(plosive_seed):
        # The source envelope fold was already computed above on the same time base (src_rms_out); reuse it directly here
        main_rms = np.zeros(n_out, dtype=np.float64)
        for i in range(n_out):
            s = _frame_bound(i, sr, fp)
            e = min(_frame_bound(i + 1, sr, fp), len(out_data))
            if e > s:
                main_rms[i] = float(np.sqrt(np.mean(out_data[s:e] ** 2)))
        env_gain = _transient_env_gain(main_rms, src_rms_out, plosive_seed,
                                       PLOSIVE_ENV_HEAD, PLOSIVE_ENV_TAIL,
                                       PLOSIVE_ENV_GAIN_MIN, PLOSIVE_ENV_GAIN_MAX)
        out_data = out_data * _frame_gain_to_samples(env_gain, sr, fp, len(out_data))
        pk = float(np.max(np.abs(out_data)))
        print(f"[Plosive transient transplant] main-signal peak after transplant {pk:.3f}"
              f"{' (close to clipping; reduce PLOSIVE_ENV_GAIN_MAX)' if pk > 0.95 else ''}")

    # 7) Call prosses_breath to build the concatenated breath and add it directly to the final output (bypassing the original file)
    if not ADD_BREATH:
        print("[Breath] ADD_BREATH=False → skipping breath generation and mixing (output is purely glottal)")
    else:
        breath_concat, br_sr = prosses_breath.build_concat_breath()
        assert br_sr == sr, f"breath sample rate {br_sr} does not match the main signal {sr}"
        breath_concat = _guard_samples(breath_concat, "breath")
        _probe_samples("breath·concat", breath_concat, sr, fp)

        # 7.1) Breath spectral coupling: shape the breath spectral envelope to the main signal sp (SP frames are 1025 bins,
        #      i.e. the output grid of pyworld cheaptrick's default fft_size=2048), removing formant misalignment and the disjointed feel
        if BREATH_SHAPE_TO_MAIN and sp_out is not None:
            r_before = float(np.sqrt(np.mean(breath_concat ** 2)))
            breath_concat = _match_breath_envelope(
                breath_concat, sp_out, sr, fp, 2048, BREATH_SHAPE_BANDS,
                BREATH_SHAPE_GAIN_MIN, BREATH_SHAPE_GAIN_MAX)
            r_after = float(np.sqrt(np.mean(breath_concat ** 2)))
            print(f"[Breath spectral coupling] spectral envelope shaped to the main-signal sp "
                  f"({BREATH_SHAPE_BANDS} log bands, gain limited to "
                  f"{BREATH_SHAPE_GAIN_MIN}~{BREATH_SHAPE_GAIN_MAX}×, "
                  f"RMS {r_before:.4f}→{r_after:.4f} preserved)")

        n_add = min(len(out_data), len(breath_concat))
        print(f"[Breath mix×{BREATH_GAIN:g}] concatenated breath {len(breath_concat)}smp → "
              f"{n_add}smp mixed in (RMS {np.sqrt(np.mean(breath_concat[:n_add] ** 2)):.4f})")
        out_data[:n_add] += BREATH_GAIN * breath_concat[:n_add]
        _probe_samples("after-breath-mix", out_data, sr, fp)

    # 7.5) Silent frames marked None by the pitch line: hard-zero in the sample domain
    #      (together with the already mixed breath), so a "silent frame" is truly mute,
    #      not merely f0=0.  The mute gain gets a short raised-cosine fade at both
    #      edges: multiplying a raw 0/1 mask would cut the waveform instantly -> click.
    if pl_silent.any():
        mute_gain = np.ones(n_out, dtype=np.float64)
        fade = max(1, int(round(SILENCE_FADE_MS / fp)))
        edges = np.diff(np.concatenate([[0], pl_silent.astype(np.int8), [0]]))
        for s, e in zip(np.where(edges == 1)[0], np.where(edges == -1)[0]):
            a = max(0, s - fade)
            u = np.linspace(0.0, 1.0, s - a, endpoint=False, dtype=np.float64)
            mute_gain[a:s] = np.minimum(mute_gain[a:s], 1.0 - (3.0 * u ** 2 - 2.0 * u ** 3))
            mute_gain[s:e] = 0.0
            b = min(n_out, e + fade)
            u = np.linspace(0.0, 1.0, b - e, endpoint=False, dtype=np.float64)
            mute_gain[e:b] = np.minimum(mute_gain[e:b], 3.0 * u ** 2 - 2.0 * u ** 3)
        out_data = out_data * _resample_1d(mute_gain, len(out_data))
        print(f"[Pitch line·mute] {int(pl_silent.sum())} silent frames zeroed in the sample domain"
              f" (including breath/plosive layers, {fade}-frame/{fade * fp:.0f}ms fade at both edges)")

    # 8) Start/end fade in/out + save
    out_data = _guard_samples(out_data, "final")
    _probe_samples("final", out_data, sr, fp)
    out_data = _edge_fade(out_data, sr, EDGE_FADE_MS)
    final = vl.WAVAudio(data=out_data, sample_rate=sr)
    final.save(OUTPUT_WAV, bit_depth=BIT_DEPTH)
    print(f"[Done] saved: {OUTPUT_WAV}  (duration {final.duration:.2f}s, RMS={final.rms:.4f})")

    # 9) Clean up intermediate artifacts (per-segment breath wav + spliced frame txt)
    #    The concatenated breath has already been read in step 7 by
    #    prosses_breath.build_concat_breath(), so deleting them here does not affect
    #    this run; turn this switch off if you want to run prosses_breath.py separately.
    if CLEAN_INTERMEDIATE:
        removed = []
        for i in range(len(SEGMENTS)):
            p = OUTPUT_BREATH_FMT.format(i)
            if os.path.isfile(p):
                os.remove(p)
                removed.append(os.path.basename(p))
        if os.path.isfile(OUTPUT_FRAMES_TXT):
            os.remove(OUTPUT_FRAMES_TXT)
            removed.append(os.path.basename(OUTPUT_FRAMES_TXT))
        print(f"[Cleanup] removed {len(removed)} intermediate artifacts: {', '.join(removed) if removed else 'none'}")
    return final


if __name__ == "__main__":
    # Running directly = synthesize to the default output path using the defaults section at the top of the file
    synthesize(DEFAULT_SEGMENTS, OUTPUT_WAV)
