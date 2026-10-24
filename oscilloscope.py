import numpy as np
import matplotlib.pyplot as plt
from scipy.io import wavfile
import tkinter as tk
from tkinter import filedialog

def select_file():
    """Open a file dialog to select a wav file"""
    root = tk.Tk()
    root.withdraw()
    filepath = filedialog.askopenfilename(
        title="Select a WAV audio file",
        filetypes=[("WAV files", "*.wav"), ("All files", "*.*")]
    )
    return filepath

def fft_plot_wav(wav_path):
    fs, sig = wavfile.read(wav_path)
    # Convert stereo to mono
    if sig.ndim == 2:
        sig = sig[:, 0]

    N = len(sig)
    # FFT computation
    fft_out = np.fft.fft(sig)
    freqs = np.fft.fftfreq(N, d=1.0/fs)

    # Keep only positive frequencies
    positive_mask = freqs >= 0
    f = freqs[positive_mask]
    amp = np.abs(fft_out[positive_mask])

    # Convert to dB, adding a tiny value to avoid log10(0)
    amp_db = 20 * np.log10(amp / (np.max(amp)+1e-12))

    plt.figure(figsize=(13,7))
    # Subplot 1: time-domain waveform
    plt.subplot(2,1,1)
    t_axis = np.arange(N)/fs
    plt.plot(t_axis, sig)
    plt.title("Time Domain Waveform")
    plt.xlabel("Time [s]")
    plt.grid(True)

    # Subplot 2: FFT spectrum
    plt.subplot(2,1,2)
    plt.plot(f, amp_db)
    plt.title("FFT Spectrum (dB)")
    plt.xlabel("Frequency [Hz]")
    plt.ylabel("Amplitude [dB]")
    plt.xlim(0, fs//2)
    plt.ylim(-100, 0)
    plt.grid(True)

    plt.tight_layout()
    plt.show()

if __name__ == "__main__":
    pth = select_file()
    if pth:
        fft_plot_wav(pth)
    else:
        print("No file selected")