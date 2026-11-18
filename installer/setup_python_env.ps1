# ScreenSub AI - User-Consented Python Environment Setup & Verification
param(
    [string]$PythonExe = "",
    [switch]$VerifyOnly,
    [switch]$ConsentToInstall
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot = Resolve-Path (Join-Path $scriptDir "..")
if (-not (Test-Path (Join-Path $appRoot "workers\translation\requirements.txt"))) {
    # Running from installed {app}\setup_python_env.ps1
    $appRoot = $scriptDir
}

$ocrReq = Join-Path $appRoot "workers\paddle-ocr\requirements.txt"
$transReq = Join-Path $appRoot "workers\translation\requirements.txt"

function Find-PythonCandidate {
    param([string]$Preferred)
    if ($Preferred -and (Test-Path $Preferred)) { return $Preferred }

    $candidates = @(
        (Join-Path $appRoot "python\python.exe"),
        (Join-Path $appRoot "runtime\python\python.exe"),
        (Join-Path $appRoot ".venv\Scripts\python.exe"),
        "C:\PyThon\python.exe",
        "C:\Program Files\Python312\python.exe",
        "C:\Program Files\Python311\python.exe",
        "C:\Program Files\Python310\python.exe",
        (Join-Path $env:LOCALAPPDATA "Programs\Python\Python312\python.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Python\Python311\python.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Python\Python310\python.exe")
    )

    foreach ($cand in $candidates) {
        if ($cand -and (Test-Path $cand)) { return $cand }
    }

    # Check standard Python.org Windows Registry keys (covers per-user / system installs not on PATH)
    foreach ($root in @("HKCU:\Software\Python\PythonCore", "HKLM:\SOFTWARE\Python\PythonCore")) {
        foreach ($ver in @("3.12", "3.11", "3.10")) {
            $keyPath = "$root\$ver\InstallPath"
            if (Test-Path $keyPath) {
                $props = Get-ItemProperty -Path $keyPath -ErrorAction SilentlyContinue
                if ($props.ExecutablePath -and (Test-Path $props.ExecutablePath)) { return $props.ExecutablePath }
                if ($props.'(default)' -and (Test-Path (Join-Path $props.'(default)' "python.exe"))) {
                    return (Join-Path $props.'(default)' "python.exe")
                }
            }
        }
    }

    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source -notlike "*WindowsApps*") {
        return $cmd.Source
    }
    return $null
}

$vcRuntimePath = Join-Path $env:SystemRoot "System32\vcruntime140.dll"
if (-not (Test-Path $vcRuntimePath)) {
    Write-Host "[WARNING] Khong tim thay vcruntime140.dll trong System32. ONNX Runtime / CTranslate2 can Microsoft Visual C++ 2015-2022 Redistributable (x64)." -ForegroundColor Yellow
}

$py = Find-PythonCandidate -Preferred $PythonExe
if (-not $py) {
    Write-Host "[ERROR] Khong tim thay Python 3.10 - 3.12 (hoac runtime python\python.exe)." -ForegroundColor Red
    Write-Host "Vui long cai Python 3.12 (x64) tu https://www.python.org/downloads/ hoac dat runtime portable vao '$appRoot\python\'." -ForegroundColor Yellow
    exit 1
}

Write-Host "Python detected: $py" -ForegroundColor Cyan
& $py --version

$checkCode = @"
import importlib, sys
pkgs = [
    ("rapidocr_onnxruntime", "OCR"),
    ("onnxruntime", "OCR"),
    ("cv2", "OCR-FastDecode"),
    ("PIL", "OCR-Fallback"),
    ("numpy", "OCR"),
    ("ctranslate2", "Translation"),
    ("sentencepiece", "Translation"),
    ("tokenizers", "Translation"),
    ("transformers", "Translation")
]
missing = []
for mod, role in pkgs:
    try:
        m = importlib.import_module(mod)
        ver = getattr(m, "__version__", "ok")
        print(f"  [OK] {mod:22s} ({role}) -> {ver}")
    except Exception:
        print(f"  [MISSING] {mod:19s} ({role})")
        missing.append(mod)
sys.exit(2 if missing else 0)
"@

Write-Host "`nChecking required Python packages..." -ForegroundColor Yellow
$checkCode | & $py -
$checkExit = $LASTEXITCODE

if ($checkExit -eq 0) {
    Write-Host "`nAll OCR and Offline Neural Translation Python dependencies are ready!" -ForegroundColor Green
    exit 0
}

if ($VerifyOnly) {
    Write-Host "`n[VerifyOnly] Some dependencies are missing. No packages were downloaded." -ForegroundColor Yellow
    exit 2
}

Write-Host "`nThe following requirements files need to be installed via pip (requires Internet):" -ForegroundColor Yellow
Write-Host "  1) $ocrReq"
Write-Host "  2) $transReq"

if (-not $ConsentToInstall) {
    $ans = Read-Host "Do you consent to download & install these Python packages now? (Y/N)"
    if ($ans -notin @("Y", "y", "Yes", "yes")) {
        Write-Host "Cancelled by user. Nothing was downloaded." -ForegroundColor Yellow
        exit 3
    }
}

Write-Host "`nInstalling OCR dependencies..." -ForegroundColor Cyan
& $py -m pip install --upgrade pip
& $py -m pip install -r $ocrReq
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nInstalling Translation dependencies..." -ForegroundColor Cyan
& $py -m pip install -r $transReq
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`nRe-verifying installed modules..." -ForegroundColor Yellow
$checkCode | & $py -
if ($LASTEXITCODE -eq 0) {
    Write-Host "`nSetup completed cleanly!" -ForegroundColor Green
}
exit $LASTEXITCODE
