# -*- coding: utf-8 -*-
"""
pit_editor.py -- PIT pitch contour / lyric segment sketch editor (pygame)

Features (sketch version):
  [Library panel] Lists every wav in the current voice library directory on the left;
                  click = select an audio to place (click again to cancel), wheel = scroll
  [Lyric area] One color block per segment; drag the middle = move the whole block,
               drag the left/right edge = change beat_start/beat_end;
               with an audio selected, click empty lyric area = add a new note
               (1 beat by default, dst_pitch taken from the PIT line at the drop point);
               right-click a block = delete that segment
  [PIT area] Drag nodes (x = beat, y = pitch, snapped to semitones); double-click empty
             space or press N = create a node;
             right-click a node / press Delete = delete node (keep at least 2);
             right-click an empty line segment = cut (creates a gap; the gap keeps the
             spliced original pitch);
             square node = line break end, drag it apart to form a gap;
             two nodes at the same beat = hard cut (red vertical line)
  Space = render current edits to _output/editor_preview.wav(16bit) and preview
          (press again to stop)
  S = save edit_pitch.json (VocalB1.py untouched)  R = reload  Esc = cancel selection/pending/drag

Data source: imports SEGMENTS / PITCH_LINE / BPM / TIME_SIGNATURE from VocalB1.py in the same directory

Run:
    C:\\Python312\\python.exe pit_editor.py
Selftest (headless, for CI):
    C:\\Python312\\python.exe pit_editor.py --selftest
Headless render of the preview wav (verifies the synthesis chain):
    C:\\Python312\\python.exe pit_editor.py --render
"""

import json
import os
import sys
import time

# Ensure the working path used to import VocalB1 matches this file
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
if _BASE_DIR not in sys.path:
    sys.path.insert(0, _BASE_DIR)

import pygame

from VocalB1Synthesizer import _parse_beat_pos
from vocal_lib.pitch import _parse_semitones

# ----------------------------------------------------------------------
# Layout constants (hard region bounds: PIT area / lyric area never overlap each other)
# ----------------------------------------------------------------------
WIN_W, WIN_H = 1360, 720
TOP_H = 40                     # top info bar
LIB_W = 168                    # left voice library panel width (hard bound)
AXIS_L = LIB_W + 60            # time axis left bound (60px reserved for note name labels)
RIGHT_PAD = 24
LYRIC_H = 130                  # fixed lyric area height (hard bound)
LYRIC_TOP = WIN_H - LYRIC_H
PIT_TOP = TOP_H + 8
PIT_BOTTOM = LYRIC_TOP - 14    # PIT area hard bottom bound
BLOCK_H = 56                   # lyric block height
EDGE_HIT = 8                   # block edge hit width px
NODE_R = 7                     # node radius
MIN_SEG_LEN = 0.25             # minimum segment length (beats)
NEW_SEG_LEN = 1.0              # default length of a newly added note (beats)
MIN_NODES = 2
LIB_ITEM_H = 22                # library list item height
LIB_LIST_TOP = TOP_H + 30      # list top (below the title)
LIB_LIST_BOTTOM = WIN_H - 10

NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']

C_BG = (24, 26, 31)
C_PANEL = (32, 35, 42)
C_GRID = (45, 49, 58)
C_GRID_BAR = (70, 76, 88)
C_TEXT = (220, 224, 232)
C_TEXT_DIM = (150, 156, 168)
C_PIT_LINE = (255, 196, 84)
C_NODE = (255, 255, 255)
C_NODE_EDGE = (255, 150, 40)
C_NODE_SEL = (90, 200, 255)
C_HARD_CUT = (240, 90, 90)
C_BLOCK = (62, 110, 170)
C_BLOCK_EDGE = (130, 190, 255)
C_BLOCK_TEXT = (255, 255, 255)
C_LIB_BG = (28, 30, 36)
C_LIB_ITEM = (44, 48, 57)
C_LIB_SEL = (62, 110, 170)
C_PREVIEW = (130, 220, 140)
C_GAP = (150, 110, 90)         # cut gap dashed line
KEEP = "KEEP"                  # matches the VocalB1Synthesizer._apply_pitch_line sentinel
PREVIEW_WAV = os.path.join(_BASE_DIR, '_output', 'editor_preview.wav')


def st_to_name(st):
    """Semitones relative to C4 -> scientific note name (st=0 -> C4)."""
    n = int(round(st))
    return f"{NOTE_NAMES[n % 12]}{n // 12 + 4}"


def lyric_label(audio_path):
    """_で.wav -> で (label shown on the block)."""
    name = os.path.splitext(os.path.basename(audio_path))[0]
    return name.lstrip('_') or name


class Editor:
    def __init__(self):
        import VocalB1
        self.mod = VocalB1
        # pending library audio (not reset by reload, so the choice survives a reload)
        self.pending_audio = None      # full path of the selected wav
        self.lib_scroll = 0            # library list scroll offset (in items)
        self.drag = None
        self.sel_node = None
        self.mouse_beat = None
        self.mouse_st = None
        self.mouse_pos = (0, 0)
        self.dirty = False          # unrendered edits pending
        self.status = ("Space = preview render; right-click line = cut; N/double-click = add node; "
                       "pick audio on left -> place in lyric area; S = save")
        self.status_until = 0.0
        self._last_click = (0.0, -1, -1)   # time, x, y
        self.reload()

    # ---------------- Data ----------------
    def reload(self):
        m = self.mod
        self.bpm = float(m.BPM)
        self.bpb = float(m.TIME_SIGNATURE[0])
        self.segments = []
        for s in m.SEGMENTS:
            self.segments.append({
                'audio': s['audio'],
                'label': lyric_label(s['audio']),
                'beat_start': float(_parse_beat_pos(s['beat_start'], self.bpb)),
                'beat_end': float(_parse_beat_pos(s['beat_end'], self.bpb)),
                'src_pitch': str(s['src_pitch']),
                'dst_pitch': str(s['dst_pitch']),
            })
        self._scan_library()
        if self.pending_audio not in self.library:
            self.pending_audio = None
        # Nodes are mutable lists [beat, st, break_before];
        # break_before=True = line break end (gap to the previous node, exports the KEEP sentinel)
        # Sort key (beat, break flag): at the same beat normal nodes first, break ends last
        self.nodes = []
        pending_break = False
        for pos, val in m.PITCH_LINE:
            if val is None:
                continue   # sketch version does not edit None (rest) nodes
            if val == KEEP:
                pending_break = True
                continue
            self.nodes.append([
                float(_parse_beat_pos(pos, self.bpb)),
                int(round(_parse_semitones(val))),
                pending_break,
            ])
            pending_break = False
        if len(self.nodes) < MIN_NODES:
            raise ValueError("PITCH_LINE has fewer than %d usable nodes" % MIN_NODES)
        self._sort_nodes()
        self.dirty = False
        self._update_view()
        self.drag = None
        self.sel_node = None
        self.set_status("Reloaded from VocalB1.py")

    def _update_view(self):
        ends = [s['beat_end'] for s in self.segments] + [n[0] for n in self.nodes]
        starts = [s['beat_start'] for s in self.segments] + [n[0] for n in self.nodes]
        self.b0 = min(1.0, min(starts)) - 0.5
        self.b1 = max(ends) + 1.0
        sts = [n[1] for n in self.nodes]
        self.st_lo = min(sts) - 4
        self.st_hi = max(sts) + 4

    def set_status(self, text, ms=2500):
        self.status = text
        self.status_until = time.time() + ms / 1000.0

    def _sort_nodes(self):
        """Sort by beat; at the same beat normal nodes come first and break ends last (stable key)."""
        self.nodes.sort(key=lambda n: (n[0], 1 if n[2] else 0))

    def build_pitch_line(self):
        """PITCH_LINE for export/render: insert a KEEP sentinel before a break end (the gap keeps the spliced original f0)."""
        out = []
        for n in self.nodes:
            if n[2]:
                out.append([round(n[0], 4), KEEP])
            out.append([round(n[0], 4), st_to_name(n[1])])
        return out

    def _scan_library(self):
        """Scan all wav files in the directory of the first segment's audio, sorted by file name."""
        if self.segments:
            lib_dir = os.path.dirname(self.segments[0]['audio'])
        else:
            lib_dir = os.path.dirname(self.mod.SEGMENTS[0]['audio'])
        self.lib_dir = lib_dir
        wavs = []
        if os.path.isdir(lib_dir):
            for fn in os.listdir(lib_dir):
                if fn.lower().endswith('.wav'):
                    wavs.append(os.path.join(lib_dir, fn))
        wavs.sort(key=lambda p: os.path.basename(p))
        self.library = wavs

    # ---------------- Coordinate conversion ----------------
    def beat_to_x(self, b):
        return AXIS_L + (b - self.b0) / (self.b1 - self.b0) * (
            WIN_W - AXIS_L - RIGHT_PAD)

    def x_to_beat(self, x):
        return self.b0 + (x - AXIS_L) / (WIN_W - AXIS_L - RIGHT_PAD) * (
            self.b1 - self.b0)

    def st_to_y(self, st):
        return PIT_BOTTOM - (st - self.st_lo) / (self.st_hi - self.st_lo) * (
            PIT_BOTTOM - PIT_TOP)

    def y_to_st(self, y):
        return self.st_lo + (PIT_BOTTOM - y) / (PIT_BOTTOM - PIT_TOP) * (
            self.st_hi - self.st_lo)

    def block_rect(self, seg):
        x0 = int(self.beat_to_x(seg['beat_start']))
        x1 = int(self.beat_to_x(seg['beat_end']))
        y0 = LYRIC_TOP + (LYRIC_H - BLOCK_H) // 2
        return pygame.Rect(x0, y0, max(2, x1 - x0), BLOCK_H)

    # ---------------- Hit testing ----------------
    def hit_node(self, x, y):
        """Return the nearest node reference within the hit radius."""
        best, best_d = None, NODE_R + 3
        for n in self.nodes:
            d = ((self.beat_to_x(n[0]) - x) ** 2 +
                 (self.st_to_y(n[1]) - y) ** 2) ** 0.5
            if d <= best_d:
                best, best_d = n, d
        return best

    def hit_block(self, x, y):
        """Return (idx, 'l'|'r'|'move') or None. Edges take priority over the block body."""
        for i in range(len(self.segments) - 1, -1, -1):
            r = self.block_rect(self.segments[i])
            if not r.collidepoint(x, y):
                continue
            if abs(x - r.left) <= EDGE_HIT:
                return (i, 'l')
            if abs(x - r.right) <= EDGE_HIT:
                return (i, 'r')
            return (i, 'move')
        return None

    # ---------------- Library list ----------------
    def lib_visible_count(self):
        return max(1, (LIB_LIST_BOTTOM - LIB_LIST_TOP) // LIB_ITEM_H)

    def lib_clamp_scroll(self):
        max_scroll = max(0, len(self.library) - self.lib_visible_count())
        self.lib_scroll = max(0, min(self.lib_scroll, max_scroll))

    def lib_item_rect(self, i):
        return pygame.Rect(8, LIB_LIST_TOP + i * LIB_ITEM_H,
                           LIB_W - 16, LIB_ITEM_H - 3)

    def lib_hit(self, x, y):
        """Hit a library list item -> return the full wav path, else None."""
        if x >= LIB_W or y < LIB_LIST_TOP or y > LIB_LIST_BOTTOM:
            return None
        self.lib_clamp_scroll()
        i = self.lib_scroll + (y - LIB_LIST_TOP) // LIB_ITEM_H
        if 0 <= i < len(self.library):
            return self.library[i]
        return None

    def lib_scroll_to_selected(self):
        if self.pending_audio is None:
            return
        try:
            i = self.library.index(self.pending_audio)
        except ValueError:
            return
        if i < self.lib_scroll:
            self.lib_scroll = i
        elif i >= self.lib_scroll + self.lib_visible_count():
            self.lib_scroll = i - self.lib_visible_count() + 1

    # ---------------- Add / remove lyric segments ----------------
    def pitch_name_at_beat(self, b):
        """PITCH_LINE left-endpoint semantics: return the note name in effect at beat b,
        None if uncovered. The gap between a break end and its previous node counts as uncovered."""
        name = None
        for n in self.nodes:
            if n[0] <= b + 1e-9:
                name = st_to_name(n[1])
            else:
                break
        for i in range(1, len(self.nodes)):
            if self.nodes[i][2] and \
                    self.nodes[i - 1][0] + 1e-9 < b < self.nodes[i][0] - 1e-9:
                return None
        return name

    def add_segment(self, audio_path, beat_start):
        """Place a new note at beat_start (NEW_SEG_LEN beats by default), inserted in time order."""
        beat_start = round(min(max(float(beat_start), self.b0 + 0.01),
                               self.b1 - 0.01), 4)
        beat_end = round(beat_start + NEW_SEG_LEN, 4)   # may exceed the view -> auto expand
        src = (self.segments[0]['src_pitch'] if self.segments
               else str(self.mod.SEGMENTS[0]['src_pitch']))
        dst = self.pitch_name_at_beat(beat_start) or src
        seg = {'audio': audio_path, 'label': lyric_label(audio_path),
               'beat_start': beat_start, 'beat_end': beat_end,
               'src_pitch': src, 'dst_pitch': dst}
        self.segments.append(seg)
        self.segments.sort(key=lambda s: s['beat_start'])
        self._update_view()      # auto-fit the view (expands horizontally when appending at the end)
        self.dirty = True
        self.set_status(
            f"Added note {seg['label']} ({src}->{dst}) @ {beat_start:g} beats; "
            f"right-click block to delete", 4000)
        return seg

    def remove_segment(self, idx):
        seg = self.segments.pop(idx)
        self._update_view()
        self.dirty = True
        self.set_status(f"Deleted segment {seg['label']}", 3000)
        return seg

    # ---------------- PIT nodes: add / cut ----------------
    def add_node_at(self, x, y):
        """Create a normal node at pixel coordinates (shared by double-click / N key)."""
        b = min(max(self.x_to_beat(x), self.b0), self.b1)
        st = int(round(min(max(self.y_to_st(y), self.st_lo), self.st_hi)))
        node = [round(b, 4), st, False]
        self.nodes.append(node)
        self._sort_nodes()
        self.dirty = True
        self.sel_node = node
        self.set_status(f"Added node {st_to_name(st)} @ {b:g} beats")
        return node

    def cut_line_at(self, x, y):
        """Right-click a line segment: cut at the clicked beat and insert a same-beat
        pair [normal end, break end]. Initially zero width; drag the square break end
        apart to open a gap (the gap keeps the spliced original f0)."""
        b = min(max(self.x_to_beat(x), self.b0 + 0.01), self.b1 - 0.01)
        for i in range(len(self.nodes) - 1):
            n0, n1 = self.nodes[i], self.nodes[i + 1]
            if n1[2]:                    # already a gap, do not cut again
                continue
            if n0[0] + 1e-9 < b < n1[0] - 1e-9:
                u = (b - n0[0]) / (n1[0] - n0[0])
                w = 3.0 * u ** 2 - 2.0 * u ** 3      # same curve as rendering/synthesis
                st = int(round(n0[1] + (n1[1] - n0[1]) * w))
                end_a = [round(b, 4), st, False]    # left segment end
                end_b = [round(b, 4), st, True]     # right segment break end (square)
                self.nodes += [end_a, end_b]
                self._sort_nodes()
                self.dirty = True
                self.sel_node = end_b
                self.set_status(
                    f"Cut at beat {b:g}; drag the square node to open a gap; "
                    f"the gap keeps the spliced pitch",
                    4000)
                return True
        self.set_status("No solid line to cut here (right-click on the line between two nodes)",
                        2500)
        return False

    # ---------------- Events ----------------
    def handle(self, ev):
        if ev.type == pygame.MOUSEBUTTONDOWN:
            x, y = ev.pos
            if ev.button == 1:
                self._on_left_down(x, y)
            elif ev.button == 3:
                self._on_right_down(x, y)
            elif ev.button in (4, 5) and x < LIB_W:
                # legacy compatibility: the wheel only works over the library panel
                self.lib_scroll += 3 if ev.button == 5 else -3
                self.lib_clamp_scroll()
        elif ev.type == pygame.MOUSEWHEEL:
            if pygame.mouse.get_pos()[0] < LIB_W:
                self.lib_scroll -= ev.y * 3
                self.lib_clamp_scroll()
        elif ev.type == pygame.MOUSEBUTTONUP and ev.button == 1:
            if self.drag:
                self.dirty = True
            self.drag = None
        elif ev.type == pygame.MOUSEMOTION:
            self._on_motion(ev.pos[0], ev.pos[1])
        elif ev.type == pygame.KEYDOWN:
            if ev.key in (pygame.K_DELETE, pygame.K_BACKSPACE):
                self._delete_selected()
            elif ev.key == pygame.K_s:
                self.save()
            elif ev.key == pygame.K_r:
                self.reload()
            elif ev.key == pygame.K_n:
                if self.mouse_st is not None:    # only when the mouse is in the PIT area
                    self.add_node_at(*self.mouse_pos)
            elif ev.key == pygame.K_ESCAPE:
                self.drag = None
                self.sel_node = None
                self.pending_audio = None
                self.set_status("Pending placement cancelled", 1500)

    def _is_double_click(self, x, y):
        now = time.time()
        t0, x0, y0 = self._last_click
        self._last_click = (now, x, y)
        return now - t0 < 0.35 and abs(x - x0) < 5 and abs(y - y0) < 5

    def _on_left_down(self, x, y):
        double = self._is_double_click(x, y)
        # library panel: click to pick a pending audio (click the same item again to cancel)
        if x < LIB_W and y > TOP_H:
            hit = self.lib_hit(x, y)
            if hit is not None:
                if self.pending_audio == hit:
                    self.pending_audio = None
                    self.set_status("Pending placement cancelled", 1500)
                else:
                    self.pending_audio = hit
                    self.set_status(
                        f"Pending: {lyric_label(hit)} -> click empty lyric area to add "
                        f"(right-click block to delete, Esc to cancel)", 5000)
            return
        # PIT area
        if PIT_TOP <= y <= PIT_BOTTOM and x >= AXIS_L - 20:
            node = self.hit_node(x, y)
            if node is not None:
                self.drag = ('node', node)
                self.sel_node = node
                return
            if double:
                node = self.add_node_at(x, y)
                self.drag = ('node', node)           # grab it right away after a double-click
                return
            self.sel_node = None
            return
        # lyric area
        if LYRIC_TOP <= y <= WIN_H and x >= LIB_W:
            hit = self.hit_block(x, y)
            if hit:
                idx, where = hit
                if where == 'move':
                    off = self.x_to_beat(x) - self.segments[idx]['beat_start']
                    self.drag = ('seg_move', idx, off)
                else:
                    self.drag = ('seg_edge', idx, where)
                return
            # empty area: with a pending audio -> add a new note
            if self.pending_audio is not None and x >= AXIS_L:
                self.add_segment(self.pending_audio, self.x_to_beat(x))
            elif self.pending_audio is None:
                self.set_status("Pick an audio in the left library first, then place it here", 2000)

    def _on_right_down(self, x, y):
        # PIT area: right-click a node = delete node; right-click empty line segment = cut
        if PIT_TOP <= y <= PIT_BOTTOM and x >= AXIS_L - 20:
            node = self.hit_node(x, y)
            if node is not None:
                if len(self.nodes) > MIN_NODES:
                    self.nodes.remove(node)
                    if self.sel_node is node:
                        self.sel_node = None
                    self.dirty = True
                    self.set_status("Node deleted")
                else:
                    self.set_status(f"Keep at least {MIN_NODES} nodes", 2000)
                return
            self.cut_line_at(x, y)
            return
        # lyric area: delete segment
        if LYRIC_TOP <= y <= WIN_H and x >= LIB_W:
            hit = self.hit_block(x, y)
            if hit and self.segments:
                self.remove_segment(hit[0])

    def _delete_selected(self):
        n = self.sel_node
        if n is not None and n in self.nodes and len(self.nodes) > MIN_NODES:
            self.nodes.remove(n)
            self.sel_node = None
            self.dirty = True
            self.set_status("Node deleted")

    def _on_motion(self, x, y):
        self.mouse_pos = (x, y)
        b = self.x_to_beat(x)
        self.mouse_beat = min(max(b, self.b0), self.b1)
        if PIT_TOP <= y <= PIT_BOTTOM:
            self.mouse_st = min(max(self.y_to_st(y), self.st_lo), self.st_hi)
        else:
            self.mouse_st = None
        if not self.drag:
            return
        kind = self.drag[0]
        if kind == 'node':
            node = self.drag[1]
            node[0] = round(min(max(b, self.b0 + 0.01), self.b1 - 0.01), 4)
            node[1] = int(round(min(max(self.y_to_st(y), self.st_lo), self.st_hi)))
            self._sort_nodes()
        elif kind == 'seg_move':
            _, idx, off = self.drag
            seg = self.segments[idx]
            length = seg['beat_end'] - seg['beat_start']
            new_start = min(max(b - off, self.b0 + 0.01),
                            self.b1 - length - 0.01)
            seg['beat_start'] = round(new_start, 4)
            seg['beat_end'] = round(new_start + length, 4)
        elif kind == 'seg_edge':
            _, idx, where = self.drag
            seg = self.segments[idx]
            v = round(min(max(b, self.b0 + 0.01), self.b1 - 0.01), 4)
            if where == 'l':
                seg['beat_start'] = min(v, seg['beat_end'] - MIN_SEG_LEN)
            else:
                seg['beat_end'] = max(v, seg['beat_start'] + MIN_SEG_LEN)

    # ---------------- Save ----------------
    def save(self):
        data = {
            'source': 'VocalB1.py',
            'bpm': self.bpm,
            'time_signature': [int(self.bpb), int(self.mod.TIME_SIGNATURE[1])],
            'lib_dir': self.lib_dir,
            'segments': [
                {'audio': os.path.basename(s['audio']),
                 'src_pitch': s['src_pitch'],
                 'dst_pitch': s['dst_pitch'],
                 'beat_start': round(s['beat_start'], 3),
                 'beat_end': round(s['beat_end'], 3)}
                for s in self.segments
            ],
            'pitch_line': [[round(p, 3), v] for p, v in
                           self.build_pitch_line()],
        }
        out = os.path.join(_BASE_DIR, 'edit_pitch.json')
        with open(out, 'w', encoding='utf-8') as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        self.set_status(f"Saved {os.path.basename(out)} (VocalB1.py untouched)", 4000)
        return out

    # ---------------- Render preview ----------------
    def render_audio(self):
        """Run the full synthesis pipeline on the current edits -> editor_preview.wav (16bit, for preview).
        Segments only carry the 5 required fields; all other global parameters come from VocalB1.py."""
        if len(self.segments) < 2:
            raise ValueError("At least 2 segments are required to render (add more notes)")
        m = self.mod
        segs = [dict(audio=s['audio'], src_pitch=s['src_pitch'],
                     dst_pitch=s['dst_pitch'],
                     beat_start=s['beat_start'], beat_end=s['beat_end'])
                for s in self.segments]
        from VocalB1Synthesizer import synthesize
        return synthesize(
            segs, PREVIEW_WAV,
            pitch_line=self.build_pitch_line(),
            follow_ratio=m.FOLLOW_RATIO, band_ratios=m.BAND_RATIOS,
            breathy_thr=m.BREATHY_THR, band_width_hz=m.BAND_WIDTH_HZ,
            ap_scale=m.AP_SCALE, ap_max=m.AP_MAX,
            sample_rate=m.SAMPLE_RATE, frame_period=m.FRAME_PERIOD,
            join_normalize_sp=m.JOIN_NORMALIZE_SP,
            bpm=self.bpm,
            time_signature=(int(self.bpb), int(m.TIME_SIGNATURE[1])),
            dur_edge_frames=m.DUR_EDGE_FRAMES,
            dur_jitter_smooth=m.DUR_JITTER_SMOOTH,
            dur_jitter_decay_ms=m.DUR_JITTER_DECAY_MS,
            dur_tail_jitter_ratio=m.DUR_TAIL_JITTER_RATIO,
            match_rms_to=m.MATCH_RMS_TO, model_type=m.MODEL_TYPE,
            sp_floor=m.SP_FLOOR,
            add_breath=m.ADD_BREATH, breath_gain=m.BREATH_GAIN,
            breath_follow_pitch=m.BREATH_FOLLOW_PITCH,
            breath_pv_frame_ms=m.BREATH_PV_FRAME_MS,
            breath_pv_hop_ms=m.BREATH_PV_HOP_MS,
            breath_shape_to_main=m.BREATH_SHAPE_TO_MAIN,
            breath_shape_bands=m.BREATH_SHAPE_BANDS,
            breath_shape_gain_min=m.BREATH_SHAPE_GAIN_MIN,
            breath_shape_gain_max=m.BREATH_SHAPE_GAIN_MAX,
            plosive_ratio=m.PLOSIVE_RATIO,
            plosive_min_grad_db=m.PLOSIVE_MIN_GRAD_DB,
            plosive_min_crest=m.PLOSIVE_MIN_CREST,
            plosive_max_valley=m.PLOSIVE_MAX_VALLEY,
            plosive_gain=m.PLOSIVE_GAIN, plosive_smooth=m.PLOSIVE_SMOOTH,
            plosive_margin_head=m.PLOSIVE_MARGIN_HEAD,
            plosive_margin_tail=m.PLOSIVE_MARGIN_TAIL,
            plosive_keep_burst=m.PLOSIVE_KEEP_BURST,
            plosive_env_transplant=m.PLOSIVE_ENV_TRANSPLANT,
            plosive_env_head=m.PLOSIVE_ENV_HEAD,
            plosive_env_tail=m.PLOSIVE_ENV_TAIL,
            plosive_env_gain_min=m.PLOSIVE_ENV_GAIN_MIN,
            plosive_env_gain_max=m.PLOSIVE_ENV_GAIN_MAX,
            edge_fade_ms=m.EDGE_FADE_MS,
            # pygame.mixer(SDL_mixer) does not support 24bit PCM, so the preview build is fixed at 16bit
            bit_depth=16,
            clean_intermediate=m.CLEAN_INTERMEDIATE, verbose=False)


# ----------------------------------------------------------------------
# Rendering
# ----------------------------------------------------------------------
class App:
    def __init__(self, font, font_path=None):
        pygame.display.set_caption("PIT Editor  (Space: preview / right-click line: cut / N: add node / pick library audio / S: save)")
        self.screen = pygame.display.set_mode((WIN_W, WIN_H))
        self.clock = pygame.time.Clock()
        self.ed = Editor()
        self.font = font
        if font_path:   # derive small / bold fonts from the same file to avoid SysFont
            self.font_s = pygame.font.Font(font_path, 13)
            self.font_b = pygame.font.Font(font_path, 17)
            self.font_b.set_bold(True)
        else:
            self.font_s = self.font
            self.font_b = self.font
        # preview playback state (a mixer failure must not block editing; errors go to the status bar, never swallowed silently)
        self.sound = None
        self.channel = None
        self.mixer_ok = False
        self.mixer_err = ""
        try:
            pygame.mixer.init(frequency=int(self.ed.mod.SAMPLE_RATE),
                              size=-16, channels=2)
            self.mixer_ok = True
        except Exception as e:
            self.mixer_err = str(e)

    def toggle_preview(self):
        """Space: stop playback / re-render on demand and preview."""
        ed = self.ed
        if self.channel is not None and self.channel.get_busy():
            self.channel.stop()
            ed.set_status("Preview stopped", 1500)
            return
        if not self.mixer_ok:
            ed.set_status(f"Audio device init failed, cannot preview: {self.mixer_err}",
                          6000)
            return
        if ed.dirty or self.sound is None or not os.path.exists(PREVIEW_WAV):
            ed.set_status("Rendering... first synthesis may take tens of seconds; window is unresponsive",
                          3600 * 1000)
            self.draw()
            pygame.display.flip()
            try:
                path = ed.render_audio()
            except Exception as e:
                ed.set_status(f"Render failed: {type(e).__name__}: {e}", 10000)
                return
            ed.dirty = False
            try:
                self.sound = pygame.mixer.Sound(path)
            except Exception as e:
                ed.set_status(f"wav saved but cannot play: {e}", 8000)
                return
        self.channel = self.sound.play()
        if self.channel is None:
            ed.set_status("Playback failed: no available channel", 5000)
            return
        ed.set_status(f"Previewing: {os.path.basename(PREVIEW_WAV)} (Space to stop)",
                      10000)

    def _text(self, s, x, y, fnt=None, color=C_TEXT, anchor='tl'):
        img = (fnt or self.font_s).render(str(s), True, color)
        r = img.get_rect()
        if anchor == 'tl': r.topleft = (x, y)
        elif anchor == 'tr': r.topright = (x, y)
        elif anchor == 'center': r.center = (x, y)
        elif anchor == 'bl': r.bottomleft = (x, y)
        self.screen.blit(img, r)

    def draw(self):
        sc = self.screen
        sc.fill(C_BG)
        # panel split (axis areas always start at LIB_W; the library panel owns the left column)
        pygame.draw.rect(sc, C_PANEL, (0, 0, WIN_W, TOP_H))
        pygame.draw.rect(sc, (28, 30, 36),
                         (LIB_W, LYRIC_TOP, WIN_W - LIB_W, LYRIC_H))
        pygame.draw.line(sc, C_GRID_BAR, (LIB_W, LYRIC_TOP),
                         (WIN_W, LYRIC_TOP), 2)
        pygame.draw.line(sc, C_GRID_BAR, (LIB_W, PIT_BOTTOM),
                         (WIN_W, PIT_BOTTOM), 1)

        self._draw_grid()
        self._draw_pitch_line()
        self._draw_blocks()
        self._draw_placement_preview()
        self._draw_library()
        self._draw_topbar()
        pygame.display.flip()

    def _draw_grid(self):
        sc = self.screen
        # vertical lines: per beat / per bar
        b = int(self.ed.b0)
        while b <= self.ed.b1 + 1:
            x = int(self.ed.beat_to_x(b))
            if b >= 1 and (b - 1) % int(self.ed.bpb) == 0:
                pygame.draw.line(sc, C_GRID_BAR, (x, PIT_TOP),
                                 (x, LYRIC_TOP - 2), 2)
                bar = (b - 1) // int(self.ed.bpb) + 1
                self._text(f"{bar}:1", x + 3, LYRIC_TOP - 16, self.font_s,
                          C_TEXT_DIM)
            else:
                pygame.draw.line(sc, C_GRID, (x, PIT_TOP),
                                 (x, LYRIC_TOP - 2), 1)
            b += 1
        # horizontal lines: per semitone; thick line + label for C
        st = int(self.ed.st_lo)
        while st <= self.ed.st_hi:
            y = int(self.ed.st_to_y(st))
            is_c = st % 12 == 0
            pygame.draw.line(sc, C_GRID_BAR if is_c else C_GRID,
                             (AXIS_L - 6, y), (WIN_W - RIGHT_PAD, y),
                             2 if is_c else 1)
            self._text(st_to_name(st), AXIS_L - 10, y, self.font_s,
                       C_TEXT_DIM if not is_c else C_TEXT, anchor='tr')
            st += 1
        # lyric area hint line
        self._text("Lyric segments (drag edge = length, drag body = move; "
                   "click empty space to place selected audio, right-click to delete)",
                   LIB_W + 12, LYRIC_TOP + 6, self.font_s, C_TEXT_DIM)

    def _dashed_line(self, p0, p1, color, width=2, dash=8, gap=6):
        """Simple evenly spaced dashed line (used for gap hints)."""
        import math
        x0, y0 = p0
        x1, y1 = p1
        dist = math.hypot(x1 - x0, y1 - y0)
        if dist < 1:
            return
        k = 0
        while k * (dash + gap) < dist:
            t0 = k * (dash + gap) / dist
            t1 = min((k * (dash + gap) + dash) / dist, 1.0)
            pygame.draw.line(self.screen, color,
                             (x0 + (x1 - x0) * t0, y0 + (y1 - y0) * t0),
                             (x0 + (x1 - x0) * t1, y0 + (y1 - y0) * t1), width)
            k += 1

    def _draw_pitch_line(self):
        sc = self.screen
        pts = self.ed.nodes
        for i in range(len(pts) - 1):
            n0, n1 = pts[i], pts[i + 1]
            b0, st0, br0 = n0
            b1, st1, br1 = n1
            x0, y0 = self.ed.beat_to_x(b0), self.ed.st_to_y(st0)
            x1, y1 = self.ed.beat_to_x(b1), self.ed.st_to_y(st1)
            if b1 - b0 < 1e-6:
                # same beat: break pairs draw nothing; normal hard-cut pairs draw a red vertical line
                if not br1:
                    pygame.draw.line(sc, C_HARD_CUT, (x0, y0), (x1, y1), 2)
                continue
            if br1:
                # cut gap: brown dashed line (synthesis keeps the spliced original pitch in this range)
                self._dashed_line((x0, y0), (x1, y1), C_GAP, 2)
                continue
            prev = (x0, y0)
            for k in range(1, 49):
                u = k / 48.0
                w = 3.0 * u ** 2 - 2.0 * u ** 3     # cubic Bézier (smoothstep)
                bv = b0 + (b1 - b0) * u
                sv = st0 + (st1 - st0) * w
                cur = (self.ed.beat_to_x(bv), self.ed.st_to_y(sv))
                pygame.draw.line(sc, C_PIT_LINE, prev, cur, 3)
                prev = cur
        # nodes: normal = circle; break end = square
        for n in pts:
            x, y = self.ed.beat_to_x(n[0]), self.ed.st_to_y(n[1])
            sel = (n is self.ed.sel_node)
            if n[2]:
                r = pygame.Rect(0, 0, NODE_R * 2 + 2, NODE_R * 2 + 2)
                r.center = (x, y)
                pygame.draw.rect(sc, C_NODE_SEL if sel else (235, 190, 150),
                                 r, border_radius=2)
                pygame.draw.rect(sc, C_GAP, r, 2, border_radius=2)
            else:
                pygame.draw.circle(sc, C_NODE_SEL if sel else C_NODE,
                                   (x, y), NODE_R)
                pygame.draw.circle(sc, C_NODE_SEL if sel else C_NODE_EDGE,
                                   (x, y), NODE_R, 2)

    def _draw_blocks(self):
        for i, s in enumerate(self.ed.segments):
            r = self.ed.block_rect(s)
            pygame.draw.rect(self.screen, C_BLOCK, r, border_radius=6)
            pygame.draw.rect(self.screen, C_BLOCK_EDGE, r, 2, border_radius=6)
            # left/right edge handles
            for cx in (r.left, r.right):
                pygame.draw.line(self.screen, C_BLOCK_EDGE,
                                 (cx, r.top + 8), (cx, r.bottom - 8), 2)
            self._text(s['label'], r.centerx, r.centery - 8, self.font_b,
                       C_BLOCK_TEXT, anchor='center')
            # narrow blocks only fit the beats; add src->dst only when wide enough
            sub_short = f"{s['beat_start']:g}~{s['beat_end']:g}"
            sub_full = f"{sub_short} · {s['src_pitch']}→{s['dst_pitch']}"
            sub = sub_full if self.font_s.size(sub_full)[0] <= r.w - 12 \
                else sub_short
            self._text(sub, r.centerx, r.centery + 11, self.font_s,
                       (215, 230, 250), anchor='center')

    def _draw_placement_preview(self):
        """Draw a translucent preview block when an audio is pending and the mouse is over empty lyric area."""
        ed = self.ed
        if ed.pending_audio is None:
            return
        x, y = ed.mouse_pos
        if not (LYRIC_TOP <= y <= WIN_H and x >= AXIS_L):
            return
        if ed.hit_block(x, y) is not None:
            return
        b = max(ed.mouse_beat or ed.b0, ed.b0 + 0.01)
        x0 = int(ed.beat_to_x(b))
        x1 = int(ed.beat_to_x(b + NEW_SEG_LEN))
        r = pygame.Rect(x0, LYRIC_TOP + (LYRIC_H - BLOCK_H) // 2,
                        max(2, x1 - x0), BLOCK_H)
        prev = pygame.Surface((r.w, r.h), pygame.SRCALPHA)
        pygame.draw.rect(prev, (*C_PREVIEW, 70), prev.get_rect(),
                         border_radius=6)
        pygame.draw.rect(prev, (*C_PREVIEW, 220), prev.get_rect(), 2,
                         border_radius=6)
        self.screen.blit(prev, r.topleft)
        dst = ed.pitch_name_at_beat(b) or ed.segments[0]['src_pitch']
        self._text(lyric_label(ed.pending_audio), r.centerx, r.centery - 8,
                   self.font_b, (20, 40, 24), anchor='center')
        self._text(f"->{dst} {b:g} beats", r.centerx, r.centery + 11,
                   self.font_s, (20, 60, 30), anchor='center')

    def _draw_library(self):
        ed = self.ed
        sc = self.screen
        pygame.draw.rect(sc, C_LIB_BG, (0, TOP_H, LIB_W, WIN_H - TOP_H))
        pygame.draw.line(sc, C_GRID_BAR, (LIB_W, TOP_H),
                         (LIB_W, WIN_H), 2)
        self._text("Library (pick -> place in lyric area)", 10, TOP_H + 8,
                   self.font_s, C_TEXT)
        # per-audio usage count (compared by full path)
        used = {}
        for s in ed.segments:
            used[s['audio']] = used.get(s['audio'], 0) + 1
        ed.lib_clamp_scroll()
        n_vis = ed.lib_visible_count()
        for vi in range(n_vis):
            i = ed.lib_scroll + vi
            if i >= len(ed.library):
                break
            path = ed.library[i]
            r = ed.lib_item_rect(vi)
            sel = (path == ed.pending_audio)
            pygame.draw.rect(sc, C_LIB_SEL if sel else C_LIB_ITEM, r,
                             border_radius=4)
            self._text(lyric_label(path), r.x + 8, r.centery,
                       self.font_s, C_TEXT, anchor='center')
            n = used.get(path, 0)
            if n:
                self._text(f"×{n}", r.right - 8, r.centery, self.font_s,
                           (170, 200, 240), anchor='center')
        # scrollbar
        total = len(ed.library)
        if total > n_vis:
            h_track = LIB_LIST_BOTTOM - LIB_LIST_TOP
            h_bar = max(20, int(h_track * n_vis / total))
            y_bar = LIB_LIST_TOP + int(
                (h_track - h_bar) * ed.lib_scroll / max(1, total - n_vis))
            pygame.draw.rect(sc, C_GRID_BAR,
                             (LIB_W - 5, y_bar, 3, h_bar), border_radius=2)

    def _draw_topbar(self):
        mb, ms = self.ed.mouse_beat, self.ed.mouse_st
        hint = f"Beat {mb:6.2f}" if mb is not None else ""
        if ms is not None:
            hint += f"   Pitch {st_to_name(ms)}"
        self._text(hint, 12, 12, self.font_s, C_TEXT)
        status = self.ed.status
        color = (120, 220, 140) if time.time() < self.ed.status_until else C_TEXT_DIM
        self._text(status, WIN_W // 2, 12, self.font_s, color, anchor='center')
        # preview status (persistent, left of BPM)
        if self.channel is not None and self.channel.get_busy():
            self._text("♪ Previewing · Space to stop", WIN_W - 150, 12,
                       self.font_s, (120, 220, 140), anchor='tr')
        elif self.sound is not None:
            if self.ed.dirty:
                self._text("● Edited · Space to re-render & preview", WIN_W - 150, 12,
                           self.font_s, (230, 170, 90), anchor='tr')
            else:
                self._text("Preview ready · Space to replay", WIN_W - 150, 12,
                           self.font_s, C_TEXT_DIM, anchor='tr')
        self._text(f"BPM {self.ed.bpm:g}  {int(self.ed.bpb)}/"
                   f"{int(self.ed.mod.TIME_SIGNATURE[1])}",
                   WIN_W - 12, 12, self.font_s, C_TEXT_DIM, anchor='tr')

    def run(self):
        running = True
        while running:
            for ev in pygame.event.get():
                if ev.type == pygame.QUIT:
                    running = False
                elif ev.type == pygame.KEYDOWN and ev.key == pygame.K_SPACE:
                    self.toggle_preview()
                else:
                    self.ed.handle(ev)
            if self.channel is not None and not self.channel.get_busy():
                self.channel = None     # playback finished naturally
            self.draw()
            self.clock.tick(60)
        if self.channel is not None:
            self.channel.stop()


def _get_font():
    # Do not use pygame.font.match_font/SysFont: on some Windows machines pygame 2.6.1
    # hits a known splitext(int) bug while enumerating registry fonts, so load by file path.
    # Returns (15pt font, font file path or None).
    fonts_dir = os.path.join(os.environ.get('WINDIR', r'C:\Windows'), 'Fonts')
    # YaHei/SimHei contain both Simplified Chinese and Japanese kana; msgothic is only a Japanese fallback (no Simplified Hanzi)
    for fname in ('msyh.ttc', 'simhei.ttf', 'ARIALUNI.ttf', 'meiryo.ttc',
                  'YuGothM.ttc', 'msgothic.ttc'):
        path = os.path.join(fonts_dir, fname)
        if os.path.exists(path):
            return pygame.font.Font(path, 15), path
    return pygame.font.Font(None, 18), None


def _selftest():
    """Headless selftest: exercise the full chain load / add-remove-drag / save / reload."""
    os.environ['SDL_VIDEODRIVER'] = 'dummy'
    pygame.init()
    pygame.display.set_mode((WIN_W, WIN_H))
    f, fp = _get_font()
    app = App(f, fp)
    ed = app.ed
    n0 = len(ed.nodes)
    # simulate a double-click in empty PIT area to add a node
    bx = ed.beat_to_x((ed.b0 + ed.b1) / 2)
    by = ed.st_to_y((ed.st_lo + ed.st_hi) / 2)
    ed._on_left_down(int(bx), int(by))   # first click
    ed._on_left_down(int(bx), int(by))   # double-click -> new node
    assert len(ed.nodes) == n0 + 1, "double-click add node failed"
    # drag that node
    new_n = ed.sel_node
    tx = ed.beat_to_x(min(ed.b1 - 0.5, new_n[0] + 0.8))
    ty = ed.st_to_y(new_n[1] + 2)
    ed._on_motion(int(tx), int(ty))
    assert abs(new_n[1] - int(round(ed.y_to_st(ty)))) <= 1, "node drag failed"
    # delete
    ed._delete_selected()
    assert len(ed.nodes) == n0, "delete node failed"
    # cut: find the first connected line segment with width and right-click its midpoint
    seg_i = next(i for i in range(len(ed.nodes) - 1)
                 if not ed.nodes[i + 1][2]
                 and ed.nodes[i + 1][0] - ed.nodes[i][0] > 0.5)
    na, nb = ed.nodes[seg_i], ed.nodes[seg_i + 1]
    cb = (na[0] + nb[0]) / 2.0
    cxk, cyk = int(ed.beat_to_x(cb)), int(ed.st_to_y((na[1] + nb[1]) / 2))
    assert ed.hit_node(cxk, cyk) is None, "cut point should not hit a node"
    n_before_cut = len(ed.nodes)
    assert ed.cut_line_at(cxk, cyk), "cut failed"
    assert len(ed.nodes) == n_before_cut + 2, "cut should insert two nodes"
    br_nodes = [n for n in ed.nodes if n[2]]
    assert len(br_nodes) == 1, "expected 1 break-end square node"
    pline = ed.build_pitch_line()
    ki = next(i for i, (p, v) in enumerate(pline) if v == KEEP)
    assert abs(pline[ki][0] - pline[ki + 1][0]) < 1e-6, "KEEP must share the beat with the note-name endpoint"
    assert pline[ki + 1][1] != KEEP
    # drag the square node 1 beat apart -> pitch_name_at_beat is None inside the gap
    br_node = br_nodes[0]
    end_a = ed.nodes[ed.nodes.index(br_node) - 1]
    br_node[0] = round(end_a[0] + 1.0, 4)
    ed._sort_nodes()
    gap_b = (end_a[0] + br_node[0]) / 2.0   # midpoint of the gap after dragging apart
    assert ed.pitch_name_at_beat(gap_b) is None, "pitch inside the gap should be uncovered"
    # synthesizer KEEP behavior (no audio dependency; spb computed from the module BPM)
    import numpy as np
    import VocalB1Synthesizer as VS
    spb = 60000.0 / (float(VS.BPM) * 5.0)
    n_out = int(round(8.0 * spb))
    base_f0 = np.full(n_out, 300.0)
    f0n, sil = VS._apply_pitch_line(
        base_f0, n_out, 44100, 5.0,
        [(1, 'G4'), (4, KEEP), (4, 'E4'), (7, 'E4')])
    f_gap = int(round(2.0 * spb))       # inside the beat 1~4 gap
    f_e4 = int(round(5.5 * spb))        # beat 4~7 E4 covered
    f_tail = int(round(7.5 * spb))      # uncovered after the last endpoint
    assert abs(f0n[f_gap] - 300.0) < 1e-9, "KEEP range must keep the original f0"
    assert abs(f0n[f_e4] - 329.628) < 1.0, "E4 segment after the gap should be covered"
    assert abs(f0n[f_tail] - 300.0) < 1e-9, "after the last endpoint the original f0 should be kept"
    assert not sil.any(), "KEEP is not a rest and should not be marked silent"
    # restore: remove the two nodes created by the cut
    ed.nodes.remove(br_node)
    ed.nodes.remove(end_a)
    assert len(ed.nodes) == n0
    # lyric block: drag the right edge longer (use the last segment to avoid overlapping blocks interfering with topmost hits)
    seg = ed.segments[-1]
    old_end = seg['beat_end']
    r = ed.block_rect(seg)
    ed._on_left_down(r.right - 2, r.centery)
    assert ed.drag and ed.drag[0] == 'seg_edge', "block edge not hit"
    ed._on_motion(r.right + 60, r.centery)
    assert seg['beat_end'] > old_end, "block length drag failed"
    # move the block body
    ed.drag = None
    ed._on_left_down(r.centerx, r.centery)
    assert ed.drag and ed.drag[0] == 'seg_move', "block body not hit"
    # library scan is non-empty
    assert len(ed.library) > len(ed.segments), "library wav scan failed"
    # UI chain: click the sidebar to pick a pending audio -> click empty lyric area to add a new note
    used = {s['audio'] for s in ed.segments}
    cand = next(p for p in ed.library if p not in used)
    ed.lib_scroll = 0
    ed.lib_clamp_scroll()
    cand_vi = ed.library.index(cand) - ed.lib_scroll
    assert 0 <= cand_vi < ed.lib_visible_count(), "candidate audio is not in the visible list area"
    cr = ed.lib_item_rect(cand_vi)
    n_seg = len(ed.segments)
    ed._on_left_down(cr.centerx, cr.centery)
    assert ed.pending_audio == cand, "sidebar selection failed"
    place_beat = ed.b1 - 0.5
    px, py = int(ed.beat_to_x(place_beat)), LYRIC_TOP + LYRIC_H // 2
    assert ed.hit_block(px, py) is None, "placement point should be in empty space"
    ed._on_left_down(px, py)
    assert len(ed.segments) == n_seg + 1, "placing new note failed"
    new_seg = next(s for s in ed.segments if s['audio'] == cand)
    assert new_seg['src_pitch'] == ed.mod.SEGMENTS[0]['src_pitch'], "wrong src_pitch default"
    assert new_seg['dst_pitch'] in ('G4', 'E4'), "wrong inferred PIT pitch at the drop point"
    # clicking the same item again cancels the pending audio
    ed._on_left_down(cr.centerx, cr.centery)
    assert ed.pending_audio is None, "clicking the same item again should cancel"
    # right-click to delete the new segment
    ed._on_right_down(*ed.block_rect(new_seg).center)
    assert len(ed.segments) == n_seg, "right-click delete segment failed"
    # direct method chain: append a note at the end (the view should expand automatically)
    b1_old = ed.b1
    seg2 = ed.add_segment(cand, b1_old - 0.2)
    assert seg2['beat_end'] > b1_old and ed.b1 > b1_old, "appending note at end did not expand the view"
    ed.remove_segment(ed.segments.index(seg2))
    assert len(ed.segments) == n_seg, "remove_segment failed"
    # save + reload
    out = ed.save()
    data = json.load(open(out, encoding='utf-8'))
    assert len(data['segments']) == len(ed.segments)
    assert len(data['pitch_line']) == len(ed.nodes)
    ed.reload()
    app.draw()   # render one frame without errors
    os.remove(out)  # do not keep selftest artifacts
    print(f"[selftest OK] segments {len(data['segments'])} / nodes "
          f"{len(data['pitch_line'])}")


def _shot():
    """Render one real frame to a PNG and exit (used to verify the layout)."""
    pygame.init()
    f, fp = _get_font()
    app = App(f, fp)
    # simulate: cut the G4 line segment and drag the square node 1 beat apart to open a gap
    ed = app.ed
    xc = int(ed.beat_to_x(2.5))
    yc = int(ed.st_to_y(7))       # G4=st7
    assert ed.cut_line_at(xc, yc)
    br = [n for n in ed.nodes if n[2]][0]
    br[0] = round(br[0] + 1.0, 4)
    ed._sort_nodes()
    # simulate: pick an unused audio and hover over empty lyric area -> show the sidebar/preview block
    used = {s['audio'] for s in ed.segments}
    cand = next(p for p in ed.library if p not in used)
    ed.pending_audio = cand
    ed.mouse_pos = (int(ed.beat_to_x(ed.b1 - 0.5)),
                    LYRIC_TOP + LYRIC_H // 2)
    ed.mouse_beat = ed.b1 - 0.5
    app.draw()
    out = os.path.join(_BASE_DIR, 'pit_editor_preview.png')
    pygame.image.save(app.screen, out)
    print(f"[shot] {out}")


def _render():
    """Headless render of the preview wav using VocalB1.py's current config plus the editor's default data (chain verification)."""
    ed = Editor()
    t0 = time.time()
    path = ed.render_audio()
    print(f"[render OK] {path}  ({time.time() - t0:.1f}s)")


def main():
    if '--selftest' in sys.argv:
        _selftest()
        return
    if '--shot' in sys.argv:
        _shot()
        return
    if '--render' in sys.argv:
        _render()
        return
    pygame.init()
    f, fp = _get_font()
    app = App(f, fp)
    app.run()
    pygame.quit()


if __name__ == '__main__':
    main()
