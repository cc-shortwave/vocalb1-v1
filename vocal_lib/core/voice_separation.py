import numpy as np
from numpy.fft import rfft, irfft

from .pyworld_processor import FRAME_PERIOD

FRAME_SIZE = 2048
HOP_SIZE = 512

def hanning_window(frame_size):
    return 0.5 * (1 - np.cos(2 * np.pi * np.arange(frame_size) / (frame_size - 1)))

def frame_audio(audio_data, frame_size=FRAME_SIZE, hop_size=HOP_SIZE):
    num_frames = int(np.ceil((len(audio_data) - frame_size) / hop_size)) + 1
    frames = []
    for i in range(num_frames):
        start = i * hop_size
        end = start + frame_size
        if end > len(audio_data):
            frame = np.zeros(frame_size)
            frame[:len(audio_data) - start] = audio_data[start:]
        else:
            frame = audio_data[start:end].copy()
        frames.append(frame)
    return np.array(frames), num_frames

def compute_stft(frames, sample_rate):
    window = hanning_window(len(frames[0])) if len(frames) > 0 else hanning_window(FRAME_SIZE)
    num_bins = len(frames[0])
    frequencies = np.fft.rfftfreq(num_bins, 1.0 / sample_rate)
    magnitude_spectra = []
    phase_spectra = []
    
    for frame in frames:
        windowed_frame = frame * window
        spectrum = rfft(windowed_frame)
        magnitude = np.abs(spectrum)
        phase = np.angle(spectrum)
        magnitude_spectra.append(magnitude)
        phase_spectra.append(phase)
    
    return np.array(magnitude_spectra), np.array(phase_spectra), frequencies

def calculate_short_time_energy(frame):
    return np.sum(frame ** 2) / len(frame)

def calculate_zero_crossing_rate(frame):
    zero_crossings = np.sum(np.abs(np.diff(np.sign(frame)))) / 2
    return zero_crossings / len(frame)

def calculate_spectral_flatness(magnitude_spectrum):
    if np.sum(magnitude_spectrum) == 0:
        return 1.0
    geometric_mean = np.exp(np.mean(np.log(magnitude_spectrum + 1e-10)))
    arithmetic_mean = np.mean(magnitude_spectrum)
    if arithmetic_mean == 0:
        return 0.0
    return geometric_mean / arithmetic_mean

def detect_voiced_unvoiced(frame, magnitude_spectrum, f0, sample_rate):
    energy = calculate_short_time_energy(frame)
    zcr = calculate_zero_crossing_rate(frame)
    spectral_flatness = calculate_spectral_flatness(magnitude_spectrum)
    
    global_energy_threshold = 0.001
    zcr_threshold = 0.15
    flatness_threshold = 0.3
    
    voiced_score = 0.0
    unvoiced_score = 0.0
    
    if f0 > 0:
        voiced_score += 0.4
    
    if energy > global_energy_threshold:
        voiced_score += 0.2
    else:
        unvoiced_score += 0.3
    
    if zcr < zcr_threshold:
        voiced_score += 0.2
    else:
        unvoiced_score += 0.2
    
    if spectral_flatness < flatness_threshold:
        voiced_score += 0.2
    else:
        unvoiced_score += 0.3
    
    if voiced_score > unvoiced_score:
        return True, voiced_score
    else:
        return False, unvoiced_score

def locate_harmonic_bands(f0, frequencies, tolerance_ratio=0.03):
    if f0 <= 0:
        return []
    
    harmonic_bands = []
    harmonic_num = 1
    
    while True:
        harmonic_freq = f0 * harmonic_num
        if harmonic_freq > frequencies[-1]:
            break
        
        tolerance = max(15, f0 * tolerance_ratio)
        
        idx = np.argmin(np.abs(frequencies - harmonic_freq))
        freq = frequencies[idx]
        
        lower_bound = max(0, freq - tolerance)
        upper_bound = freq + tolerance
        
        lower_idx = np.argmin(np.abs(frequencies - lower_bound))
        upper_idx = np.argmin(np.abs(frequencies - upper_bound))
        
        harmonic_bands.append((lower_idx, upper_idx, harmonic_num))
        harmonic_num += 1
    
    return harmonic_bands

def extract_formants(magnitude_spectrum, frequencies):
    formants = []
    spectrum_smooth = np.convolve(magnitude_spectrum, np.ones(15)/15, mode='same')
    
    peaks = np.where((spectrum_smooth[1:-1] > spectrum_smooth[:-2]) & 
                     (spectrum_smooth[1:-1] > spectrum_smooth[2:]))[0] + 1
    
    for peak_idx in peaks:
        freq = frequencies[peak_idx]
        if 200 <= freq <= 4000:
            formants.append({'frequency': freq, 'magnitude': magnitude_spectrum[peak_idx], 'index': peak_idx})
    
    formants.sort(key=lambda x: x['frequency'])
    return formants[:5]

def separate_three_components(magnitude_spectrum, phase_spectrum, frequencies, f0, is_voiced):
    formants = extract_formants(magnitude_spectrum, frequencies)
    
    if is_voiced and f0 > 0:
        harmonic_bands = locate_harmonic_bands(f0, frequencies)
        
        harmonic_mask = np.zeros_like(magnitude_spectrum)
        for lower_idx, upper_idx, _ in harmonic_bands:
            harmonic_mask[lower_idx:upper_idx + 1] = 1.0
        
        # Mask dilation: the peak must stay at exactly 1. The original implementation
        # smoothed with np.ones(3)/3, which is a mean filter; since harmonic bands
        # cover only 1~2 bins, the mask peak was reduced to 1/3~2/3 after convolution,
        # so (1 - harmonic_mask) still leaked 0.33~0.67 at harmonic bins and harmonics
        # still mixed into the breath component (measured breath harmonic energy share
        # was 40%+, ACF 0.978). Switching to a non-normalized convolution kernel
        # (equivalent to dilation) keeps the peak at 1, so harmonic bins are truly excluded.
        harmonic_mask = np.clip(
            np.convolve(harmonic_mask, np.ones(5), mode='same'), 0.0, 1.0)
        
        formant_mask = np.zeros_like(magnitude_spectrum)
        for formant in formants:
            formant_freq = formant['frequency']
            bandwidth = 80
            lower_bound = max(0, formant_freq - bandwidth)
            upper_bound = formant_freq + bandwidth
            lower_idx = np.argmin(np.abs(frequencies - lower_bound))
            upper_idx = np.argmin(np.abs(frequencies - upper_bound))
            for i in range(lower_idx, upper_idx + 1):
                dist = abs(frequencies[i] - formant_freq)
                formant_mask[i] = max(formant_mask[i], 1.0 - dist / bandwidth)
        
        spectral_flatness = calculate_spectral_flatness(magnitude_spectrum)
        breath_ratio = min(0.3, spectral_flatness * 0.5)
        
        harmonic_only_mask = harmonic_mask * (1 - breath_ratio)
        formant_enhanced_mask = formant_mask * (1 + breath_ratio)
        
        glottal_magnitude = magnitude_spectrum * harmonic_only_mask * 0.6
        formant_magnitude = magnitude_spectrum * formant_enhanced_mask * 0.3
        
        # Breath = pure non-harmonic residual.
        # Note: harmonic bins must NOT be injected into the breath component here.
        # The original implementation additionally added
        #   magnitude_spectrum * harmonic_mask * breath_ratio * 0.1
        # and reused the original phase (see breath_complex below, which uses phase_spectrum),
        # which is coherent injection. The harmonic bin energy of periodic vowels is far
        # larger than that of non-harmonic bins, so this injected term dominated the
        # breath component (measured breath_seg harmonic energy share was 30~42%, ACF 0.978),
        # turning the breath into a quasi-periodic signal; when concatenating/shifting
        # segments its F0 differs from the main signal's f0, causing a fundamental-frequency
        # split in the spectrogram (heard as "another person's voice").
        breath_magnitude = magnitude_spectrum * (1 - harmonic_mask) * breath_ratio
        
        total = glottal_magnitude + formant_magnitude + breath_magnitude
        nonzero = total > 0
        glottal_magnitude[nonzero] = magnitude_spectrum[nonzero] * glottal_magnitude[nonzero] / (total[nonzero] + 1e-10)
        formant_magnitude[nonzero] = magnitude_spectrum[nonzero] * formant_magnitude[nonzero] / (total[nonzero] + 1e-10)
        breath_magnitude[nonzero] = magnitude_spectrum[nonzero] * breath_magnitude[nonzero] / (total[nonzero] + 1e-10)
    else:
        glottal_magnitude = np.zeros_like(magnitude_spectrum)
        formant_magnitude = np.zeros_like(magnitude_spectrum)
        breath_magnitude = magnitude_spectrum
    
    glottal_complex = glottal_magnitude * np.exp(1j * phase_spectrum)
    formant_complex = formant_magnitude * np.exp(1j * phase_spectrum)
    breath_complex = breath_magnitude * np.exp(1j * phase_spectrum)
    
    return glottal_complex, formant_complex, breath_complex, formants

def synthesize_frame(complex_spectrum, frame_size=FRAME_SIZE):
    return np.real(irfft(complex_spectrum))

def overlap_add(frames, hop_size=HOP_SIZE):
    frame_size = len(frames[0])
    output_length = (len(frames) - 1) * hop_size + frame_size
    output = np.zeros(output_length)
    
    for i, frame in enumerate(frames):
        start = i * hop_size
        output[start:start + frame_size] += frame
    
    return output

def decompose_voice_frame(frame, magnitude_spectrum, phase_spectrum, frequencies, f0, sample_rate):
    is_voiced, confidence = detect_voiced_unvoiced(frame, magnitude_spectrum, f0, sample_rate)
    
    glottal_complex, formant_complex, breath_complex, formants = separate_three_components(
        magnitude_spectrum, phase_spectrum, frequencies, f0, is_voiced
    )
    
    glottal_frame = synthesize_frame(glottal_complex)
    formant_frame = synthesize_frame(formant_complex)
    breath_frame = synthesize_frame(breath_complex)
    
    original_energy = np.sum(frame ** 2)
    combined_energy = np.sum(glottal_frame ** 2) + np.sum(formant_frame ** 2) + np.sum(breath_frame ** 2)
    
    if combined_energy > 0 and original_energy > 0:
        scale_factor = np.sqrt(original_energy / combined_energy)
        glottal_frame *= scale_factor
        formant_frame *= scale_factor
        breath_frame *= scale_factor
    
    glottal_energy = np.sum(glottal_frame ** 2)
    formant_energy = np.sum(formant_frame ** 2)
    breath_energy = np.sum(breath_frame ** 2)
    total_energy = glottal_energy + formant_energy + breath_energy
    
    if total_energy > 0:
        glottal_ratio = glottal_energy / total_energy
        formant_ratio = formant_energy / total_energy
        breath_ratio = breath_energy / total_energy
    else:
        glottal_ratio = 0.0
        formant_ratio = 0.0
        breath_ratio = 0.0
    
    return {
        'glottal': glottal_frame,
        'formant': formant_frame,
        'breath': breath_frame,
        'is_voiced': is_voiced,
        'confidence': confidence,
        'formants': formants,
        'glottal_ratio': glottal_ratio,
        'formant_ratio': formant_ratio,
        'breath_ratio': breath_ratio
    }

def decompose_voice(audio_data, sample_rate, f0_sequence):
    frames, num_frames = frame_audio(audio_data)
    magnitude_spectra, phase_spectra, frequencies = compute_stft(frames, sample_rate)

    # f0_sequence is provided by pyworld (FRAME_PERIOD=5ms/frame -> sample_rate*0.005
    # samples/frame), whose frame rate differs from that of the STFT frames
    # (HOP_SIZE=512 samples/frame), so indices must be remapped by [frame center].
    # The original implementation used f0_sequence[i] directly: a 588-frame f0 sequence
    # was indexed by 250 STFT frames, misaligning the time axis by about 2.35x, so most
    # frames picked f0=0 and fell into the unvoiced branch, and the breath degraded into
    # "the whole original audio" (measured 0.5~0.68s breath harmonic share 0.92, RMS ~= source,
    # ACF 0.978) -> when concatenating/shifting segments this conflicted with the main
    # signal's f0, splitting the fundamental frequency in the spectrogram.
    n_f0 = len(f0_sequence)
    f0_step = sample_rate * FRAME_PERIOD / 1000.0
    stft_centers = np.arange(num_frames) * HOP_SIZE + FRAME_SIZE / 2.0
    f0_idx = np.clip(np.round(stft_centers / f0_step).astype(int), 0, max(n_f0 - 1, 0))
    
    glottal_frames = []
    formant_frames = []
    breath_frames = []
    formants_list = []
    glottal_intensity = []
    formant_intensity = []
    breath_intensity = []
    voiced_flags = []
    
    for i in range(num_frames):
        f0 = f0_sequence[f0_idx[i]] if n_f0 > 0 else 0
        magnitude = magnitude_spectra[i]
        phase = phase_spectra[i]
        frame = frames[i]
        
        result = decompose_voice_frame(frame, magnitude, phase, frequencies, f0, sample_rate)
        
        glottal_frames.append(result['glottal'])
        formant_frames.append(result['formant'])
        breath_frames.append(result['breath'])
        formants_list.append(result['formants'])
        glottal_intensity.append(result['glottal_ratio'])
        formant_intensity.append(result['formant_ratio'])
        breath_intensity.append(result['breath_ratio'])
        voiced_flags.append(result['is_voiced'])
    
    glottal_audio = overlap_add(glottal_frames)
    formant_audio = overlap_add(formant_frames)
    breath_audio = overlap_add(breath_frames)
    
    glottal_audio = glottal_audio[:len(audio_data)]
    formant_audio = formant_audio[:len(audio_data)]
    breath_audio = breath_audio[:len(audio_data)]
    
    original_energy = np.sum(audio_data ** 2)
    combined_energy = np.sum(glottal_audio ** 2) + np.sum(formant_audio ** 2) + np.sum(breath_audio ** 2)
    
    if combined_energy > 0 and original_energy > 0:
        scale_factor = np.sqrt(original_energy / combined_energy)
        glottal_audio *= scale_factor
        formant_audio *= scale_factor
        breath_audio *= scale_factor
    
    return {
        'glottal': glottal_audio,
        'formant': formant_audio,
        'breath': breath_audio,
        'formants': formants_list,
        'glottal_intensity': np.array(glottal_intensity),
        'formant_intensity': np.array(formant_intensity),
        'breath_intensity': np.array(breath_intensity),
        'voiced_flags': np.array(voiced_flags),
        'frequencies': frequencies
    }
