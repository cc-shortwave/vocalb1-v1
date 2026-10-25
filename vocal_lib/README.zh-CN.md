# vocal_lib

[English](README.md) | **中文** | [日本語](README.ja.md)

人声分析与合成库 — 面向对象的声门激励分解 / 合成 / 音高变换工具包。

## 安装

```bash
# 方式一：pip 安装（推荐）
pip install .
pip install -e .          # 开发模式
pip install .[full]       # 包含 pyworld

# 方式二：直接复制 vocal_lib/ 文件夹到你的项目
```

## 依赖

| 依赖 | 是否必须 | 用途 |
|---|---|---|
| numpy >= 1.20 | 是 | 数值计算 |
| soundfile >= 0.12 | 是 | WAV 读写 |
| pyworld >= 0.3.0 | 否 | F0/sp/ap 提取（不装则功能降级） |

## 快速上手

```python
from vocal_lib import decompose_vocal, synthesize_vocal, pitch_shift

# 1. 分解：WAV -> F0 + 气声 + 声门激励
f0, breath, glottal = decompose_vocal("ai.wav")

# 2. 音高变换：C4 -> E4（升 4 半音）
f0_new, glottal_new = pitch_shift(f0, glottal, "C4", "E4")

# 3. 合成：F0 + 气声 + 声门激励 -> WAV
output = synthesize_vocal(f0_new, breath, glottal_new)
output.save("_output/result.wav", bit_depth=24)
```

---

## 一、数据类（data_classes.py）

### 1.1 F0 — 基频轨迹类

封装 F0 值序列、时间轴、浊音标记等元数据，是纯数据容器。

**属性**

| 属性 | 类型 | 说明 |
|---|---|---|
| `values` | `np.ndarray[float64]` | F0 值序列（Hz），静音帧为 0 |
| `time_axis` | `np.ndarray[float64]` | 对应时间轴（秒） |
| `frame_period` | `float` | 帧周期（毫秒），默认 5.0 |
| `sample_rate` | `int` | 采样率 |
| `voiced_flags` | `np.ndarray[bool]` | 浊音标记（True=有声） |

**属性访问**

| 属性 | 返回 | 说明 |
|---|---|---|
| `n_frames` | `int` | 帧数 = len(values) |

**方法**

| 方法 | 说明 |
|---|---|
| `copy()` | 深拷贝，返回新的 F0 对象 |
| `save(path)` | 序列化为 `.npy` 文件（dict 格式） |
| `load(path)` | 类方法，从 `.npy` 反序列化 |
| `__len__()` | 返回帧数 |
| `__repr__()` | 返回摘要字符串 `F0(n_frames=..., avg=...Hz)` |

**底层结构**：构造时若未提供 `time_axis`，则自动按 `arange(n) * frame_period / 1000` 生成。若未提供 `voiced_flags`，则自动按 `values > 0` 生成。save/load 使用 `np.save` 将 dict 打包为 `.npy`。

---

### 1.2 WAVAudio — WAV 音频类

封装音频样本数据和采样率，支持文件读写。

**属性**

| 属性 | 类型 | 说明 |
|---|---|---|
| `data` | `np.ndarray[float64]` | 音频样本数据（单声道为一维，多声道为二维） |
| `sample_rate` | `int` | 采样率 |
| `source_path` | `Optional[str]` | 原始文件路径 |

**属性访问**

| 属性 | 返回 | 说明 |
|---|---|---|
| `duration` | `float` | 时长（秒）= len(data) / sample_rate |
| `n_samples` | `int` | 采样点数 |
| `n_channels` | `int` | 声道数 |
| `is_mono` | `bool` | 是否单声道 |
| `rms` | `float` | RMS 能量值 |

**方法**

| 方法 | 说明 |
|---|---|
| `from_file(path, mono=True)` | 类方法，用 soundfile 加载 WAV 文件，`mono=True` 时自动混合为单声道 |
| `to_mono()` | 多声道转单声道（取均值），已是单声道则返回 copy |
| `save(path, bit_depth=24, target_sample_rate=None)` | 保存为 WAV，支持 PCM_8/16/24 位深，可选重采样 |
| `copy()` | 深拷贝 |
| `__len__()` | 返回采样点数 |
| `__repr__()` | 返回摘要字符串 |

**底层结构**：`save()` 内部先做归一化（峰值 > 1.0 时缩放），再通过 `soundfile.write` 写入。重采样使用线性插值。位深通过 `subtype_map = {8: 'PCM_U8', 16: 'PCM_16', 24: 'PCM_24'}` 映射。

---

### 1.3 GlottalExcitation — 声门激励类

封装声门时域信号及 pyworld 参数、开商、强度轨迹等。

**属性**

| 属性 | 类型 | 说明 |
|---|---|---|
| `signal` | `np.ndarray[float64]` | 声门激励时域信号 |
| `sample_rate` | `int` | 采样率 |
| `sp` | `Optional[np.ndarray]` | pyworld 频谱包络 (n_frames, n_bins) |
| `ap` | `Optional[np.ndarray]` | pyworld 非周期性参数 (n_frames, n_bins) |
| `oq` | `float` | 开口商 Open Quotient |
| `glottal_intensity` | `Optional[np.ndarray]` | 逐帧声门强度比例 |
| `parameters` | `Dict[str, Any]` | 扩展参数字典 |

**`parameters` 字典可包含的键**

| 键 | 类型 | 说明 |
|---|---|---|
| `jitter` | `Dict[str, float]` | 频率微扰：jitter_local, jitter_rap, jitter_ppq5, jitter_ddp |
| `shimmer` | `Dict[str, float]` | 振幅微扰：shimmer_local, shimmer_apq3, shimmer_apq5, shimmer_dda |
| `formants` | `List[List[Dict]]` | 共振峰轨迹：每帧含 F1-F5 的 frequency/magnitude/index |
| `f0_stats` | `Dict[str, float]` | F0 统计：avg_f0, min_f0, max_f0, f0_std, voiced_ratio |
| `glottal_pulse` | `np.ndarray` | 声门脉冲轨迹 |
| `formant_intensity` | `np.ndarray` | 逐帧共振峰强度比例 |
| `breath_intensity` | `np.ndarray` | 逐帧气声强度比例 |
| `_pitch_shift_info` | `Dict` | 音高变换元数据（semitones, pitch_factor, max_f0 等） |

**属性访问**

| 属性 | 返回 | 说明 |
|---|---|---|
| `duration` | `float` | 时长（秒） |
| `n_samples` | `int` | 采样点数 |
| `has_pyworld_params` | `bool` | sp 和 ap 是否均存在 |
| `jitter` | `Optional[Dict]` | 快捷访问 parameters['jitter'] |
| `shimmer` | `Optional[Dict]` | 快捷访问 parameters['shimmer'] |

**方法**

| 方法 | 说明 |
|---|---|
| `get_param(key, default=None)` | 从 parameters 字典取值 |
| `set_param(key, value)` | 写入 parameters 字典 |
| `copy()` | 深拷贝（包括 parameters 中所有可拷贝的值） |
| `save(path)` | 序列化为 `.npy` |
| `load(path)` | 类方法，从 `.npy` 反序列化 |

---

## 二、顶层 API 函数

### 2.1 decompose_vocal — 人声分解

**位置**：`vocal_lib/decomposition.py`

```python
decompose_vocal(input_audio, sample_rate=44100, frame_period=5.0) -> (F0, WAVAudio, GlottalExcitation)
```

**功能**：将输入音频分解为三个分量：F0 基频轨迹、气声 WAV、声门激励。

**输入**：`input_audio` 支持三种类型：
- `str`：文件路径，通过 `WAVAudio.from_file()` 加载
- `WAVAudio`：直接使用
- `np.ndarray`：需指定 `sample_rate`

**处理流程**：

```
输入音频
  |
  v
_normalize_input()  ----> WAVAudio 对象
  |
  v
engine.decompose_vocal(data_mono, sr)
  |
  |  1. f0_detection.detect_f0_sequence()
  |     -> pyworld DIO + StoneMask (优先)
  |     -> 或自研三方法融合 (自相关 + 倒谱 + YIN)
  |     -> 中值滤波 + Viterbi 平滑
  |
  |  2. voice_separation.decompose_voice()
  |     -> 分帧 (2048pt, hop=512)
  |     -> STFT (Hanning 窗)
  |     -> 逐帧浊音/清音检测 (能量 + ZCR + 谱平坦度)
  |     -> 谐波带定位 -> 三分量掩码分离
  |        - 声门分量 = 谐波带 * (1 - breath_ratio)
  |        - 共振峰分量 = 共振峰区域 * (1 + breath_ratio)
  |        - 气声分量 = 非谐波区域 * breath_ratio
  |     -> IRFFT 重构 + overlap-add 合成
  |     -> 能量归一化
  |
  |  3. pyworld_processor.extract_pyworld_features() (可选)
  |     -> DIO 提取 F0
  |     -> StoneMask 精修 F0
  |     -> CheapTrick 提取频谱包络 sp
  |     -> D4C 提取非周期性参数 ap
  |     -> compute_jitter() / compute_shimmer()
  |     -> compute_oq() / compute_f0_statistics()
  |
  v
封装为 (F0, WAVAudio_breath, GlottalExcitation)
```

**底层算法详解**：

1. **F0 检测**（`f0_detection.py`）
   - 优先使用 pyworld 的 DIO 算法（基于谐波结构）+ StoneMask 精修
   - 若 pyworld 不可用，回退到三方法融合：
     - **自相关法**：加 Hanning 窗后做自相关，在 min_lag~max_lag 范围找峰值，谐波倍频校验
     - **倒谱法**：对幅度谱取对数后做 IFFT 得到倒谱，在 quefrency 域找峰值
     - **YIN 算法**：计算差分函数 + 累积均值归一化差分函数，找低于阈值的局部最小值，抛物线插值精修
   - **融合策略**：三方法结果在标准差 < 30Hz 时加权平均（YIN 权重 0.4，其余 0.3），否则取置信度最高者
   - **后处理**：5 帧中值滤波 + Viterbi 风格平滑（处理跳变和静音填充）

2. **声道分离**（`voice_separation.py`）
   - **分帧**：frame_size=2048, hop_size=512, Hanning 窗
   - **STFT**：`rfft` 计算幅度谱和相位谱
   - **浊音检测**：综合短时能量（阈值 0.001）、过零率（阈值 0.15）、谱平坦度（阈值 0.3）打分
   - **谐波带定位**：以 F0 为基频，在各次谐波频率附近（容差 = max(15Hz, F0*3%)）定位谐波带
   - **三分量分离**：
     - 谐波掩码：谐波带内为 1，经 3 点平滑
     - 共振峰掩码：在共振峰频率附近用三角窗（带宽 80Hz）
     - 气声比例：由谱平坦度决定（最大 0.3）
     - 按比例分配能量，确保三分量之和等于原始谱
   - **重构**：IRFFT 还原时域帧 + overlap-add 拼接 + 能量缩放

3. **pyworld 参数提取**（`pyworld_processor.py`）
   - `pw.dio()`：F0 粗估计
   - `pw.stonemask()`：F0 精修
   - `pw.cheaptrick()`：频谱包络 sp
   - `pw.d4c()`：非周期性参数 ap
   - **Jitter** 计算：jitter_local（相邻帧差均值/均值）、RAP（3 点平均）、PPQ5（5 点平均）、DDP（二阶差分）
   - **Shimmer** 计算：shimmer_local（相邻帧振幅差均值/均值）、APQ3、APQ5、DDA
   - **OQ** 计算：将每周期 ap 分前后半段，取能量比值的均值
   - **F0 统计**：avg/min/max/std/voiced_ratio

---

### 2.2 synthesize_vocal — 人声合成

**位置**：`vocal_lib/synthesis.py`

```python
synthesize_vocal(f0, breath, glottal, *, match_rms_to=None, model_type='lf') -> WAVAudio
```

**功能**：从 F0 + 气声 + 声门激励还原合成语音。

**参数**：

| 参数 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `f0` | `F0` | - | F0 基频轨迹 |
| `breath` | `WAVAudio` | - | 气声分量 |
| `glottal` | `GlottalExcitation` | - | 声门激励 |
| `match_rms_to` | `Optional[float]` | None | 目标 RMS 能量 |
| `model_type` | `str` | `'lf'` | 声门模型：`'lf'` 或 `'rosenberg'` |

**处理流程**：

```
F0 + GlottalExcitation
  |
  v
提取参数: f0.values, sp, ap, oq, jitter, shimmer, formants
  |
  v
engine.synthesize_voice_from_parameters()
  |
  |  路径 A (pyworld 可用 + sp + ap):
  |    VoiceSynthesizer._synthesize_with_pyworld()
  |    -> pw.synthesize(f0, sp, ap, sr)
  |    -> 动态范围压缩 (threshold=0.5, ratio=3:1)
  |    -> RMS 匹配
  |    -> 软削波 (tanh 限幅器, threshold=0.85)
  |    -> 归一化到 0.9
  |
  |  路径 B (无 pyworld):
  |    -> ExcitationGenerator.generate_with_perturbations(f0, jitter, shimmer)
  |       (相位累加器生成冲激序列 + jitter/shimmer 微扰)
  |    -> GlottalPulseGenerator.generate_pulse(t0, sr)
  |       (LF 模型或 Rosenberg 模型生成单周期声门脉冲)
  |    -> convolve_with_pulse(excitation, pulse)
  |    -> differentiate_glottal_flow(u_g)  (声门微分得声源)
  |    -> synthesize_formant_filtered_voice(source, sr, formants)
  |       (预加重 -> VocalTractFilter 共振峰滤波 -> 去加重)
  |
  v
WAVAudio(data=synthesized, sample_rate=sr)
```

**底层算法详解**：

1. **pyworld 合成**（`pyworld_processor.py`）
   - `pw.synthesize(f0, sp, ap, sr, frame_period)`：直接从参数合成语音
   - 合成后经过动态范围压缩 + 软削波防破音

2. **声门模型合成**（`glottal_models.py` + `excitation_generator.py`）
   - **LF 模型**（Liljencrants-Fant）：四阶段声门气流模型
     - 关闭阶段（0 ~ ta）：输出 0
     - 开启阶段（ta ~ tb）：正弦上升 `sin(pi * t1)`
     - 减速阶段（tb ~ te）：余弦平方衰减 `cos(pi*t2/2)^2 * (1 - rd*t2)`
     - 返回阶段（te ~ 1）：输出 0
   - **Rosenberg 模型**：二项式 + 三项式组合
     - 上升部分（0 ~ epsilon）：二次多项式 `2t/eps - (t/eps)^2`
     - 下降部分（epsilon ~ 1）：三次多项式 `(1-alpha)(1-3t'^2+2t'^3) + alpha(1-t')`
   - **激励生成**：`ExcitationGenerator` 用相位累加器逐帧生成冲激序列，加入 jitter（周期扰动 ±10%）和 shimmer（幅度扰动）
   - **卷积**：激励信号与声门脉冲卷积得到声门气流 `u_g`
   - **微分**：`diff(u_g)` 得到声门声源（模拟声带闭合瞬间的负冲激）

3. **声道滤波**（`vocal_tract_filter.py`）
   - **预加重**：`y[i] = x[i] - 0.97 * x[i-1]`，提升高频
   - **共振峰滤波**：`VocalTractFilter.apply_formant_filter()` 逐帧应用二阶 IIR 谐振滤波器
     - 每个共振峰构建一个双二阶节（biquad）：`omega = 2*pi*f/sr`, `alpha = sin(omega)*sinh(ln2/2 * bw * 2*pi/sr)`
     - 分子 `[1, -2cos(omega), 1]`，分母 `[1+alpha, -2cos(omega), 1-alpha]`
     - 多个共振峰级联（系数卷积）
   - **去加重**：`y[i] = x[i] + 0.97 * y[i-1]`，恢复低频

---

### 2.3 pitch_shift — 音高变换

**位置**：`vocal_lib/pitch.py`

```python
pitch_shift(f0, glottal, original_pitch, target_pitch) -> (F0, GlottalExcitation)
```

**功能**：根据原始音高和目标音高，修改 F0 与声门参数，返回新对象。

**音高表示**（`_parse_semitones()` 支持的格式）：

| 格式 | 示例 | 解析逻辑 |
|---|---|---|
| 科学音高 | `"C4"`, `"A4"`, `"F#5"`, `"Bb3"` | MIDI = (octave+1)*12 + 音名偏移，半音差 = MIDI - 60 |
| Hz | `"440Hz"`, `440`（数值 > 50） | `12 * log2(hz / C4_hz)`，C4 = 261.63Hz |
| 项目风格 | `"1D"`, `"-1C"`, `"0C#"` | 八度偏移*12 + 音名偏移 |
| 半音数 | `+3`, `-5`, `0.0`（数值 <= 50） | 直接使用 |

**处理流程**：

```
original_pitch, target_pitch
  |
  v
_parse_semitones() -> 源半音数, 目标半音数
  |
  v
semitones = dst_semi - src_semi
  |
  v
pitch_shifter.process_pitch_shift(f0.values, semitones, sr, oq, ap, sp)
  |
  |  1. calculate_pitch_factor(semitones)
  |     -> k = 2^(semitones/12)
  |
  |  2. shift_f0_logarithmic(f0, semitones)
  |     -> f0_new = exp(log(f0) + log(k))  [对数域平移]
  |     -> 软限幅: 550Hz 软拐点, 900Hz 硬上限 (tanh 压缩)
  |
  |  3. smooth_f0_boundaries(f0_shifted)
  |     -> 首尾 10ms 淡入淡出
  |
  |  4. adjust_oq(oq, semitones)
  |     -> 升调: OQ 减小 (良好音域 -0.01/st, 超出 -0.02/st, 下限 0.30)
  |     -> 降调: OQ 增大 (良好音域 +0.008/st, 超出 +0.015/st, 上限 0.75)
  |
  |  5. enhance_breathiness(ap, semitones)
  |     -> ap *= (1 + semitones * 0.03), 超出良好音域额外 +0.04/st
  |     -> 上限 2.5x, clip 到 [0, 1]
  |
  |  6. warp_spectral_envelope(sp, semitones, sr)  [防共振峰冲突破音]
  |     -> 共振峰跟随音高上移 30%, 保留 70% 原始位置
  |     -> 频率轴插值映射
  |
  v
封装为 (新 F0, 新 GlottalExcitation)
  |
  v
GlottalExcitation 使用 sp_warped (频率轴映射后的频谱包络)
parameters 写入 _pitch_shift_info 元数据
```

**防破音机制（5 重）**：

| 机制 | 位置 | 作用 |
|---|---|---|
| F0 软限幅 | `shift_f0_logarithmic()` | 550~900Hz tanh 压缩，模拟假声区过渡 |
| OQ 加速修正 | `adjust_oq()` | 极高音 OQ 降至 0.30，极低音升至 0.75 |
| 频谱包络映射 | `warp_spectral_envelope()` | 共振峰跟随 30%，避免谐波-共振峰冲突 |
| 动态范围压缩 | `voice_synthesizer._apply_compression()` | 超过 0.5 的峰值按 3:1 压缩 |
| 软削波 | `voice_synthesizer._soft_clip()` | 超过 0.85 的幅度 tanh 平滑限幅 |

---

## 三、引擎层（engine.py）

引擎层是顶层 API 与底层核心模块之间的桥梁，所有 import 均使用包内相对导入。

### 3.1 decompose_vocal(audio_data, sample_rate) -> Dict

**流程**：
1. `detect_f0_sequence(audio_data, sr)` -> F0 轨迹
2. `decompose_voice(audio_data, sr, f0_sequence)` -> 三分量分离
3. `extract_pyworld_features(audio_data, sr)` -> sp/ap/jitter/shimmer/oq/f0_stats（可选）
4. 组装结果字典返回

### 3.2 synthesize_voice_from_parameters(f0_sequence, sample_rate, jitter, shimmer, formants_trajectory, model_type, sp, ap, target_rms, glottal_parameters) -> np.ndarray

**流程**：
1. 实例化 `VoiceSynthesizer(sample_rate, model_type)`
2. 调用 `synthesizer.synthesize_from_parameters()` 
3. 根据是否有 sp/ap 选择 pyworld 合成或声门模型合成

---

## 四、核心模块详解（core/）

### 4.1 f0_detection.py — F0 检测

| 函数 | 算法 | 说明 |
|---|---|---|
| `detect_f0_autocorrelation(frame, sr)` | 自相关 | Hanning 加窗 -> 自相关 -> 谐波倍频校验 -> 置信度阈值 0.3 |
| `detect_f0_cepstrum(mag_spec, sr, freqs)` | 倒谱 | log|FFT| -> IFFT -> quefrency 域峰值检测 |
| `yin_f0_detection(frame, sr)` | YIN | 差分函数 -> 累积均值归一化 -> 阈值 0.15 -> 抛物线插值精修 |
| `fuse_f0_methods(...)` | 融合 | std < 30Hz 时加权平均（YIN 0.4, 其余 0.3），否则取最高置信度 |
| `smooth_f0(f0, win=5)` | 中值滤波 | 5 帧窗口中值滤波 |
| `viterbi_smooth(f0, ...)` | Viterbi 风格 | 跳变惩罚 + 静音填充（连续 < 3 帧静音时保留 50% 前值） |
| `detect_f0_sequence(audio, sr)` | 主入口 | pyworld 优先，回退到三方法融合 |

### 4.2 voice_separation.py — 声道分离

| 函数 | 说明 |
|---|---|
| `frame_audio(audio, frame_size=2048, hop=512)` | 分帧，末尾零填充 |
| `compute_stft(frames, sr)` | Hanning 加窗 + rfft，返回幅度谱/相位谱/频率轴 |
| `calculate_short_time_energy(frame)` | 短时能量 = sum(x^2)/N |
| `calculate_zero_crossing_rate(frame)` | 过零率 = sign 变化次数 / 2 / N |
| `calculate_spectral_flatness(mag)` | 谱平坦度 = 几何均值 / 算术均值 |
| `detect_voiced_unvoiced(frame, mag, f0, sr)` | 综合评分：F0>0 (+0.4) + 能量 (+0.2) + ZCR (+0.2) + 平坦度 (+0.2) |
| `locate_harmonic_bands(f0, freqs)` | 在 n*F0 附近定位谐波带，容差 = max(15, F0*3%) |
| `extract_formants(mag, freqs)` | 15 点平滑 -> 峰值检测 -> 200~4000Hz 范围内取前 5 个 |
| `separate_three_components(mag, phase, freqs, f0, voiced)` | 核心分离：谐波掩码 + 共振峰掩码 + 气声比例分配 |
| `decompose_voice_frame(frame, mag, phase, freqs, f0, sr)` | 单帧完整处理：检测+分离+重构+能量归一化 |
| `decompose_voice(audio, sr, f0_seq)` | 主入口：逐帧分解 + overlap-add 重构 + 全局能量归一化 |

### 4.3 pyworld_processor.py — pyworld 参数处理

| 函数 | 说明 |
|---|---|
| `extract_pyworld_features(audio, sr)` | DIO F0 -> StoneMask 精修 -> CheapTrick sp -> D4C ap |
| `compute_jitter(f0, time_axis)` | jitter_local/RAP/PPQ5/DDP（百分比） |
| `compute_shimmer(f0, audio, sr, time_axis)` | shimmer_local/APQ3/APQ5/DDA（百分比） |
| `compute_oq(ap, f0, sr)` | 开口商：每周期 ap 前后半段能量比的均值 |
| `extract_glottal_pulse(f0, time_axis, sr)` | 生成高斯包络声门脉冲轨迹 |
| `compute_f0_statistics(f0)` | avg/min/max/std/voiced_ratio |
| `synthesize_pyworld(f0, sp, ap, sr)` | `pw.synthesize()` 参数合成 |

### 4.4 voice_synthesizer.py — 语音合成器

**VoiceSynthesizer 类**

| 方法 | 说明 |
|---|---|
| `synthesize_from_parameters(f0, jitter, shimmer, ...)` | 主入口：有 sp/ap 走 pyworld，否则走声门模型 |
| `_synthesize_with_pyworld(f0, sp, ap, target_rms)` | pyworld 合成 + 动态压缩 + 软削波 |
| `_synthesize_with_lf_trajectory(f0, lf_traj, jitter, shimmer)` | 逐帧 LF 参数合成（支持参数平滑过渡） |
| `synthesize_from_glottal_signal(glottal, f0, ...)` | 从已有声门信号合成（微分 + 共振峰滤波） |
| `synthesize_from_decomposition(result)` | 从分解结果字典直接合成 |
| `export_audio(signal, path, bit_depth, target_sr)` | 导出高保真音频（支持重采样） |
| `_resample(signal, src, dst)` | 线性插值重采样 |

**模块级函数**

| 函数 | 说明 |
|---|---|
| `_soft_clip(signal, threshold=0.85)` | tanh 软限幅器，超阈值部分平滑压缩 |
| `_apply_compression(signal, threshold=0.5, ratio=3.0)` | 动态范围压缩，超阈值峰值按比例缩小 |
| `synthesize_voice(f0, sr, jitter, shimmer, ...)` | 便捷函数：实例化合成器并合成 |
| `synthesize_voice_from_glottal(signal, sr, ...)` | 便捷函数：从声门信号合成 |
| `final_audio_processing(signal, sr, normalize, preemphasis)` | 后处理：预加重 + 归一化 |

### 4.5 pitch_shifter.py — 音高变换

| 函数 | 说明 |
|---|---|
| `calculate_pitch_factor(semitones)` | `k = 2^(semitones/12)` |
| `shift_f0_logarithmic(f0, semitones)` | 对数域平移 + 软限幅（550~900Hz tanh 压缩） |
| `smooth_f0_boundaries(f0, sr, frame_period)` | 首尾 10ms 淡入淡出 |
| `adjust_oq(oq, semitones)` | 自适应 OQ 修正（良好音域线性，超出加速，极限 0.30~0.75） |
| `enhance_breathiness(ap, semitones)` | 气声增强（+0.03/st，超出 +0.04/st，上限 2.5x） |
| `warp_spectral_envelope(sp, semitones, sr)` | 频谱包络频率轴映射（共振峰跟随 30%） |
| `soft_clip(signal, threshold=0.85)` | tanh 软削波 |
| `process_pitch_shift(f0, semitones, sr, oq, ap, sp)` | 一键处理全部参数，返回结果字典 |

### 4.6 glottal_models.py — 声门模型

**LFModel 类**（Liljencrants-Fant 模型）

| 参数 | 范围 | 说明 |
|---|---|---|
| `rd` | 0.0~1.0 | 减速系数，控制 cd 段斜率 |
| `ta` | 0.0~1.0 | 归一化上升时间 |
| `tb` | 0.0~1.0 | 归一化峰值时间 |
| `te` | 通常 1.0 | 归一化总周期 |

| 方法 | 说明 |
|---|---|
| `generate_pulse(t0, sr)` | 生成单周期 LF 波形（4 阶段：关闭->正弦上升->余弦衰减->关闭） |
| `generate_continuous(f0_seq, sr)` | 逐周期拼接生成连续声门气流 |

**RosenbergModel 类**

| 参数 | 范围 | 说明 |
|---|---|---|
| `alpha` | 0.0~1.0 | 归一化上升时间 |
| `epsilon` | 0.0~1.0 | 归一化峰值位置 |

| 方法 | 说明 |
|---|---|
| `generate_pulse(t0, sr)` | 上升段二次多项式 + 下降段三次多项式 |
| `generate_continuous(f0_seq, sr)` | 逐周期拼接 |

**GlottalPulseGenerator 类**：统一接口，根据 model_type 调用 LF 或 Rosenberg。

**模块级函数**

| 函数 | 说明 |
|---|---|
| `differentiate_glottal_flow(u_g)` | 声门气流微分得声源信号：`source = diff(u_g)` |
| `compute_glottal_parameters(signal, sr)` | 从声门信号提取 LF 参数（峰值、上升/下降时间、过零点） |

### 4.7 excitation_generator.py — 激励生成

**ExcitationGenerator 类**

| 方法 | 说明 |
|---|---|
| `generate_impulse_train(f0_seq, frame_period=5.0)` | 相位累加器生成精确冲激序列 |
| `generate_with_perturbations(f0_seq, jitter, shimmer)` | 加入 jitter（周期扰动 ±10%）和 shimmer（幅度扰动）的冲激序列 |

**模块级函数**

| 函数 | 说明 |
|---|---|
| `apply_jitter(f0, jitter_pct)` | 对 F0 轨迹施加高斯扰动 |
| `apply_shimmer(amp_seq, shimmer_pct)` | 对幅度序列施加高斯扰动 |
| `generate_white_noise(duration, sr, amp)` | 白噪声生成 |
| `generate_pink_noise(duration, sr, amp)` | 粉红噪声生成（FFT 域 1/sqrt(f) 滤波） |
| `generate_excitation(f0, sr, jitter, shimmer, noise_level)` | 完整激励 = 冲激序列 + 粉红噪声 |
| `convolve_with_pulse(excitation, pulse)` | 激励与声门脉冲卷积得声门气流 |

### 4.8 vocal_tract_filter.py — 声道滤波器

**VocalTractFilter 类**

| 方法 | 说明 |
|---|---|
| `apply_lpc_filter(signal, lpc_coeffs)` | 逐帧 LPC 全极点滤波 |
| `apply_formant_filter(signal, formants, bandwidths)` | 逐帧共振峰二阶 IIR 级联滤波 |
| `get_impulse_response(formants, bandwidths, length)` | 获取脉冲响应 |
| `get_lpc_impulse_response(lpc_coeffs, length)` | 获取 LPC 脉冲响应 |
| `get_frequency_response(formants, bandwidths, n_fft)` | 获取频率响应 (freqs, mag_db) |
| `get_lpc_frequency_response(lpc_coeffs, n_fft)` | 获取 LPC 频率响应 |
| `get_filter_coefficients(formants, bandwidths, frame_idx)` | 获取指定帧 (b, a) 系数 |

**模块级函数**

| 函数 | 说明 |
|---|---|
| `compute_lpc_coefficients(signal, sr, frame_size, hop, order)` | Levinson-Durbin 递归求解 LPC 系数 |
| `lpc_to_formants(lpc_coeffs, sr)` | LPC 多项式求根 -> 共振峰频率/带宽 |
| `apply_preemphasis(signal, alpha=0.97)` | 预加重 `y[i] = x[i] - alpha*x[i-1]` |
| `apply_deemphasis(signal, alpha=0.97)` | 去加重 `y[i] = x[i] + alpha*y[i-1]` |
| `synthesize_formant_filtered_voice(source, sr, formants)` | 预加重 -> 共振峰滤波 -> 去加重 -> 归一化 |
| `synthesize_lpc_filtered_voice(source, sr, lpc_coeffs)` | 预加重 -> LPC 滤波 -> 去加重 -> 归一化 |

---

## 五、项目结构

### 5.1 整体布局

```
VocalB1/
├── VocalB1Synthesizer.py      # 主入口：多段有声帧拼接合成（节拍编排 + 气声 + 爆破）
│                              #   库用法：synthesize(segments, output_path, ...)
├── VocalDecomposer.py         # 分解封装（WAV -> 逐帧参数）
├── VocalSynthesizer.py        # 合成封装（逐帧参数 -> WAV）
├── prosses_breath.py          # 气声拼接（由 VocalB1Synthesizer 内部调用）
├── tools/                     # 辅助工具（引擎运行不依赖）
│   ├── spectrogram.py         # STFT 语谱图查看（支持框选放大）
│   ├── oscilloscope.py        # 波形查看
│   ├── pit_editor.py          # 音高曲线编辑器（pygame）
│   ├── _plosive_diag.py       # 爆破音诊断
│   └── test_vocal_lib.py      # 回归测试（库功能自检，直接运行）
├── _output/                   # 合成产物输出目录
└── vocal_lib/                 # 核心库（详见 5.2）
```

### 5.2 vocal_lib 内部

```
vocal_lib/
├── __init__.py            # 统一导出 + 元数据
├── data_classes.py        # F0 / WAVAudio / GlottalExcitation
├── engine.py              # 核心引擎（分解 / 合成）
├── decomposition.py       # decompose_vocal() 顶层封装
├── synthesis.py           # synthesize_vocal() 顶层封装
├── pitch.py               # pitch_shift() 顶层封装
└── core/                  # 底层实现
    ├── f0_detection.py    # F0 检测（pyworld + 自相关 + 倒谱 + YIN）
    ├── voice_separation.py # 声道分离（STFT + 谐波掩码 + 三分量）
    ├── pyworld_processor.py # pyworld 参数提取/合成
    ├── voice_synthesizer.py # 语音合成器（pyworld + LF/Rosenberg + 共振峰滤波）
    ├── vocal_tract_filter.py # 声道滤波器（LPC + 共振峰 IIR）
    ├── pitch_shifter.py   # 音高变换（F0缩放 + OQ修正 + 频谱映射 + 防破音）
    ├── glottal_models.py  # 声门模型（LF + Rosenberg）
    └── excitation_generator.py # 激励生成（冲激序列 + 微扰 + 噪声）
```

---

## 六、完整示例

```python
from vocal_lib import *
import numpy as np
import os

os.makedirs("_output", exist_ok=True)

# ========== 1. 分解 ==========
f0, breath, glot = decompose_vocal("ai.wav")
print(f"F0: {f0}")
print(f"气声: {breath}")
print(f"声门: {glot}")

# ========== 2. 查看参数 ==========
if glot.jitter:
    print(f"Jitter: {glot.jitter['jitter_local']:.3f}%")
if glot.shimmer:
    print(f"Shimmer: {glot.shimmer['shimmer_local']:.3f}%")
print(f"OQ: {glot.oq:.3f}")

# ========== 3. 音高变换 ==========
f0_new, glot_new = pitch_shift(f0, glot, "C4", "E4")  # 升 4 半音

# ========== 4. 合成 ==========
output = synthesize_vocal(f0_new, breath, glot_new, match_rms_to=0.15)
output.save("_output/ai_to_E4.wav", bit_depth=24)

# ========== 5. 从 numpy 数组直接处理 ==========
audio_np = output.data  # 获取 numpy 数组
f0_2, breath_2, glot_2 = decompose_vocal(audio_np, sample_rate=44100)
output_2 = synthesize_vocal(f0_2, breath_2, glot_2)
output_2.save("_output/roundtrip.wav")

# ========== 6. 序列化/反序列化 ==========
f0.save("_output/f0.npy")
glot.save("_output/glot.npy")
f0_loaded = F0.load("_output/f0.npy")
glot_loaded = GlottalExcitation.load("_output/glot.npy")
```

---

## 七、回归测试

`tools/test_vocal_lib.py` 对库做端到端自检，共 7 项：

| 用例 | 校验内容 |
|---|---|
| 包导入与公开 API | `vocal_lib.__all__` 全部符号可访问 |
| 数据类 | F0 / WAVAudio / GlottalExcitation 构造、属性、参数读写、深拷贝 |
| WAV 读写往返 | 16bit 存盘再读回，峰值误差 < 1e-3 |
| decompose_vocal | 真实音频分解出 F0 / 气声 / 声门激励，sp·ap 齐备 |
| pitch_shift | 变换后频率比严格等于 `2^(半音/12)`（避开软限幅区取 +2 半音） |
| synthesize_vocal | 输出非空、无 NaN、能量正常 |
| 端到端 | `VocalB1Synthesizer.synthesize` 落盘成功且中间产物已清理 |

```bash
C:\Python312\python.exe tools\test_vocal_lib.py            # 全部用例
C:\Python312\python.exe tools\test_vocal_lib.py --no-e2e   # 跳过端到端（省时）
```

测试产物写入 `_test_output/` 便于试听排查；任一用例失败时退出码非 0。
