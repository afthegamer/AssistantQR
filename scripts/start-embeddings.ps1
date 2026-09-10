# Demarre le service HTTP d'embeddings sur le port 8088.
#
# Cree l'environnement virtuel s'il manque, installe les dependances epinglees,
# puis lance uvicorn. Le service demarre meme si Ollama n'est pas encore lance :
# la panne se lira alors dans GET /health (ollama_reachable = false).
#
# Usage :
#   .\scripts\start-embeddings.ps1
#   .\scripts\start-embeddings.ps1 -Model bge-m3
#   .\scripts\start-embeddings.ps1 -Reload

[CmdletBinding()]
param(
    [string]$Model = $env:EMBEDDING_MODEL,
    [string]$OllamaUrl = $env:OLLAMA_URL,
    [string]$BindHost = "127.0.0.1",
    [int]$Port = 8088,
    [switch]$Reload
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$serviceDir = Join-Path $repoRoot "python\embeddings_service"
$venvDir = Join-Path $serviceDir ".venv"
$venvPython = Join-Path $venvDir "Scripts\python.exe"

$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCommand) {
    throw "Python 3.11+ est introuvable dans le PATH. Installe-le depuis python.org."
}

if (-not (Test-Path $venvPython)) {
    Write-Host "Creation de l'environnement virtuel dans $venvDir"
    & $pythonCommand.Source -m venv $venvDir
}

Write-Host "Installation des dependances (versions epinglees)"
& $venvPython -m pip install --disable-pip-version-check --quiet --upgrade pip
& $venvPython -m pip install --disable-pip-version-check --quiet -r (Join-Path $serviceDir "requirements.txt")

if ($Model) { $env:EMBEDDING_MODEL = $Model }
if ($OllamaUrl) { $env:OLLAMA_URL = $OllamaUrl }

$displayedModel = if ($env:EMBEDDING_MODEL) { $env:EMBEDDING_MODEL } else { "qwen3-embedding:0.6b" }
$displayedOllama = if ($env:OLLAMA_URL) { $env:OLLAMA_URL } else { "http://localhost:11434" }

Write-Host ""
Write-Host "Service d'embeddings : http://${BindHost}:$Port"
Write-Host "  modele  : $displayedModel"
Write-Host "  ollama  : $displayedOllama"
Write-Host "  sonde   : curl http://${BindHost}:$Port/health"
Write-Host ""

$uvicornArgs = @("-m", "uvicorn", "app.main:app", "--host", $BindHost, "--port", $Port)
if ($Reload) { $uvicornArgs += "--reload" }

Push-Location $serviceDir
try {
    & $venvPython @uvicornArgs
}
finally {
    Pop-Location
}
