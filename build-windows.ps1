#requires -Version 5.1
<#
  Builds Nahr on Windows, runs the verification suite, publishes the app, and optionally compiles Nahr-Setup.exe.
  Usage:   .\build-windows.ps1                 # build + verify + publish (self-contained)
           .\build-windows.ps1 -BuildInstaller # also compile .\dist\Nahr-Setup-1.3.6.exe via Inno Setup 6
           .\build-windows.ps1 -SkipTests
           .\build-windows.ps1 -FrameworkDependent
  Result:  .\publish\Nahr.exe  (and .\dist\Nahr-Setup-1.3.6.exe when -BuildInstaller is set)
#>
param(
    [switch]$SkipTests,
    [switch]$FrameworkDependent,
    [switch]$BuildInstaller
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = (& dotnet --version) 2>$null
if (-not $version -or [int]($version.Split('.')[0]) -lt 10) {
    throw ".NET 10 SDK is required (found: '$version'). Install it from https://dotnet.microsoft.com/download/dotnet/10.0"
}

Write-Host "==> Building (Release)" -ForegroundColor Cyan
dotnet build DownloadManager.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed. Please send the first compiler errors so they can be fixed." }

if (-not $SkipTests) {
    Write-Host "==> Running the verification suite" -ForegroundColor Cyan
    dotnet run -c Release --no-build --project tests/DownloadManager.Verification
    if ($LASTEXITCODE -ne 0) { throw "Verification failed (see FAIL lines above)." }
}

Write-Host "==> Publishing" -ForegroundColor Cyan
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish src/DownloadManager.App -c Release -r win-x64 --self-contained $selfContained -o publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

if ($BuildInstaller) {
    Write-Host "==> Building Windows Setup Installer (Inno Setup 6)" -ForegroundColor Cyan
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install via: winget install JRSoftware.InnoSetup" }
    & $iscc "installer\Nahr.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
    Write-Host "Installer ready in .\dist\" -ForegroundColor Green
}

Write-Host "Done. Run:  .\publish\Nahr.exe" -ForegroundColor Green
Write-Host "Data folder: $env:LOCALAPPDATA\Nahr  (database + logs)"
