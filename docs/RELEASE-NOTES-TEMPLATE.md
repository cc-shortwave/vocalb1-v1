## vocalb1 v{{VERSION}}

Offline lightweight singing synthesizer with per-note control.
No pretraining, no GPU, no network.

### Downloads
{{DOWNLOADS}}

### Quick start
1. Unzip the archive (it contains a single folder).
2. Put a UTAU 単独音 voicebank into `voicebank/`, e.g. wav files and `icon.jpg` under `voicebank/<your-voicebank>/`.
3. Double-click `VocalB1.exe`.

### Not included
No voicebank is bundled, for licensing reasons. See `voicebank/README.md`.

### Verify
```
sha256sum -c SHA256SUMS.txt
```

### Notes
- Windows x64 only. The Python engine itself is cross-platform.
{{BUILDNOTE}}

---

### 中文

**v{{VERSION}}** —— 离线轻量化歌声合成引擎，音轨参数独立可控，无预训练、无 GPU、无网络。

下载后解压（只有一个文件夹），把 UTAU 単独音 声库放进 `voicebank/`，双击 `VocalB1.exe` 即可。
仓库不附带声库（版权原因）。

### 日本語

**v{{VERSION}}** —— オフライン軽量歌声合成エンジン。音ごとの制御、事前学習なし、GPU 不要、ネットワーク不要。

展開後（フォルダは 1 つだけ）、UTAU 単独音 音源を `voicebank/` に置き、`VocalB1.exe` を実行してください。
音源はライセンスの都合で同梱していません。
