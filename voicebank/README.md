# voicebank — put your voicebank here

**English** | [中文](#中文) | [日本語](#日本語)

This folder is **intentionally empty**: voicebanks are copyrighted and are not
distributed with this repository.

## How to use

**Method 1 — drag and drop.** With VocalB1 running, drag the voicebank folder onto the
window. It is copied into `voicebank/<name>/` and loaded right away. The folder is checked
before anything is copied:

- it contains at least one `.wav`
- it contains an `icon.jpg` (shown next to the New button in the top bar)
- no folder of the same name already exists in `voicebank/`

If a check fails, nothing is copied and a dialog says why.

**Method 2 — place it yourself.** Put a **single-note voicebank** folder in here:

```
voicebank/
└── <your-voicebank>/
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

`icon.jpg` sits next to the audio files and is shown next to the New button in the top bar.

## What counts as a usable voicebank

- a **single-note voicebank**
- contains wav files named `_あ.wav`, `_か.wav`, … and an `icon.jpg`
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

**办法一：拖进来。** 启动 VocalB1 后，把声库文件夹拖到窗口上。程序会先检查，再复制到
`voicebank/<名字>/` 并立即载入：

- 里面有至少一个 `.wav`
- 里面有一个 `icon.jpg`（显示在顶栏「新建」按钮旁）
- `voicebank/` 下没有同名文件夹

任何一条不过就整个不复制，并弹窗说明原因。

**办法二：自己放。** 把**单独音声库**文件夹整个放进本目录：

```
voicebank/
└── <你的声库>/
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

`icon.jpg` 与音频文件同级，会显示在顶栏「新建」按钮旁边。

### 什么算一个可用的声库

- **单独音声库**
- 目录里有 wav（`_あ.wav`、`_か.wav`…）与 `icon.jpg`
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

**方法 1：ドラッグ＆ドロップ。** VocalB1 を起動した状態で、音源フォルダをウィンドウに
ドロップします。コピー前に次の点が検査され、問題なければ
`voicebank/<名前>/` にコピーされてすぐ読み込まれます：

- `.wav` が 1 つ以上ある
- `icon.jpg` がある（上部バーの「新規」ボタンの横に表示されます）
- `voicebank/` に同名フォルダがない

検査に通らない場合はコピーされず、理由がダイアログで表示されます。

**方法 2：手動で置く。** **単独音音源**フォルダをここに置きます：

```
voicebank/
└── <お使いの音源>/
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

`icon.jpg` は音声ファイルと同じ階層に置きます。上部バーの「新規」ボタンの横に表示されます。

### 使える音源の条件

- **単独音音源**
- wav（`_あ.wav`、`_か.wav` …）と `icon.jpg` が入っていること
- 網羅するには基本仮名 142 音が必要

### ライセンスについて

音源は同梱していません。正規に入手した音源を使い、その利用規約に従ってください。
多くの場合（例：「重音テト音声ライブラリー」）は**個人非商用可・再配布禁止**です。
自分の PC に置くのは問題ありませんが、**git にコミットしたり公開の場にアップロードしたりしないでください**。

`voicebank/*` は `.gitignore` で除外され、この README だけが管理対象です。
そのためフォルダはリポジトリに存在し、音源そのものが誤ってコミットされることはありません。
