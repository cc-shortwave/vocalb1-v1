import os
import numpy as np
import matplotlib.pyplot as plt
import matplotlib as mpl
from scipy.io import wavfile
from scipy.signal import stft, decimate
from matplotlib.widgets import RectangleSelector, Button

# Configure a font that supports Chinese to avoid Glyph missing warnings
mpl.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "SimSun"]
mpl.rcParams["axes.unicode_minus"] = False


def _render_one_window(wav_path: str, nperseg, noverlap, downsample: int, zoom_factor: int = 4):
    """Create an independent window for a single wav file and set up interactive components."""
    sample_rate, raw_data = wavfile.read(wav_path)

    # Use the left channel for stereo input
    if len(raw_data.shape) == 2:
        data = raw_data[:, 0].astype(np.float64)
    else:
        data = raw_data.astype(np.float64)

    # Downsample to speed up
    if downsample > 1:
        data = decimate(data, downsample)
        sample_rate = sample_rate // downsample

    sr = sample_rate
    n_total = len(data)

    def _stft_db(seg, nperseg_, noverlap_):
        """Compute STFT on the given signal segment and convert it to dB magnitude."""
        f_, t_, Zxx_ = stft(seg, fs=sr, nperseg=nperseg_, noverlap=noverlap_)
        mag_db_ = 20 * np.log10(np.clip(np.abs(Zxx_), 1e-12, None))
        return f_, t_, mag_db_

    # The initial (low-resolution) STFT is computed only once
    f_stft, t_stft, mag_db = _stft_db(data, nperseg, noverlap)

    fig, ax_spec = plt.subplots(figsize=(14, 8), num=wav_path)
    plt.subplots_adjust(bottom=0.12)

    im = ax_spec.imshow(
        mag_db,
        aspect="auto",
        origin="lower",
        extent=[t_stft[0], t_stft[-1], f_stft[0], f_stft[-1]],
        cmap="inferno"
    )
    ax_spec.set_title(f"STFT spectrogram | {wav_path} | drag to zoom (high-resolution STFT is recomputed automatically), Reset button restores")
    ax_spec.set_xlabel("Time (s)")
    ax_spec.set_ylabel("Frequency (Hz)")

    cbar = fig.colorbar(im, ax=ax_spec)
    cbar.set_label("Magnitude (dB)")

    # Save the original view ranges for restoring
    orig_xlim = ax_spec.get_xlim()
    orig_ylim = ax_spec.get_ylim()

    def _apply(f_, t_, mag_db_, xlim=None, ylim=None):
        """Apply new STFT results to the same imshow."""
        im.set_data(mag_db_)
        im.set_extent([t_[0], t_[-1], f_[0], f_[-1]])
        if xlim is not None:
            ax_spec.set_xlim(*xlim)
        if ylim is not None:
            ax_spec.set_ylim(*ylim)
        fig.canvas.draw_idle()

    # Rectangle-selection callback (closure bound to this window's fig / ax_spec)
    def on_select(eclick, erelease):
        x1, y1 = eclick.xdata, eclick.ydata
        x2, y2 = erelease.xdata, erelease.ydata
        if None in (x1, y1, x2, y2):
            return
        x_min, x_max = min(x1, x2), max(x1, x2)
        y_min, y_max = min(y1, y2), max(y1, y2)

        # Sample indices corresponding to the selected time range
        s0 = max(0, int(np.floor(x_min * sr)))
        s1 = min(n_total, int(np.ceil(x_max * sr)))
        if s1 - s0 < max(64, nperseg):
            return

        # Recompute only the selected range with a higher-resolution STFT:
        # The window length is scaled up by zoom_factor (higher frequency resolution), while the hop size shrinks (higher time resolution).
        # Note: the window length is re-derived from the selected range; the original STFT window parameters are neither locked nor reused.
        new_nperseg = int(min(s1 - s0, nperseg * zoom_factor))
        new_hop = max(16, new_nperseg // 16, (s1 - s0) // 2000)
        new_hop = min(new_hop, new_nperseg - 1)
        new_noverlap = new_nperseg - new_hop  # always smaller than new_nperseg

        f_zoom, t_zoom, mag_db_zoom = _stft_db(data[s0:s1], new_nperseg, new_noverlap)

        # The local STFT time axis starts at 0, so the segment start offset must be added
        t_off = s0 / sr
        _apply(
            f_zoom, t_zoom + t_off, mag_db_zoom,
            xlim=(t_zoom[0] + t_off, t_zoom[-1] + t_off),
            ylim=(y_min, y_max)
        )
        # After recomputation the rectangle selector stays active, so further zooming by selection is possible (the window is not locked)
        _refresh_selector_bg(erelease)

    # Reset button restores the view and the original-resolution data
    def reset_view(event):
        _apply(f_stft, t_stft, mag_db, xlim=orig_xlim, ylim=orig_ylim)
        _refresh_selector_bg(event)

    def _refresh_selector_bg(event):
        """Refresh the selector's blit background after image data updates to avoid stale image remnants."""
        updater = getattr(rect_selector, "update_background", None)
        if updater is None or event is None:
            return
        try:
            updater(event)
        except Exception:
            pass

    # Rectangle selector, drag with the left mouse button to select
    rect_selector = RectangleSelector(
        ax_spec,
        on_select,
        useblit=True,  # enabling blit greatly improves interaction speed
        button=[1],
        interactive=False
    )

    # Add a reset button
    ax_btn_reset = plt.axes([0.45, 0.03, 0.12, 0.05])
    btn_reset = Button(ax_btn_reset, "Reset")
    btn_reset.on_clicked(reset_view)

    # Prevent local variables from being garbage-collected and breaking callbacks
    fig._rect_selector = rect_selector
    fig._btn_reset = btn_reset


def analyze_wav_stft_zoom(wav_paths, nperseg=1024, noverlap=256, downsample: int = 1,
                          zoom_factor: int = 4):
    """Display the spectrograms of multiple files at once, each in its own independent window.

    wav_paths: a single path string or a list of paths.
    zoom_factor: when recomputing the STFT after a selection, the factor by which the window length is scaled relative to nperseg (resolution improvement factor).
    """
    if isinstance(wav_paths, (str, bytes)):
        wav_paths = [wav_paths]
    if not wav_paths:
        raise ValueError("wav_paths must not be empty")

    for p in wav_paths:
        _render_one_window(p, nperseg, noverlap, downsample, zoom_factor)

    plt.show()


if __name__ == "__main__":
    # Anchor the output directory to the script directory to avoid missing files when the working directory differs at runtime
    _BASE_DIR = os.path.dirname(os.path.abspath(__file__))
    WAV_FILES = [
        os.path.join(_BASE_DIR, "_output", "e2e_song.wav"),
    ]
    analyze_wav_stft_zoom(WAV_FILES, nperseg=1024, noverlap=256, downsample=1)