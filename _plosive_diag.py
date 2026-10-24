"""Plosive transient diagnostics: frame-by-frame energy comparison of source audio / breath / output + detection feature values."""
import os
import numpy as np
from scipy.io import wavfile

_HERE = os.path.dirname(os.path.abspath(__file__))
_OUT = os.path.join(_HERE, "_output")
D = os.environ.get("VOCALB1_LIB") or os.path.join(_HERE, "voicebank")
BA = os.path.join(_OUT, "breath_seg0.wav")
BB = os.path.join(_OUT, "breath_seg1.wav")
OUT = os.path.join(_OUT, "multi_voiced_bezier.wav")
sr = 44100
FP = 220.5


def load(p):
    s, x = wavfile.read(p)
    if x.ndim > 1:
        x = x[:, 0]
    if x.dtype == np.int16:
        x = x.astype(np.float64) / 32768.0
    elif x.dtype == np.int32:
        x = x.astype(np.float64) / 2147483648.0
    else:
        x = np.asarray(x, dtype=np.float64)
    return x


def frms(x, nfr):
    r = np.zeros(nfr)
    for i in range(nfr):
        a, b = int(round(i * FP)), int(round((i + 1) * FP))
        b = min(b, len(x))
        r[i] = np.sqrt(np.mean(x[a:b] ** 2)) if b > a else 0.0
    return r


src_b = load(rf'{D}\_ば.wav')
src_k = load(rf'{D}\_か.wav')
brA = load(BA)
brB = load(BB)
out = load(OUT)

NF = 30
rb, rk = frms(src_b, NF), frms(src_k, NF)
rba, rbb = frms(brA, NF), frms(brB, NF)
ro_a = frms(out[:int(244 * FP)], NF)
ro_b = frms(out[int(196 * FP):int(196 * FP) + int(NF * FP)], NF)

print('Frame(ms) |  _ば源   breathA | Out A |   _か源   breathB | Out B')
print('-' * 76)
for i in range(NF):
    print(f'{i*5:3d}-{(i+1)*5:3d} | {rb[i]:.4f} {rba[i]:.4f} | {ro_a[i]:.4f} | '
          f'{rk[i]:.4f} {rbb[i]:.4f} | {ro_b[i]:.4f}')

print('\nPeak frames:')
print(f'  _ば源    frame{np.argmax(rb)} ({np.argmax(rb)*5}ms) peak={rb.max():.4f}')
print(f'  breathA  frame{np.argmax(rba)} ({np.argmax(rba)*5}ms) peak={rba.max():.4f}')
print(f'  Out A    frame{np.argmax(ro_a)} ({np.argmax(ro_a)*5}ms) peak={ro_a.max():.4f}')
print(f'  _か源    frame{np.argmax(rk)} ({np.argmax(rk)*5}ms) peak={rk.max():.4f}')
print(f'  breathB  frame{np.argmax(rbb)} ({np.argmax(rbb)*5}ms) peak={rbb.max():.4f}')
print(f'  Out B    frame{np.argmax(ro_b)} ({np.argmax(ro_b)*5}ms) peak={ro_b.max():.4f}')

# _か three features (reproducing the _plosive_gain decision)
print('\n=== _か first 30 frames, three plosive features (thresholds: ratio>=5, grad>=12dB, crest>=2, valley<0.3) ===')
med = np.median(rbb[rbb > 0])
print(f'breathB frame RMS median={med:.5f}')
for i in range(12):
    a, b = int(round(i * FP)), int(round((i + 1) * FP))
    seg = brB[a:b]
    r = np.sqrt(np.mean(seg ** 2))
    crest = np.max(np.abs(seg)) / r if r > 1e-12 else 0
    g = 20 * np.log10(r / rbb[i - 1]) if i >= 1 and rbb[i - 1] > 0 else 0
    lead = rbb[max(0, i - 8):i]
    lv = lead.min() / r if len(lead) and r > 0 else 9
    print(f'  frame{i:2d}({i*5:3d}ms) rms={r:.5f} ({r/med:5.2f}x median) grad={g:+6.1f}dB '
          f'crest={crest:.2f} lead valley={lv:.3f}')