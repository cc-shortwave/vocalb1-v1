#Requires -Version 5.1
<#
.SYNOPSIS
    Build the GitHub Release assets for VocalB1.

.DESCRIPTION
    Reads VERSION, publishes the WPF app, assembles a portable folder, optionally
    bundles an embedded Python, zips everything, writes SHA256SUMS.txt and a
    RELEASE-NOTES.md that already contains the real file sizes.

    Layout inside each zip (single top-level folder, nothing spills out):

        vocalb1-v1.0.0-win-x64-full\
            VocalB1.exe
            VocalB1Synthesizer.py ... (engine)
            vocal_lib\
            ui\bridge\vocal_bridge.py
            languages\
            python\                (full only)
            voicebank\README.md    (empty voicebank folder, as in the repo)
            README.md  LICENSE  VERSION  QUICKSTART.txt  requirements.txt

.EXAMPLE
    .\scripts\package-release.ps1

.EXAMPLE
    .\scripts\package-release.ps1 -Mode lite -Version 1.0.1

.EXAMPLE
    .\scripts\package-release.ps1 -SkipDotnet

.NOTES
    Requires: .NET 9 SDK (to publish), internet access (full build only).
#>
[CmdletBinding()]
param(
    [ValidateSet('both', 'full', 'lite')]
    [string]$Mode = 'both',

    # Defaults to the contents of the VERSION file.
    [string]$Version,

    # Bump VERSION before building: patch (bugfix), minor (feature), major (breaking).
    [ValidateSet('none', 'patch', 'minor', 'major')]
    [string]$Bump = 'none',

    # With -Bump: do not create the "chore: bump version" commit.
    [switch]$NoCommit,

    # With -Bump: also create the annotated tag v<version> locally (never pushed).
    [switch]$Tag,

    [string]$Runtime = 'win-x64',

    # Output folder, relative to the repository root.
    [string]$DistDir = 'dist',

    # Smaller app, but the user must install the .NET 9 Desktop Runtime.
    [switch]$FrameworkDependent,

    # Single-file is the default: one VocalB1.exe (~70 MB) with the runtime compressed
    # inside and unpacked to %TEMP% on first run. Use this switch for the old layout
    # (VocalB1.exe plus ~200 loose runtime DLLs).
    [switch]$NoSingleFile,

    # Reuse the existing publish output instead of running dotnet again.
    [switch]$SkipDotnet,

    # Same as -Mode lite: skip the embedded Python runtime.
    [switch]$SkipPython,

    [string]$PythonEmbedUrl = 'https://npmmirror.com/mirrors/python/3.12.10/python-3.12.10-embed-amd64.zip',
    [string]$PipIndex = 'https://pypi.tuna.tsinghua.edu.cn/simple'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$Root     = Split-Path -Parent $PSScriptRoot
$DistPath = Join-Path $Root $DistDir
$Stage    = Join-Path $DistPath '_stage'

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Write-Ok([string]$Text) {
    Write-Host "    $Text" -ForegroundColor Green
}

function Get-SizeMB([string]$Path) {
    return [math]::Round((Get-ChildItem -LiteralPath $Path -Recurse -File |
        Measure-Object -Property Length -Sum).Sum / 1MB, 1)
}

# ------------------------------------------------------------------ version
if (-not $Version) {
    $versionFile = Join-Path $Root 'VERSION'
    if (-not (Test-Path -LiteralPath $versionFile)) { throw "VERSION file not found: $versionFile" }
    $Version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.0.0 (got '$Version')"
}

# ------------------------------------------------------------------ bump
if ($Bump -ne 'none') {
    $nums = @($Version.Split('.') | ForEach-Object { [int]$_ })
    switch ($Bump) {
        'major' { $nums[0] = $nums[0] + 1; $nums[1] = 0; $nums[2] = 0 }
        'minor' { $nums[1] = $nums[1] + 1; $nums[2] = 0 }
        'patch' { $nums[2] = $nums[2] + 1 }
    }
    $Version = ($nums -join '.')
    Set-Content -LiteralPath (Join-Path $Root 'VERSION') -Value $Version -Encoding ASCII
    Write-Host "  bumped  : $Bump -> $Version (VERSION updated)" -ForegroundColor Yellow

    if (-not $NoCommit) {
        & git -C $Root add VERSION
        if ($LASTEXITCODE -ne 0) { throw "git add VERSION failed" }
        & git -C $Root commit -q -m "chore: bump version to $Version"
        if ($LASTEXITCODE -ne 0) { throw "git commit failed (nothing to commit?)" }
        Write-Ok "committed: chore: bump version to $Version"
    }

    if ($Tag) {
        $tagName = "v$Version"
        $existing = (& git -C $Root tag -l $tagName)
        if ($existing) { throw "Tag $tagName already exists" }
        & git -C $Root tag -a $tagName -m "$tagName"
        if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
        Write-Ok "created tag $tagName (push it with: git push origin $tagName)"
    }
}
Write-Host "VocalB1 release builder" -ForegroundColor White
Write-Host "  version : $Version"
Write-Host "  mode    : $Mode"
Write-Host "  runtime : $Runtime"

# ------------------------------------------------------------------ variants
$variants = @()
if ($Mode -eq 'both') { $variants = @('full', 'lite') } else { $variants = @($Mode) }
if ($SkipPython) { $variants = $variants | Where-Object { $_ -ne 'full' } }
if (-not $variants) { throw "Nothing to build (mode=$Mode, SkipPython=$SkipPython)" }

# ------------------------------------------------------------------ prerequisites
if (-not $SkipDotnet) {
    Write-Step 'Checking .NET SDK'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw "dotnet not found in PATH. Install the .NET 9 SDK: https://dotnet.microsoft.com/download" }
    $sdkVersion = (& dotnet --version).Trim()
    Write-Ok "dotnet $sdkVersion"
}

# ------------------------------------------------------------------ clean
Write-Step 'Cleaning dist'
if (Test-Path -LiteralPath $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
New-Item -ItemType Directory -Path $Stage -Force | Out-Null

# ------------------------------------------------------------------ publish
$publishDir = Join-Path $Stage '_publish'
if ($SkipDotnet -and (Test-Path -LiteralPath $publishDir)) {
    Write-Step 'Skipping dotnet publish (reusing previous output)'
} else {
    Write-Step 'Publishing VocalB1.App'
    $csproj = Join-Path $Root 'ui/src/VocalB1.App/VocalB1.App.csproj'
    if (-not (Test-Path -LiteralPath $csproj)) { throw "Project not found: $csproj" }

    $singleFile = -not $NoSingleFile.IsPresent
    if ($singleFile -and $FrameworkDependent) {
        throw "Single-file needs the runtime inside the exe, so it cannot be combined with -FrameworkDependent. Add -NoSingleFile."
    }

    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    $dotnetArgs = @(
        'publish', $csproj,
        '-c', 'Release',
        '-r', $Runtime,
        "--self-contained=$selfContained",
        "-p:Version=$Version",
        "-p:PublishSingleFile=$($singleFile.ToString().ToLower())",
        '-p:DebugType=none',
        '-p:GenerateDocumentationFile=false',
        '-o', $publishDir
    )
    if ($singleFile) {
        $dotnetArgs += @(
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            '-p:PublishTrimmed=false'
        )
    }
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    Write-Ok "published to $publishDir"
}

# The csproj sets <AssemblyName>VocalB1</AssemblyName>, so the apphost is VocalB1.exe.
$exe = Join-Path $publishDir 'VocalB1.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    $anyExe = Get-ChildItem -LiteralPath $publishDir -Filter '*.exe' -File |
        Where-Object { $_.Name -notmatch '^(createdump|vcruntime.*|api-ms-.*)\.exe$' } |
        Select-Object -First 1
    if (-not $anyExe) { throw "Publish output contains no executable: $publishDir" }
    $exe = $anyExe.FullName
    Write-Verbose "Falling back to $exe"
}
Write-Ok "apphost: $(Split-Path -Leaf $exe)"

# ------------------------------------------------------------------ embedded python (built once)
$pythonRuntime = Join-Path $Stage '_python'
if ($variants -contains 'full') {
    if (Test-Path -LiteralPath $pythonRuntime) {
        Write-Step 'Reusing embedded Python runtime'
    } else {
        Write-Step 'Building embedded Python runtime'
        $zipPath = Join-Path $Stage 'python-embed.zip'
        Write-Host "    downloading $PythonEmbedUrl"
        Invoke-WebRequest -Uri $PythonEmbedUrl -OutFile $zipPath -UseBasicParsing
        Expand-Archive -LiteralPath $zipPath -DestinationPath $pythonRuntime -Force
        Remove-Item -LiteralPath $zipPath -Force

        $pth = Get-ChildItem -LiteralPath $pythonRuntime -Filter 'python*._pth' | Select-Object -First 1
        if (-not $pth) { throw "No python*._pth inside the embeddable package" }
        $lines = @(Get-Content -LiteralPath $pth.FullName)
        $lines = @($lines | ForEach-Object { if ($_ -eq '#import site') { 'import site' } else { $_ } })
        if ($lines -notcontains 'Lib\site-packages') { $lines += 'Lib\site-packages' }
        Set-Content -LiteralPath $pth.FullName -Value $lines -Encoding ASCII

        $py = Join-Path $pythonRuntime 'python.exe'
        $getPip = Join-Path $Stage 'get-pip.py'
        Invoke-WebRequest -Uri 'https://bootstrap.pypa.io/get-pip.py' -OutFile $getPip -UseBasicParsing
        & $py $getPip --no-warn-script-location --index-url $PipIndex
        if ($LASTEXITCODE -ne 0) { throw "get-pip.py failed" }
        Remove-Item -LiteralPath $getPip -Force

        & $py -m pip install --no-warn-script-location --index-url $PipIndex `
            -r (Join-Path $Root 'requirements.txt')
        if ($LASTEXITCODE -ne 0) { throw "pip install failed" }

        & $py -c "import numpy, scipy, soundfile, pyworld; print('deps ok')"
        if ($LASTEXITCODE -ne 0) { throw "embedded Python cannot import the required packages" }
        Write-Ok ("embedded Python ready ({0} MB)" -f (Get-SizeMB $pythonRuntime))
    }
}

# ------------------------------------------------------------------ assemble
$engineFiles = @(
    'VocalB1Synthesizer.py', 'VocalDecomposer.py', 'VocalSynthesizer.py', 'VocalB1.py',
    'spectrogram.py', 'oscilloscope.py', 'pit_editor.py', 'prosses_breath.py'
)

$built = @()

foreach ($variant in $variants) {
    $folderName = "vocalb1-v$Version-$Runtime-$variant"
    $target = Join-Path $Stage $folderName
    Write-Step "Assembling $folderName"

    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # 1. app binaries at the folder root
    Copy-Item -Path (Join-Path $publishDir '*') -Destination $target -Recurse -Force
    # Keep a stable entry point name: VocalB1.exe
    $publishedExe = Join-Path $target (Split-Path -Leaf $exe)
    if ((Split-Path -Leaf $publishedExe) -ne 'VocalB1.exe' -and (Test-Path -LiteralPath $publishedExe)) {
        Move-Item -LiteralPath $publishedExe -Destination (Join-Path $target 'VocalB1.exe') -Force
    }

    # 2. python engine, at the folder root so auto-discovery finds it
    foreach ($f in $engineFiles) {
        $src = Join-Path $Root $f
        if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $target -Force }
    }
    Copy-Item -LiteralPath (Join-Path $Root 'vocal_lib') -Destination $target -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $Root 'languages') -Destination $target -Recurse -Force

    # 3. the bridge script keeps its relative path ui\bridge
    $bridgeDir = Join-Path $target 'ui/bridge'
    New-Item -ItemType Directory -Path $bridgeDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root 'ui/bridge/vocal_bridge.py') -Destination $bridgeDir -Force

    # 4. empty voicebank folder (README only - never the audio)
    $vb = Join-Path $target 'voicebank'
    New-Item -ItemType Directory -Path $vb -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root 'voicebank/README.md') -Destination $vb -Force

    # 5. docs and metadata
    Copy-Item -LiteralPath (Join-Path $Root 'README.md')      -Destination $target -Force
    Copy-Item -LiteralPath (Join-Path $Root 'LICENSE')        -Destination $target -Force
    Copy-Item -LiteralPath (Join-Path $Root 'requirements.txt') -Destination $target -Force
    Copy-Item -LiteralPath (Join-Path $Root 'docs/QUICKSTART.txt') -Destination $target -Force
    Set-Content -LiteralPath (Join-Path $target 'VERSION') -Value $Version -Encoding ASCII

    # 6. embedded python (full only)
    if ($variant -eq 'full') {
        Copy-Item -LiteralPath $pythonRuntime -Destination (Join-Path $target 'python') -Recurse -Force
    }

    # 7. never ship a local settings file or stray cache
    Get-ChildItem -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'engine.settings.json' -or $_.Name -eq '__pycache__' -or $_.Extension -eq '.pyc' } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    $sizeMB = Get-SizeMB $target
    $zipName = "$folderName.zip"
    $zipPath = Join-Path $DistPath $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    Write-Step "Compressing $zipName"
    Compress-Archive -Path $target -DestinationPath $zipPath -CompressionLevel Optimal
    $zipMB = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Ok ("{0}  folder {1} MB -> zip {2} MB" -f $zipName, $sizeMB, $zipMB)

    $built += [pscustomobject]@{ Variant = $variant; Zip = $zipName; SizeMB = $zipMB; FolderMB = $sizeMB }
}

# ------------------------------------------------------------------ checksums
Write-Step 'Writing SHA256SUMS.txt'
$sumsPath = Join-Path $DistPath 'SHA256SUMS.txt'
$lines = foreach ($b in $built) {
    $h = (Get-FileHash -LiteralPath (Join-Path $DistPath $b.Zip) -Algorithm SHA256).Hash.ToLower()
    "$h  $($b.Zip)"
}
Set-Content -LiteralPath $sumsPath -Value $lines -Encoding ASCII
$lines | ForEach-Object { Write-Ok $_ }

# ------------------------------------------------------------------ release notes
Write-Step 'Writing RELEASE-NOTES.md'
$downloads = foreach ($b in $built) {
    if ($b.Variant -eq 'full') {
        "- **full** ($($b.SizeMB) MB) - Python runtime included, unzip and run."
    } else {
        "- **lite** ($($b.SizeMB) MB) - requires Python 3.10+ on PATH, then ``pip install -r requirements.txt``."
    }
}
$buildNote = if ($variants -contains 'full') {
    '- The full build bundles an embedded Python; the lite build uses your own.'
} else {
    '- This build uses your installed Python.'
}

# The template lives in docs/ so this script can stay pure ASCII. PowerShell 5.1
# would otherwise read a BOM-less UTF-8 .ps1 as ANSI and mangle any non-ASCII text.
$templatePath = Join-Path $Root 'docs/RELEASE-NOTES-TEMPLATE.md'
if (-not (Test-Path -LiteralPath $templatePath)) { throw "Template not found: $templatePath" }
$notes = Get-Content -LiteralPath $templatePath -Raw -Encoding UTF8
$notes = $notes.Replace('{{VERSION}}', $Version)
$notes = $notes.Replace('{{DOWNLOADS}}', ($downloads -join "`r`n"))
$notes = $notes.Replace('{{BUILDNOTE}}', $buildNote)
[IO.File]::WriteAllText((Join-Path $DistPath 'RELEASE-NOTES.md'), $notes, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "Done. Artefacts in $DistPath :" -ForegroundColor Green
Get-ChildItem -LiteralPath $DistPath -File | Select-Object Name, @{
    Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 1) }
} | Format-Table -AutoSize
Write-Host "Next: commit/push, create the tag v$Version, then publish the release on GitHub with the" -ForegroundColor White
Write-Host "      .zip files plus SHA256SUMS.txt, and paste RELEASE-NOTES.md as the description." -ForegroundColor White
