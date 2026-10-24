# voicebank — 声库放这里 / Put your voicebank here

这个目录**故意是空的**：声库有版权，不随仓库分发。

This folder is **intentionally empty**: voicebanks are copyrighted and are not
distributed with this repository.

## 怎么用 / How to use

把 UTAU 単独音 声库文件夹整个放进本目录，例如：

```
VocalB1一代/
└── voicebank/
    └── 重音テト単独音/          ← 你自己的声库放这里
        ├── oto.ini
        ├── _あ.wav
        ├── _い.wav
        └── ...
```

程序会自动取 `voicebank/` 下**名字排序第一个**的子目录作为声库。
只有一个声库时不用管；想指定别的，用环境变量覆盖：

```
set VOCALB1_LIB=D:\my-voicebank\some-voice
```

（macOS / Linux：`export VOCALB1_LIB=/path/to/voicebank`）

The engine picks the **first subdirectory** of `voicebank/` (sorted by name).
Override it with the `VOCALB1_LIB` environment variable.

## 什么算一个可用的声库 / What counts as a usable voicebank

- UTAU 格式的**単独音**（single-note）音源
- 目录里有 `oto.ini`，以及以 `_` 开头的 wav（`_あ.wav`、`_か.wav`…）
- 需要 142 个基本假名才能完整演唱

- UTAU **単独音 (single-note)** voicebank
- must contain `oto.ini` and wav files named `_あ.wav`, `_か.wav`, …
- 142 basic kana are required for full coverage

## 版权提醒 / Licensing

本仓库**不包含任何声库**。请使用你合法获得的声库，并遵守它自己的使用条款。

典型条款（例如「重音テト音声ライブラリー」）：**个人非商用、禁止再分发**。
把声库放进自己的电脑没问题，但**不要提交进 git、不要上传到任何公开平台**。

No voicebank is included. Use one you legitimately obtained and follow its own
terms — many (e.g. Kasane Teto) allow personal non-commercial use but **prohibit
redistribution**. Keeping it in your own folder is fine; never commit or upload it.

## 关于本文件 / About this file

`voicebank/*` 已在 `.gitignore` 中忽略，只保留这个 README，
所以目录能存在于仓库里，而声库本身永远不会被误提交。
