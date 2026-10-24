"""
VocalDecomposer - vocal decomposer

Decompose speech into F0 / GE / GI / OQ / CQ / QQ / jitter / shimmer,
all saved as pure numeric files (26 .npy + 1 .json),
without pickle deserialization to avoid security risks.
"""
import sys

import os
import json
import numpy as np
from typing import Union, Optional

import vocal_lib as vl


class VocalDecomposer:
    """
    Vocal decomposer.

    Input WAV → call vocal_lib to decompose → extract all parameters as np.ndarray
    → save as 27 independent pure numeric files (26 .npy + 1 .json).

    Saved parameters (27 files):
        F0:        f0_values.npy, f0_time_axis.npy, f0_voiced_flags.npy, f0_frame_period.npy
        GE:        ge_signal.npy, ge_sp.npy, ge_ap.npy, ge_oq.npy, ge_glottal_intensity.npy
                   ge_parameters.json  ← nested dict serialized as JSON (plain text)
        GI:        gi_values.npy
        OQ:        oq.npy
        CQ:        cq.npy
        QQ:        qq.npy
        jitter:    jitter_local.npy, jitter_rap.npy, jitter_ppq5.npy, jitter_ddp.npy
        shimmer:   shimmer_local.npy, shimmer_apq3.npy, shimmer_apq5.npy, shimmer_dda.npy
        breath:    breath_data.npy
        meta:      sample_rate.npy, frame_period.npy, n_frames.npy, n_samples.npy
    """

    def __init__(self, sample_rate: int = 44100, frame_period: float = 5.0):
        self.sample_rate = sample_rate
        self.frame_period = frame_period

    def decompose(self,
                  input_audio: Union[str, vl.WAVAudio, np.ndarray],
                  ) -> dict:
        """
        Decompose the input audio and return a parameter dict (all values are Python native types or np.ndarray).

        Args:
            input_audio: str (file path) / WAVAudio / np.ndarray

        Returns:
            params: dict containing all decomposition parameters
        """
        f0, breath, glottal = vl.decompose_vocal(
            input_audio,
            sample_rate=self.sample_rate,
            frame_period=self.frame_period,
        )

        sr = glottal.sample_rate
        self.sample_rate = sr

        # ── F0 ──
        f0_values = np.asarray(f0.values, dtype=np.float64)
        f0_time_axis = np.asarray(f0.time_axis, dtype=np.float64)
        f0_voiced_flags = np.asarray(f0.voiced_flags, dtype=bool)
        f0_frame_period = float(f0.frame_period)

        # ── GE (GlottalExcitation) ──
        ge_signal = np.asarray(glottal.signal, dtype=np.float64)
        ge_sp = np.asarray(glottal.sp, dtype=np.float64) if glottal.sp is not None else None
        ge_ap = np.asarray(glottal.ap, dtype=np.float64) if glottal.ap is not None else None
        ge_oq = float(glottal.oq)
        ge_glottal_intensity = (
            np.asarray(glottal.glottal_intensity, dtype=np.float64)
            if glottal.glottal_intensity is not None else None
        )
        ge_parameters = dict(glottal.parameters) if glottal.parameters else {}

        # ── GI (glottal_intensity per frame) ──
        gi_values = ge_glottal_intensity.copy() if ge_glottal_intensity is not None else None

        # ── OQ (open quotient) ──
        oq = ge_oq

        # ── CQ (closed quotient = 1 - OQ) ──
        cq = 1.0 - oq

        # ── QQ (quality quotient = glottal energy ratio) ──
        # If no intensity is available, approximate with (1 - ap_mean)
        if gi_values is not None and len(gi_values) > 0:
            qq = float(np.mean(gi_values))
        elif ge_ap is not None:
            ap_mean = float(np.mean(ge_ap))
            qq = 1.0 - np.clip(ap_mean, 0.0, 1.0)
        else:
            qq = 0.5

        # ── jitter / shimmer ──
        ji = ge_parameters.get('jitter', {})
        jitter_local = float(ji.get('jitter_local', 0.0))
        jitter_rap = float(ji.get('jitter_rap', 0.0))
        jitter_ppq5 = float(ji.get('jitter_ppq5', 0.0))
        jitter_ddp = float(ji.get('jitter_ddp', 0.0))

        sh = ge_parameters.get('shimmer', {})
        shimmer_local = float(sh.get('shimmer_local', 0.0))
        shimmer_apq3 = float(sh.get('shimmer_apq3', 0.0))
        shimmer_apq5 = float(sh.get('shimmer_apq5', 0.0))
        shimmer_dda = float(sh.get('shimmer_dda', 0.0))

        # ── breath (breath waveform) ──
        breath_data = np.asarray(breath.data, dtype=np.float64)

        # ── meta ──
        n_frames = len(f0_values)
        n_samples = len(ge_signal)

        params = {
            # F0
            'f0_values': f0_values,
            'f0_time_axis': f0_time_axis,
            'f0_voiced_flags': f0_voiced_flags,
            'f0_frame_period': f0_frame_period,
            # GE
            'ge_signal': ge_signal,
            'ge_sp': ge_sp,
            'ge_ap': ge_ap,
            'ge_oq': ge_oq,
            'ge_glottal_intensity': ge_glottal_intensity,
            'ge_parameters': ge_parameters,
            # GI
            'gi_values': gi_values,
            # OQ / CQ / QQ
            'oq': oq,
            'cq': cq,
            'qq': qq,
            # jitter
            'jitter_local': jitter_local,
            'jitter_rap': jitter_rap,
            'jitter_ppq5': jitter_ppq5,
            'jitter_ddp': jitter_ddp,
            # shimmer
            'shimmer_local': shimmer_local,
            'shimmer_apq3': shimmer_apq3,
            'shimmer_apq5': shimmer_apq5,
            'shimmer_dda': shimmer_dda,
            # breath
            'breath_data': breath_data,
            # meta
            'sample_rate': sr,
            'frame_period': self.frame_period,
            'n_frames': n_frames,
            'n_samples': n_samples,
        }

        # Cache for later use
        self._last_params = params
        self._last_breath = breath

        return params

    # ------------------------------------------------------------------
    # Save: 27 independent pure numeric files (26 .npy + 1 .json)
    # ------------------------------------------------------------------
    # Array parameters → saved as .npy (allow_pickle=False, pure numeric binary)
    # Scalar parameters → saved as .npy (0-d array, allow_pickle=False)
    # ge_parameters (nested dict) → saved as .json (plain text, no code execution risk)
    # ------------------------------------------------------------------
    _ARRAY_KEYS = [
        # F0
        'f0_values', 'f0_time_axis', 'f0_voiced_flags',
        # GE
        'ge_signal', 'ge_sp', 'ge_ap', 'ge_glottal_intensity',
        # GI
        'gi_values',
        # breath
        'breath_data',
    ]

    _SCALAR_KEYS = [
        # F0 scalar
        'f0_frame_period',
        # GE scalar
        'ge_oq',
        # OQ / CQ / QQ
        'oq', 'cq', 'qq',
        # jitter
        'jitter_local', 'jitter_rap', 'jitter_ppq5', 'jitter_ddp',
        # shimmer
        'shimmer_local', 'shimmer_apq3', 'shimmer_apq5', 'shimmer_dda',
        # meta
        'sample_rate', 'frame_period', 'n_frames', 'n_samples',
    ]

    # ge_parameters is handled separately as JSON

    def save(self, params: dict, dir_path: str) -> str:
        """
        Save the parameter dict as 27 independent pure numeric files.

        File layout:
            dir_path/
            ├── f0_values.npy              (n_frames,) float64
            ├── f0_time_axis.npy           (n_frames,) float64
            ├── f0_voiced_flags.npy        (n_frames,) bool
            ├── f0_frame_period.npy        () float64
            ├── ge_signal.npy              (n_samples,) float64
            ├── ge_sp.npy                  (n_frames, n_bins) float64
            ├── ge_ap.npy                  (n_frames, n_bins) float64
            ├── ge_oq.npy                  () float64
            ├── ge_glottal_intensity.npy   (n_frames,) float64 or empty
            ├── ge_parameters.json         nested dict (plain text)
            ├── gi_values.npy             (n_frames,) float64 or empty
            ├── oq.npy                    () float64
            ├── cq.npy                    () float64
            ├── qq.npy                    () float64
            ├── jitter_local.npy           () float64
            ├── jitter_rap.npy             () float64
            ├── jitter_ppq5.npy            () float64
            ├── jitter_ddp.npy             () float64
            ├── shimmer_local.npy          () float64
            ├── shimmer_apq3.npy           () float64
            ├── shimmer_apq5.npy           () float64
            ├── shimmer_dda.npy           () float64
            ├── breath_data.npy            (n_samples,) float64
            ├── sample_rate.npy            () int64
            ├── frame_period.npy           () float64
            ├── n_frames.npy               () int64
            └── n_samples.npy              () int64

        All .npy files use allow_pickle=False, pure numeric binary format.

        Args:
            params:   parameter dict returned by decompose()
            dir_path: save directory path

        Returns:
            dir_path
        """
        os.makedirs(dir_path, exist_ok=True)

        n_saved = 0

        # 1. Save array parameters (8)
        for key in self._ARRAY_KEYS:
            value = params.get(key)
            if value is None or (isinstance(value, np.ndarray) and len(value) == 0):
                # Empty arrays are saved as zero-length arrays
                np.save(os.path.join(dir_path, f"{key}.npy"),
                        np.array([], dtype=np.float64),
                        allow_pickle=False)
            else:
                np.save(os.path.join(dir_path, f"{key}.npy"),
                        np.asarray(value),
                        allow_pickle=False)
            n_saved += 1

        # 2. Save scalar parameters (18)
        for key in self._SCALAR_KEYS:
            value = params.get(key, 0.0)
            if isinstance(value, (int, np.integer)):
                np.save(os.path.join(dir_path, f"{key}.npy"),
                        np.array(value, dtype=np.int64),
                        allow_pickle=False)
            else:
                np.save(os.path.join(dir_path, f"{key}.npy"),
                        np.array(value, dtype=np.float64),
                        allow_pickle=False)
            n_saved += 1

        # 3. Save ge_parameters as JSON (1)
        ge_params = params.get('ge_parameters', {})
        json_path = os.path.join(dir_path, "ge_parameters.json")
        with open(json_path, 'w', encoding='utf-8') as f:
            json.dump(self._make_json_serializable(ge_params),
                      f, ensure_ascii=False, indent=2)
        n_saved += 1

        print(f"[VocalDecomposer] Saved {n_saved} files to {dir_path}/")
        return dir_path

    @staticmethod
    def _make_json_serializable(obj):
        """Convert an object containing numpy types into JSON-serializable Python native types"""
        if isinstance(obj, dict):
            return {k: VocalDecomposer._make_json_serializable(v)
                    for k, v in obj.items()}
        elif isinstance(obj, (list, tuple)):
            return [VocalDecomposer._make_json_serializable(v) for v in obj]
        elif isinstance(obj, np.ndarray):
            return obj.tolist()
        elif isinstance(obj, (np.integer,)):
            return int(obj)
        elif isinstance(obj, (np.floating,)):
            return float(obj)
        elif isinstance(obj, (np.bool_,)):
            return bool(obj)
        elif isinstance(obj, (int, float, str, bool, type(None))):
            return obj
        else:
            return str(obj)

    @staticmethod
    def load(dir_path: str, validate: bool = True) -> dict:
        """
        Read the 27 parameter files from a directory and rebuild the parameter dict.

        Args:
            dir_path: save directory path
            validate: whether to perform numeric safety checks (default True)

        Returns:
            params: parameter dict

        Raises:
            ValueError: raised when missing files, abnormal dimensions, NaN, Inf or out-of-range values are detected
                       all errors are collected and reported together instead of failing midway
        """
        if not os.path.isdir(dir_path):
            raise FileNotFoundError(f"Parameter directory does not exist: {dir_path}")

        params = {}
        missing_files = []

        # 1. Read array parameters (missing ones do not raise; recorded in missing_files)
        for key in VocalDecomposer._ARRAY_KEYS:
            file_path = os.path.join(dir_path, f"{key}.npy")
            if not os.path.exists(file_path):
                missing_files.append(f"{key}.npy")
                params[key] = None
                continue
            arr = np.load(file_path, allow_pickle=False)
            if len(arr) == 0:
                params[key] = None
            else:
                params[key] = arr

        # 2. Read scalar parameters (missing ones do not raise; recorded in missing_files)
        for key in VocalDecomposer._SCALAR_KEYS:
            file_path = os.path.join(dir_path, f"{key}.npy")
            if not os.path.exists(file_path):
                missing_files.append(f"{key}.npy")
                params[key] = None
                continue
            arr = np.load(file_path, allow_pickle=False)
            if arr.dtype == np.int64:
                params[key] = int(arr.item())
            else:
                params[key] = float(arr.item())

        # 3. Read the ge_parameters JSON (missing file recorded in missing_files)
        json_path = os.path.join(dir_path, "ge_parameters.json")
        if os.path.exists(json_path):
            with open(json_path, 'r', encoding='utf-8') as f:
                params['ge_parameters'] = json.load(f)
        else:
            missing_files.append("ge_parameters.json")
            params['ge_parameters'] = {}

        # 4. Numeric safety checks (pass the missing-file list and report all errors together)
        if validate:
            VocalDecomposer._validate_params(params, missing_files)

        print(f"[VocalDecomposer] Read {len(params)} parameters from {dir_path}/")
        return params

    # ------------------------------------------------------------------
    # Numeric safety checks: dimensions / NaN / Inf / ranges
    # ------------------------------------------------------------------
    @staticmethod
    def _validate_params(params: dict, missing_files: list = None) -> None:
        """
        Perform numeric safety checks on the loaded parameters.

        Checks:
            0. Missing files (read from the missing_files list)
            1. NaN values
            2. Inf values
            3. Abnormal dimensions (shape does not match expectations)
            4. Consistency checks (n_frames / n_samples match the array lengths)
            5. Out-of-range values (F0 / sp / ap / oq / jitter / shimmer)
            6. Scalar consistency (OQ + CQ = 1)
            7. JSON nesting depth

        Args:
            params:        parameter dict
            missing_files: list of missing file names (from the load stage)

        Raises:
            ValueError: raised when any anomaly is detected; all anomalies are collected and reported together
        """
        errors = []

        # ── 0. Missing file check ──
        if missing_files:
            for fname in missing_files:
                errors.append(f"Missing parameter file: {fname}")

        # ── 1. Read meta (n_frames/n_samples may be None) ──
        n_frames_raw = params.get('n_frames')
        n_samples_raw = params.get('n_samples')
        sample_rate_raw = params.get('sample_rate')
        frame_period_raw = params.get('frame_period')

        # None means the file is missing, so skip checks that depend on this value
        n_frames = int(n_frames_raw) if n_frames_raw is not None else -1
        n_samples = int(n_samples_raw) if n_samples_raw is not None else -1
        sample_rate = int(sample_rate_raw) if sample_rate_raw is not None else -1
        frame_period = float(frame_period_raw) if frame_period_raw is not None else -1.0

        if sample_rate_raw is not None:
            if sample_rate <= 0 or sample_rate > 192000:
                errors.append(f"Abnormal sample_rate: {sample_rate} (expected 1~192000)")
        if frame_period_raw is not None:
            if frame_period <= 0 or frame_period > 100:
                errors.append(f"Abnormal frame_period: {frame_period} (expected 0~100ms)")
        if n_frames_raw is not None:
            if n_frames <= 0 or n_frames > 10_000_000:
                errors.append(f"Abnormal n_frames: {n_frames} (expected 1~10M)")
        if n_samples_raw is not None:
            if n_samples <= 0 or n_samples > 1_000_000_000:
                errors.append(f"Abnormal n_samples: {n_samples} (expected 1~1B)")

        # ── 2. Array dimensions + NaN/Inf checks ──
        # n_frames/n_samples of -1 means the meta file is missing, so skip shape comparison
        # Only NaN/Inf and ndim are checked
        expected_shapes = {
            'f0_values':           (n_frames,) if n_frames > 0 else None,
            'f0_time_axis':        (n_frames,) if n_frames > 0 else None,
            'f0_voiced_flags':     (n_frames,) if n_frames > 0 else None,
            'ge_signal':           (n_samples,) if n_samples > 0 else None,
            'breath_data':         (n_samples,) if n_samples > 0 else None,
        }

        for key, expected in expected_shapes.items():
            arr = params.get(key)
            if arr is None:
                # Already recorded in missing_files, so do not report again
                continue

            # Dimension check (shape comparison only when meta is available)
            if expected is not None:
                if arr.ndim != len(expected):
                    errors.append(
                        f"{key} dimension mismatch: ndim={arr.ndim}, expected ndim={len(expected)}"
                    )
                elif arr.shape != expected:
                    errors.append(
                        f"{key} shape mismatch: {arr.shape}, expected {expected}"
                    )
            else:
                # meta is missing, so only check basic ndim sanity
                if arr.ndim < 1:
                    errors.append(f"{key} dimension mismatch: ndim={arr.ndim}")

            # NaN / Inf check (skip bool arrays)
            if arr.dtype != bool:
                if np.any(np.isnan(arr)):
                    nan_count = int(np.sum(np.isnan(arr)))
                    errors.append(f"{key} contains {nan_count} NaN values")
                if np.any(np.isinf(arr)):
                    inf_count = int(np.sum(np.isinf(arr)))
                    errors.append(f"{key} contains {inf_count} Inf values")

        # ── 3. Optional array dimension checks (may be None) ──
        optional_shapes = {
            'ge_sp':               2,   # (n_frames, n_bins)
            'ge_ap':               2,   # (n_frames, n_bins)
            'ge_glottal_intensity': 1,   # (n_frames,) or shorter
            'gi_values':           1,
        }

        for key, expected_ndim in optional_shapes.items():
            arr = params.get(key)
            if arr is None:
                continue

            if arr.ndim != expected_ndim:
                errors.append(
                    f"{key} dimension mismatch: ndim={arr.ndim}, expected ndim={expected_ndim}"
                )

            # NaN / Inf check
            if np.any(np.isnan(arr)):
                nan_count = int(np.sum(np.isnan(arr)))
                errors.append(f"{key} contains {nan_count} NaN values")
            if np.any(np.isinf(arr)):
                inf_count = int(np.sum(np.isinf(arr)))
                errors.append(f"{key} contains {inf_count} Inf values")

        # ── 4. ge_sp / ge_ap frame count consistency ──
        ge_sp = params.get('ge_sp')
        ge_ap = params.get('ge_ap')
        if ge_sp is not None and ge_sp.ndim == 2:
            if ge_sp.shape[0] != n_frames:
                errors.append(
                    f"ge_sp frame count mismatch: {ge_sp.shape[0]}, expected {n_frames}"
                )
        if ge_ap is not None and ge_ap.ndim == 2:
            if ge_ap.shape[0] != n_frames:
                errors.append(
                    f"ge_ap frame count mismatch: {ge_ap.shape[0]}, expected {n_frames}"
                )
            if ge_sp is not None and ge_ap.shape != ge_sp.shape:
                errors.append(
                    f"ge_ap and ge_sp shape mismatch: "
                    f"{ge_ap.shape} vs {ge_sp.shape}"
                )

        # ── 5. Value range checks ──
        f0_values = params.get('f0_values')
        if f0_values is not None and f0_values.dtype != bool:
            voiced_mask = params.get('f0_voiced_flags', np.ones(len(f0_values), dtype=bool))
            voiced_mask = np.asarray(voiced_mask, dtype=bool)
            f0_voiced = f0_values[voiced_mask]
            if len(f0_voiced) > 0:
                f0_min = float(np.min(f0_voiced))
                f0_max = float(np.max(f0_voiced))
                if f0_min < 20.0:
                    errors.append(f"F0 minimum too low: {f0_min:.1f}Hz (expected >= 20Hz)")
                if f0_max > 5000.0:
                    errors.append(f"F0 maximum too high: {f0_max:.1f}Hz (expected <= 5000Hz)")

        if ge_ap is not None and ge_ap.ndim == 2:
            ap_min = float(np.min(ge_ap))
            ap_max = float(np.max(ge_ap))
            if ap_min < -0.01:
                errors.append(f"ge_ap minimum out of range: {ap_min:.4f} (expected >= 0)")
            if ap_max > 1.01:
                errors.append(f"ge_ap maximum out of range: {ap_max:.4f} (expected <= 1)")

        if ge_sp is not None and ge_sp.ndim == 2:
            sp_min = float(np.min(ge_sp))
            if sp_min < -1e-6:
                errors.append(f"ge_sp contains negative values: min={sp_min:.6f} (expected >= 0)")

        # ── 6. Scalar range checks ──
        scalar_ranges = {
            'oq':               (0.0, 1.0),
            'cq':               (0.0, 1.0),
            'ge_oq':            (0.0, 1.0),
            'qq':               (0.0, 1.0),
            'f0_frame_period':  (0.1, 100.0),
            'jitter_local':     (0.0, 100.0),
            'jitter_rap':       (0.0, 100.0),
            'jitter_ppq5':      (0.0, 100.0),
            'jitter_ddp':       (0.0, 100.0),
            'shimmer_local':    (0.0, 100.0),
            'shimmer_apq3':     (0.0, 100.0),
            'shimmer_apq5':     (0.0, 100.0),
            'shimmer_dda':      (0.0, 100.0),
        }

        for key, (low, high) in scalar_ranges.items():
            val = params.get(key)
            if val is None:
                continue
            val = float(val)
            if np.isnan(val) or np.isinf(val):
                errors.append(f"{key} contains NaN/Inf: {val}")
            elif val < low or val > high:
                errors.append(
                    f"{key} value out of range: {val:.4f} (expected within [{low}, {high}])"
                )

        # ── 7. OQ and CQ consistency ──
        oq = params.get('oq')
        cq = params.get('cq')
        ge_oq = params.get('ge_oq')
        if oq is not None and cq is not None:
            if abs((oq + cq) - 1.0) > 0.01:
                errors.append(f"OQ + CQ != 1: oq={oq:.4f}, cq={cq:.4f}")
        if oq is not None and ge_oq is not None:
            if abs(oq - ge_oq) > 0.01:
                errors.append(f"oq and ge_oq mismatch: {oq:.4f} vs {ge_oq:.4f}")

        # ── 8. ge_parameters JSON depth limit (guard against nesting bombs) ──
        ge_params = params.get('ge_parameters', {})
        depth = VocalDecomposer._json_depth(ge_params)
        if depth > 10:
            errors.append(f"Abnormal ge_parameters nesting depth: {depth} (expected <= 10)")

        # ── Summary ──
        if errors:
            error_msg = "\n".join(f"  [{i+1}] {e}" for i, e in enumerate(errors))
            raise ValueError(
                f"Numeric safety validation failed with {len(errors)} anomalies:\n{error_msg}"
            )

    @staticmethod
    def _json_depth(obj, current_depth=0) -> int:
        """Compute the JSON nesting depth"""
        if current_depth > 100:  # hard limit
            return current_depth
        if isinstance(obj, dict):
            if not obj:
                return current_depth + 1
            return max(VocalDecomposer._json_depth(v, current_depth + 1)
                       for v in obj.values())
        elif isinstance(obj, list):
            if not obj:
                return current_depth + 1
            return max(VocalDecomposer._json_depth(v, current_depth + 1)
                       for v in obj)
        return current_depth

    def decompose_and_save(self,
                           input_audio: Union[str, vl.WAVAudio, np.ndarray],
                           output_dir: str,
                           ) -> dict:
        """
        Decompose and save in one step.

        Args:
            input_audio: input audio
            output_dir:  save directory path

        Returns:
            params: parameter dict
        """
        params = self.decompose(input_audio)
        self.save(params, output_dir)
        return params


# ----------------------------------------------------------------------
# Usage example
# ----------------------------------------------------------------------
if __name__ == "__main__":
    input_wav = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("VOCALB1_DEMO_WAV", "")
    if not input_wav:
        raise SystemExit("usage: python VocalDecomposer.py <input.wav>")

    decomposer = VocalDecomposer(sample_rate=44100, frame_period=5.0)

    params = decomposer.decompose_and_save(
        input_wav,
        "_output/ei_params",
    )

    print("\n=== Decomposition Parameter Summary ===")
    print(f"  F0:        {params['n_frames']} frames, "
          f"avg={np.mean(params['f0_values'][params['f0_voiced_flags']]):.1f}Hz")
    print(f"  GE:        {params['n_samples']} samples, "
          f"sp={params['ge_sp'].shape if params['ge_sp'] is not None else 'None'}")
    print(f"  GI:        {params['gi_values'].shape if params['gi_values'] is not None else 'None'}")
    print(f"  OQ:        {params['oq']:.4f}")
    print(f"  CQ:        {params['cq']:.4f}")
    print(f"  QQ:        {params['qq']:.4f}")
    print(f"  jitter:    local={params['jitter_local']:.4f}%")
    print(f"  shimmer:   local={params['shimmer_local']:.4f}%")
    print(f"  breath:    {len(params['breath_data'])} samples")
    print(f"  sample_rate: {params['sample_rate']}")