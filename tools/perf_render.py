"""Run one full synthesis with the global performance probe enabled.

    python tools/perf_render.py                     # built-in 12-segment stress run
    python tools/perf_render.py --segments 24       # longer run
    python tools/perf_render.py --project song.json # 1:1 replay of a real project
    python tools/perf_render.py --profile           # additionally cProfile the run

The probe (vocal_lib/perf.py) records wall/CPU time, self time and RSS peaks for
every instrumented stage. The report lands in `_output/perf/`.
"""

import argparse
import cProfile
import json
import os
import pstats
import sys
import time

_HERE = os.path.dirname(os.path.abspath(__file__))
_ROOT = os.path.dirname(_HERE)
_BRIDGE = os.path.join(_ROOT, "ui", "bridge")
for _path in (_ROOT, _BRIDGE):
    if _path not in sys.path:
        sys.path.insert(0, _path)

os.environ.setdefault("VOCALB1_PERF", "1")


_VOICEBANK = "voicebank"


def _find_voice_files(limit=32):
    """Pick a few real voicebank wav files; returns [] when none is installed.

    Uses the plain relative ``voicebank`` path (resolved against the current working
    directory), so the generated demo project also carries relative audio paths.
    """
    found = []
    if not os.path.isdir(_VOICEBANK):
        return found
    for current, _dirs, files in os.walk(_VOICEBANK):
        for name in sorted(files):
            if name.lower().endswith(".wav"):
                found.append(os.path.join(current, name))
                if len(found) >= limit:
                    return found
    return found


_PITCHES = ["D4", "E4", "F4", "E4", "D4", "C4", "D4", "E4"]


def _demo_project(count):
    files = _find_voice_files()
    if not files:
        raise SystemExit(
            "No wav file found under the relative path voicebank/ (run from the repo "
            "root), or pass --project <project.json>."
        )
    segments = []
    for i in range(count):
        start = float(i)
        segments.append({
            "audio": files[i % len(files)],
            "srcPitch": "D4",
            "dstPitch": _PITCHES[i % len(_PITCHES)],
            "beatStart": start,
            "beatEnd": start + 1.0,
            "keepUnvoicedHead": i == 0,
        })
    return {
        "bpm": 120.0,
        "beatsPerBar": 4,
        "beatUnit": 4,
        "segments": segments,
        "pitchLine": [
            {"beat": 0.0, "pitch": "D4"},
            {"beat": count / 3.0, "pitch": "F4"},
            {"beat": 2.0 * count / 3.0, "pitch": "E4"},
            {"beat": float(count), "pitch": "D4"},
        ],
        "settings": {"cleanIntermediate": True, "verbose": False},
    }


def main():
    parser = argparse.ArgumentParser(description="Run one synthesis under the global performance probe")
    parser.add_argument("--project", help="render a real project JSON instead of the built-in demo")
    parser.add_argument("--segments", type=int, default=12, help="segment count for the built-in demo (default 12)")
    parser.add_argument("--output", default=os.path.join(_ROOT, "_output", "perf", "render_perf.wav"))
    parser.add_argument("--profile", action="store_true", help="also run cProfile and print the top functions")
    args = parser.parse_args()

    if args.project:
        with open(args.project, "r", encoding="utf-8") as fh:
            project = json.load(fh)
    else:
        project = _demo_project(args.segments)

    from vocal_bridge import _build_kwargs
    from VocalB1Synthesizer import synthesize
    from vocal_lib import perf as perf_probe

    segments, kwargs = _build_kwargs(project)
    os.makedirs(os.path.dirname(args.output), exist_ok=True)

    print(f"[perf] python {sys.version.split()[0]}")
    print(f"[perf] {len(segments)} segments, {kwargs.get('bpm')} BPM -> {args.output}")
    print(f"[perf] probe enabled, sampling every {perf_probe.SAMPLE_INTERVAL * 1000:.0f} ms\n")

    started = time.time()
    if args.profile:
        profiler = cProfile.Profile()
        profiler.enable()
        synthesize(segments, args.output, **kwargs)
        profiler.disable()
        stats = pstats.Stats(profiler)
        stats.sort_stats("tottime")
        print("\n[cProfile] top functions by tottime")
        stats.print_stats(25)
        print("\n[cProfile] top functions by cumulative time")
        stats.sort_stats("cumulative").print_stats(25)
    else:
        synthesize(segments, args.output, **kwargs)

    elapsed = time.time() - started
    print(f"\n[perf] synthesis wall time {elapsed:.3f}s")
    report_path = perf_probe.report(title="VocalB1 synthesis performance report")
    if report_path:
        print(f"[perf] report written to {report_path}")


if __name__ == "__main__":
    main()
