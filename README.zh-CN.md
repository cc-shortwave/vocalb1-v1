# VocalB1

[English](README.md) | **中文** | [日本語](README.ja.md)

离线轻量化歌声合成引擎 —— 音轨参数独立可控，无预训练，支持 UTAU 声库接入。

VocalB1 把一段人声录音拆成「基频 + 频谱包络 + 气声 + 激励」，再用逐音指定的音高、时长、
气声量等参数重新拼成一首歌。**不训练模型，不需要 GPU，不联网**，靠纯信号处理完成合成，
2 核 / 2 GB 的机器就能跑。

---

## 特点

- **逐音独立控制** —— 音高、拍位、气声量、共振峰跟随比、衰减曲线
- **支持 UTAU 単独音 声库** —— 142 个基本假名
- **零预训练** —— 没有模型权重、没有数据集、不需要 GPU
- **完全离线** —— 数据不出本机
- **桌面界面**（C# / WPF）+ 命令行入口
- **迭代够快** —— 25 秒的歌约 60 秒出；参数没变的段落走缓存，几乎瞬间

---

## 目录结构

```
.
├── VocalB1Synthesizer.py     # 合成核心
├── VocalDecomposer.py        # 分解：音频 -> 参数（.npy + .json）
├── VocalSynthesizer.py       # 由参数重建语音
├── VocalB1.py                # 命令行入口
├── vocal_lib/                # 基频检测、共振峰、气声、声门模型等底层库
├── ui/                       # Windows 桌面界面（C# / WPF）
│   ├── src/
│   └── engine.settings.example.json
├── languages/                # 界面语言包（en / ja / zh）
├── voicebank/                # 声库放这里（仓库不含声库）
└── tools/                    # 辅助工具（引擎运行不依赖）
    ├── spectrogram.py        # 语谱图
    ├── oscilloscope.py       # 波形视图
    ├── pit_editor.py         # 音高曲线编辑器
    ├── _plosive_diag.py      # 爆破音诊断
    └── test_vocal_lib.py     # 库回归测试
```

这个库有独立的 API 文档：[vocal_lib/README.md](vocal_lib/README.md)（中 / English / 日本語）。

---

## 依赖

- Python 3.10+
- 桌面界面需要 Windows 10/11；Python 引擎本身跨平台

```bash
pip install -r requirements.txt
```

`matplotlib`（频谱 / 语谱图）和 `pygame`（试听）是可选的，只跑合成不需要。

---

## 快速开始

### 1. 放一个声库

把 UTAU **単独音** 声库文件夹放进 `voicebank/`：

```
voicebank/
└── <你的声库>/
    ├── _あ.wav
    ├── ...
    └── icon.jpg
```

仓库**不附带声库**（版权限制），详见 [voicebank/README.md](voicebank/README.md)。

### 2. 跑一次引擎

```bash
python VocalB1Synthesizer.py
```

会用文件顶部的 `DEFAULT_SEGMENTS` 段落合成一次，输出到 `_output/`。

### 3. 打开桌面界面

用 Visual Studio 打开 `ui/src/` 下的解决方案编译 `VocalB1.App`，或直接运行发布好的
`VocalB1.App.exe`。界面把 Python 引擎当作**独立进程**，通过标准输入输出上的 JSON
行协议通信——不嵌入解释器。

不需要任何配置：界面会从自己的可执行文件向上查找含 `VocalB1Synthesizer.py` 的目录，
再从那里读 `languages/`。需要覆盖时，把 `ui/engine.settings.example.json` 复制成
`ui/engine.settings.json`（已被 git 忽略）再改。

---

## 声库怎么找

优先级：

1. 环境变量 `VOCALB1_LIB`（直接指向声库目录）
2. `<仓库>/voicebank/` 下**按名字排序的第一个子目录**
3. 都没有 -> 报错并提示

---

## 参数

每个音独立设置：

- **目标音高** —— C4 … A5
- **拍位 / 拍数** —— 0.25 拍为最小单位，可拆成两半
- **气声量** —— 气声与主声的混合比
- **共振峰跟随比** —— 音高移动时共振峰跟随的程度（0 = 完全不变，1 = 完全跟随）
- **衰减曲线** —— 逐段音量包络

参数存在工程文件里，可以反复编辑、撤销，合成时未变动的段落走缓存。

---

## 性能

在 2 核 / 2 GB 的机器上实测：

- 4–5 个音：约 2–5 秒
- 25 秒的歌：约 60 秒（0.42× 实时）
- 重复合成（参数未变）：命中缓存，几乎瞬间

---


---

## 打包发布

```powershell
.\scripts\package-release.ps1              # 两个版本都打
.\scripts\package-release.ps1 -Mode lite   # 只打 lite
```

脚本读 `VERSION`、发布 WPF 程序、(可选) 内置 Python，输出
`dist/vocalb1-v<版本>-win-x64-{full,lite}.zip`，并生成 `SHA256SUMS.txt` 与 `RELEASE-NOTES.md`。

版本号由脚本自增，不用手改：

```powershell
.\scripts\package-release.ps1 -Bump patch -Tag   # 1.0.0 -> 1.0.1，提交 VERSION，打 tag v1.0.1
```

`-Bump` 可取值：`patch`（修 bug）、`minor`（加功能）、`major`（不兼容改动）；
`-NoCommit` 只改文件不提交，`-Tag` 在本地创建附注 tag（不会自动推送）。

完整的发布流程（发布前检查、验证、GitHub Release、撤回、故障排查）见 **[docs/RELEASE.zh-CN.md](docs/RELEASE.zh-CN.md)**。
两个压缩包都解压成单一文件夹，引擎、`ui/bridge/`、`languages/` 和空的 `voicebank/`
都放在程序能自动发现的位置。

## 许可

源代码采用 **MIT** 许可，见 [LICENSE](LICENSE)。
适用范围与第三方许可见 [NOTICE.md](NOTICE.md)。

**声库不在此许可范围内。** 声库由各自的版权方授权，本项目不附带、也不再分发任何声库。
请遵守你所使用声库的使用条款——多数允许个人非商用，禁止再分发。

---

## 开发方式

这套方案的创意、需求定义、参数取舍与听感调整由作者完成；**代码实现主要由 AI 编程助手生成**，
作者负责逐段验证、调试与最终决策。项目使用了 numpy / scipy / soundfile / pyworld 等开源库，
未包含任何预训练模型。

---

## 致谢

- **Mingxuan** —— 视觉设计与发布测试：设计应用图标；在 Windows 11 上安装
  `setup.exe` 发布包并完整跑通，提出了过程中发现的问题。

图标是他的作品，与 MIT 许可的关系见 [NOTICE.md](NOTICE.md)。

---

## 作者

Xichun Guo · 2026

项目主页：<https://vocalb1.com>
