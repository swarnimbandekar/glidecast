<#
.SYNOPSIS
  Builds a Glidecast release: self-contained app + CLI, a portable zip, and the installer.

.EXAMPLE
  ./build.ps1                 # version from Directory.Build.props
  ./build.ps1 -Version 1.2.0
  ./build.ps1 -SkipInstaller  # no Inno Setup needed
#>
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipInstaller
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
    & $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root "installer/glidecast.iss")
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
    Write-Host "Installer: $(Join-Path $artifacts "Glidecast-Setup-$Version-x64.exe")" -ForegroundColor Green
}
