# Notices

## License scope

The MIT license in [LICENSE](LICENSE) covers **the source code in this repository only**.

It does **not** cover any voicebank (声库 / 音源). Voicebanks are copyrighted works
distributed under their own terms by their respective rights holders. No voicebank is
included in this repository, and none may be redistributed under this license.

If you use a voicebank with this project, follow the terms of that voicebank. Most of
them allow personal, non-commercial use and prohibit redistribution — including
redistribution of modified copies and of models trained on them.

## Logo and icon

`VocalB1_Version_1.0.ico` — the application icon, also used as the favicon of
vocalb1.com — was designed by **Mingxuan** and is included here with his permission.

It is **not** covered by the MIT license above. That license applies to the source
code; reusing the icon elsewhere needs his permission.

## Third-party dependencies

These keep their own licenses; the MIT license above does not override them.

- **numpy** — BSD-3-Clause
- **scipy** — BSD-3-Clause
- **soundfile** — BSD-3-Clause
- **pyworld** — MIT (bundles WORLD, modified BSD)
- **matplotlib** — BSD-compatible (PSF-based)
- **pygame** — LGPL-2.1

The `lite` build requires these to be installed by the user. The `full` build bundles
an unmodified Python runtime together with these packages; their license files are
kept inside the bundled packages under `python/Lib/site-packages/`.

## Third-party sample audio

The `full` build bundles the Python packages above including their test/example data,
which contains a small number of third-party sample sounds (e.g. `pygame/examples/data`).
They are not used by VocalB1 at runtime and are not voicebanks.
