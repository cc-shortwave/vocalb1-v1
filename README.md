# VocalB1（一代）

离线轻量化歌声合成引擎 —— 音轨参数独立可控，无预训练，支持 UTAU 声库接入。
Offline lightweight singing synthesizer — per-note parameters, no pretraining, UTAU voicebank support.

---

## 这是什么 / What is this

VocalB1 把一段人声录音拆成「基频 + 频谱包络 + 气声 + 激励」，再用逐音指定的音高、时长、
气声量等参数重新拼成一首歌。**不训练模型，不需要 GPU，不联网**，靠纯信号处理完成合成。

VocalB1 decomposes a voice recording into F0 / spectral envelope / breath / excitation, then
re-synthesizes a song from per-note parameters. **No model training, no GPU, no network** —
pure signal processing.

- 每个音独立控制：音高、拍位、气声量、共振峰跟随比、衰减曲线
- 支持 UTAU **単独音** 声库（142 个基本假名）
- 附带 Windows 桌面界面（C# / WPF）和命令行入口
- 2 核 / 2 GB 内存的机器就能跑

---

## 目录结构 / Layout

```
.
├── VocalB1Synthesizer.py     # 合成核心
├── VocalDecomposer.py        # 分解：音频 → 参数（.npy + .json）
├── VocalSynthesizer.py       # 由参数重建语音
├── VocalB1.py                # 命令行入口
├── vocal_lib/                # 底层库：基频检测、共振峰、气声、声门模型…
├── ui/                       # Windows 界面（C# / WPF）
│   ├── src/                  # 解决方案源码
│   └── engine.settings.example.json
├── languages/                # 界面语言包（en / ja / zh）
├── voicebank/                # 声库放这里（本仓库不含声库，见其中 README）
├── spectrogram.py            # 语谱图
├── oscilloscope.py           # 波形视图
└── pit_editor.py             # 音高曲线编辑器
```

---

## 依赖 / Requirements

- Python 3.10+
- Windows 10/11（界面）；Python 引擎本身跨平台

```
pip install -r requirements.txt
```

`matplotlib`（频谱/语谱图）和 `pygame`（试听）是可选的，只跑合成不需要。

---

## 快速开始 / Quick start

### 1. 放一个声库 / Add a voicebank

把 UTAU **単独音** 声库文件夹放进 `voicebank/`：

```
voicebank/
└── 重音テト単独音/
    ├── oto.ini
    ├── _あ.wav
    └── …
```

本仓库**不附带声库**（版权限制）。详见 [voicebank/README.md](voicebank/README.md)。

No voicebank is bundled (licensing). See [voicebank/README.md](voicebank/README.md).

### 2. 命令行试一次 / Try from the command line

```bash
python VocalB1Synthesizer.py
```

用文件顶部的 `DEFAULT_SEGMENTS` 段落配置合成一次，输出到 `_output/`。

### 3. 图形界面 / Desktop UI

```
ui/src/VocalB1.App/bin/.../VocalB1.App.exe
```

或用 Visual Studio 打开 `ui/src` 里的解决方案编译。界面通过标准输入输出（JSON 行协议）
调用 Python 引擎，两者是独立进程。

界面不需要配置——它会自动向上查找含 `VocalB1Synthesizer.py` 的目录作为仓库根，
再从中读取 `languages/`。需要覆盖时，把 `ui/engine.settings.example.json` 复制成
`ui/engine.settings.json` 再改（该文件不会被提交）。

The UI auto-detects the repository root and the Python interpreter. To override, copy
`ui/engine.settings.example.json` to `ui/engine.settings.json` (git-ignored) and edit it.

---

## 声库怎么找 / How the voicebank is located

优先级 / priority:

1. 环境变量 `VOCALB1_LIB`（直接指向声库目录）
2. `<仓库>/voicebank/` 下**按名字排序的第一个子目录**
3. 都没有 → 报错并提示

---

## 参数 / Parameters

每个音可独立设置：

- **目标音高** — C4 … A5
- **拍位 / 拍数** — 0.25 拍为最小单位，可拆成两半
- **气声量** — 气声与主声的混合比
- **共振峰跟随比** — 音高移动时共振峰跟随的程度（0 = 完全不变，1 = 完全跟随）
- **衰减曲线** — 逐段音量包络

这些参数存在工程文件里，可反复编辑、撤销，合成时可逐段缓存。

---

## 性能 / Performance

在 2 核 / 2 GB 的机器上实测：

- 4–5 个音：约 2–5 秒
- 25 秒的歌：约 60 秒（0.42× 实时）
- 重复合成（参数未变）：命中缓存，几乎瞬间

---

## 许可 / License

源代码采用 **MIT** 许可，见 [LICENSE](LICENSE)。

**声库不在此许可范围内**——声库由各自的版权方授权，本项目不附带、也不再分发任何声库。
请遵守你所使用声库的使用条款（多数允许个人非商用，禁止再分发）。

The source code is MIT-licensed. **Voicebanks are not covered** — they are licensed by
their own rights holders and are not bundled or redistributed here.

---

## 开发方式 / How this was built

这套方案的创意、需求定义、参数取舍与听感调整由作者完成；**代码实现主要由 AI 编程助手生成**，
作者负责逐段验证、调试与最终决策。项目使用了 numpy / scipy / soundfile / pyworld 等开源库，
未包含任何预训练模型。

The idea, requirements, parameter choices and listening-based tuning are the author's;
**the code was largely written by an AI coding assistant**, with the author doing design,
verification, debugging and final decisions. Open-source libraries (numpy, scipy, soundfile,
pyworld) are used; no pretrained models are included.

---

## 作者 / Author

郭希淳（Xichun Guo）· 2026

项目主页 / Homepage: <https://vocalb1.com>
