# Telecharge les modeles Ollama utilises par AssistantQR.
#
#   granite4.2:3b         modele de generation (les reponses citees)
#   qwen3-embedding:0.6b  embeddings, dimension 1024, aime les consignes
#   bge-m3                embeddings, dimension 1024, n'en veut pas
#
# Les deux modeles d'embeddings sont volontairement interchangeables : c'est
# avec eux qu'on met en scene la panne silencieuse (interroger un index construit
# par l'un avec des vecteurs produits par l'autre).
#
# Compter plusieurs gigaoctets de telechargement au premier appel.

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$models = @(
    "granite4.2:3b",
    "qwen3-embedding:0.6b",
    "bge-m3"
)

$ollama = Get-Command ollama -ErrorAction SilentlyContinue
if (-not $ollama) {
    Write-Error @'
La commande 'ollama' est introuvable.
Installe Ollama (https://ollama.com/download), ou passe par le conteneur :
  docker compose --profile ollama up -d
  docker compose --profile ollama exec ollama ollama pull qwen3-embedding:0.6b
'@
}

$failures = 0
foreach ($model in $models) {
    Write-Host "=== ollama pull $model"
    & $ollama.Source pull $model
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Echec du telechargement de $model."
        $failures++
    }
}

Write-Host ""
if ($failures -eq 0) {
    Write-Host "Les $($models.Count) modeles sont installes. Verification :"
    & $ollama.Source list
}
else {
    Write-Error "$failures modele(s) n'ont pas pu etre telecharges."
}
