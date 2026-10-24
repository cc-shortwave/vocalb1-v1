# -*- coding: utf-8 -*-
"""
vocal_bridge.py - fixed bridge process between the VocalB1 UI and the Python synthesis engine

Constraints (important):
  * This file does not modify any existing .py file, nor change/install any venv dependency;
  * It only reuses VocalB1Synthesizer.synthesize through import;
  * The protocol runs over stdin/stdout, one JSON object per line (UTF-8, no BOM).

Startup:
    <python> -u vocal_bridge.py --serve

Requests (C# -> Python):
    {"id": "1", "cmd": "ping"}
    {"id": "2", "cmd": "selfcheck"}
    {"id": "3", "cmd": "render", "output": "D:/out/a.wav", "project": { ... }}
    {"id": "4", "cmd": "shutdown"}

Responses (Python -> C#):
    {"id": "1", "type": "ready", "python": "3.12.2", "root": "..."}
    {"id": "3", "type": "log", "level": "info", "message": "..."}
    {"id": "3", "type": "progress", "value": 0.35, "message": "..."}
    {"id": "3", "type": "result", "output": "...", "elapsedSec": 12.3, "durationSec": 7.5}
    {"id": "3", "type": "error", "message": "...", "traceback": "..."}

The project inside a render request is serialized by the C# project model (camelCase);
this script also accepts snake_case keys and finally translates them into
the keyword arguments of synthesize().
"""

import json
import os
import sys
import time
import traceback
import wave

_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
# Repository root = two levels above ui/bridge/
_REPO_ROOT = os.path.abspath(os.path.join(_BASE_DIR, "..", ".."))
if _REPO_ROOT not in sys.path:
    sys.path.insert(0, _REPO_ROOT)

_PROTOCOL_OUT = sys.__stdout__
_CURRENT_ID = [None]


def _emit(obj):
    """Write one line of JSON to the protocol channel; stay silent on failure (avoid recursive errors)."""
    try:
        _PROTOCOL_OUT.write(json.dumps(obj, ensure_ascii=False) + "\n")
        _PROTOCOL_OUT.flush()
    except Exception:
        pass


def _log(level, message):
    _emit({"id": _CURRENT_ID[0], "type": "log", "level": level, "message": message})


def _progress(value, message=""):
    _emit({"id": _CURRENT_ID[0], "type": "progress", "value": float(value), "message": message})


class _LineEmitter:
    """Convert text printed by the engine into per-line JSON log events."""

    def __init__(self, level="info"):
        self._level = level
        self._buf = ""

    def write(self, text):
        if not isinstance(text, str):
            text = str(text)
        self._buf += text
        while "\n" in self._buf:
            line, self._buf = self._buf.split("\n", 1)
            self._flush_line(line)
        return len(text)

    def flush(self):
        if self._buf:
            self._flush_line(self._buf)
            self._buf = ""

    def isatty(self):
        return False

    def _flush_line(self, line):
        line = line.rstrip("\r")
        if line.strip():
            _log(self._level, line)


def _pick(d, *names, default=None):
    for name in names:
        if isinstance(d, dict) and name in d and d[name] is not None:
            return d[name]
    return default


# ----------------------------------------------------------------------
# Project -> synthesize() arguments
# ----------------------------------------------------------------------
def _segment_to_dict(seg, fallback_ratios):
    out = {
        "audio": _pick(seg, "audio", "audioPath", "audio_path", default=""),
        "src_pitch": _pick(seg, "srcPitch", "src_pitch", default="C4"),
        "dst_pitch": _pick(seg, "dstPitch", "dst_pitch", default="C4"),
        "beat_start": float(_pick(seg, "beatStart", "beat_start", default=1.0)),
        "beat_end": float(_pick(seg, "beatEnd", "beat_end", default=2.0)),
    }

    transition = _pick(seg, "transition")
    if transition is not None:
        out["transition"] = float(transition)
    keep_head = _pick(seg, "keepUnvoicedHead", "keep_unvoiced_head")
    if keep_head is not None:
        out["keep_unvoiced_head"] = bool(keep_head)

    ratios = _pick(seg, "followRatio", "follow_ratio")
    if ratios is not None:
        out["follow_ratio"] = tuple(float(v) for v in ratios)
    else:
        out["follow_ratio"] = fallback_ratios[0]

    bands = _pick(seg, "bandRatios", "band_ratios")
    if bands is not None:
        out["band_ratios"] = [float(v) for v in bands]
    else:
        out["band_ratios"] = fallback_ratios[1]

    thr = _pick(seg, "breathyThr", "breathy_thr")
    if thr is not None:
        out["breathy_thr"] = float(thr)
    else:
        out["breathy_thr"] = fallback_ratios[2]

    return out


def _pitch_line(points):
    if not points:
        return None
    line = []
    for point in points:
        beat = float(_pick(point, "beat", default=0.0))
        pitch = _pick(point, "pitch")
        line.append((beat, pitch))
    return line


_SETTING_MAP = {
    "followRatio": ("follow_ratio", "tuple"),
    "bandRatios": ("band_ratios", "list"),
    "breathyThr": ("breathy_thr", "float"),
    "bandWidthHz": ("band_width_hz", "float"),
    "apScale": ("ap_scale", "float"),
    "apMax": ("ap_max", "float"),
    "sampleRate": ("sample_rate", "int"),
    "framePeriod": ("frame_period", "float"),
    "joinNormalizeSp": ("join_normalize_sp", "bool"),
    "durEdgeFrames": ("dur_edge_frames", "int"),
    "durJitterSmooth": ("dur_jitter_smooth", "int"),
    "durJitterDecayMs": ("dur_jitter_decay_ms", "float"),
    "durTailJitterRatio": ("dur_tail_jitter_ratio", "float"),
    "matchRmsTo": ("match_rms_to", "float"),
    "modelType": ("model_type", "str"),
    "spFloor": ("sp_floor", "float"),
    "addBreath": ("add_breath", "bool"),
    "breathGain": ("breath_gain", "float"),
    "breathFollowPitch": ("breath_follow_pitch", "bool"),
    "breathPvFrameMs": ("breath_pv_frame_ms", "float"),
    "breathPvHopMs": ("breath_pv_hop_ms", "float"),
    "breathShapeToMain": ("breath_shape_to_main", "bool"),
    "breathShapeBands": ("breath_shape_bands", "int"),
    "breathShapeGainMin": ("breath_shape_gain_min", "float"),
    "breathShapeGainMax": ("breath_shape_gain_max", "float"),
    "plosiveRatio": ("plosive_ratio", "float"),
    "plosiveMinGradDb": ("plosive_min_grad_db", "float"),
    "plosiveMinCrest": ("plosive_min_crest", "float"),
    "plosiveMaxValley": ("plosive_max_valley", "float"),
    "plosiveGain": ("plosive_gain", "float"),
    "plosiveSmooth": ("plosive_smooth", "int"),
    "plosiveMarginHead": ("plosive_margin_head", "int"),
    "plosiveMarginTail": ("plosive_margin_tail", "int"),
    "plosiveKeepBurst": ("plosive_keep_burst", "bool"),
    "plosiveEnvTransplant": ("plosive_env_transplant", "bool"),
    "plosiveEnvHead": ("plosive_env_head", "int"),
    "plosiveEnvTail": ("plosive_env_tail", "int"),
    "plosiveEnvGainMin": ("plosive_env_gain_min", "float"),
    "plosiveEnvGainMax": ("plosive_env_gain_max", "float"),
    "edgeFadeMs": ("edge_fade_ms", "float"),
    "bitDepth": ("bit_depth", "int"),
    "cleanIntermediate": ("clean_intermediate", "bool"),
}


def _convert(value, kind):
    if kind == "tuple":
        return tuple(float(v) for v in value)
    if kind == "list":
        return [float(v) for v in value]
    if kind == "float":
        return float(value)
    if kind == "int":
        return int(value)
    if kind == "bool":
        return bool(value)
    return str(value)


def _build_kwargs(project):
    settings = project.get("settings") or {}

    follow = settings.get("followRatio") or [0.5, 0.45, 0.25, 0.15, 0.1, 0.05, 0.0]
    bands = settings.get("bandRatios") or [1.0] * 8
    thr = settings.get("breathyThr")
    fallback = (tuple(follow), list(bands), thr)

    segments = [_segment_to_dict(s, fallback) for s in (project.get("segments") or [])]

    kwargs = {
        "pitch_line": _pitch_line(project.get("pitchLine") or project.get("pitch_line")),
        "bpm": float(project.get("bpm") or 120.0),
        "time_signature": (
            int(project.get("beatsPerBar") or 3),
            int(project.get("beatUnit") or 4),
        ),
        # Logs must be forwarded to the UI, so force this on
        "verbose": True,
    }

    for camel, (snake, kind) in _SETTING_MAP.items():
        value = settings.get(camel, settings.get(snake))
        if value is not None:
            kwargs[snake] = _convert(value, kind)

    return segments, kwargs


def _wav_duration(path):
    try:
        with wave.open(path, "rb") as wf:
            frames = wf.getnframes()
            rate = wf.getframerate() or 1
            return frames / float(rate)
    except Exception:
        return 0.0


# ----------------------------------------------------------------------
# Command implementations
# ----------------------------------------------------------------------
def cmd_ping(req):
    info = {"id": req.get("id"), "type": "ready", "python": sys.version.split()[0],
            "root": _REPO_ROOT}
    try:
        import VocalB1Synthesizer as vs
        info["engine"] = getattr(vs, "__name__", "VocalB1Synthesizer")
        info["version"] = getattr(vs, "__version__", "unknown")
    except Exception as exc:
        info["engine"] = None
        info["importError"] = f"{type(exc).__name__}: {exc}"
    _emit(info)


def cmd_selfcheck(req):
    started = time.time()
    try:
        import VocalB1Synthesizer as vs
        ok = callable(getattr(vs, "synthesize", None))
        _emit({"id": req.get("id"), "type": "result", "ok": ok,
               "python": sys.version.split()[0], "root": _REPO_ROOT,
               "elapsedSec": time.time() - started})
    except Exception as exc:
        _emit({"id": req.get("id"), "type": "error",
               "message": f"{type(exc).__name__}: {exc}",
               "traceback": traceback.format_exc()})


def cmd_render(req, req_id):
    started = time.time()
    project = req.get("project") or {}
    output = req.get("output") or ""

    if not output:
        _emit({"id": req_id, "type": "error", "message": "Missing output path"})
        return

    output = os.path.abspath(output)
    os.makedirs(os.path.dirname(output) or ".", exist_ok=True)

    try:
        from VocalB1Synthesizer import synthesize

        segments, kwargs = _build_kwargs(project)
        _log("info", f"Rendering {len(segments)} segments -> {output}")
        _progress(0.0, "Starting")

        out_emitter = _LineEmitter("info")
        err_emitter = _LineEmitter("warn")
        old_out, old_err = sys.stdout, sys.stderr
        sys.stdout, sys.stderr = out_emitter, err_emitter
        try:
            synthesize(segments, output, **kwargs)
        finally:
            out_emitter.flush()
            err_emitter.flush()
            sys.stdout, sys.stderr = old_out, old_err

        if not os.path.exists(output):
            raise RuntimeError(f"Synthesis finished but output file was not found: {output}")

        _progress(1.0, "Done")
        _emit({"id": req_id, "type": "result", "output": output,
               "elapsedSec": round(time.time() - started, 3),
               "durationSec": round(_wav_duration(output), 3)})
    except Exception as exc:
        _emit({"id": req_id, "type": "error",
               "message": f"{type(exc).__name__}: {exc}",
               "traceback": traceback.format_exc(),
               "elapsedSec": round(time.time() - started, 3)})


def main():
    if hasattr(sys.stdin, "reconfigure"):
        # utf-8-sig: tolerate a BOM written by the peer on the first line
        sys.stdin.reconfigure(encoding="utf-8-sig")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    _emit({"type": "boot", "python": sys.version.split()[0], "root": _REPO_ROOT,
           "pid": os.getpid()})

    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            req = json.loads(raw)
        except Exception as exc:
            _emit({"type": "error", "message": f"Failed to parse request JSON: {exc} | raw={raw[:200]!r}"})
            continue

        cmd = req.get("cmd")
        req_id = req.get("id")
        _CURRENT_ID[0] = req_id

        if cmd == "ping":
            cmd_ping(req)
        elif cmd == "selfcheck":
            cmd_selfcheck(req)
        elif cmd == "render":
            cmd_render(req, req_id)
        elif cmd == "shutdown":
            _emit({"id": req_id, "type": "bye"})
            break
        else:
            _emit({"id": req_id, "type": "error", "message": f"Unknown command: {cmd}"})

        _CURRENT_ID[0] = None

    return 0


if __name__ == "__main__":
    sys.exit(main())
