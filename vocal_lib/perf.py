"""Global performance probe for the VocalB1 synthesis pipeline.

Enable it by setting the environment variable ``VOCALB1_PERF=1`` (or ``true`` /
``yes`` / ``on``) before the Python engine starts, or run ``tools/perf_render.py``
which does it automatically. When disabled, :func:`span` is a no-op context
manager and the sampler thread never starts, so the production path only pays
one truthiness check per instrumented block.

Every span records:

* wall time (``time.perf_counter``) and CPU time (``time.process_time``),
  which together give the CPU utilization of that block;
* self time (wall time minus the wall time of the child spans it contains), i.e.
  the time actually spent inside the block;
* peak resident memory inside the span window -- the process working set is
  sampled in a background thread every 20 ms;
* call count, so per-segment loops (``decompose``, ``pitch_shift`` ...) show how
  the cost accumulates over the whole render.

Usage::

    from vocal_lib.perf import span, report

    with span("stage"):
        ...

    report()                       # prints the table and writes a markdown file
"""

import os
import sys
import threading
import time
from contextlib import contextmanager

_TRUTHY = {"1", "true", "yes", "on"}
ENABLED = os.environ.get("VOCALB1_PERF", "").strip().lower() in _TRUTHY
SAMPLE_INTERVAL = 0.02          # seconds between working-set samples

_lock = threading.Lock()
_t0 = time.perf_counter()
_records = {}                   # name -> aggregated record dict
_stack = []                     # active span frames
_events = []                    # (t, label) milestones
_rss_samples = []               # (t, rss_mb)
_process_peak = [0.0]
_sampler = None
_process_info = {}


_WIN_MEM = None


def _win_mem_fn():
    """Resolve K32GetProcessMemoryInfo once (psapi is an API set on modern Windows)."""
    global _WIN_MEM
    if _WIN_MEM is not None:
        return _WIN_MEM
    import ctypes
    from ctypes import wintypes

    class _PMC(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
        ]

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    try:
        fn = k32.K32GetProcessMemoryInfo
    except AttributeError:
        fn = ctypes.WinDLL("psapi", use_last_error=True).GetProcessMemoryInfo
    fn.argtypes = [wintypes.HANDLE, ctypes.POINTER(_PMC), wintypes.DWORD]
    fn.restype = wintypes.BOOL
    k32.GetCurrentProcess.restype = wintypes.HANDLE
    _WIN_MEM = (fn, k32.GetCurrentProcess, _PMC)
    return _WIN_MEM


def _read_rss_mb():
    """Current process working set in MB (Windows, /proc fallback elsewhere)."""
    try:
        if os.name == "nt":
            import ctypes
            fn, get_current, pmc_type = _win_mem_fn()
            pmc = pmc_type()
            pmc.cb = ctypes.sizeof(pmc_type)
            if fn(get_current(), ctypes.byref(pmc), pmc.cb):
                return pmc.WorkingSetSize / (1024.0 * 1024.0)
        with open("/proc/self/statm", "r", encoding="ascii") as fh:
            pages = int(fh.read().split()[1])
        return pages * os.sysconf("SC_PAGE_SIZE") / (1024.0 * 1024.0)
    except Exception:
        return 0.0


def _sampler_loop():
    while True:
        rss = _read_rss_mb()
        t = time.perf_counter()
        _rss_samples.append((t, rss))
        if rss > _process_peak[0]:
            _process_peak[0] = rss
        if len(_rss_samples) > 60000:      # ~20 min at 20 ms; drop the oldest half
            del _rss_samples[:30000]
        time.sleep(SAMPLE_INTERVAL)


def _ensure_started():
    global _sampler
    if _sampler is not None:
        return
    _process_info.update({
        "python": sys.version.split()[0],
        "pid": os.getpid(),
        "cpuCount": os.cpu_count(),
        "startRssMb": _read_rss_mb(),
    })
    _process_peak[0] = _process_info["startRssMb"]
    _sampler = threading.Thread(target=_sampler_loop, name="vocalb1-perf-sampler", daemon=True)
    _sampler.start()


def _window_peak(start_wall):
    peak = 0.0
    for t, rss in _rss_samples:
        if t < start_wall:
            continue
        if rss > peak:
            peak = rss
    return peak


class _Frame:
    __slots__ = ("name", "wall0", "cpu0", "rss0", "child_wall", "child_cpu")

    def __init__(self, name):
        self.name = name
        self.wall0 = time.perf_counter()
        self.cpu0 = time.process_time()
        self.rss0 = _read_rss_mb()
        self.child_wall = 0.0
        self.child_cpu = 0.0


class _NoopSpan:
    __slots__ = ()
    detail = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


_NOOP = _NoopSpan()


class _Span:
    __slots__ = ("frame", "record", "detail")

    def __init__(self, frame, record, detail):
        self.frame = frame
        self.record = record
        self.detail = detail

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        frame = self.frame
        record = self.record
        wall = time.perf_counter() - frame.wall0
        cpu = time.process_time() - frame.cpu0
        peak = _window_peak(frame.wall0)
        rss_end = _read_rss_mb()

        with _lock:
            if _stack and _stack[-1] is frame:
                _stack.pop()
            if _stack:
                _stack[-1].child_wall += wall
                _stack[-1].child_cpu += cpu
            record["calls"] += 1
            record["total"] += wall
            record["self"] += wall - frame.child_wall
            record["cpu"] += cpu
            record["selfCpu"] += cpu - frame.child_cpu
            record["rssPeak"] = max(record["rssPeak"], peak)
            record["rssDelta"] = max(record["rssDelta"], rss_end - frame.rss0)
        return False


@contextmanager
def _null_ctx():
    yield _NOOP


def span(name):
    """Context manager timing one named block; returns an object with ``detail``."""
    if not ENABLED:
        return _null_ctx()
    _ensure_started()
    with _lock:
        record = _records.get(name)
        if record is None:
            record = {
                "calls": 0, "total": 0.0, "self": 0.0, "cpu": 0.0,
                "selfCpu": 0.0, "rssPeak": 0.0, "rssDelta": 0.0,
                "first": time.perf_counter() - _t0, "last": 0.0,
            }
            _records[name] = record
        frame = _Frame(name)
        _stack.append(frame)
        record["last"] = frame.wall0 - _t0
    return _Span(frame, record, None)


def mark(label):
    """Record a timeline milestone (start of a segment, checkpoint, ...)."""
    if not ENABLED:
        return
    _ensure_started()
    _events.append((time.perf_counter() - _t0, label))


def elapsed():
    return time.perf_counter() - _t0


def summary_text(top: int = 40):
    """Build the plain-text report table (also used by report())."""
    if not ENABLED:
        return "[perf] probe disabled (set VOCALB1_PERF=1 to enable)"
    wall_total = elapsed()
    cpu_total = time.process_time()
    lines = [
        "=== VocalB1 performance probe ===",
        f"python {_process_info.get('python')} | pid {_process_info.get('pid')} | "
        f"cpu {_process_info.get('cpuCount')} threads | wall {wall_total:.3f}s | "
        f"process cpu {cpu_total:.3f}s ({100.0 * cpu_total / max(wall_total, 1e-9):.1f}% of wall)",
        f"RSS start {_process_info.get('startRssMb', 0.0):.1f} MB | peak {_process_peak[0]:.1f} MB | "
        f"now {_read_rss_mb():.1f} MB",
        "",
        f"{'span':48s} {'calls':>6s} {'total_s':>9s} {'self_s':>9s} {'self%':>7s} "
        f"{'cpu/wall':>9s} {'peakRSS':>9s} {'rssΔ':>8s}",
        "-" * 116,
    ]
    rows = sorted(_records.items(), key=lambda kv: kv[1]["self"], reverse=True)
    for name, rec in rows[:top]:
        util = 100.0 * rec["cpu"] / max(rec["total"], 1e-9)
        lines.append(
            f"{name[:48]:48s} {rec['calls']:6d} {rec['total']:9.3f} {rec['self']:9.3f} "
            f"{100.0 * rec['self'] / max(wall_total, 1e-9):6.1f}% {util:8.1f}% "
            f"{rec['rssPeak']:8.1f}M {rec['rssDelta']:7.1f}M"
        )
    if len(rows) > top:
        lines.append(f"... {len(rows) - top} more spans (see the markdown report)")
    return "\n".join(lines)


def report(out_dir: str = "", title: str = "VocalB1 performance report", top: int = 40):
    """Print the summary and write ``perf_report.md`` + ``perf_timeline.tsv``.

    Returns the report path (empty string when the probe is disabled).
    """
    if not ENABLED:
        return ""
    text = summary_text(top=top)
    print(text, flush=True)

    if not out_dir:
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        out_dir = os.path.join(root, "_output", "perf")
    os.makedirs(out_dir, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    md_path = os.path.join(out_dir, f"perf_report_{stamp}.md")

    wall_total = elapsed()
    cpu_total = time.process_time()
    rows = sorted(_records.items(), key=lambda kv: kv[1]["self"], reverse=True)
    with open(md_path, "w", encoding="utf-8") as fh:
        fh.write(f"# {title}\n\n")
        fh.write(f"- generated: {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
        fh.write(f"- python {_process_info.get('python')}, pid {_process_info.get('pid')}, "
                 f"{_process_info.get('cpuCount')} CPU threads\n")
        fh.write(f"- total wall {wall_total:.3f}s, process CPU {cpu_total:.3f}s "
                 f"({100.0 * cpu_total / max(wall_total, 1e-9):.1f}% of wall)\n")
        fh.write(f"- RSS start {_process_info.get('startRssMb', 0.0):.1f} MB, "
                 f"peak {_process_peak[0]:.1f} MB, end {_read_rss_mb():.1f} MB\n\n")
        fh.write("Sorted by self time: `self` excludes the child spans, so it is the "
                 "time genuinely spent inside that block.\n\n")
        fh.write("| span | calls | total (s) | self (s) | self % | cpu/wall | peak RSS (MB) | RSS Δ (MB) |\n")
        fh.write("|---|---:|---:|---:|---:|---:|---:|---:|\n")
        for name, rec in rows:
            util = 100.0 * rec["cpu"] / max(rec["total"], 1e-9)
            fh.write(f"| `{name}` | {rec['calls']} | {rec['total']:.3f} | {rec['self']:.3f} | "
                     f"{100.0 * rec['self'] / max(wall_total, 1e-9):.1f}% | {util:.1f}% | "
                     f"{rec['rssPeak']:.1f} | {rec['rssDelta']:.1f} |\n")
        fh.write("\n## Timeline\n\n")
        events = [(_events[i][0], f"mark: {_events[i][1]}") for i in range(len(_events))]
        fh.write("```\n")
        for t, label in sorted(events)[:200]:
            fh.write(f"{t:8.3f}s  {label}\n")
        fh.write("```\n")

    tsv_path = os.path.join(out_dir, f"perf_timeline_{stamp}.tsv")
    with open(tsv_path, "w", encoding="utf-8") as fh:
        fh.write("name\tcalls\ttotal_s\tself_s\tcpu_s\trss_peak_mb\trss_delta_mb\n")
        for name, rec in rows:
            fh.write(f"{name}\t{rec['calls']}\t{rec['total']:.4f}\t{rec['self']:.4f}\t"
                     f"{rec['cpu']:.4f}\t{rec['rssPeak']:.2f}\t{rec['rssDelta']:.2f}\n")
    print(f"[perf] report: {md_path}", flush=True)
    return md_path


def reset():
    """Clear all collected spans (mostly useful for repeated runs in one process)."""
    global _t0
    with _lock:
        _records.clear()
        _events.clear()
        _stack.clear()
    _rss_samples.clear()
    _t0 = time.perf_counter()
