# vocal_lib

[English](README.md) | [中文](README.zh-CN.md) | **日本語**

音声分析・合成ライブラリ — オブジェクト指向の声門励振の分解 / 合成 / ピッチ変換ツールキット。

## インストール

```bash
# 方法 1：pip によるインストール（推奨）
pip install .
pip install -e .          # 開発モード
pip install .[full]       # pyworld を含む

# 方法 2：vocal_lib/ フォルダをプロジェクトに直接コピーする
```

## 依存関係

| 依存パッケージ | 必須 | 用途 |
|---|---|---|
| numpy >= 1.20 | はい | 数値計算 |
| soundfile >= 0.12 | はい | WAV の読み書き |
| pyworld >= 0.3.0 | いいえ | F0/sp/ap の抽出（未インストールの場合は機能が制限される） |

## クイックスタート

```python
from vocal_lib import decompose_vocal, synthesize_vocal, pitch_shift

# 1. 分解：WAV -> F0 + 息成分 + 声門励振
f0, breath, glottal = decompose_vocal("ai.wav")

# 2. ピッチ変換：C4 -> E4（4 半音上げ）
f0_new, glottal_new = pitch_shift(f0, glottal, "C4", "E4")

# 3. 合成：F0 + 息成分 + 声門励振 -> WAV
output = synthesize_vocal(f0_new, breath, glottal_new)
output.save("_output/result.wav", bit_depth=24)
```

---

## 1. データクラス（data_classes.py）

### 1.1 F0 — 基本周波数軌跡クラス

F0 値の系列、時間軸、有声音フラグなどのメタデータを保持する純粋なデータコンテナ。

**属性**

| 属性 | 型 | 説明 |
|---|---|---|
| `values` | `np.ndarray[float64]` | F0 値の系列（Hz）。無音フレームは 0 |
| `time_axis` | `np.ndarray[float64]` | 対応する時間軸（秒） |
| `frame_period` | `float` | フレーム周期（ミリ秒）。デフォルトは 5.0 |
| `sample_rate` | `int` | サンプリング周波数 |
| `voiced_flags` | `np.ndarray[bool]` | 有声音フラグ（True=有声） |

**プロパティ**

| プロパティ | 戻り値 | 説明 |
|---|---|---|
| `n_frames` | `int` | フレーム数 = len(values) |

**メソッド**

| メソッド | 説明 |
|---|---|
| `copy()` | 深いコピー。新しい F0 オブジェクトを返す |
| `save(path)` | `.npy` ファイルへシリアライズ（dict 形式） |
| `load(path)` | クラスメソッド。`.npy` からデシリアライズ |
| `__len__()` | フレーム数を返す |
| `__repr__()` | 要約文字列 `F0(n_frames=..., avg=...Hz)` を返す |

**内部実装**：構築時に `time_axis` が指定されていない場合は `arange(n) * frame_period / 1000` で自動生成する。`voiced_flags` が指定されていない場合は `values > 0` で自動生成する。save/load は `np.save` を用いて dict を `.npy` としてまとめる。

---

### 1.2 WAVAudio — WAV 音声クラス

音声サンプルデータとサンプリング周波数を保持し、ファイルの読み書きをサポートする。

**属性**

| 属性 | 型 | 説明 |
|---|---|---|
| `data` | `np.ndarray[float64]` | 音声サンプルデータ（モノラルは 1 次元、マルチチャンネルは 2 次元） |
| `sample_rate` | `int` | サンプリング周波数 |
| `source_path` | `Optional[str]` | 元ファイルのパス |

**プロパティ**

| プロパティ | 戻り値 | 説明 |
|---|---|---|
| `duration` | `float` | 長さ（秒）= len(data) / sample_rate |
| `n_samples` | `int` | サンプル数 |
| `n_channels` | `int` | チャンネル数 |
| `is_mono` | `bool` | モノラルかどうか |
| `rms` | `float` | RMS エネルギー値 |

**メソッド**

| メソッド | 説明 |
|---|---|
| `from_file(path, mono=True)` | クラスメソッド。soundfile で WAV ファイルを読み込み、`mono=True` のとき自動でモノラルにミックスする |
| `to_mono()` | マルチチャンネルをモノラルに変換（平均を取る）。すでにモノラルの場合は copy を返す |
| `save(path, bit_depth=24, target_sample_rate=None)` | WAV として保存。PCM_8/16/24 ビット深度に対応し、リサンプリングも指定可能 |
| `copy()` | 深いコピー |
| `__len__()` | サンプル数を返す |
| `__repr__()` | 要約文字列を返す |

**内部実装**：`save()` は内部でまず正規化（ピークが 1.0 を超える場合にスケーリング）を行い、その後 `soundfile.write` で書き込む。リサンプリングには線形補間を用いる。ビット深度は `subtype_map = {8: 'PCM_U8', 16: 'PCM_16', 24: 'PCM_24'}` でマッピングする。

---

### 1.3 GlottalExcitation — 声門励振クラス

声門の時間領域信号、および pyworld パラメータ、開商、強度軌跡などを保持する。

**属性**

| 属性 | 型 | 説明 |
|---|---|---|
| `signal` | `np.ndarray[float64]` | 声門励振の時間領域信号 |
| `sample_rate` | `int` | サンプリング周波数 |
| `sp` | `Optional[np.ndarray]` | pyworld のスペクトル包絡 (n_frames, n_bins) |
| `ap` | `Optional[np.ndarray]` | pyworld の非周期性指標 (n_frames, n_bins) |
| `oq` | `float` | 開商 Open Quotient |
| `glottal_intensity` | `Optional[np.ndarray]` | フレームごとの声門強度比 |
| `parameters` | `Dict[str, Any]` | 拡張パラメータの辞書 |

**`parameters` 辞書に含められるキー**

| キー | 型 | 説明 |
|---|---|---|
| `jitter` | `Dict[str, float]` | 周波数ゆらぎ：jitter_local, jitter_rap, jitter_ppq5, jitter_ddp |
| `shimmer` | `Dict[str, float]` | 振幅ゆらぎ：shimmer_local, shimmer_apq3, shimmer_apq5, shimmer_dda |
| `formants` | `List[List[Dict]]` | フォルマント軌跡：各フレームに F1-F5 の frequency/magnitude/index を含む |
| `f0_stats` | `Dict[str, float]` | F0 統計：avg_f0, min_f0, max_f0, f0_std, voiced_ratio |
| `glottal_pulse` | `np.ndarray` | 声門パルス軌跡 |
| `formant_intensity` | `np.ndarray` | フレームごとのフォルマント強度比 |
| `breath_intensity` | `np.ndarray` | フレームごとの息成分強度比 |
| `_pitch_shift_info` | `Dict` | ピッチ変換のメタデータ（semitones, pitch_factor, max_f0 など） |

**プロパティ**

| プロパティ | 戻り値 | 説明 |
|---|---|---|
| `duration` | `float` | 長さ（秒） |
| `n_samples` | `int` | サンプル数 |
| `has_pyworld_params` | `bool` | sp と ap の両方が存在するか |
| `jitter` | `Optional[Dict]` | parameters['jitter'] への簡易アクセス |
| `shimmer` | `Optional[Dict]` | parameters['shimmer'] への簡易アクセス |

**メソッド**

| メソッド | 説明 |
|---|---|
| `get_param(key, default=None)` | parameters 辞書から値を取得 |
| `set_param(key, value)` | parameters 辞書へ書き込み |
| `copy()` | 深いコピー（parameters 内のコピー可能な値をすべて含む） |
| `save(path)` | `.npy` へシリアライズ |
| `load(path)` | クラスメソッド。`.npy` からデシリアライズ |

---

## 2. トップレベル API 関数

### 2.1 decompose_vocal — 音声の分解

**場所**：`vocal_lib/decomposition.py`

```python
decompose_vocal(input_audio, sample_rate=44100, frame_period=5.0) -> (F0, WAVAudio, GlottalExcitation)
```

**機能**：入力音声を 3 つの成分（F0 基本周波数軌跡、息成分 WAV、声門励振）に分解する。

**入力**：`input_audio` は次の 3 種類に対応する。
- `str`：ファイルパス。`WAVAudio.from_file()` で読み込む
- `WAVAudio`：そのまま使用
- `np.ndarray`：`sample_rate` の指定が必要

**処理フロー**：

```
入力音声
  |
  v
_normalize_input()  ----> WAVAudio オブジェクト
  |
  v
engine.decompose_vocal(data_mono, sr)
  |
  |  1. f0_detection.detect_f0_sequence()
  |     -> pyworld DIO + StoneMask（優先）
  |     -> または独自の 3 手法融合（自己相関 + ケプストラム + YIN）
  |     -> メディアンフィルタ + Viterbi 平滑化
  |
  |  2. voice_separation.decompose_voice()
  |     -> フレーム分割（2048pt, hop=512）
  |     -> STFT（Hanning 窓）
  |     -> フレームごとの有声音/無声音判定（エネルギー + ZCR + スペクトル平坦度）
  |     -> 倍音帯の特定 -> 3 成分マスク分離
  |        - 声門成分 = 倍音帯 * (1 - breath_ratio)
  |        - フォルマント成分 = フォルマント領域 * (1 + breath_ratio)
  |        - 息成分 = 非倍音領域 * breath_ratio
  |     -> IRFFT 再構成 + overlap-add 合成
  |     -> エネルギー正規化
  |
  |  3. pyworld_processor.extract_pyworld_features()（任意）
  |     -> DIO で F0 を抽出
  |     -> StoneMask で F0 を精緻化
  |     -> CheapTrick でスペクトル包絡 sp を抽出
  |     -> D4C で非周期性指標 ap を抽出
  |     -> compute_jitter() / compute_shimmer()
  |     -> compute_oq() / compute_f0_statistics()
  |
  v
(F0, WAVAudio_breath, GlottalExcitation) としてカプセル化
```

**内部アルゴリズムの詳細**：

1. **F0 検出**（`f0_detection.py`）
   - pyworld の DIO アルゴリズム（倍音構造に基づく）+ StoneMask による精緻化を優先する
   - pyworld が利用できない場合は 3 手法の融合にフォールバックする
     - **自己相関法**：Hanning 窓を掛けて自己相関を計算し、min_lag〜max_lag の範囲でピークを探索。倍音の整数倍チェックを行う
     - **ケプストラム法**：振幅スペクトルの対数を取り IFFT してケプストラムを求め、ケフレンシー領域でピークを探索
     - **YIN アルゴリズム**：差分関数 + 累積平均正規化差分関数を計算し、しきい値を下回る局所最小値を探索。放物線補間で精緻化
   - **融合戦略**：3 手法の結果の標準偏差が 30Hz 未満のときは重み付き平均（YIN の重み 0.4、その他 0.3）、そうでない場合は信頼度が最も高いものを採用
   - **後処理**：5 フレームのメディアンフィルタ + Viterbi 風の平滑化（不連続の処理と無音区間の補間）

2. **声道分離**（`voice_separation.py`）
   - **フレーム分割**：frame_size=2048, hop_size=512, Hanning 窓
   - **STFT**：`rfft` で振幅スペクトルと位相スペクトルを計算
   - **有声音判定**：短時間エネルギー（しきい値 0.001）、零交差率（しきい値 0.15）、スペクトル平坦度（しきい値 0.3）を総合してスコア化
   - **倍音帯の特定**：F0 を基本周波数とし、各次倍音の周波数付近（許容幅 = max(15Hz, F0*3%)）で倍音帯を特定
   - **3 成分分離**：
     - 倍音マスク：倍音帯の内側を 1 とし、3 点平滑化を行う
     - フォルマントマスク：フォルマント周波数の付近で三角窓（帯域幅 80Hz）
     - 息成分比：スペクトル平坦度から決定（最大 0.3）
     - 比率に応じてエネルギーを配分し、3 成分の和が元のスペクトルと等しくなるようにする
   - **再構成**：IRFFT で時間領域フレームを復元 + オーバーラップ加算で結合 + エネルギー補正

3. **pyworld パラメータ抽出**（`pyworld_processor.py`）
   - `pw.dio()`：F0 の粗推定
   - `pw.stonemask()`：F0 の精緻化
   - `pw.cheaptrick()`：スペクトル包絡 sp
   - `pw.d4c()`：非周期性指標 ap
   - **Jitter** の計算：jitter_local（隣接フレーム差分の平均/平均）、RAP（3 点平均）、PPQ5（5 点平均）、DDP（2 階差分）
   - **Shimmer** の計算：shimmer_local（隣接フレーム振幅差分の平均/平均）、APQ3、APQ5、DDA
   - **OQ** の計算：各周期の ap を前半・後半に分割し、エネルギー比の平均を取る
   - **F0 統計**：avg/min/max/std/voiced_ratio

---

### 2.2 synthesize_vocal — 音声の合成

**場所**：`vocal_lib/synthesis.py`

```python
synthesize_vocal(f0, breath, glottal, *, match_rms_to=None, model_type='lf') -> WAVAudio
```

**機能**：F0 + 息成分 + 声門励振から音声を再合成する。

**パラメータ**：

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `f0` | `F0` | - | F0 基本周波数軌跡 |
| `breath` | `WAVAudio` | - | 息成分 |
| `glottal` | `GlottalExcitation` | - | 声門励振 |
| `match_rms_to` | `Optional[float]` | None | 目標 RMS エネルギー |
| `model_type` | `str` | `'lf'` | 声門モデル：`'lf'` または `'rosenberg'` |

**処理フロー**：

```
F0 + GlottalExcitation
  |
  v
パラメータ抽出: f0.values, sp, ap, oq, jitter, shimmer, formants
  |
  v
engine.synthesize_voice_from_parameters()
  |
  |  経路 A（pyworld が利用可能 + sp + ap）:
  |    VoiceSynthesizer._synthesize_with_pyworld()
  |    -> pw.synthesize(f0, sp, ap, sr)
  |    -> ダイナミックレンジ圧縮 (threshold=0.5, ratio=3:1)
  |    -> RMS マッチング
  |    -> ソフトクリッピング（tanh リミッタ, threshold=0.85）
  |    -> 0.9 に正規化
  |
  |  経路 B（pyworld なし）:
  |    -> ExcitationGenerator.generate_with_perturbations(f0, jitter, shimmer)
  |       （位相アキュムレータでインパルス列を生成 + jitter/shimmer の揺らぎ）
  |    -> GlottalPulseGenerator.generate_pulse(t0, sr)
  |       （LF モデルまたは Rosenberg モデルで 1 周期の声門パルスを生成）
  |    -> convolve_with_pulse(excitation, pulse)
  |    -> differentiate_glottal_flow(u_g)  （声門气流の微分で音源を得る）
  |    -> synthesize_formant_filtered_voice(source, sr, formants)
  |       （プリエンファシス -> VocalTractFilter フォルマントフィルタ -> デエンファシス）
  |
  v
WAVAudio(data=synthesized, sample_rate=sr)
```

**内部アルゴリズムの詳細**：

1. **pyworld による合成**（`pyworld_processor.py`）
   - `pw.synthesize(f0, sp, ap, sr, frame_period)`：パラメータから直接音声を合成
   - 合成後はダイナミックレンジ圧縮 + ソフトクリッピングで破音を防ぐ

2. **声門モデルによる合成**（`glottal_models.py` + `excitation_generator.py`）
   - **LF モデル**（Liljencrants-Fant）：4 段階の声門気流モデル
     - 閉鎖段階（0 ~ ta）：出力 0
     - 開放段階（ta ~ tb）：正弦上昇 `sin(pi * t1)`
     - 減速段階（tb ~ te）：余弦二乗減衰 `cos(pi*t2/2)^2 * (1 - rd*t2)`
     - 復帰段階（te ~ 1）：出力 0
   - **Rosenberg モデル**：二項式 + 三項式の組み合わせ
     - 上昇部（0 ~ epsilon）：二次多項式 `2t/eps - (t/eps)^2`
     - 下降部（epsilon ~ 1）：三次多項式 `(1-alpha)(1-3t'^2+2t'^3) + alpha(1-t')`
   - **励振生成**：`ExcitationGenerator` が位相アキュムレータでフレームごとにインパルス列を生成し、jitter（周期の揺らぎ ±10%）と shimmer（振幅の揺らぎ）を加える
   - **畳み込み**：励振信号と声門パルスを畳み込んで声門気流 `u_g` を得る
   - **微分**：`diff(u_g)` で声門音源を得る（声帯閉鎖瞬間の負のインパルスを模擬する）

3. **声道フィルタリング**（`vocal_tract_filter.py`）
   - **プリエンファシス**：`y[i] = x[i] - 0.97 * x[i-1]`、高域を強調
   - **フォルマントフィルタ**：`VocalTractFilter.apply_formant_filter()` がフレームごとに 2 次 IIR 共振フィルタを適用
     - 各フォルマントごとにバイクワッド（biquad）を構成：`omega = 2*pi*f/sr`, `alpha = sin(omega)*sinh(ln2/2 * bw * 2*pi/sr)`
     - 分子 `[1, -2cos(omega), 1]`、分母 `[1+alpha, -2cos(omega), 1-alpha]`
     - 複数のフォルマントを縦続接続（係数の畳み込み）
   - **デエンファシス**：`y[i] = x[i] + 0.97 * y[i-1]`、低域を復元

---

### 2.3 pitch_shift — ピッチ変換

**場所**：`vocal_lib/pitch.py`

```python
pitch_shift(f0, glottal, original_pitch, target_pitch) -> (F0, GlottalExcitation)
```

**機能**：元のピッチと目標ピッチに基づいて F0 と声門パラメータを変更し、新しいオブジェクトを返す。

**ピッチの表現**（`_parse_semitones()` が対応する形式）：

| 形式 | 例 | 解析ロジック |
|---|---|---|
| 音名表記 | `"C4"`, `"A4"`, `"F#5"`, `"Bb3"` | MIDI = (octave+1)*12 + 音名オフセット、半音差 = MIDI - 60 |
| Hz | `"440Hz"`, `440`（50 より大きい数値） | `12 * log2(hz / C4_hz)`、C4 = 261.63Hz |
| プロジェクト独自形式 | `"1D"`, `"-1C"`, `"0C#"` | オクターブオフセット*12 + 音名オフセット |
| 半音数 | `+3`, `-5`, `0.0`（50 以下の数値） | そのまま使用 |

**処理フロー**：

```
original_pitch, target_pitch
  |
  v
_parse_semitones() -> 変換元の半音数, 変換先の半音数
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
  |     -> f0_new = exp(log(f0) + log(k))  [対数領域でのシフト]
  |     -> ソフトリミッティング: 550Hz ソフトニー, 900Hz ハードリミット (tanh 圧縮)
  |
  |  3. smooth_f0_boundaries(f0_shifted)
  |     -> 先頭と末尾 10ms のフェードイン/フェードアウト
  |
  |  4. adjust_oq(oq, semitones)
  |     -> 昇調: OQ を減少（良好音域 -0.01/st, 逸脱時 -0.02/st, 下限 0.30）
  |     -> 降調: OQ を増加（良好音域 +0.008/st, 逸脱時 +0.015/st, 上限 0.75）
  |
  |  5. enhance_breathiness(ap, semitones)
  |     -> ap *= (1 + semitones * 0.03), 良好音域を超える場合は追加で +0.04/st
  |     -> 上限 2.5x, [0, 1] に clip
  |
  |  6. warp_spectral_envelope(sp, semitones, sr)  [フォルマント衝突による破音の防止]
  |     -> フォルマントはピッチに 30% 追従して上方移動し、元の位置を 70% 保持
  |     -> 周波数軸の補間マッピング
  |
  v
(新しい F0, 新しい GlottalExcitation) としてカプセル化
  |
  v
GlottalExcitation は sp_warped（周波数軸マッピング後のスペクトル包絡）を使用
parameters に _pitch_shift_info メタデータを書き込む
```

**破音防止機構（5 段階）**：

| 機構 | 位置 | 効果 |
|---|---|---|
| F0 ソフトリミッティング | `shift_f0_logarithmic()` | 550〜900Hz の tanh 圧縮。ファルセット領域への移行を模擬する |
| OQ 加速補正 | `adjust_oq()` | 極端な高音では OQ を 0.30 まで下げ、極端な低音では 0.75 まで上げる |
| スペクトル包絡マッピング | `warp_spectral_envelope()` | フォルマントを 30% 追従させ、倍音とフォルマントの衝突を避ける |
| ダイナミックレンジ圧縮 | `voice_synthesizer._apply_compression()` | 0.5 を超えるピークを 3:1 で圧縮する |
| ソフトクリッピング | `voice_synthesizer._soft_clip()` | 0.85 を超える振幅を tanh で滑らかに制限する |

---

## 3. エンジン層（engine.py）

エンジン層はトップレベル API と下位のコアモジュールをつなぐ橋渡しであり、すべての import はパッケージ内の相対インポートを使用する。

### 3.1 decompose_vocal(audio_data, sample_rate) -> Dict

**フロー**：
1. `detect_f0_sequence(audio_data, sr)` -> F0 軌跡
2. `decompose_voice(audio_data, sr, f0_sequence)` -> 3 成分分離
3. `extract_pyworld_features(audio_data, sr)` -> sp/ap/jitter/shimmer/oq/f0_stats（任意）
4. 結果の辞書を組み立てて返す

### 3.2 synthesize_voice_from_parameters(f0_sequence, sample_rate, jitter, shimmer, formants_trajectory, model_type, sp, ap, target_rms, glottal_parameters) -> np.ndarray

**フロー**：
1. `VoiceSynthesizer(sample_rate, model_type)` をインスタンス化
2. `synthesizer.synthesize_from_parameters()` を呼び出す
3. sp/ap の有無に応じて pyworld 合成か声門モデル合成を選択する

---

## 4. コアモジュールの詳細（core/）

### 4.1 f0_detection.py — F0 検出

| 関数 | アルゴリズム | 説明 |
|---|---|---|
| `detect_f0_autocorrelation(frame, sr)` | 自己相関 | Hanning 窓掛け -> 自己相関 -> 倍音の整数倍チェック -> 信頼度しきい値 0.3 |
| `detect_f0_cepstrum(mag_spec, sr, freqs)` | ケプストラム | log|FFT| -> IFFT -> ケフレンシー領域でのピーク検出 |
| `yin_f0_detection(frame, sr)` | YIN | 差分関数 -> 累積平均正規化 -> しきい値 0.15 -> 放物線補間で精緻化 |
| `fuse_f0_methods(...)` | 融合 | std が 30Hz 未満なら重み付き平均（YIN 0.4、その他 0.3）、そうでなければ最も信頼度の高いものを採用 |
| `smooth_f0(f0, win=5)` | メディアンフィルタ | 5 フレーム窓でのメディアンフィルタ |
| `viterbi_smooth(f0, ...)` | Viterbi 風 | 不連続ペナルティ + 無音区間の補間（3 フレーム未満の連続無音では直前値の 50% を保持） |
| `detect_f0_sequence(audio, sr)` | メインエントリ | pyworld を優先し、3 手法の融合にフォールバック |

### 4.2 voice_separation.py — 声道分離

| 関数 | 説明 |
|---|---|
| `frame_audio(audio, frame_size=2048, hop=512)` | フレーム分割。末尾はゼロ埋め |
| `compute_stft(frames, sr)` | Hanning 窓掛け + rfft。振幅スペクトル/位相スペクトル/周波数軸を返す |
| `calculate_short_time_energy(frame)` | 短時間エネルギー = sum(x^2)/N |
| `calculate_zero_crossing_rate(frame)` | 零交差率 = sign の変化回数 / 2 / N |
| `calculate_spectral_flatness(mag)` | スペクトル平坦度 = 幾何平均 / 算術平均 |
| `detect_voiced_unvoiced(frame, mag, f0, sr)` | 総合スコア：F0>0 (+0.4) + エネルギー (+0.2) + ZCR (+0.2) + 平坦度 (+0.2) |
| `locate_harmonic_bands(f0, freqs)` | n*F0 の付近で倍音帯を特定。許容幅 = max(15, F0*3%) |
| `extract_formants(mag, freqs)` | 15 点平滑化 -> ピーク検出 -> 200〜4000Hz の範囲で先頭 5 個を取得 |
| `separate_three_components(mag, phase, freqs, f0, voiced)` | 中核の分離：倍音マスク + フォルマントマスク + 息成分比の配分 |
| `decompose_voice_frame(frame, mag, phase, freqs, f0, sr)` | 単一フレームの一括処理：検出+分離+再構成+エネルギー正規化 |
| `decompose_voice(audio, sr, f0_seq)` | メインエントリ：フレームごとの分解 + オーバーラップ加算による再構成 + 全体のエネルギー正規化 |

### 4.3 pyworld_processor.py — pyworld パラメータ処理

| 関数 | 説明 |
|---|---|
| `extract_pyworld_features(audio, sr)` | DIO による F0 -> StoneMask で精緻化 -> CheapTrick で sp -> D4C で ap |
| `compute_jitter(f0, time_axis)` | jitter_local/RAP/PPQ5/DDP（パーセント） |
| `compute_shimmer(f0, audio, sr, time_axis)` | shimmer_local/APQ3/APQ5/DDA（パーセント） |
| `compute_oq(ap, f0, sr)` | 開商：各周期の ap の前半・後半のエネルギー比の平均 |
| `extract_glottal_pulse(f0, time_axis, sr)` | ガウス包絡の声門パルス軌跡を生成 |
| `compute_f0_statistics(f0)` | avg/min/max/std/voiced_ratio |
| `synthesize_pyworld(f0, sp, ap, sr)` | `pw.synthesize()` によるパラメータ合成 |

### 4.4 voice_synthesizer.py — 音声合成器

**VoiceSynthesizer クラス**

| メソッド | 説明 |
|---|---|
| `synthesize_from_parameters(f0, jitter, shimmer, ...)` | メインエントリ：sp/ap があれば pyworld、なければ声門モデルを使用 |
| `_synthesize_with_pyworld(f0, sp, ap, target_rms)` | pyworld 合成 + ダイナミック圧縮 + ソフトクリッピング |
| `_synthesize_with_lf_trajectory(f0, lf_traj, jitter, shimmer)` | フレームごとの LF パラメータ合成（パラメータの滑らかな遷移に対応） |
| `synthesize_from_glottal_signal(glottal, f0, ...)` | 既存の声門信号からの合成（微分 + フォルマントフィルタ） |
| `synthesize_from_decomposition(result)` | 分解結果の辞書から直接合成 |
| `export_audio(signal, path, bit_depth, target_sr)` | 高忠実度音声のエクスポート（リサンプリング対応） |
| `_resample(signal, src, dst)` | 線形補間によるリサンプリング |

**モジュールレベル関数**

| 関数 | 説明 |
|---|---|
| `_soft_clip(signal, threshold=0.85)` | tanh ソフトリミッタ。しきい値を超える部分を滑らかに圧縮 |
| `_apply_compression(signal, threshold=0.5, ratio=3.0)` | ダイナミックレンジ圧縮。しきい値を超えるピークを比率に応じて縮小 |
| `synthesize_voice(f0, sr, jitter, shimmer, ...)` | 簡易関数：合成器をインスタンス化して合成 |
| `synthesize_voice_from_glottal(signal, sr, ...)` | 簡易関数：声門信号から合成 |
| `final_audio_processing(signal, sr, normalize, preemphasis)` | 後処理：プリエンファシス + 正規化 |

### 4.5 pitch_shifter.py — ピッチ変換

| 関数 | 説明 |
|---|---|
| `calculate_pitch_factor(semitones)` | `k = 2^(semitones/12)` |
| `shift_f0_logarithmic(f0, semitones)` | 対数領域でのシフト + ソフトリミッティング（550〜900Hz の tanh 圧縮） |
| `smooth_f0_boundaries(f0, sr, frame_period)` | 先頭と末尾 10ms のフェードイン/フェードアウト |
| `adjust_oq(oq, semitones)` | 適応的 OQ 補正（良好音域では線形、逸脱時は加速、限界 0.30〜0.75） |
| `enhance_breathiness(ap, semitones)` | 息成分の強調（+0.03/st、逸脱時 +0.04/st、上限 2.5x） |
| `warp_spectral_envelope(sp, semitones, sr)` | スペクトル包絡の周波数軸マッピング（フォルマントが 30% 追従） |
| `soft_clip(signal, threshold=0.85)` | tanh ソフトクリッピング |
| `process_pitch_shift(f0, semitones, sr, oq, ap, sp)` | すべてのパラメータを一括処理し、結果の辞書を返す |

### 4.6 glottal_models.py — 声門モデル

**LFModel クラス**（Liljencrants-Fant モデル）

| パラメータ | 範囲 | 説明 |
|---|---|---|
| `rd` | 0.0~1.0 | 減速係数。cd 区間の傾きを制御する |
| `ta` | 0.0~1.0 | 正規化された上昇時間 |
| `tb` | 0.0~1.0 | 正規化されたピーク時間 |
| `te` | 通常 1.0 | 正規化された全周期 |

| メソッド | 説明 |
|---|---|
| `generate_pulse(t0, sr)` | 1 周期の LF 波形を生成（4 段階：閉鎖->正弦上昇->余弦減衰->閉鎖） |
| `generate_continuous(f0_seq, sr)` | 周期ごとに連結して連続的な声門気流を生成 |

**RosenbergModel クラス**

| パラメータ | 範囲 | 説明 |
|---|---|---|
| `alpha` | 0.0~1.0 | 正規化された上昇時間 |
| `epsilon` | 0.0~1.0 | 正規化されたピーク位置 |

| メソッド | 説明 |
|---|---|
| `generate_pulse(t0, sr)` | 上昇部の二次多項式 + 下降部の三次多項式 |
| `generate_continuous(f0_seq, sr)` | 周期ごとに連結 |

**GlottalPulseGenerator クラス**：共通インターフェース。model_type に応じて LF または Rosenberg を呼び出す。

**モジュールレベル関数**

| 関数 | 説明 |
|---|---|
| `differentiate_glottal_flow(u_g)` | 声門気流を微分して音源信号を得る：`source = diff(u_g)` |
| `compute_glottal_parameters(signal, sr)` | 声門信号から LF パラメータを抽出（ピーク、上昇/下降時間、零交差点） |

### 4.7 excitation_generator.py — 励振生成

**ExcitationGenerator クラス**

| メソッド | 説明 |
|---|---|
| `generate_impulse_train(f0_seq, frame_period=5.0)` | 位相アキュムレータで正確なインパルス列を生成 |
| `generate_with_perturbations(f0_seq, jitter, shimmer)` | jitter（周期の揺らぎ ±10%）と shimmer（振幅の揺らぎ）を加えたインパルス列 |

**モジュールレベル関数**

| 関数 | 説明 |
|---|---|
| `apply_jitter(f0, jitter_pct)` | F0 軌跡にガウス揺らぎを加える |
| `apply_shimmer(amp_seq, shimmer_pct)` | 振幅系列にガウス揺らぎを加える |
| `generate_white_noise(duration, sr, amp)` | 白色雑音の生成 |
| `generate_pink_noise(duration, sr, amp)` | ピンク雑音の生成（FFT 領域で 1/sqrt(f) フィルタ） |
| `generate_excitation(f0, sr, jitter, shimmer, noise_level)` | 完全な励振 = インパルス列 + ピンク雑音 |
| `convolve_with_pulse(excitation, pulse)` | 励振と声門パルスの畳み込みで声門気流を得る |

### 4.8 vocal_tract_filter.py — 声道フィルタ

**VocalTractFilter クラス**

| メソッド | 説明 |
|---|---|
| `apply_lpc_filter(signal, lpc_coeffs)` | フレームごとの LPC 全極フィルタ |
| `apply_formant_filter(signal, formants, bandwidths)` | フレームごとのフォルマント 2 次 IIR 縦続フィルタ |
| `get_impulse_response(formants, bandwidths, length)` | インパルス応答を取得 |
| `get_lpc_impulse_response(lpc_coeffs, length)` | LPC インパルス応答を取得 |
| `get_frequency_response(formants, bandwidths, n_fft)` | 周波数応答を取得 (freqs, mag_db) |
| `get_lpc_frequency_response(lpc_coeffs, n_fft)` | LPC 周波数応答を取得 |
| `get_filter_coefficients(formants, bandwidths, frame_idx)` | 指定フレームの (b, a) 係数を取得 |

**モジュールレベル関数**

| 関数 | 説明 |
|---|---|
| `compute_lpc_coefficients(signal, sr, frame_size, hop, order)` | Levinson-Durbin 再帰で LPC 係数を解く |
| `lpc_to_formants(lpc_coeffs, sr)` | LPC 多項式の根を求める -> フォルマント周波数/帯域幅 |
| `apply_preemphasis(signal, alpha=0.97)` | プリエンファシス `y[i] = x[i] - alpha*x[i-1]` |
| `apply_deemphasis(signal, alpha=0.97)` | デエンファシス `y[i] = x[i] + alpha*y[i-1]` |
| `synthesize_formant_filtered_voice(source, sr, formants)` | プリエンファシス -> フォルマントフィルタ -> デエンファシス -> 正規化 |
| `synthesize_lpc_filtered_voice(source, sr, lpc_coeffs)` | プリエンファシス -> LPC フィルタ -> デエンファシス -> 正規化 |

---

## 5. プロジェクト構成

### 5.1 全体レイアウト

```
VocalB1/
├── VocalB1Synthesizer.py      # メインエントリ：複数区間の有声音フレーム連結合成（ビート構成 + 息成分 + 破裂音）
│                              #   ライブラリとしての用法：synthesize(segments, output_path, ...)
├── VocalDecomposer.py         # 分解ラッパー（WAV -> フレームごとのパラメータ）
├── VocalSynthesizer.py        # 合成ラッパー（フレームごとのパラメータ -> WAV）
├── prosses_breath.py          # 息成分の連結（VocalB1Synthesizer 内部から呼び出される）
├── tools/                     # 補助ツール（エンジンの実行には依存しない）
│   ├── spectrogram.py         # STFT スペクトログラム表示（矩形選択による拡大に対応）
│   ├── oscilloscope.py        # 波形表示
│   ├── pit_editor.py          # ピッチ曲線エディタ（pygame）
│   ├── _plosive_diag.py       # 破裂音の診断
│   └── test_vocal_lib.py      # 回帰テスト（ライブラリ機能の自己診断。直接実行する）
├── _output/                   # 合成結果の出力ディレクトリ
└── vocal_lib/                 # コアライブラリ（詳細は 5.2）
```

### 5.2 vocal_lib の内部

```
vocal_lib/
├── __init__.py            # 一括エクスポート + メタデータ
├── data_classes.py        # F0 / WAVAudio / GlottalExcitation
├── engine.py              # コアエンジン（分解 / 合成）
├── decomposition.py       # decompose_vocal() のトップレベルラッパー
├── synthesis.py           # synthesize_vocal() のトップレベルラッパー
├── pitch.py               # pitch_shift() のトップレベルラッパー
└── core/                  # 下位実装
    ├── f0_detection.py    # F0 検出（pyworld + 自己相関 + ケプストラム + YIN）
    ├── voice_separation.py # 声道分離（STFT + 倍音マスク + 3 成分）
    ├── pyworld_processor.py # pyworld パラメータの抽出/合成
    ├── voice_synthesizer.py # 音声合成器（pyworld + LF/Rosenberg + フォルマントフィルタ）
    ├── vocal_tract_filter.py # 声道フィルタ（LPC + フォルマント IIR）
    ├── pitch_shifter.py   # ピッチ変換（F0 スケーリング + OQ 補正 + スペクトルマッピング + 破音防止）
    ├── glottal_models.py  # 声門モデル（LF + Rosenberg）
    └── excitation_generator.py # 励振生成（インパルス列 + 揺らぎ + 雑音）
```

---

## 6. 完全な使用例

```python
from vocal_lib import *
import numpy as np
import os

os.makedirs("_output", exist_ok=True)

# ========== 1. 分解 ==========
f0, breath, glot = decompose_vocal("ai.wav")
print(f"F0: {f0}")
print(f"息成分: {breath}")
print(f"声門励振: {glot}")

# ========== 2. パラメータの確認 ==========
if glot.jitter:
    print(f"Jitter: {glot.jitter['jitter_local']:.3f}%")
if glot.shimmer:
    print(f"Shimmer: {glot.shimmer['shimmer_local']:.3f}%")
print(f"OQ: {glot.oq:.3f}")

# ========== 3. ピッチ変換 ==========
f0_new, glot_new = pitch_shift(f0, glot, "C4", "E4")  # 4 半音上げ

# ========== 4. 合成 ==========
output = synthesize_vocal(f0_new, breath, glot_new, match_rms_to=0.15)
output.save("_output/ai_to_E4.wav", bit_depth=24)

# ========== 5. numpy 配列から直接処理 ==========
audio_np = output.data  # numpy 配列を取得
f0_2, breath_2, glot_2 = decompose_vocal(audio_np, sample_rate=44100)
output_2 = synthesize_vocal(f0_2, breath_2, glot_2)
output_2.save("_output/roundtrip.wav")

# ========== 6. シリアライズ/デシリアライズ ==========
f0.save("_output/f0.npy")
glot.save("_output/glot.npy")
f0_loaded = F0.load("_output/f0.npy")
glot_loaded = GlottalExcitation.load("_output/glot.npy")
```

---

## 7. 回帰テスト

`tools/test_vocal_lib.py` はライブラリのエンドツーエンド自己診断を 7 項目実行する：

| テストケース | 検証内容 |
|---|---|
| パッケージのインポートと公開 API | `vocal_lib.__all__` のすべてのシンボルにアクセスできる |
| データクラス | F0 / WAVAudio / GlottalExcitation の構築、属性、パラメータの読み書き、深いコピー |
| WAV の読み書き往復 | 16bit で保存して読み戻し、ピーク誤差 < 1e-3 |
| decompose_vocal | 実音声から F0 / 息成分 / 声門励振を分解し、sp・ap が揃っている |
| pitch_shift | 変換後の周波数比が `2^(半音/12)` に厳密に一致する（ソフトリミッティング領域を避けて +2 半音を使用） |
| synthesize_vocal | 出力が空でなく、NaN がなく、エネルギーが正常 |
| エンドツーエンド | `VocalB1Synthesizer.synthesize` が正常にファイル出力し、中間生成物が削除されている |

```bash
python tools\test_vocal_lib.py            # すべてのテストケース
python tools\test_vocal_lib.py --no-e2e   # エンドツーエンドを省略（時間短縮）
```

テスト生成物は `_test_output/` に書き出され、聴感による確認に使える。いずれかのテストケースが失敗した場合、終了コードは非 0 になる。
