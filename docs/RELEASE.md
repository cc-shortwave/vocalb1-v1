# Release manual

**English** | [中文](RELEASE.zh-CN.md)

How to cut a VocalB1 release, from a clean checkout to a published GitHub Release.
Follow it top to bottom; every step has a way to verify it worked.

---

## 0. Prerequisites

| | |
|---|---|
| OS | Windows 10/11 x64 |
| .NET | .NET 9 SDK (`dotnet --version` → `9.x`) |
| Python | 3.10+ on PATH — only needed to test the `lite` build |
| Git | any recent version |
| Network | needed for the `full` build (downloads the embedded Python + wheels) |
| Space | ~1.5 GB free in `dist\` |

---

## 1. Versioning rules

Semantic versioning, `MAJOR.MINOR.PATCH`, stored in the `VERSION` file at the repo root.

| Change | Bump | Example |
|---|---|---|
| bug fix, no behaviour change for existing projects | `patch` | 1.0.0 → 1.0.1 |
| new feature, existing projects still load | `minor` | 1.0.1 → 1.1.0 |
| project files / CLI / engine behaviour break | `major` | 1.1.0 → 2.0.0 |

The first public release is `1.0.0`. Never edit `VERSION` by hand — the script does it.

---

## 2. Pre-flight checklist

Run these before building anything.

```powershell
cd D:\ccwork\vocalb1-v1
git switch main
git pull
git status                 # must be clean: nothing to commit
dotnet --version           # must start with 9.
```

- [ ] on `main`, up to date with the remote
- [ ] working tree clean (no half-finished edits)
- [ ] `dotnet --version` reports 9.x
- [ ] the previous release's issues are fixed and verified
- [ ] nothing from `voicebank\` or `ui\engine.settings.json` is staged (`git status` shows them as ignored)

Quick sanity check that nothing forbidden is tracked:

```powershell
git ls-files | Select-String -Pattern '\.wav$|oto\.ini$|engine\.settings\.json$'
```

Must print nothing. If it prints something, stop and fix `.gitignore` first.

---

## 3. Build

```powershell
# both variants, version bumped, tag created locally
.\scripts\package-release.ps1 -Bump minor -Tag

# or, for the very first release (VERSION is already 1.0.0)
.\scripts\package-release.ps1 -Tag
```

What the script does, in order:

1. reads `VERSION` (and bumps it when `-Bump` is given), committing only that file
2. `dotnet publish` of `ui/src/VocalB1.App` for `win-x64`
3. assembles `vocalb1-v<version>-win-x64-{full,lite}\` (engine at the folder root,
   `ui/bridge/`, `languages/`, empty `voicebank/`)
4. for `full`: downloads the Python embeddable package, wires up pip, installs the
   requirements and self-tests `import numpy, scipy, soundfile, pyworld`
5. zips each folder, writes `dist\SHA256SUMS.txt` and `dist\RELEASE-NOTES.md`

Expected artefacts:

```
dist\
  vocalb1-v1.0.0-win-x64-full.zip
  vocalb1-v1.0.0-win-x64-lite.zip
  SHA256SUMS.txt
  RELEASE-NOTES.md
```

Useful switches: `-Mode lite|full|both`, `-FrameworkDependent` (no bundled .NET,
much smaller, requires the .NET 9 Desktop Runtime), `-SkipDotnet` (repack only),
`-SkipPython`.

---

## 4. Verify the build

**Do not skip this.** A release that does not run is worse than no release.

### 4.1 Inspect the archives

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$a = [IO.Compression.ZipFile]::OpenRead((Resolve-Path 'dist\vocalb1-v1.0.0-win-x64-lite.zip'))
$a.Entries | Where-Object { $_.FullName -match '\.wav$|oto\.ini$|engine\.settings\.json$' }
$a.Dispose()          # must print nothing
```

- [ ] no `.wav` / `oto.ini` / `engine.settings.json` inside either zip
- [ ] each zip has exactly **one** top-level folder
- [ ] `VocalB1.exe` exists at the top level of that folder
- [ ] `voicebank\README.md` exists and `voicebank\` holds no audio

### 4.2 Run it on a clean-ish path

Unzip somewhere **outside** your working copy, e.g. `C:\Users\<you>\Desktop\reltest\`.

```powershell
mklink /J "C:\Users\<you>\Desktop\reltest\vocalb1-v1.0.0-win-x64-lite\voicebank\testbank" "D:\ccwork\vocalb1-v1\voicebank\重音テト単独音"
```

- [ ] double-clicking `VocalB1.exe` opens the window (no console error)
- [ ] the voicebank is detected (status bar shows the audio count, not an error)
- [ ] a 5-note render finishes and can be played back
- [ ] exporting a wav works
- [ ] switching the UI language to English and Japanese shows translated text
- [ ] the `full` build starts **without** any system Python installed (test on a machine
      or VM without Python, or set `VOCALB1_PYTHON` to nothing and temporarily rename
      your system Python)

### 4.3 Record the numbers

Copy the two sizes from the script output into the release notes file — they are already
written there, just confirm they look sane:

- lite ≈ 100–180 MB (self-contained .NET, no Python)
- full ≈ 150–250 MB (adds the embedded Python)

If `full` is above ~300 MB, consider shipping `lite` only and documenting the Python install.

---

## 5. Publish the code and the tag

```powershell
git push                       # the version bump commit, if any
git push origin v1.0.0         # the tag
```

- [ ] `git log --oneline -3` shows the bump commit on top
- [ ] on GitHub, the tag `v1.0.0` now exists under **Code → Tags**
- [ ] Actions/CI (if any) is green

---

## 6. Create the GitHub Release

In the browser, on the repository page:

1. **Releases** → **Draft a new release**
2. **Choose a tag** → pick the tag you just pushed (`v1.0.0`).
   Do **not** use "Create new tag on publish" — that would tag whatever `main` points at now.
3. **Release title**: `v1.0.0 — first public release`
4. **Describe this release**: paste the whole of `dist\RELEASE-NOTES.md`
5. **Attach binaries**: drag in both `.zip` files and `SHA256SUMS.txt`
6. tick **Set as the latest release** (leave **pre-release** unticked for a stable release)
7. **Publish release**

- [ ] the release page lists 3 assets with the expected sizes
- [ ] the description renders as markdown (code fence around the checksum command)
- [ ] the tag in the URL matches `VERSION`

---

## 7. Verify the published release

Pretend you are a stranger:

1. download `vocalb1-v1.0.0-win-x64-lite.zip` back from the release page
2. unzip into a fresh folder, run the same checks as §4.2
3. compare the checksum:

```powershell
Get-FileHash .\vocalb1-v1.0.0-win-x64-lite.zip -Algorithm SHA256
# must equal the line in SHA256SUMS.txt
```

- [ ] downloaded asset hash matches `SHA256SUMS.txt`
- [ ] the downloaded build runs
- [ ] `README.md` inside the zip shows the right version in `VERSION`

---

## 8. After the release

- announce it (project page, social, wherever)
- open a milestone for the next version so issues land in the right place
- if the repository was private and is now public, the release and assets become public
  with it — no re-upload needed

---

## 9. Rollback

A published release can be withdrawn, but **downloads cannot be un-downloaded**. Act fast.

**Bad asset / broken build**

1. Releases → the release → **Edit** → delete the bad asset(s)
2. rebuild, re-upload, edit the description with a note
3. keep the tag: the code is fine, only the binaries were wrong

**Bad code**

1. Releases → **Edit** → tick **Set as a pre-release** (or delete the release entirely)
2. delete the tag: `git push --delete origin v1.0.0` and `git tag -d v1.0.0`
3. fix on a branch, then release `v1.0.1`

**Wrong commit tagged**

```powershell
git tag -d v1.0.0
git push --delete origin v1.0.0
git tag -a v1.0.0 <correct-sha> -m "v1.0.0"
git push origin v1.0.0
```

Note: force-pushing or re-tagging never removes objects that were already fetched.
If a secret was ever in the history, rotate the secret — do not rely on rewriting history.

---

## 10. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Publish output contains no executable` | the project renamed its assembly | check `<AssemblyName>` in `VocalB1.App.csproj`; the script falls back to any `.exe` |
| `dotnet publish failed` with NETSDK1045 | SDK too old | install the .NET 9 SDK |
| `pip install failed` / timeouts | mirror unreachable | rerun with `-PipIndex https://pypi.org/simple` |
| `ModuleNotFoundError: pyworld` in the full build | upstream `pyworld` has no wheels for Python 3.9+ | `requirements.txt` must select `pyworld-prebuilt` on Windows |
| app starts but says *engine not initialized* | bundled Python not found | check that `python\python.exe` sits next to `VocalB1.exe`; the app looks there before `C:\Python312` |
| app says *voicebank not found* | empty `voicebank\` in the fresh unzip | add a voicebank (this is expected on a clean download) |
| zip is 300+ MB | self-contained .NET + Python | use `-FrameworkDependent`, or ship lite only |
| `Compress-Archive` very slow / fails | huge folder | delete `dist\_stage` and retry; keep the folder under ~2 GB |
| push rejected by the server hook | a forbidden file slipped in (`*.wav`, `oto.ini`, `*.db`, >5 MB, "二代" keywords) | read the hook message, remove the file, `git commit --amend`, push again |

---

## 11. One-page checklist

```
[ ] git switch main && git pull && git status (clean)
[ ] dotnet --version = 9.x
[ ] scripts/package-release.ps1 -Bump <patch|minor|major> -Tag
[ ] zip contains no wav / oto.ini / engine.settings.json
[ ] unzip outside the repo, add a voicebank, launch, render, export
[ ] full build runs with no system Python
[ ] sizes look sane; RELEASE-NOTES.md updated with real numbers
[ ] git push && git push origin v<version>
[ ] GitHub: draft release from the existing tag, upload 2 zips + SHA256SUMS.txt
[ ] download the asset back, verify the hash, run it once
```
