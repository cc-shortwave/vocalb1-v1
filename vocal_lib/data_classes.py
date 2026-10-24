"""
vocal_lib.data_classes - core data classes

Pure data containers that only wrap numpy arrays + metadata + basic copy/save/load.
They contain no analysis or synthesis algorithm logic.
"""

import os
import numpy as np
import soundfile as sf
from typing import Optional, Dict, Any, Union


class F0:
    """
    F0 fundamental-frequency trajectory class.

    Attributes:
        values (np.ndarray): F0 sequence in Hz; silent frames are 0
        time_axis (np.ndarray): matching time axis in seconds
        frame_period (float): frame period in milliseconds
        sample_rate (int): sample rate
        voiced_flags (np.ndarray): voicing flags (True = voiced)
    """

    def __init__(self,
                 values: np.ndarray,
                 time_axis: Optional[np.ndarray] = None,
                 frame_period: float = 5.0,
                 sample_rate: int = 44100,
                 voiced_flags: Optional[np.ndarray] = None):
        self.values = np.asarray(values, dtype=np.float64)
        self.frame_period = frame_period
        self.sample_rate = sample_rate

        if time_axis is None:
            self.time_axis = np.arange(len(self.values)) * (frame_period / 1000.0)
        else:
            self.time_axis = np.asarray(time_axis, dtype=np.float64)

        if voiced_flags is None:
            self.voiced_flags = self.values > 0
        else:
            self.voiced_flags = np.asarray(voiced_flags, dtype=bool)

    @property
    def n_frames(self) -> int:
        return len(self.values)

    def copy(self) -> 'F0':
        return F0(
            values=self.values.copy(),
            time_axis=self.time_axis.copy(),
            frame_period=self.frame_period,
            sample_rate=self.sample_rate,
            voiced_flags=self.voiced_flags.copy(),
        )

    def save(self, file_path: str) -> None:
        data = {
            'values': self.values,
            'time_axis': self.time_axis,
            'frame_period': self.frame_period,
            'sample_rate': self.sample_rate,
            'voiced_flags': self.voiced_flags,
        }
        np.save(file_path, data)

    @classmethod
    def load(cls, file_path: str) -> 'F0':
        data = np.load(file_path, allow_pickle=True).item()
        return cls(
            values=data['values'],
            time_axis=data.get('time_axis'),
            frame_period=float(data.get('frame_period', 5.0)),
            sample_rate=int(data.get('sample_rate', 44100)),
            voiced_flags=data.get('voiced_flags'),
        )

    def __len__(self) -> int:
        return self.n_frames

    def __repr__(self) -> str:
        voiced = self.values[self.voiced_flags]
        avg = float(np.mean(voiced)) if len(voiced) > 0 else 0.0
        return f"F0(n_frames={self.n_frames}, avg={avg:.1f}Hz)"


class WAVAudio:
    """
    WAV audio class.

    Attributes:
        data (np.ndarray): audio sample data
        sample_rate (int): sample rate in Hz
        source_path (Optional[str]): original file path
    """

    def __init__(self,
                 data: np.ndarray,
                 sample_rate: int = 44100,
                 source_path: Optional[str] = None):
        self.data = np.asarray(data, dtype=np.float64)
        self.sample_rate = sample_rate
        self.source_path = source_path

    @property
    def duration(self) -> float:
        return len(self.data) / self.sample_rate

    @property
    def n_samples(self) -> int:
        return len(self.data)

    @property
    def n_channels(self) -> int:
        return self.data.shape[1] if self.data.ndim > 1 else 1

    @property
    def is_mono(self) -> bool:
        return self.data.ndim == 1

    @property
    def rms(self) -> float:
        if len(self.data) == 0:
            return 0.0
        return float(np.sqrt(np.mean(self.data ** 2)))

    def to_mono(self) -> 'WAVAudio':
        if self.is_mono:
            return self.copy()
        mono = np.mean(self.data, axis=1)
        return WAVAudio(mono, self.sample_rate, self.source_path)

    @classmethod
    def from_file(cls, file_path: str, mono: bool = True) -> 'WAVAudio':
        data, sr = sf.read(file_path)
        if mono and data.ndim > 1:
            data = np.mean(data, axis=1)
        return cls(data=data, sample_rate=sr, source_path=file_path)

    def save(self, file_path: str, bit_depth: int = 24,
             target_sample_rate: Optional[int] = None) -> None:
        sr = target_sample_rate if target_sample_rate else self.sample_rate
        data_out = self.data.copy()

        if target_sample_rate and target_sample_rate != self.sample_rate:
            ratio = target_sample_rate / self.sample_rate
            new_len = int(len(data_out) * ratio)
            idx = np.linspace(0, len(data_out) - 1, new_len)
            idx_floor = np.floor(idx).astype(int)
            idx_ceil = np.minimum(idx_floor + 1, len(data_out) - 1)
            frac = idx - idx_floor
            if data_out.ndim == 1:
                data_out = data_out[idx_floor] * (1 - frac) + data_out[idx_ceil] * frac
            else:
                frac = frac[:, np.newaxis]
                data_out = data_out[idx_floor] * (1 - frac) + data_out[idx_ceil] * frac

        max_val = np.max(np.abs(data_out))
        if max_val > 1.0:
            data_out = data_out / max_val

        subtype_map = {8: 'PCM_U8', 16: 'PCM_16', 24: 'PCM_24'}
        subtype = subtype_map.get(bit_depth, None)
        if subtype:
            sf.write(file_path, data_out, sr, subtype=subtype)
        else:
            sf.write(file_path, data_out.astype(np.float32), sr)

    def copy(self) -> 'WAVAudio':
        return WAVAudio(self.data.copy(), self.sample_rate, self.source_path)

    def __len__(self) -> int:
        return self.n_samples

    def __repr__(self) -> str:
        return f"WAVAudio(n_samples={self.n_samples}, sr={self.sample_rate}, dur={self.duration:.2f}s, ch={self.n_channels})"


class GlottalExcitation:
    """
    Glottal excitation class.

    Attributes:
        signal (np.ndarray): glottal excitation time-domain signal
        sample_rate (int): sample rate
        sp (Optional[np.ndarray]): pyworld spectral envelope
        ap (Optional[np.ndarray]): pyworld aperiodicity parameters
        oq (float): open quotient
        glottal_intensity (Optional[np.ndarray]): per-frame glottal intensity
        parameters (Dict): extra parameters (jitter/shimmer/formants, etc.)
    """

    def __init__(self,
                 signal: np.ndarray,
                 sample_rate: int = 44100,
                 sp: Optional[np.ndarray] = None,
                 ap: Optional[np.ndarray] = None,
                 oq: float = 0.6,
                 glottal_intensity: Optional[np.ndarray] = None,
                 parameters: Optional[Dict[str, Any]] = None):
        self.signal = np.asarray(signal, dtype=np.float64)
        self.sample_rate = sample_rate
        self.sp = np.asarray(sp, dtype=np.float64) if sp is not None else None
        self.ap = np.asarray(ap, dtype=np.float64) if ap is not None else None
        self.oq = float(oq)
        self.glottal_intensity = (np.asarray(glottal_intensity, dtype=np.float64)
                                  if glottal_intensity is not None else None)
        self.parameters = dict(parameters) if parameters is not None else {}

    @property
    def duration(self) -> float:
        return len(self.signal) / self.sample_rate

    @property
    def n_samples(self) -> int:
        return len(self.signal)

    @property
    def has_pyworld_params(self) -> bool:
        return self.sp is not None and self.ap is not None

    @property
    def jitter(self) -> Optional[Dict[str, float]]:
        return self.parameters.get('jitter')

    @property
    def shimmer(self) -> Optional[Dict[str, float]]:
        return self.parameters.get('shimmer')

    def get_param(self, key: str, default: Any = None) -> Any:
        return self.parameters.get(key, default)

    def set_param(self, key: str, value: Any) -> None:
        self.parameters[key] = value

    def save(self, file_path: str) -> None:
        data = {
            'signal': self.signal,
            'sample_rate': self.sample_rate,
            'sp': self.sp,
            'ap': self.ap,
            'oq': self.oq,
            'glottal_intensity': self.glottal_intensity,
            'parameters': self.parameters,
        }
        np.save(file_path, data)

    @classmethod
    def load(cls, file_path: str) -> 'GlottalExcitation':
        data = np.load(file_path, allow_pickle=True).item()
        return cls(
            signal=data['signal'],
            sample_rate=int(data.get('sample_rate', 44100)),
            sp=data.get('sp'),
            ap=data.get('ap'),
            oq=float(data.get('oq', 0.6)),
            glottal_intensity=data.get('glottal_intensity'),
            parameters=data.get('parameters', {}),
        )

    def copy(self) -> 'GlottalExcitation':
        return GlottalExcitation(
            signal=self.signal.copy(),
            sample_rate=self.sample_rate,
            sp=self.sp.copy() if self.sp is not None else None,
            ap=self.ap.copy() if self.ap is not None else None,
            oq=self.oq,
            glottal_intensity=(self.glottal_intensity.copy()
                               if self.glottal_intensity is not None else None),
            parameters={k: (v.copy() if hasattr(v, 'copy') else v)
                        for k, v in self.parameters.items()},
        )

    def __len__(self) -> int:
        return self.n_samples

    def __repr__(self) -> str:
        pyw = 'yes' if self.has_pyworld_params else 'no'
        return f"GlottalExcitation(n={self.n_samples}, sr={self.sample_rate}, oq={self.oq:.3f}, pyworld={pyw})"
