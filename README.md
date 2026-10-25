# VocalB1

**English** | [中文](README.zh-CN.md) | [日本語](README.ja.md)

Offline lightweight singing synthesizer — per-note parameters, no pretraining, UTAU voicebank support.

VocalB1 decomposes a voice recording into F0 / spectral envelope / breath / excitation, then
re-synthesizes a song from per-note parameters. **No model training, no GPU, no network** —
pure signal processing that runs on a 2-core / 2 GB machine.

---

## Features

- **Per-note control** — pitch, beat position, breath level, formant follow ratio, decay curve
- **UTAU 単独音 voicebanks** — 142 basic kana
- **Zero pretraining** — no model weights, no dataset, no GPU
- **Offline** — nothing leaves your machine
- **Desktop UI** (C# / WPF) plus a plain command-line entry point
- **Fast enough to iterate** — a 25-second song renders in about 60 seconds; unchanged segments come from cache almost instantly

---

## Layout

```
.
├── VocalB1Synthesizer.py     # synthesis core
├── VocalDecomposer.py        # audio -> parameters (.npy + .json)
├── VocalSynthesizer.py       # rebuild speech from parameters
├── VocalB1.py                # command-line entry
├── vocal_lib/                # F0 detection, formants, breath, glottal models
├── ui/                       # Windows desktop UI (C# / WPF)
│   ├── src/
│   └── engine.settings.example.json
├── languages/                # UI language packs (en / ja / zh)
├── voicebank/                # put your voicebank here (none is bundled)
└── tools/                    # helper scripts (the engine does not need them)
    ├── spectrogram.py        # spectrogram view
    ├── oscilloscope.py       # waveform view
    ├── pit_editor.py         # pitch-curve editor
    ├── _plosive_diag.py      # plosive transient diagnostics
    └── test_vocal_lib.py     # library regression tests
```

The library has its own API reference: [vocal_lib/README.md](vocal_lib/README.md) (English / 中文 / 日本語).

---

## Requirements

- Python 3.10+
- Windows 10/11 for the desktop UI; the Python engine itself is cross-platform

```bash
pip install -r requirements.txt
```

`matplotlib` (spectrum / spectrogram) and `pygame` (preview playback) are optional —
synthesis alone does not need them.

On Windows the requirements pull `pyworld-prebuilt` instead of `pyworld`, because upstream
`pyworld` ships no wheels for Python 3.9+ (it would need a local MSVC build).

---

## Quick start

### 1. Add a voicebank

Put a UTAU **単独音 (single-note)** voicebank folder inside `voicebank/`:

```
voicebank/
└── <your-voicebank>/
    ├── _あ.wav
    ├── ...
    └── icon.jpg
```

No voicebank is bundled, for licensing reasons — see [voicebank/README.md](voicebank/README.md).

### 2. Try the engine

```bash
python VocalB1Synthesizer.py
```

This renders the `DEFAULT_SEGMENTS` block near the top of that file into `_output/`.

### 3. Run the desktop UI

Open the solution under `ui/src/` in Visual Studio and build `VocalB1.App`, or run the
published `VocalB1.App.exe`. The UI talks to the Python engine as a **separate process over
a stdin/stdout JSON line protocol** — no embedded interpreter.

No configuration is needed: the UI walks up from its own executable to find the directory
containing `VocalB1Synthesizer.py`, and reads `languages/` from there. To override anything,
copy `ui/engine.settings.example.json` to `ui/engine.settings.json` (git-ignored) and edit it.

---

## How the voicebank is located

Priority:

1. `VOCALB1_LIB` environment variable (points straight at a voicebank directory)
2. the **first subdirectory** of `<repository>/voicebank/`, sorted by name
3. otherwise: error out with a hint

---

## Parameters

Each note carries its own settings:

- **Target pitch** — C4 ... A5
- **Beat position / length** — 0.25 beat minimum, splittable in half
- **Breath level** — mix ratio between breath and voiced signal
- **Formant follow ratio** — how much formants track the pitch shift (0 = fixed, 1 = follows fully)
- **Decay curve** — per-segment volume envelope

These live in the project file, so they can be edited and undone repeatedly, and synthesis
caches unchanged segments.

---

## Performance

Measured on a 2-core / 2 GB machine:

- 4-5 notes: about 2-5 seconds
- a 25-second song: about 60 seconds (0.42x real time)
- re-rendering with unchanged parameters: cache hit, effectively instant

---


---

## Building a release

```powershell
.\scripts\package-release.ps1              # both variants
.\scripts\package-release.ps1 -Mode lite   # lite only
```

Reads `VERSION`, publishes the WPF app, optionally bundles an embedded Python, and writes
`dist/vocalb1-v<version>-win-x64-{full,lite}.zip` plus `SHA256SUMS.txt` and `RELEASE-NOTES.md`.

Version numbers are bumped by the script, not by hand:

```powershell
.\scripts\package-release.ps1 -Bump patch -Tag   # 1.0.0 -> 1.0.1, commits VERSION, tags v1.0.1
```

`-Bump` takes `patch` (bugfix), `minor` (new feature) or `major` (breaking change); `-NoCommit`
leaves the change uncommitted and `-Tag` creates the annotated tag locally (never pushes).

The full step-by-step checklist — pre-flight, verification, GitHub Release, rollback and
troubleshooting — is in **[docs/RELEASE.md](docs/RELEASE.md)**.
Both archives unpack into a single folder; the engine, `ui/bridge/`, `languages/` and an empty
`voicebank/` are placed where the app auto-discovers them.

## Licensing

Source code is released under the **MIT** license — see [LICENSE](LICENSE).
Scope and third-party licenses are listed in [NOTICE.md](NOTICE.md).

**Voicebanks are not covered.** They are licensed by their respective rights holders; this
project neither bundles nor redistributes any. Follow the terms of the voicebank you use —
most allow personal non-commercial use and prohibit redistribution.

---

## How this was built

The idea, requirements, parameter choices and listening-based tuning are the author's.
**The code was largely written by an AI coding assistant**; the author did the design,
verification, debugging and final decisions. The project uses open-source libraries
(numpy, scipy, soundfile, pyworld) and contains no pretrained models.

---

## Acknowledgements

- **Mingxuan** — visual identity and release testing: designed the application icon
  and the project mark, installed the `setup.exe` build on Windows 11, ran it end to
  end, and reported the problems he found.

The icon is his work; see [NOTICE.md](NOTICE.md) for how it relates to the MIT license.

---

## Author

Xichun Guo · 2026

Homepage: <https://vocalb1.com>
