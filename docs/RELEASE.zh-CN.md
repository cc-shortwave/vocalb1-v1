# 发布手册

[English](RELEASE.md) | **中文**

从干净的工作副本到 GitHub 上发布完成，照着从上往下做。每一步都写了怎么确认它做对了。

---

## 0. 前置条件

| | |
|---|---|
| 系统 | Windows 10/11 x64 |
| .NET | .NET 9 SDK（`dotnet --version` 输出 `9.x`） |
| Python | 3.10+ 且在 PATH 里 —— 只有测 `lite` 包时才需要 |
| Git | 任意较新版本 |
| 网络 | `full` 包需要联网下载内嵌 Python 和依赖轮子 |
| 空间 | `dist\` 下留约 1.5 GB |

---

## 1. 版本号规则

语义化版本 `主.次.修`，存在仓库根目录的 `VERSION` 文件里。

| 这次改了什么 | 升哪一位 | 例子 |
|---|---|---|
| 修 bug，现有工程行为不变 | `patch` | 1.0.0 → 1.0.1 |
| 加功能，旧工程照样能打开 | `minor` | 1.0.1 → 1.1.0 |
| 工程文件 / 命令行 / 引擎行为不兼容 | `major` | 1.1.0 → 2.0.0 |

首个公开版本是 `1.0.0`。**不要手改 `VERSION`**，交给脚本。

---

## 2. 发布前检查

动手打包之前先跑这几条。

```powershell
cd D:\ccwork\vocalb1-v1
git switch main
git pull
git status                 # 必须是干净的：nothing to commit
dotnet --version           # 必须以 9. 开头
```

- [ ] 在 `main` 上，且和远端同步
- [ ] 工作区干净（没有改到一半的代码）
- [ ] `dotnet --version` 是 9.x
- [ ] 上一个版本遗留的问题都修好并验证过
- [ ] `voicebank\` 里的声库、`ui\engine.settings.json` 都没有被 add 进去

顺手确认一下没有禁止入库的文件被跟踪：

```powershell
git ls-files | Select-String -Pattern '\.wav$|oto\.ini$|engine\.settings\.json$'
```

**必须一行都不打印**。打印了就先回去修 `.gitignore`，别往下走。

---

## 3. 打包

```powershell
# 两个版本都打，自动升版本号，并在本地建 tag
.\scripts\package-release.ps1 -Bump minor -Tag

# 第一次发布（VERSION 已经是 1.0.0），不升号
.\scripts\package-release.ps1 -Tag
```

脚本按顺序做了这些事：

1. 读 `VERSION`（给了 `-Bump` 就先升号），**只提交 `VERSION` 这一个文件**
2. `dotnet publish` 编译 `ui/src/VocalB1.App`（`win-x64`）
3. 组装出 `vocalb1-v<版本>-win-x64-{full,lite}\`（引擎放在包根、
   `ui/bridge/`、`languages/`、空的 `voicebank/`）
4. `full` 版再下载 Python embeddable 包、接上 pip、装依赖，并自检
   `import numpy, scipy, soundfile, pyworld`
5. 两个目录各压成 zip，生成 `dist\SHA256SUMS.txt` 和 `dist\RELEASE-NOTES.md`

产物应该是：

```
dist\
  vocalb1-v1.0.0-win-x64-full.zip
  vocalb1-v1.0.0-win-x64-lite.zip
  SHA256SUMS.txt
  RELEASE-NOTES.md
```

常用参数：`-Mode lite|full|both`、`-FrameworkDependent`（不打包 .NET，体积小很多，
但要求用户装 .NET 9 Desktop Runtime）、`-SkipDotnet`（只重打包不重新编译）、`-SkipPython`。

---

## 4. 验证（这一步不能省）

**一个跑不起来的发布比不发布更糟。**

### 4.1 看压缩包里有什么

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$a = [IO.Compression.ZipFile]::OpenRead((Resolve-Path 'dist\vocalb1-v1.0.0-win-x64-lite.zip'))
$a.Entries | Where-Object { $_.FullName -match '\.wav$|oto\.ini$|engine\.settings\.json$' }
$a.Dispose()          # 必须什么都不打印
```

- [ ] 两个 zip 里都没有 `.wav` / `oto.ini` / `engine.settings.json`
- [ ] 每个 zip 只有**一个**顶层文件夹
- [ ] 那个文件夹的顶层有 `VocalB1.exe`
- [ ] `voicebank\README.md` 在，且 `voicebank\` 里没有任何音频

### 4.2 真正跑一遍

解压到一个**仓库外面**的地方，例如 `%USERPROFILE%\Desktop\reltest\`。

```powershell
mklink /J "%USERPROFILE%\Desktop\reltest\vocalb1-v1.0.0-win-x64-lite\voicebank\testbank" "D:\ccwork\vocalb1-v1\voicebank\重音テト単独音"
```

- [ ] 双击 `VocalB1.exe` 能开窗口（不弹控制台报错）
- [ ] 声库被识别（状态栏显示音频数量，不是报错）
- [ ] 5 个音的合成能跑完并试听
- [ ] 导出 wav 正常
- [ ] 界面语言切到 English / 日本語，文字都变了
- [ ] `full` 版在**没装 Python 的机器**上也能启动
      （没有第二台机器的话：把系统 Python 临时改名，或把 `VOCALB1_PYTHON` 清空）

### 4.3 记下真实体积

脚本已经把它们写进 `RELEASE-NOTES.md`，确认一下数字合理：

- lite 约 100–180 MB（.NET 自包含，没有 Python）
- full 约 150–250 MB（再加上内嵌 Python）

如果 full 超过 300 MB，考虑只发 lite，把装 Python 的说明写清楚。

---

## 5. 推送代码和 tag

```powershell
git push                       # 升版本号的那个提交（如果有）
git push origin v1.0.0         # tag
```

- [ ] `git log --oneline -3` 最上面是升版本号的提交
- [ ] GitHub 上 **Code → Tags** 里能看到 `v1.0.0`
- [ ] 如果有 CI，跑绿了

---

## 6. 在 GitHub 上建 Release

浏览器里打开仓库页面：

1. 右侧 **Releases** → **Draft a new release**
2. **Choose a tag** → 选刚推上去的 `v1.0.0`
   **不要**用 “Create new tag on publish” —— 那会把 tag 打在当前 `main` 上，不一定是你打包的那个提交
3. **Release title**：`v1.0.0 — first public release`
4. **Describe this release**：把 `dist\RELEASE-NOTES.md` 整段粘进去
5. **Attach binaries**：两个 `.zip` 加 `SHA256SUMS.txt` 一起拖进去
6. 勾 **Set as the latest release**（正式版不要勾 pre-release）
7. **Publish release**

- [ ] 发布页有 3 个附件，体积对得上
- [ ] 说明里的 markdown 渲染正常（校验命令那段是代码框）
- [ ] URL 里的 tag 和 `VERSION` 一致

---

## 7. 发布后再验证一次

假装自己是陌生人：

1. 从发布页把 `vocalb1-v1.0.0-win-x64-lite.zip` **下载回来**
2. 解压到全新目录，按 §4.2 再走一遍
3. 校验哈希：

```powershell
Get-FileHash .\vocalb1-v1.0.0-win-x64-lite.zip -Algorithm SHA256
# 必须和 SHA256SUMS.txt 里那一行完全一致
```

- [ ] 下载回来的包哈希对得上
- [ ] 下载回来的包能跑
- [ ] 包里的 `VERSION` 文件写的是对的版本号

---

## 8. 发布之后

- 去该宣布的地方宣布一下
- 为下个版本开一个 milestone，issue 才不会乱
- 仓库如果本来私有、现在转公开，Release 和附件会跟着一起公开，**不用重传**

---

## 9. 撤回

已发布的 Release 可以撤，但**已经下载下去的东西收不回来**。所以发现得越快越好。

**包坏了（代码没问题）**

1. Releases → 那个版本 → **Edit** → 删掉坏的附件
2. 重新打包、重新上传，说明里加一句备注
3. **tag 保留** —— 代码没坏，只是二进制打错了

**代码坏了**

1. Releases → **Edit** → 勾 **Set as a pre-release**（或整个删掉）
2. 删 tag：`git push --delete origin v1.0.0` 加 `git tag -d v1.0.0`
3. 在分支上修好，发 `v1.0.1`

**tag 打错提交了**

```powershell
git tag -d v1.0.0
git push --delete origin v1.0.0
git tag -a v1.0.0 <正确的 sha> -m "v1.0.0"
git push origin v1.0.0
```

注意：force push 或重打 tag **都删不掉别人已经拉到本地的东西**。
如果历史里出现过密钥，第一件事是换掉密钥，不是改历史。

---

## 10. 故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `Publish output contains no executable` | 项目改了程序集名 | 看 `VocalB1.App.csproj` 的 `<AssemblyName>`；脚本会自动退而找任意 `.exe` |
| `dotnet publish failed` 报 NETSDK1045 | SDK 太旧 | 装 .NET 9 SDK |
| `pip install failed` / 超时 | 镜像连不上 | 加 `-PipIndex https://pypi.org/simple` 重跑 |
| full 版报 `ModuleNotFoundError: pyworld` | 官方 pyworld 没有 Python 3.9+ 的 Windows 轮子 | 确认 `requirements.txt` 在 Windows 下选的是 `pyworld-prebuilt` |
| 界面能开但提示*引擎尚未初始化* | 没找到内嵌 Python | 确认 `python\python.exe` 和 `VocalB1.exe` 在同一层；程序会先找这个位置，再找 `C:\Python312` |
| 提示*找不到声库* | 全新解压的 `voicebank\` 是空的 | 放一个声库进去（这是预期行为，不是 bug） |
| zip 有 300+ MB | .NET 自包含 + Python | 用 `-FrameworkDependent`，或只发 lite |
| `Compress-Archive` 很慢或失败 | 目录太大 | 删掉 `dist\_stage` 重来；目录控制在 2 GB 以内 |
| push 被服务器钩子拒绝 | 混进了禁止的文件（`*.wav`、`oto.ini`、`*.db`、超过 5 MB、或不该出现的路线关键词） | 看钩子提示，删掉那个文件，`git commit --amend` 后重推 |

---

## 11. 一页清单

```
[ ] git switch main && git pull && git status（干净）
[ ] dotnet --version = 9.x
[ ] scripts/package-release.ps1 -Bump <patch|minor|major> -Tag
[ ] zip 里没有 wav / oto.ini / engine.settings.json
[ ] 解压到仓库外，放声库，启动，合成，导出
[ ] full 版在没有系统 Python 的环境能跑
[ ] 体积合理，RELEASE-NOTES.md 数字已确认
[ ] git push && git push origin v<版本>
[ ] GitHub：用已有 tag 建 Release，上传 2 个 zip + SHA256SUMS.txt
[ ] 把附件下载回来，核对哈希，再跑一次
```
