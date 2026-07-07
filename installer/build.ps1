<#
.SYNOPSIS
  Builds the BackupApp installer end to end.

  1. Publishes the tray app as a self-contained, single-file win-x64 exe
     (no .NET install needed on the target machine).
  2. Compiles installer\BackupApp.iss with Inno Setup into a friendly Setup.exe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build.ps1
  powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.1.0
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repo "publish\app"

Write-Host "==> Publishing self-contained single-file exe (v$Version)..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo "src\BackupApp.Tray") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "BackupApp.Tray.exe"
if (-not (Test-Path $exe)) { throw "Published exe not found at $exe" }
"    exe size: {0} MB" -f [math]::Round((Get-Item $exe).Length / 1MB, 1) | Write-Host

Write-Host "==> Locating Inno Setup compiler (ISCC)..." -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    (Get-Command iscc -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup not found. Install it with:  winget install --id JRSoftware.InnoSetup -e"
}
Write-Host "    using $iscc"

Write-Host "==> Compiling installer..." -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot "BackupApp.iss") `
    "/DAppVersion=$Version" `
    "/DSourceDir=$publishDir"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

$setup = Join-Path $PSScriptRoot "Output\BackupApp-Setup-$Version.exe"
if (Test-Path $setup) {
    Write-Host ""
    Write-Host "==> Done. Installer:" -ForegroundColor Green
    Write-Host "    $setup"
    "    size: {0} MB" -f [math]::Round((Get-Item $setup).Length / 1MB, 1) | Write-Host
} else {
    throw "Expected installer not found at $setup"
}
