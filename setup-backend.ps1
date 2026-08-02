param(
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$backendRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$venvPath = Join-Path $backendRoot ".venv"
$requirementsPath = Join-Path $backendRoot "requirements.txt"

if (-not (Test-Path -LiteralPath $requirementsPath)) {
    throw "requirements.txt non trovato in $backendRoot"
}

& $Python -m venv $venvPath
if ($LASTEXITCODE -ne 0) {
    throw "Creazione dell'ambiente Python non riuscita."
}

$venvPython = Join-Path $venvPath "Scripts\python.exe"
& $venvPython -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) {
    throw "Aggiornamento di pip non riuscito."
}

& $venvPython -m pip install -r $requirementsPath
if ($LASTEXITCODE -ne 0) {
    throw "Installazione delle dipendenze non riuscita."
}

Write-Host "Backend WhisperLS pronto: $venvPython"
