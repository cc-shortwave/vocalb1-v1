# vocal_lib

**English** | [中文](README.zh-CN.md) | [日本語](README.ja.md)

A vocal analysis and synthesis library — an object-oriented toolkit for glottal excitation decomposition, synthesis, and pitch shifting.

## Installation

```bash
# Option 1: pip install (recommended)
pip install .
pip install -e .          # development mode
pip install .[full]       # includes pyworld

# Option 2: copy the vocal_lib/ folder directly into your project
```

## Dependencies

| Dependency | Required | Purpose |
|---|---|---|
| numpy >= 1.20 | Yes | Numerical computation |
| soundfile >= 0.12 | Yes | WAV read/write |
| pyworld >= 0.3.0 | No | F0/sp/ap extraction (functionality degrades if absent) |

## Quick start

```python
from vocal_lib import decompose_vocal, synthesize_vocal, pitch_shift

# 1. Decompose: WAV -> F0 + breath + glottal excitation
f0, breath, glottal = decompose_vocal("ai.wav")

# 2. Pitch shift: C4 -> E4 (up 4 semitones)
f0_new, glottal_new = pitch_shift(f0, glottal, "C4", "E4")

# 3. Synthesize: F0 + breath + glottal excitation -> WAV
output = synthesize_vocal(f0_new, breath, glottal_new)
output.save("_output/result.wav", bit_depth=24)
```

---

## 1. Data classes (data_classes.py)

### 1.1 F0 — fundamental frequency track class

Wraps a sequence of F0 values, the time axis, voiced flags, and other metadata; it is a pure data container.

**Attributes**

| Attribute | Type | Description |
|---|---|---|
| `values` | `np.ndarray[float64]` | F0 value sequence (Hz), 0 for silent frames |
| `time_axis` | `np.ndarray[float64]` | Corresponding time axis (seconds) |
| `frame_period` | `float` | Frame period (milliseconds), default 5.0 |
| `sample_rate` | `int` | Sample rate |
| `voiced_flags` | `np.ndarray[bool]` | Voiced flags (True = voiced) |

**Property access**

| Property | Returns | Description |
|---|---|---|
| `n_frames` | `int` | Number of frames = len(values) |

**Methods**

| Method | Description |
|---|---|
| `copy()` | Deep copy, returns a new F0 object |
| `save(path)` | Serialize to a `.npy` file (dict format) |
| `load(path)` | Class method, deserialize from `.npy` |
| `__len__()` | Returns the number of frames |
| `__repr__()` | Returns a summary string `F0(n_frames=..., avg=...Hz)` |

**Internal structure**: at construction, if `time_axis` is not provided it is generated automatically as `arange(n) * frame_period / 1000`. If `voiced_flags` is not provided it is generated automatically as `values > 0`. save/load use `np.save` to pack the dict into a `.npy` file.

---

### 1.2 WAVAudio — WAV audio class

Wraps audio sample data and the sample rate, and supports file read/write.

**Attributes**

| Attribute | Type | Description |
|---|---|---|
| `data` | `np.ndarray[float64]` | Audio sample data (1-D for mono, 2-D for multi-channel) |
| `sample_rate` | `int` | Sample rate |
| `source_path` | `Optional[str]` | Original file path |

**Property access**

| Property | Returns | Description |
|---|---|---|
| `duration` | `float` | Duration (seconds) = len(data) / sample_rate |
| `n_samples` | `int` | Number of samples |
| `n_channels` | `int` | Number of channels |
| `is_mono` | `bool` | Whether the audio is mono |
| `rms` | `float` | RMS energy value |

**Methods**

| Method | Description |
|---|---|
| `from_file(path, mono=True)` | Class method, loads a WAV file with soundfile; when `mono=True` the channels are mixed down to mono |
| `to_mono()` | Convert multi-channel to mono (averaging); returns a copy if already mono |
| `save(path, bit_depth=24, target_sample_rate=None)` | Save as WAV, supports PCM_8/16/24 bit depths, optional resampling |
| `copy()` | Deep copy |
| `__len__()` | Returns the number of samples |
| `__repr__()` | Returns a summary string |

**Internal structure**: `save()` first normalizes (scales down when the peak exceeds 1.0), then writes through `soundfile.write`. Resampling uses linear interpolation. Bit depth is mapped through `subtype_map = {8: 'PCM_U8', 16: 'PCM_16', 24: 'PCM_24'}`.

---

### 1.3 GlottalExcitation — glottal excitation class

Wraps the glottal time-domain signal together with pyworld parameters, open quotient, intensity tracks, and related data.

**Attributes**

| Attribute | Type | Description |
|---|---|---|
| `signal` | `np.ndarray[float64]` | Glottal excitation time-domain signal |
| `sample_rate` | `int` | Sample rate |
| `sp` | `Optional[np.ndarray]` | pyworld spectral envelope (n_frames, n_bins) |
| `ap` | `Optional[np.ndarray]` | pyworld aperiodicity (n_frames, n_bins) |
| `oq` | `float` | Open Quotient |
| `glottal_intensity` | `Optional[np.ndarray]` | Per-frame glottal intensity ratio |
| `parameters` | `Dict[str, Any]` | Dictionary of extended parameters |

**Keys the `parameters` dictionary may contain**

| Key | Type | Description |
|---|---|---|
| `jitter` | `Dict[str, float]` | Frequency perturbation: jitter_local, jitter_rap, jitter_ppq5, jitter_ddp |
| `shimmer` | `Dict[str, float]` | Amplitude perturbation: shimmer_local, shimmer_apq3, shimmer_apq5, shimmer_dda |
| `formants` | `List[List[Dict]]` | Formant trajectories: each frame holds F1-F5 with frequency/magnitude/index |
| `f0_stats` | `Dict[str, float]` | F0 statistics: avg_f0, min_f0, max_f0, f0_std, voiced_ratio |
| `glottal_pulse` | `np.ndarray` | Glottal pulse trajectory |
| `formant_intensity` | `np.ndarray` | Per-frame formant intensity ratio |
| `breath_intensity` | `np.ndarray` | Per-frame breath intensity ratio |
| `_pitch_shift_info` | `Dict` | Pitch-shift metadata (semitones, pitch_factor, max_f0, etc.) |

**Property access**

| Property | Returns | Description |
|---|---|---|
| `duration` | `float` | Duration (seconds) |
| `n_samples` | `int` | Number of samples |
| `has_pyworld_params` | `bool` | Whether both sp and ap are present |
| `jitter` | `Optional[Dict]` | Shortcut to parameters['jitter'] |
| `shimmer` | `Optional[Dict]` | Shortcut to parameters['shimmer'] |

**Methods**

| Method | Description |
|---|---|
| `get_param(key, default=None)` | Read a value from the parameters dictionary |
| `set_param(key, value)` | Write a value into the parameters dictionary |
| `copy()` | Deep copy (including all copyable values in parameters) |
| `save(path)` | Serialize to `.npy` |
| `load(path)` | Class method, deserialize from `.npy` |

---

## 2. Top-level API functions

### 2.1 decompose_vocal — vocal decomposition

**Location**: `vocal_lib/decomposition.py`

```python
decompose_vocal(input_audio, sample_rate=44100, frame_period=5.0) -> (F0, WAVAudio, GlottalExcitation)
```

**Purpose**: decompose the input audio into three components: the F0 fundamental frequency track, breath WAV, and glottal excitation.

**Input**: `input_audio` accepts three types:
- `str`: a file path, loaded through `WAVAudio.from_file()`
- `WAVAudio`: used directly
- `np.ndarray`: `sample_rate` must be specified

**Processing flow**:

```
Input audio
  |
  v
_normalize_input()  ----> WAVAudio object
  |
  v
engine.decompose_vocal(data_mono, sr)
  |
  |  1. f0_detection.detect_f0_sequence()
  |     -> pyworld DIO + StoneMask (preferred)
  |     -> or the in-house three-method fusion (autocorrelation + cepstrum + YIN)
  |     -> median filtering + Viterbi smoothing
  |
  |  2. voice_separation.decompose_voice()
  |     -> framing (2048pt, hop=512)
  |     -> STFT (Hanning window)
  |     -> per-frame voiced/unvoiced detection (energy + ZCR + spectral flatness)
  |     -> harmonic band location -> three-component mask separation
  |        - glottal component = harmonic bands * (1 - breath_ratio)
  |        - formant component = formant regions * (1 + breath_ratio)
  |        - breath component = non-harmonic regions * breath_ratio
  |     -> IRFFT reconstruction + overlap-add synthesis
  |     -> energy normalization
  |
  |  3. pyworld_processor.extract_pyworld_features() (optional)
  |     -> DIO extracts F0
  |     -> StoneMask refines F0
  |     -> CheapTrick extracts the spectral envelope sp
  |     -> D4C extracts the aperiodicity ap
  |     -> compute_jitter() / compute_shimmer()
  |     -> compute_oq() / compute_f0_statistics()
  |
  v
packed as (F0, WAVAudio_breath, GlottalExcitation)
```

**Underlying algorithms in detail**:

1. **F0 detection** (`f0_detection.py`)
   - Prefers pyworld's DIO algorithm (based on harmonic structure) plus StoneMask refinement
   - If pyworld is unavailable, falls back to a three-method fusion:
     - **Autocorrelation**: apply a Hanning window, compute the autocorrelation, find peaks in the min_lag~max_lag range, and verify with harmonic multiples
     - **Cepstrum**: take the log of the magnitude spectrum, apply an IFFT to obtain the cepstrum, and find peaks in the quefrency domain
     - **YIN algorithm**: compute the difference function and the cumulative mean normalized difference function, find local minima below the threshold, and refine with parabolic interpolation
   - **Fusion strategy**: when the standard deviation of the three results is < 30Hz, take a weighted average (YIN weight 0.4, the others 0.3); otherwise take the result with the highest confidence
   - **Post-processing**: 5-frame median filtering + Viterbi-style smoothing (handling jumps and silence filling)

2. **Vocal tract separation** (`voice_separation.py`)
   - **Framing**: frame_size=2048, hop_size=512, Hanning window
   - **STFT**: `rfft` computes the magnitude and phase spectra
   - **Voiced detection**: scores short-time energy (threshold 0.001), zero crossing rate (threshold 0.15), and spectral flatness (threshold 0.3) together
   - **Harmonic band location**: using F0 as the fundamental, locate harmonic bands around each harmonic frequency (tolerance = max(15Hz, F0*3%))
   - **Three-component separation**:
     - Harmonic mask: 1 inside the harmonic bands, smoothed over 3 points
     - Formant mask: a triangular window around formant frequencies (bandwidth 80Hz)
     - Breath ratio: determined by spectral flatness (at most 0.3)
     - Energy is distributed according to these ratios so that the sum of the three components equals the original spectrum
   - **Reconstruction**: IRFFT restores the time-domain frames + overlap-add stitching + energy scaling

3. **pyworld parameter extraction** (`pyworld_processor.py`)
   - `pw.dio()`: coarse F0 estimate
   - `pw.stonemask()`: F0 refinement
   - `pw.cheaptrick()`: spectral envelope sp
   - `pw.d4c()`: aperiodicity ap
   - **Jitter** computation: jitter_local (mean of adjacent-frame differences / mean), RAP (3-point average), PPQ5 (5-point average), DDP (second-order difference)
   - **Shimmer** computation: shimmer_local (mean of adjacent-frame amplitude differences / mean), APQ3, APQ5, DDA
   - **OQ** computation: split each period's ap into a first and second half, take the mean of the energy ratios
   - **F0 statistics**: avg/min/max/std/voiced_ratio

---

### 2.2 synthesize_vocal — vocal synthesis

**Location**: `vocal_lib/synthesis.py`

```python
synthesize_vocal(f0, breath, glottal, *, match_rms_to=None, model_type='lf') -> WAVAudio
```

**Purpose**: reconstruct synthesized speech from F0 + breath + glottal excitation.

**Parameters**:

| Parameter | Type | Default | Description |
|---|---|---|---|
| `f0` | `F0` | - | F0 fundamental frequency track |
| `breath` | `WAVAudio` | - | Breath component |
| `glottal` | `GlottalExcitation` | - | Glottal excitation |
| `match_rms_to` | `Optional[float]` | None | Target RMS energy |
| `model_type` | `str` | `'lf'` | Glottal model: `'lf'` or `'rosenberg'` |

**Processing flow**:

```
F0 + GlottalExcitation
  |
  v
extract parameters: f0.values, sp, ap, oq, jitter, shimmer, formants
  |
  v
engine.synthesize_voice_from_parameters()
  |
  |  Path A (pyworld available + sp + ap):
  |    VoiceSynthesizer._synthesize_with_pyworld()
  |    -> pw.synthesize(f0, sp, ap, sr)
  |    -> dynamic range compression (threshold=0.5, ratio=3:1)
  |    -> RMS matching
  |    -> soft clipping (tanh limiter, threshold=0.85)
  |    -> normalize to 0.9
  |
  |  Path B (no pyworld):
  |    -> ExcitationGenerator.generate_with_perturbations(f0, jitter, shimmer)
  |       (phase accumulator generates the impulse train + jitter/shimmer perturbation)
  |    -> GlottalPulseGenerator.generate_pulse(t0, sr)
  |       (LF model or Rosenberg model generates a single-cycle glottal pulse)
  |    -> convolve_with_pulse(excitation, pulse)
  |    -> differentiate_glottal_flow(u_g)  (differentiate the glottal flow to get the source)
  |    -> synthesize_formant_filtered_voice(source, sr, formants)
  |       (pre-emphasis -> VocalTractFilter formant filtering -> de-emphasis)
  |
  v
WAVAudio(data=synthesized, sample_rate=sr)
```

**Underlying algorithms in detail**:

1. **pyworld synthesis** (`pyworld_processor.py`)
   - `pw.synthesize(f0, sp, ap, sr, frame_period)`: synthesize speech directly from the parameters
   - The synthesized output then goes through dynamic range compression + soft clipping to prevent clipping

2. **Glottal model synthesis** (`glottal_models.py` + `excitation_generator.py`)
   - **LF model** (Liljencrants-Fant): a four-phase glottal airflow model
     - Closed phase (0 ~ ta): output 0
     - Opening phase (ta ~ tb): sinusoidal rise `sin(pi * t1)`
     - Deceleration phase (tb ~ te): cosine-squared decay `cos(pi*t2/2)^2 * (1 - rd*t2)`
     - Return phase (te ~ 1): output 0
   - **Rosenberg model**: a combination of a binomial and a trinomial
     - Rising part (0 ~ epsilon): quadratic polynomial `2t/eps - (t/eps)^2`
     - Falling part (epsilon ~ 1): cubic polynomial `(1-alpha)(1-3t'^2+2t'^3) + alpha(1-t')`
   - **Excitation generation**: `ExcitationGenerator` uses a phase accumulator to generate the impulse train frame by frame, adding jitter (period perturbation ±10%) and shimmer (amplitude perturbation)
   - **Convolution**: convolving the excitation signal with the glottal pulse gives the glottal airflow `u_g`
   - **Differentiation**: `diff(u_g)` gives the glottal source (modeling the negative impulse at vocal fold closure)

3. **Vocal tract filtering** (`vocal_tract_filter.py`)
   - **Pre-emphasis**: `y[i] = x[i] - 0.97 * x[i-1]`, boosting high frequencies
   - **Formant filtering**: `VocalTractFilter.apply_formant_filter()` applies a second-order IIR resonator per frame
     - Each formant builds one biquad: `omega = 2*pi*f/sr`, `alpha = sin(omega)*sinh(ln2/2 * bw * 2*pi/sr)`
     - Numerator `[1, -2cos(omega), 1]`, denominator `[1+alpha, -2cos(omega), 1-alpha]`
     - Multiple formants are cascaded (coefficients convolved)
   - **De-emphasis**: `y[i] = x[i] + 0.97 * y[i-1]`, restoring low frequencies

---

### 2.3 pitch_shift — pitch shifting

**Location**: `vocal_lib/pitch.py`

```python
pitch_shift(f0, glottal, original_pitch, target_pitch) -> (F0, GlottalExcitation)
```

**Purpose**: from the original pitch and the target pitch, modify F0 and the glottal parameters and return new objects.

**Pitch representations** (formats supported by `_parse_semitones()`):

| Format | Example | Parsing logic |
|---|---|---|
| Scientific pitch | `"C4"`, `"A4"`, `"F#5"`, `"Bb3"` | MIDI = (octave+1)*12 + note-name offset, semitone difference = MIDI - 60 |
| Hz | `"440Hz"`, `440` (numeric value > 50) | `12 * log2(hz / C4_hz)`, C4 = 261.63Hz |
| Project style | `"1D"`, `"-1C"`, `"0C#"` | octave offset*12 + note-name offset |
| Semitone count | `+3`, `-5`, `0.0` (numeric value <= 50) | used directly |

**Processing flow**:

```
original_pitch, target_pitch
  |
  v
_parse_semitones() -> source semitones, target semitones
  |
  v
semitones = dst_semi - src_semi
  |
  v
pitch_shifter.process_pitch_shift(f0.values, semitones, sr, oq, ap, sp)
  |
  |  1. calculate_pitch_factor(semitones)
  |     -> k = 2^(semitones/12)
  |
  |  2. shift_f0_logarithmic(f0, semitones)
  |     -> f0_new = exp(log(f0) + log(k))  [shift in the log domain]
  |     -> soft limiting: 550Hz soft knee, 900Hz hard ceiling (tanh compression)
  |
  |  3. smooth_f0_boundaries(f0_shifted)
  |     -> 10ms fade-in/fade-out at the start and end
  |
  |  4. adjust_oq(oq, semitones)
  |     -> upward shift: OQ decreases (comfortable range -0.01/st, beyond it -0.02/st, floor 0.30)
  |     -> downward shift: OQ increases (comfortable range +0.008/st, beyond it +0.015/st, ceiling 0.75)
  |
  |  5. enhance_breathiness(ap, semitones)
  |     -> ap *= (1 + semitones * 0.03), an extra +0.04/st beyond the comfortable range
  |     -> ceiling 2.5x, clipped to [0, 1]
  |
  |  6. warp_spectral_envelope(sp, semitones, sr)  [prevents formant-conflict clipping]
  |     -> formants follow the pitch shift by 30%, keeping 70% of their original position
  |     -> frequency-axis interpolation mapping
  |
  v
packed as (new F0, new GlottalExcitation)
  |
  v
GlottalExcitation uses sp_warped (the frequency-axis mapped spectral envelope)
parameters stores the _pitch_shift_info metadata
```

**Anti-clipping mechanisms (5 layers)**:

| Mechanism | Location | Effect |
|---|---|---|
| F0 soft limiting | `shift_f0_logarithmic()` | 550~900Hz tanh compression, modeling the transition into the falsetto range |
| Accelerated OQ correction | `adjust_oq()` | At very high pitches OQ drops to 0.30, at very low pitches it rises to 0.75 |
| Spectral envelope mapping | `warp_spectral_envelope()` | Formants follow by 30%, avoiding harmonic-formant conflicts |
| Dynamic range compression | `voice_synthesizer._apply_compression()` | Peaks above 0.5 are compressed at 3:1 |
| Soft clipping | `voice_synthesizer._soft_clip()` | Amplitudes above 0.85 are smoothly limited with tanh |

---

## 3. Engine layer (engine.py)

The engine layer is the bridge between the top-level API and the low-level core modules; all imports use package-relative imports.

### 3.1 decompose_vocal(audio_data, sample_rate) -> Dict

**Flow**:
1. `detect_f0_sequence(audio_data, sr)` -> F0 track
2. `decompose_voice(audio_data, sr, f0_sequence)` -> three-component separation
3. `extract_pyworld_features(audio_data, sr)` -> sp/ap/jitter/shimmer/oq/f0_stats (optional)
4. Assemble the result dictionary and return it

### 3.2 synthesize_voice_from_parameters(f0_sequence, sample_rate, jitter, shimmer, formants_trajectory, model_type, sp, ap, target_rms, glottal_parameters) -> np.ndarray

**Flow**:
1. Instantiate `VoiceSynthesizer(sample_rate, model_type)`
2. Call `synthesizer.synthesize_from_parameters()`
3. Choose pyworld synthesis or glottal-model synthesis depending on whether sp/ap are present

---

## 4. Core modules (core/)

### 4.1 f0_detection.py — F0 detection

| Function | Algorithm | Description |
|---|---|---|
| `detect_f0_autocorrelation(frame, sr)` | Autocorrelation | Hanning windowing -> autocorrelation -> harmonic multiple verification -> confidence threshold 0.3 |
| `detect_f0_cepstrum(mag_spec, sr, freqs)` | Cepstrum | log|FFT| -> IFFT -> peak detection in the quefrency domain |
| `yin_f0_detection(frame, sr)` | YIN | difference function -> cumulative mean normalization -> threshold 0.15 -> parabolic interpolation refinement |
| `fuse_f0_methods(...)` | Fusion | weighted average when std < 30Hz (YIN 0.4, the others 0.3), otherwise the highest confidence |
| `smooth_f0(f0, win=5)` | Median filtering | 5-frame window median filtering |
| `viterbi_smooth(f0, ...)` | Viterbi style | jump penalty + silence filling (keeps 50% of the previous value when silence lasts < 3 frames) |
| `detect_f0_sequence(audio, sr)` | Main entry | pyworld preferred, falling back to the three-method fusion |

### 4.2 voice_separation.py — Vocal tract separation

| Function | Description |
|---|---|
| `frame_audio(audio, frame_size=2048, hop=512)` | Framing, zero-padded at the end |
| `compute_stft(frames, sr)` | Hanning windowing + rfft, returns magnitude spectrum/phase spectrum/frequency axis |
| `calculate_short_time_energy(frame)` | Short-time energy = sum(x^2)/N |
| `calculate_zero_crossing_rate(frame)` | Zero crossing rate = number of sign changes / 2 / N |
| `calculate_spectral_flatness(mag)` | Spectral flatness = geometric mean / arithmetic mean |
| `detect_voiced_unvoiced(frame, mag, f0, sr)` | Combined scoring: F0>0 (+0.4) + energy (+0.2) + ZCR (+0.2) + flatness (+0.2) |
| `locate_harmonic_bands(f0, freqs)` | Locate harmonic bands around n*F0, tolerance = max(15, F0*3%) |
| `extract_formants(mag, freqs)` | 15-point smoothing -> peak detection -> take the first 5 in the 200~4000Hz range |
| `separate_three_components(mag, phase, freqs, f0, voiced)` | Core separation: harmonic mask + formant mask + breath ratio allocation |
| `decompose_voice_frame(frame, mag, phase, freqs, f0, sr)` | Complete single-frame processing: detection + separation + reconstruction + energy normalization |
| `decompose_voice(audio, sr, f0_seq)` | Main entry: frame-by-frame decomposition + overlap-add reconstruction + global energy normalization |

### 4.3 pyworld_processor.py — pyworld parameter processing

| Function | Description |
|---|---|
| `extract_pyworld_features(audio, sr)` | DIO F0 -> StoneMask refinement -> CheapTrick sp -> D4C ap |
| `compute_jitter(f0, time_axis)` | jitter_local/RAP/PPQ5/DDP (percentage) |
| `compute_shimmer(f0, audio, sr, time_axis)` | shimmer_local/APQ3/APQ5/DDA (percentage) |
| `compute_oq(ap, f0, sr)` | Open quotient: mean of the energy ratios between the first and second half of ap in each period |
| `extract_glottal_pulse(f0, time_axis, sr)` | Generate a Gaussian-envelope glottal pulse trajectory |
| `compute_f0_statistics(f0)` | avg/min/max/std/voiced_ratio |
| `synthesize_pyworld(f0, sp, ap, sr)` | `pw.synthesize()` parametric synthesis |

### 4.4 voice_synthesizer.py — Speech synthesizer

**VoiceSynthesizer class**

| Method | Description |
|---|---|
| `synthesize_from_parameters(f0, jitter, shimmer, ...)` | Main entry: uses pyworld when sp/ap are present, otherwise the glottal model |
| `_synthesize_with_pyworld(f0, sp, ap, target_rms)` | pyworld synthesis + dynamic compression + soft clipping |
| `_synthesize_with_lf_trajectory(f0, lf_traj, jitter, shimmer)` | Per-frame LF parameter synthesis (supports smooth parameter transitions) |
| `synthesize_from_glottal_signal(glottal, f0, ...)` | Synthesize from an existing glottal signal (differentiation + formant filtering) |
| `synthesize_from_decomposition(result)` | Synthesize directly from a decomposition result dictionary |
| `export_audio(signal, path, bit_depth, target_sr)` | Export high-fidelity audio (supports resampling) |
| `_resample(signal, src, dst)` | Linear interpolation resampling |

**Module-level functions**

| Function | Description |
|---|---|
| `_soft_clip(signal, threshold=0.85)` | tanh soft limiter, smoothly compressing the part above the threshold |
| `_apply_compression(signal, threshold=0.5, ratio=3.0)` | Dynamic range compression, peaks above the threshold are scaled down proportionally |
| `synthesize_voice(f0, sr, jitter, shimmer, ...)` | Convenience function: instantiate the synthesizer and synthesize |
| `synthesize_voice_from_glottal(signal, sr, ...)` | Convenience function: synthesize from a glottal signal |
| `final_audio_processing(signal, sr, normalize, preemphasis)` | Post-processing: pre-emphasis + normalization |

### 4.5 pitch_shifter.py — Pitch shifting

| Function | Description |
|---|---|
| `calculate_pitch_factor(semitones)` | `k = 2^(semitones/12)` |
| `shift_f0_logarithmic(f0, semitones)` | Log-domain shift + soft limiting (550~900Hz tanh compression) |
| `smooth_f0_boundaries(f0, sr, frame_period)` | 10ms fade-in/fade-out at the start and end |
| `adjust_oq(oq, semitones)` | Adaptive OQ correction (linear in the comfortable range, accelerated beyond it, limits 0.30~0.75) |
| `enhance_breathiness(ap, semitones)` | Breath enhancement (+0.03/st, +0.04/st beyond the range, ceiling 2.5x) |
| `warp_spectral_envelope(sp, semitones, sr)` | Spectral envelope frequency-axis mapping (formants follow by 30%) |
| `soft_clip(signal, threshold=0.85)` | tanh soft clipping |
| `process_pitch_shift(f0, semitones, sr, oq, ap, sp)` | Handle all parameters in one call, returns a result dictionary |

### 4.6 glottal_models.py — Glottal models

**LFModel class** (Liljencrants-Fant model)

| Parameter | Range | Description |
|---|---|---|
| `rd` | 0.0~1.0 | Deceleration coefficient, controls the slope of the cd segment |
| `ta` | 0.0~1.0 | Normalized rise time |
| `tb` | 0.0~1.0 | Normalized peak time |
| `te` | Usually 1.0 | Normalized total period |

| Method | Description |
|---|---|
| `generate_pulse(t0, sr)` | Generate a single-cycle LF waveform (4 phases: closed -> sinusoidal rise -> cosine decay -> closed) |
| `generate_continuous(f0_seq, sr)` | Concatenate cycle by cycle into a continuous glottal airflow |

**RosenbergModel class**

| Parameter | Range | Description |
|---|---|---|
| `alpha` | 0.0~1.0 | Normalized rise time |
| `epsilon` | 0.0~1.0 | Normalized peak position |

| Method | Description |
|---|---|
| `generate_pulse(t0, sr)` | Quadratic polynomial for the rising segment + cubic polynomial for the falling segment |
| `generate_continuous(f0_seq, sr)` | Concatenate cycle by cycle |

**GlottalPulseGenerator class**: a unified interface that calls LF or Rosenberg depending on model_type.

**Module-level functions**

| Function | Description |
|---|---|
| `differentiate_glottal_flow(u_g)` | Differentiate the glottal airflow to get the source signal: `source = diff(u_g)` |
| `compute_glottal_parameters(signal, sr)` | Extract LF parameters from a glottal signal (peak, rise/fall times, zero crossings) |

### 4.7 excitation_generator.py — Excitation generation

**ExcitationGenerator class**

| Method | Description |
|---|---|
| `generate_impulse_train(f0_seq, frame_period=5.0)` | Phase accumulator generates an exact impulse train |
| `generate_with_perturbations(f0_seq, jitter, shimmer)` | Impulse train with jitter (period perturbation ±10%) and shimmer (amplitude perturbation) added |

**Module-level functions**

| Function | Description |
|---|---|
| `apply_jitter(f0, jitter_pct)` | Apply Gaussian perturbation to the F0 track |
| `apply_shimmer(amp_seq, shimmer_pct)` | Apply Gaussian perturbation to the amplitude sequence |
| `generate_white_noise(duration, sr, amp)` | White noise generation |
| `generate_pink_noise(duration, sr, amp)` | Pink noise generation (1/sqrt(f) filtering in the FFT domain) |
| `generate_excitation(f0, sr, jitter, shimmer, noise_level)` | Full excitation = impulse train + pink noise |
| `convolve_with_pulse(excitation, pulse)` | Convolve the excitation with the glottal pulse to get the glottal airflow |

### 4.8 vocal_tract_filter.py — Vocal tract filter

**VocalTractFilter class**

| Method | Description |
|---|---|
| `apply_lpc_filter(signal, lpc_coeffs)` | Per-frame LPC all-pole filtering |
| `apply_formant_filter(signal, formants, bandwidths)` | Per-frame cascaded second-order IIR formant filtering |
| `get_impulse_response(formants, bandwidths, length)` | Get the impulse response |
| `get_lpc_impulse_response(lpc_coeffs, length)` | Get the LPC impulse response |
| `get_frequency_response(formants, bandwidths, n_fft)` | Get the frequency response (freqs, mag_db) |
| `get_lpc_frequency_response(lpc_coeffs, n_fft)` | Get the LPC frequency response |
| `get_filter_coefficients(formants, bandwidths, frame_idx)` | Get the (b, a) coefficients for a given frame |

**Module-level functions**

| Function | Description |
|---|---|
| `compute_lpc_coefficients(signal, sr, frame_size, hop, order)` | Solve for LPC coefficients with Levinson-Durbin recursion |
| `lpc_to_formants(lpc_coeffs, sr)` | Find the roots of the LPC polynomial -> formant frequencies/bandwidths |
| `apply_preemphasis(signal, alpha=0.97)` | Pre-emphasis `y[i] = x[i] - alpha*x[i-1]` |
| `apply_deemphasis(signal, alpha=0.97)` | De-emphasis `y[i] = x[i] + alpha*y[i-1]` |
| `synthesize_formant_filtered_voice(source, sr, formants)` | Pre-emphasis -> formant filtering -> de-emphasis -> normalization |
| `synthesize_lpc_filtered_voice(source, sr, lpc_coeffs)` | Pre-emphasis -> LPC filtering -> de-emphasis -> normalization |

---

## 5. Project structure

### 5.1 Overall layout

```
VocalB1/
├── VocalB1Synthesizer.py      # Main entry: multi-segment voiced-frame concatenation synthesis (beat arrangement + breath + plosives)
│                              #   Library usage: synthesize(segments, output_path, ...)
├── VocalDecomposer.py         # Decomposition wrapper (WAV -> per-frame parameters)
├── VocalSynthesizer.py        # Synthesis wrapper (per-frame parameters -> WAV)
├── prosses_breath.py          # Breath concatenation (called internally by VocalB1Synthesizer)
├── tools/                     # Auxiliary tools (the engine does not depend on them)
│   ├── spectrogram.py         # STFT spectrogram viewer (supports box-selection zoom)
│   ├── oscilloscope.py        # Waveform viewer
│   ├── pit_editor.py          # Pitch curve editor (pygame)
│   ├── _plosive_diag.py       # Plosive diagnostics
│   └── test_vocal_lib.py      # Regression tests (library self-check, run directly)
├── _output/                   # Directory for synthesized output
└── vocal_lib/                 # Core library (see 5.2)
```

### 5.2 Inside vocal_lib

```
vocal_lib/
├── __init__.py            # Unified exports + metadata
├── data_classes.py        # F0 / WAVAudio / GlottalExcitation
├── engine.py              # Core engine (decomposition / synthesis)
├── decomposition.py       # decompose_vocal() top-level wrapper
├── synthesis.py           # synthesize_vocal() top-level wrapper
├── pitch.py               # pitch_shift() top-level wrapper
└── core/                  # Low-level implementation
    ├── f0_detection.py    # F0 detection (pyworld + autocorrelation + cepstrum + YIN)
    ├── voice_separation.py # Vocal tract separation (STFT + harmonic mask + three components)
    ├── pyworld_processor.py # pyworld parameter extraction/synthesis
    ├── voice_synthesizer.py # Speech synthesizer (pyworld + LF/Rosenberg + formant filtering)
    ├── vocal_tract_filter.py # Vocal tract filter (LPC + formant IIR)
    ├── pitch_shifter.py   # Pitch shifting (F0 scaling + OQ correction + spectral mapping + anti-clipping)
    ├── glottal_models.py  # Glottal models (LF + Rosenberg)
    └── excitation_generator.py # Excitation generation (impulse train + perturbation + noise)
```

---

## 6. Complete example

```python
from vocal_lib import *
import numpy as np
import os

os.makedirs("_output", exist_ok=True)

# ========== 1. Decomposition ==========
f0, breath, glot = decompose_vocal("ai.wav")
print(f"F0: {f0}")
print(f"breath: {breath}")
print(f"glottal: {glot}")

# ========== 2. Inspect parameters ==========
if glot.jitter:
    print(f"Jitter: {glot.jitter['jitter_local']:.3f}%")
if glot.shimmer:
    print(f"Shimmer: {glot.shimmer['shimmer_local']:.3f}%")
print(f"OQ: {glot.oq:.3f}")

# ========== 3. Pitch shift ==========
f0_new, glot_new = pitch_shift(f0, glot, "C4", "E4")  # up 4 semitones

# ========== 4. Synthesis ==========
output = synthesize_vocal(f0_new, breath, glot_new, match_rms_to=0.15)
output.save("_output/ai_to_E4.wav", bit_depth=24)

# ========== 5. Process directly from a numpy array ==========
audio_np = output.data  # get the numpy array
f0_2, breath_2, glot_2 = decompose_vocal(audio_np, sample_rate=44100)
output_2 = synthesize_vocal(f0_2, breath_2, glot_2)
output_2.save("_output/roundtrip.wav")

# ========== 6. Serialization/deserialization ==========
f0.save("_output/f0.npy")
glot.save("_output/glot.npy")
f0_loaded = F0.load("_output/f0.npy")
glot_loaded = GlottalExcitation.load("_output/glot.npy")
```

---

## 7. Regression tests

`tools/test_vocal_lib.py` runs an end-to-end self-check of the library, 7 items in total:

| Case | What is checked |
|---|---|
| Package import and public API | Every symbol in `vocal_lib.__all__` is accessible |
| Data classes | Construction, properties, parameter read/write, and deep copy of F0 / WAVAudio / GlottalExcitation |
| WAV read/write round trip | Save as 16bit and read back, peak error < 1e-3 |
| decompose_vocal | Real audio decomposes into F0 / breath / glottal excitation, with sp and ap both present |
| pitch_shift | After shifting, the frequency ratio equals `2^(semitones/12)` exactly (+2 semitones is used to avoid the soft-limiting region) |
| synthesize_vocal | Output is non-empty, free of NaN, and has normal energy |
| End to end | `VocalB1Synthesizer.synthesize` writes to disk successfully and intermediate artifacts are cleaned up |

```bash
C:\Python312\python.exe tools\test_vocal_lib.py            # all cases
C:\Python312\python.exe tools\test_vocal_lib.py --no-e2e   # skip end-to-end (faster)
```

Test artifacts are written to `_test_output/` for listening and troubleshooting; if any case fails, the exit code is non-zero.
