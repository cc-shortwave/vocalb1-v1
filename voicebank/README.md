# voicebank — put your voicebank here

**English** | [中文](#中文) | [日本語](#日本語)

This folder is **intentionally empty**: voicebanks are copyrighted and are not
distributed with this repository.

## How to use

Drop a **single-note voicebank** folder in here:

```
voicebank/
└── <your-voicebank>/
    ├── oto.ini
    ├── _あ.wav
    ├── _い.wav
    └── ...
```

The engine picks the **first subdirectory** of `voicebank/`, sorted by name.
With a single voicebank you do not need to configure anything. To point somewhere else, set:

```
set VOCALB1_LIB=D:\my-voicebank\some-voice
```

(macOS / Linux: `export VOCALB1_LIB=/path/to/voicebank`)

## Voicebank structure

```
voicebank/
└── <your-virtualsinger>/
    ├── œ.wav
    ├── ...
    └── icon.jpg
```

`icon.jpg` is optional and sits next to the audio files; when present it is shown
next to the New button in the top bar.

## What counts as a usable voicebank

- a **single-note voicebank**
- contains `oto.ini` plus wav files named `_あ.wav`, `_か.wav`, …
- needs the 142 basic kana for full coverage

## Licensing

No voicebank is included. Use one you legitimately obtained and follow its own
terms — many (e.g. Kasane Teto) allow personal non-commercial use but **prohibit
redistribution**. Keeping it in your own folder is fine; never commit it or upload it anywhere public.

`voicebank/*` is ignored by `.gitignore`; only this README is tracked, so the folder
exists in the repository while the voicebank itself can never be committed by accident.

---

## 中文

这个目录**故意是空的**：声库有版权，不随仓库分发。

### 怎么用

把**单独音声库**文件夹整个放进本目录：

```
voicebank/
└── <你的声库>/
    ├── oto.ini
    ├── _あ.wav
    ├── _い.wav
    └── ...
```

程序会取 `voicebank/` 下**按名字排序的第一个子目录**。只有一个声库时不用配置；
想指到别处，设置环境变量：

```
set VOCALB1_LIB=D:\声库\别的音源
```

（macOS / Linux：`export VOCALB1_LIB=/path/to/voicebank`）

### 声库结构

```
voicebank/
└── <你的声库>/
    ├── œ.wav
    ├── ...
    └── icon.jpg
```

`icon.jpg` 与音频文件同级，可选；存在时会显示在顶栏「新建」按钮旁边。

### 什么算一个可用的声库

- **单独音声库**
- 目录里有 `oto.ini`，以及以 `_` 开头的 wav（`_あ.wav`、`_か.wav`…）
- 需要 142 个基本假名才能完整演唱

### 版权提醒

本仓库**不包含任何声库**。请使用你合法获得的声库，并遵守它自己的使用条款——
典型条款（例如「重音テト音声ライブラリー」）：**个人非商用、禁止再分发**。
放在自己电脑里没问题，但**不要提交进 git、不要上传到任何公开平台**。

`voicebank/*` 已在 `.gitignore` 中忽略，只保留这个 README，
所以目录能存在于仓库里，而声库本身永远不会被误提交。

---

## 日本語

このフォルダは**意図的に空です**。音源には著作権があり、リポジトリには同梱していません。

### 使い方

**単独音音源**フォルダをここに置きます：

```
voicebank/
└── <お使いの音源>/
    ├── oto.ini
    ├── _あ.wav
    ├── _い.wav
    └── ...
```

エンジンは `voicebank/` 直下の**名前順で最初のサブディレクトリ**を使います。
音源が 1 つだけなら設定は不要です。別の場所を指定する場合は環境変数を設定します：

```
set VOCALB1_LIB=D:\my-voicebank\some-voice
```

（macOS / Linux：`export VOCALB1_LIB=/path/to/voicebank`）

### 音源の構成

```
voicebank/
└── <お使いの音源>/
    ├── œ.wav
    ├── ...
    └── icon.jpg
```

`icon.jpg` は音声ファイルと同じ階層に置く任意の画像です。ある場合は上部バーの
「新規」ボタンの横に表示されます。

### 使える音源の条件

- **単独音音源**
- `oto.ini` と `_あ.wav`、`_か.wav` … といった wav が入っていること
- 網羅するには基本仮名 142 音が必要

### ライセンスについて

音源は同梱していません。正規に入手した音源を使い、その利用規約に従ってください。
多くの場合（例：「重音テト音声ライブラリー」）は**個人非商用可・再配布禁止**です。
自分の PC に置くのは問題ありませんが、**git にコミットしたり公開の場にアップロードしたりしないでください**。

`voicebank/*` は `.gitignore` で除外され、この README だけが管理対象です。
そのためフォルダはリポジトリに存在し、音源そのものが誤ってコミットされることはありません。
