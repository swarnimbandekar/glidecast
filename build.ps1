<#
.SYNOPSIS
  Builds a Glidecast release: self-contained app + CLI, a portable zip, and the installer.

.EXAMPLE
  ./build.ps1                 # version from Directory.Build.props
  ./build.ps1 -Version 1.2.0
  ./build.ps1 -SkipInstaller  # no Inno Setup needed
  ./build.ps1 -FFmpegPath C:\ffmpeg\bin\ffmpeg.exe   # bundle this ffmpeg build
  ./build.ps1 -NoFFmpeg       # don't bundle; the installer offers winget instead

FFmpeg is bundled by default (found via -FFmpegPath, GLIDECAST_FFMPEG, PATH or
winget). It is a separate program under its own license (LGPL or GPL depending
on the build): its LICENSE is shipped next to it as FFMPEG-LICENSE.txt, and
whoever publishes the release must also make that build's source available.
An LGPL build keeps those obligations lightest.
#>
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipInstaller,
    [string]$FFmpegPath,
    [switch]$NoFFmpeg
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version
}
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish | Out-Null

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }

Write-Host "Publishing Glidecast $Version..." -ForegroundColor Cyan
foreach ($proj in "src/Glidecast/Glidecast.csproj", "src/Glidecast.Cli/Glidecast.Cli.csproj") {
    & $dotnet publish (Join-Path $root $proj) -c $Configuration -r win-x64 --self-contained true `
        -p:Version=$Version -p:PublishReadyToRun=true -p:DebugType=none -o $publish
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

$bundled = $false
if (-not $NoFFmpeg) {
    $ff = $FFmpegPath
    if (-not $ff -and $env:GLIDECAST_FFMPEG) { $ff = $env:GLIDECAST_FFMPEG }
    if (-not $ff) { $ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source }
    if ($ff) {
        # winget's Links\ffmpeg.exe is a shim; resolve the real binary in the package folder.
        $item = Get-Item $ff
        if ($item.LinkType -and $item.Target) { $ff = @($item.Target)[0] }
        if ($ff -like "*\WinGet\Links\*") {
            $real = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -like "*Gyan.FFmpeg*\bin" } | Select-Object -First 1
            if ($real) { $ff = $real.FullName }
        }
    }
    if (-not $ff -or -not (Test-Path $ff)) { throw "ffmpeg.exe not found to bundle. Pass -FFmpegPath, or -NoFFmpeg to skip bundling." }
    Copy-Item $ff (Join-Path $publish "ffmpeg.exe")
    $lic = @("LICENSE", "LICENSE.txt", "COPYING.GPLv3", "COPYING.LGPLv2.1") |
        ForEach-Object { Join-Path (Split-Path (Split-Path $ff)) $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($lic) { Copy-Item $lic (Join-Path $publish "FFMPEG-LICENSE.txt") }
    else { Write-Warning "No LICENSE found next to $ff; add FFMPEG-LICENSE.txt by hand before publishing." }
    $ver = (& $ff -hide_banner -version | Select-Object -First 1)
    Write-Host "Bundled $ver ($([math]::Round((Get-Item $ff).Length / 1MB)) MB)" -ForegroundColor Cyan
    $bundled = $true
}

$zip = Join-Path $artifacts "Glidecast-$Version-win-x64-portable.zip"
Compress-Archive -Path "$publish/*" -DestinationPath $zip
Write-Host "Portable: $zip" -ForegroundColor Green

if (-not $SkipInstaller) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup), or pass -SkipInstaller." }
    $isccArgs = @("/DAppVersion=$Version", "/DPublishDir=$publish")
    if ($bundled) { $isccArgs += "/DBundledFFmpeg=1" }
    & $iscc @isccArgs (Join-Path $root "installer/glidecast.iss")
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
    Write-Host "Installer: $(Join-Path $artifacts "Glidecast-Setup-$Version-x64.exe")" -ForegroundColor Green
}
