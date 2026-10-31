# -*- coding: utf-8 -*-
"""
test_vocal_lib.py -- vocal_lib functional regression tests

Run:
    python tools\\test_vocal_lib.py            # all tests
    python tools\\test_vocal_lib.py --no-e2e   # skip end-to-end (faster)

Coverage:
    [1] Package import and public API completeness
    [2] Data classes F0 / WAVAudio / GlottalExcitation
    [3] WAVAudio file read/write round-trip precision
    [4] decompose_vocal decomposition (real audio -> F0 + breath + glottal excitation)
    [5] pitch_shift pitch transformation (frequency ratio matches the semitone relation)
    [6] synthesize_vocal synthesis (duration / no NaN / energy)
    [7] End-to-end VocalB1Synthesizer.synthesize (file output + intermediate cleanup)

Runs directly without pytest; any failing case returns a non-zero exit code.
Test artifacts are written to _test_output/ for listening and troubleshooting.
"""

import os
import shutil
import sys
import traceback
import numpy as np

# 开发仓库中本文件位于 tools/，库与引擎在上一级；发布包中所有脚本平铺在同一目录。
_BASE = os.path.dirname(os.path.abspath(__file__))
_ROOT = _BASE if os.path.exists(os.path.join(_BASE, "vocal_lib")) else os.path.dirname(_BASE)
for _path in (_ROOT, _BASE):
    if _path not in sys.path:
        sys.path.insert(0, _path)

OUT_DIR = os.path.join(_ROOT, "_test_output")

# Candidate test audio: the first existing one is used (append here when the library path changes)
_CANDIDATES = [
    os.path.join(os.environ.get("VOCALB1_LIB") or os.path.join(_ROOT, "voicebank"), "_で.wav"),
]

_CACHE = {}          # intermediate results shared between cases (avoids repeated decomposition)
TESTS = []


def test(name):
    """Register a test case."""
    def deco(fn):
        TESTS.append((name, fn))
        return fn
    return deco


def approx(a, b, tol=0.05):
    """Relative error comparison (falls back to absolute error when b is near 0)."""
    return abs(a - b) <= tol * max(abs(b), 1e-9)


def find_sample():
    for p in _CANDIDATES:
        if os.path.isfile(p):
            return p
    return None


# ----------------------------------------------------------------------
# Test cases
# ----------------------------------------------------------------------
@test("Package import and public API completeness")
def t_import():
    import vocal_lib as vl
    for name in vl.__all__:
        assert hasattr(vl, name), f"vocal_lib is missing public symbol {name}"
    for name in ("F0", "WAVAudio", "GlottalExcitation",
                 "decompose_vocal", "synthesize_vocal", "pitch_shift"):
        assert hasattr(vl, name), f"missing {name}"
    assert len(vl.__all__) >= 6
    return f"exported {len(vl.__all__)} symbols"


@test("Data classes F0 / WAVAudio / GlottalExcitation")
def t_dataclass():
    import vocal_lib as vl

    f0 = vl.F0(values=np.array([100.0, 0.0, 200.0]),
               frame_period=5.0, sample_rate=44100)
    assert f0.n_frames == 3
    assert len(f0.time_axis) == 3
    assert list(f0.voiced_flags) == [True, False, True]
    assert abs(f0.time_axis[-1] - 0.010) < 1e-9

    w = vl.WAVAudio(data=np.zeros(44100), sample_rate=44100)
    assert w.n_samples == 44100 and w.is_mono
    assert approx(w.duration, 1.0, 1e-9)

    g = vl.GlottalExcitation(signal=np.zeros(2205), sample_rate=44100)
    assert g.n_samples == 2205
    assert approx(g.duration, 2205 / 44100, 1e-9)
    assert g.has_pyworld_params is False
    g.set_param("probe", 1)
    assert g.get_param("probe") == 1

    c = f0.copy()
    c.values[0] = 999.0
    assert f0.values[0] == 100.0, "copy() did not deep-copy"
    return "construction / properties / param read-write / deep copy OK"


@test("WAVAudio file read/write round-trip")
def t_wav_roundtrip():
    import vocal_lib as vl
    sr = 44100
    t = np.arange(sr // 2) / sr
    tone = 0.5 * np.sin(2 * np.pi * 440.0 * t)
    path = os.path.join(OUT_DIR, "roundtrip.wav")
    vl.WAVAudio(data=tone, sample_rate=sr).save(path, bit_depth=16)
    back = vl.WAVAudio.from_file(path, mono=True)
    assert back.sample_rate == sr
    assert abs(back.n_samples - len(tone)) <= 2
    err = float(np.max(np.abs(back.data[:len(tone)] - tone)))
    assert err < 1e-3, f"round-trip error too large {err}"
    return f"16bit round-trip peak error {err:.2e}"


@test("decompose_vocal decomposition")
def t_decompose():
    import vocal_lib as vl
    path = find_sample()
    assert path, "test audio not found, check the voice library path"
    f0, breath, glot = vl.decompose_vocal(path, sample_rate=44100, frame_period=5.0)
    assert isinstance(f0, vl.F0)
    assert isinstance(breath, vl.WAVAudio)
    assert isinstance(glot, vl.GlottalExcitation)
    assert f0.n_frames > 10, f"abnormal frame count {f0.n_frames}"
    voiced = int((f0.values > 0).sum())
    assert voiced > 0, "no voiced frames detected"
    assert glot.sp is not None and glot.ap is not None, "missing sp/ap"
    assert breath.n_samples > 0
    _CACHE["decomp"] = (f0, breath, glot)
    return (f"frames {f0.n_frames}, voiced {voiced}, "
            f"sp{glot.sp.shape}, breath {breath.n_samples}smp")


@test("pitch_shift pitch transformation")
def t_pitch():
    import vocal_lib as vl
    assert "decomp" in _CACHE, "prerequisite decompose case did not pass"
    f0, _breath, glot = _CACHE["decomp"]

    # D4 -> E4 (+2 semitones). A small interval avoids the F0 soft-clipping region (550~900Hz)
    # and keeps the frequency ratio exactly 2^(2/12).
    f0n, glotn = vl.pitch_shift(f0, glot, "D4", "E4")
    assert isinstance(f0n, vl.F0) and isinstance(glotn, vl.GlottalExcitation)

    a = f0.values[f0.values > 0]
    b = f0n.values[f0n.values > 0]
    assert len(a) > 0 and len(b) > 0
    ratio = float(np.median(b) / np.median(a))
    expect = 2.0 ** (2.0 / 12.0)
    assert approx(ratio, expect, 0.05), f"frequency ratio {ratio:.4f} deviates from expected {expect:.4f}"
    assert glotn.sp is not None and glotn.sp.shape == glot.sp.shape, "sp shape changed"
    _CACHE["shift"] = (f0n, glotn)
    return f"frequency ratio {ratio:.4f} (expected {expect:.4f})"


@test("synthesize_vocal synthesis")
def t_synth():
    import vocal_lib as vl
    assert "shift" in _CACHE, "prerequisite pitch_shift case did not pass"
    f0n, glotn = _CACHE["shift"]
    _f0, breath, _g = _CACHE["decomp"]

    out = vl.synthesize_vocal(f0n, breath, glotn, match_rms_to=0.3)
    assert isinstance(out, vl.WAVAudio)
    assert out.n_samples > 0
    assert not np.isnan(out.data).any(), "output contains NaN"
    rms = float(np.sqrt(np.mean(out.data ** 2)))
    assert rms > 0.01, f"output energy too low rms={rms}"
    out.save(os.path.join(OUT_DIR, "pitch_shift_demo.wav"), bit_depth=24)
    return f"duration {out.duration:.2f}s, RMS {rms:.4f}"


@test("end-to-end VocalB1Synthesizer.synthesize")
def t_e2e():
    import vocal_lib as vl
    from VocalB1Synthesizer import synthesize, DEFAULT_SEGMENTS

    assert all(os.path.isfile(s["audio"]) for s in DEFAULT_SEGMENTS), \
        "voice library files referenced by DEFAULT_SEGMENTS are missing"

    path = os.path.join(OUT_DIR, "e2e_song.wav")
    ret = synthesize(DEFAULT_SEGMENTS, path, verbose=False)
    assert ret == os.path.abspath(path), f"abnormal return value {ret}"
    assert os.path.isfile(path), "artifact was not generated"

    back = vl.WAVAudio.from_file(path, mono=True)
    assert back.n_samples > 0
    assert not np.isnan(back.data).any(), "artifact contains NaN"

    leftovers = [f for f in os.listdir(OUT_DIR)
                 if f.startswith("breath_seg") or f == "spliced_frames.txt"]
    assert not leftovers, f"intermediate artifacts not cleaned up: {leftovers}"
    return f"artifact {back.duration:.2f}s, intermediate artifacts cleaned up"


# ----------------------------------------------------------------------
# Runner
# ----------------------------------------------------------------------
def main():
    if os.path.isdir(OUT_DIR):
        shutil.rmtree(OUT_DIR, ignore_errors=True)
    os.makedirs(OUT_DIR, exist_ok=True)

    skip_e2e = "--no-e2e" in sys.argv
    print("=" * 72)
    print("vocal_lib regression tests")
    print("=" * 72)

    passed = failed = skipped = 0
    for name, fn in TESTS:
        if skip_e2e and "end-to-end" in name:
            skipped += 1
            print(f"[SKIP] {name}")
            continue
        try:
            detail = fn()
            passed += 1
            print(f"[PASS] {name}" + (f"  -- {detail}" if detail else ""))
        except Exception as exc:
            failed += 1
            print(f"[FAIL] {name}")
            print("       " + "".join(
                traceback.format_exception_only(type(exc), exc)).strip())

    print("-" * 72)
    print(f"Results: {passed} passed, {failed} failed" +
          (f", {skipped} skipped" if skipped else ""))
    print(f"Test artifacts: {OUT_DIR}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
