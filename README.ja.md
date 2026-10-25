# VocalB1

[English](README.md) | [中文](README.zh-CN.md) | **日本語**

オフライン軽量歌声合成エンジン — 音ごとのパラメータを個別に制御、事前学習なし、UTAU音源対応。

VocalB1 は歌声の録音を「F0（基本周波数）／スペクトル包絡／息成分／励振源」に分解し、
音ごとに指定した音高・長さ・息の量などのパラメータから歌を組み立て直します。
**モデル学習なし、GPU不要、ネットワーク不要** — 純粋な信号処理だけで、
2コア／2GB のマシンでも動作します。

---

## 特長

- **音ごとの独立制御** — 音高、拍位置、息の量、フォルマント追従比、減衰カーブ
- **UTAU 単独音 音源に対応** — 基本仮名142音
- **事前学習ゼロ** —モデルの重みもデータセットも GPU も不要
- **完全オフライン** — データがマシンの外に出ません
- **デスクトップ UI**（C# / WPF）とコマンドライン実行の両方
- **試行錯誤が速い** — 25秒の曲で約60秒。変化のない区間はキャッシュからほぼ即時

---

## 構成

```
.
├── VocalB1Synthesizer.py     # 合成コア
├── VocalDecomposer.py        # 分解：音声 -> パラメータ（.npy + .json）
├── VocalSynthesizer.py       # パラメータから音声を再構築
├── VocalB1.py                # コマンドライン入口
├── vocal_lib/                # F0検出、フォルマント、息成分、声門モデルなど
├── ui/                       # Windows デスクトップ UI（C# / WPF）
│   ├── src/
│   └── engine.settings.example.json
├── languages/                # UI 言語パック（en / ja / zh）
├── voicebank/                # 音源はここに置く（同梱なし）
└── tools/                    # 補助ツール（エンジンの実行には不要）
    ├── spectrogram.py        # スペクトログラム表示
    ├── oscilloscope.py       # 波形表示
    ├── pit_editor.py         # ピッチカーブエディタ
    ├── _plosive_diag.py      # 破裂音の過渡診断
    └── test_vocal_lib.py     # ライブラリの回帰テスト
```

ライブラリの API リファレンスは独立しています：[vocal_lib/README.md](vocal_lib/README.md)（日本語 / English / 中文）。

---

## 動作環境

- Python 3.10 以降
- デスクトップ UI は Windows 10/11。Python エンジン自体はクロスプラットフォーム

```bash
pip install -r requirements.txt
```

`matplotlib`（スペクトル／スペクトログラム）と `pygame`（試聴）は任意です。
合成だけなら不要です。

---

## クイックスタート

### 1. 音源を置く

UTAU の**単独音**音源フォルダを `voicebank/` に入れます：

```
voicebank/
└── <お使いの音源>/
    ├── _あ.wav
    ├── ...
    └── icon.jpg
```

ライセンスの都合で、音源は同梱していません。
詳しくは [voicebank/README.md](voicebank/README.md) を参照してください。

### 2. エンジンを試す

```bash
python VocalB1Synthesizer.py
```

ファイル冒頭の `DEFAULT_SEGMENTS` の設定で1回合成し、`_output/` に出力します。

### 3. デスクトップ UI を起動する

`ui/src/` のソリューションを Visual Studio で開いて `VocalB1.App` をビルドするか、
公開済みの `VocalB1.App.exe` を実行します。UI は Python エンジンを**別プロセス**として扱い、
標準入出力上の JSON 行プロトコルで通信します（インタプリタの埋め込みではありません）。

設定は不要です。UI は自分の実行ファイルから上位ディレクトリへたどり、
`VocalB1Synthesizer.py` があるフォルダをリポジトリルートとして認識し、
そこから `languages/` を読み込みます。上書きしたい場合は
`ui/engine.settings.example.json` を `ui/engine.settings.json` にコピーして編集します
（このファイルは git 管理外です）。

---

## 音源の探索順

1. 環境変数 `VOCALB1_LIB`（音源ディレクトリを直接指定）
2. `<リポジトリ>/voicebank/` 直下の**名前順で最初のサブディレクトリ**
3. どちらも無い場合はエラーとヒントを表示

---

## パラメータ

音ごとに個別に設定できます：

- **目標音高** — C4 … A5
- **拍位置／拍数** — 最小 0.25 拍、半分に分割可能
- **息の量** — 息成分と有声成分の混合比
- **フォルマント追従比** — 音高移動時にフォルマントが追従する度合い（0 = 固定、1 = 完全追従）
- **減衰カーブ** — 区間ごとの音量エンベロープ

これらはプロジェクトファイルに保存され、何度でも編集・アンドゥでき、
合成時には変化のない区間がキャッシュされます。

---

## 性能

2コア／2GB のマシンでの実測：

- 4〜5音：約2〜5秒
- 25秒の曲：約60秒（実時間の0.42倍）
- パラメータを変えずに再合成：キャッシュヒットでほぼ即時

---


---

## リリースのビルド

```powershell
.\scripts\package-release.ps1              # full と lite の両方
.\scripts\package-release.ps1 -Mode lite   # lite のみ
```

`VERSION` を読み、WPF アプリを publish し、（任意で）Python を同梱して
`dist/vocalb1-v<version>-win-x64-{full,lite}.zip` と `SHA256SUMS.txt`、
`RELEASE-NOTES.md` を出力します。

バージョン番号はスクリプトが更新します（手で書き換えません）：

```powershell
.\scripts\package-release.ps1 -Bump patch -Tag   # 1.0.0 -> 1.0.1、VERSION をコミットし tag v1.0.1 を作成
```

`-Bump` は `patch`（バグ修正）/ `minor`（機能追加）/ `major`（互換性のない変更）。
`-NoCommit` はコミットせず、`-Tag` はローカルに注釈付きタグを作成します（push はしません）。

リリース手順の全体（事前チェック、検証、GitHub Release、ロールバック、トラブルシュート）は
**[docs/RELEASE.md](docs/RELEASE.md)** にあります（英語）。どちらのアーカイブも単一フォルダに展開され、
エンジン・`ui/bridge/`・`languages/`・空の `voicebank/` が自動検出される位置に置かれます。

## ライセンス

ソースコードは **MIT** ライセンスです — [LICENSE](LICENSE) を参照してください。
適用範囲と第三者ライセンスは [NOTICE.md](NOTICE.md) に記載しています。

**音源（ボイスバンク）はこのライセンスの対象外です。** 音源はそれぞれの権利者が
独自の条件で許諾しているもので、本プロジェクトは同梱も再配布も行いません。
お使いの音源の利用規約に従ってください（多くは個人非商用可、再配布禁止です）。

---

## 開発体制

アイデア、要件定義、パラメータ設計、聴感に基づく調整は作者が行いました。
**コードの実装は主に AI コーディングアシスタントが生成**し、作者が検証・デバッグ・
最終判断を担当しています。numpy / scipy / soundfile / pyworld などのオープンソース
ライブラリを使用しており、事前学習済みモデルは含みません。

---

## 謝辞

- **Mingxuan** —— ビジュアルとリリース検証：アプリケーションアイコンとプロジェクト
  マークを制作。Windows 11 で `setup.exe` をインストールして一通り動作を確認し、
  見つかった問題を報告しました。

アイコンは氏の作品です。MIT ライセンスとの関係は [NOTICE.md](NOTICE.md) を参照してください。

---

## 作者

Xichun Guo · 2026

プロジェクトページ：<https://vocalb1.com>
