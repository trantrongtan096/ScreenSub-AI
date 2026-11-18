# ScreenSub AI - Automated Windows Beta Installer Build Script
param(
    [string]$Configuration = "Release",
    [bool]$SelfContained = $true,
    [string]$BundledPythonDir = "",
    [string]$Version = "1.0.0-beta.2"
)

$ErrorActionPreference = "Stop"
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "   ScreenSub AI - Windows Beta Installer Builder" -ForegroundColor Cyan
Write-Host "   Mode: SelfContained=$SelfContained ($Configuration)" -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptRoot "..")
Set-Location $repoRoot

$publishDir = Join-Path $repoRoot "publish\ScreenSubAI"
$distDir = Join-Path $repoRoot "dist"
$issFile = Join-Path $repoRoot "installer\ScreenSubAI_Setup.iss"

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir | Out-Null
}

# Clean previous publish directory so self-contained / framework-dependent artifacts do not mix
if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

$scFlag = if ($SelfContained) { "true" } else { "false" }

# 1. Publish .NET Desktop App
Write-Host "`n[1/4] Publishing .NET 10 WPF Application ($Configuration, self-contained=$scFlag)..." -ForegroundColor Yellow
dotnet publish "src\ScreenSub.Desktop\ScreenSub.Desktop.csproj" `
    -c $Configuration `
    -r win-x64 `
    --self-contained $scFlag `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

# 2. Copy Python workers, setup helper, and license documentation cleanly
Write-Host "`n[2/4] Staging Python workers, setup helper, and license files..." -ForegroundColor Yellow
$workersDest = Join-Path $publishDir "workers"
if (-not (Test-Path $workersDest)) {
    New-Item -ItemType Directory -Path $workersDest | Out-Null
}
Copy-Item -Path "workers\*" -Destination $workersDest -Recurse -Force

# Remove Python cache folders from published staging
Get-ChildItem -Path $workersDest -Directory -Recurse -Filter "__pycache__" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

$docsDest = Join-Path $publishDir "docs"
if (-not (Test-Path $docsDest)) {
    New-Item -ItemType Directory -Path $docsDest | Out-Null
}
Copy-Item -Path "docs\THIRD_PARTY_LICENSES.txt" -Destination (Join-Path $docsDest "THIRD_PARTY_LICENSES.txt") -Force
Copy-Item -Path "docs\INSTALL.md" -Destination (Join-Path $docsDest "INSTALL.md") -Force
Copy-Item -Path "docs\MODEL_GUIDE.md" -Destination (Join-Path $docsDest "MODEL_GUIDE.md") -Force
Copy-Item -Path "installer\setup_python_env.ps1" -Destination (Join-Path $publishDir "setup_python_env.ps1") -Force

# Optional: Bundle pre-staged offline portable Python runtime if explicitly provided by builder
if (-not [string]::IsNullOrWhiteSpace($BundledPythonDir)) {
    $resolvedPy = Resolve-Path $BundledPythonDir
    $pyExe = Join-Path $resolvedPy "python.exe"
    if (-not (Test-Path $pyExe)) {
        Write-Error "BundledPythonDir must contain python.exe: $resolvedPy"
    }
    Write-Host "[2b/4] Bundling portable Python runtime from $resolvedPy..." -ForegroundColor Yellow
    $pyDest = Join-Path $publishDir "python"
    Copy-Item -Path (Join-Path $resolvedPy "*") -Destination $pyDest -Recurse -Force
    Get-ChildItem -Path $pyDest -Directory -Recurse -Filter "__pycache__" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
}

# 3. Locate Inno Setup Compiler (ISCC.exe)
Write-Host "`n[3/4] Locating Inno Setup Compiler (ISCC.exe)..." -ForegroundColor Yellow
$isccCandidates = @(
    "C:\Users\Tan\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
)

$isccPath = $null
foreach ($cand in $isccCandidates) {
    if ($cand -and (Test-Path $cand)) {
        $isccPath = $cand
        break
    }
}

if (-not $isccPath) {
    Write-Error "Could not find Inno Setup compiler (ISCC.exe). Please install Inno Setup 6."
}

Write-Host "Found ISCC: $isccPath" -ForegroundColor Green

# 4. Compile Installer
Write-Host "`n[4/4] Compiling Inno Setup Package (v$Version)..." -ForegroundColor Yellow
$isccDefine = "/DSelfContainedBuild=$scFlag"
$isccVersionDefine = "/DMyAppVersion=$Version"
& $isccPath $isccDefine $isccVersionDefine $issFile

if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
}

$installerExe = Join-Path $distDir "ScreenSubAI_v${Version}_Setup_x64.exe"
if (Test-Path $installerExe) {
    $item = Get-Item $installerExe
    $hash = Get-FileHash -Path $installerExe -Algorithm SHA256
    Write-Host "`n======================================================" -ForegroundColor Green
    Write-Host "BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "Installer:     $($item.FullName)" -ForegroundColor White
    Write-Host "SelfContained: $scFlag" -ForegroundColor White
    Write-Host "Size:          $([math]::Round($item.Length / 1MB, 2)) MB ($($item.Length) bytes)" -ForegroundColor White
    Write-Host "SHA256:        $($hash.Hash)" -ForegroundColor White
    Write-Host "======================================================" -ForegroundColor Green
} else {
    Write-Error "Expected installer not found at $installerExe"
}
