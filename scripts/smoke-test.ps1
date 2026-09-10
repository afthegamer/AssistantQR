# Verifie a la main le contrat HTTP du service d'embeddings.
#
# C'est le troisieme argument en faveur du service HTTP plutot que d'un
# sous-processus : on diagnostique toute la couche Python depuis un terminal,
# sans SDK .NET installe, et on sait immediatement de quel cote du pont se
# trouve le probleme.
#
# Usage :
#   .\scripts\smoke-test.ps1
#   .\scripts\smoke-test.ps1 -BaseUrl http://localhost:8088
#   .\scripts\smoke-test.ps1 -Write        # DETRUIT l'index en place

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:8088",
    [switch]$Write
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd("/")
$script:Failures = 0

function Write-Ok   { param([string]$Message) Write-Host "  [ok]   $Message" }
function Write-Warn  { param([string]$Message) Write-Host "  [!]    $Message" -ForegroundColor Yellow }
function Write-Ko    { param([string]$Message) Write-Host "  [KO]   $Message" -ForegroundColor Red; $script:Failures++ }
function Write-Step  { param([string]$Message) Write-Host ""; Write-Host "=== $Message" -ForegroundColor Cyan }

function Invoke-Api {
    <#
        Retourne un objet { Ok; Status; Body; Error } plutot que de lever :
        un corps d'erreur du service est une information utile, pas un incident.
    #>
    param(
        [string]$Method,
        [string]$Path,
        $Body = $null
    )
    $parameters = @{
        Method      = $Method
        Uri         = "$BaseUrl$Path"
        ContentType = "application/json; charset=utf-8"
    }
    if ($null -ne $Body) {
        $parameters.Body = ([System.Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 6 -Compress)))
    }
    try {
        $response = Invoke-RestMethod @parameters
        return [pscustomobject]@{ Ok = $true; Status = 200; Body = $response; Error = $null }
    }
    catch {
        $status = 0
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        $apiError = $null
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            try { $apiError = ($_.ErrorDetails.Message | ConvertFrom-Json).error } catch { }
        }
        if (-not $apiError) {
            $apiError = [pscustomobject]@{ code = "TRANSPORT"; message = $_.Exception.Message; hint = "" }
        }
        return [pscustomobject]@{ Ok = $false; Status = $status; Body = $null; Error = $apiError }
    }
}

function New-UnitVector {
    param([int]$Dimension, [int[]]$Axes)
    $values = New-Object 'double[]' $Dimension
    foreach ($axis in $Axes) { $values[$axis] = 1.0 }
    $norm = [math]::Sqrt([double]$Axes.Count)
    if ($norm -eq 0) { $norm = 1.0 }
    return @($values | ForEach-Object { $_ / $norm })
}

function New-FakeChunk {
    param([string]$ChunkId, [string]$Level, [double[]]$Vector)
    $documentId = $ChunkId.Split("#")[0]
    return [ordered]@{
        chunk_id       = $ChunkId
        document_id    = $documentId
        document_title = "Document $documentId"
        access_level   = $Level
        ordinal        = 0
        text           = "Texte factice du chunk $ChunkId."
        vector         = $Vector
    }
}

Write-Host "Service vise : $BaseUrl"

# ----------------------------------------------------------------- /health
Write-Step "GET /health"
$health = Invoke-Api -Method GET -Path "/health"
if (-not $health.Ok) {
    Write-Ko "le service ne repond pas ($($health.Error.message)). Lance .\scripts\start-embeddings.ps1"
    exit 1
}
$currentModel = $health.Body.embedding_model
$dimension = [int]$health.Body.dimension
Write-Ok "modele courant : $currentModel (dimension $dimension)"
Write-Ok "chunks indexes : $($health.Body.index_chunk_count)"
if ($health.Body.ollama_reachable) {
    Write-Ok "ollama joignable"
}
else {
    Write-Warn "ollama injoignable (les appels /embed vont echouer)"
}

# -------------------------------------------------------- /index/metadata
Write-Step "GET /index/metadata"
$metadata = Invoke-Api -Method GET -Path "/index/metadata"
if (-not $metadata.Ok) {
    Write-Ko "code $($metadata.Status) : $($metadata.Error.message)"
}
else {
    $indexModel = $metadata.Body.embedding_model
    Write-Ok "index construit par : $(if ($indexModel) { $indexModel } else { '(jamais construit)' })"
    Write-Ok "decoupage : $($metadata.Body.chunking_strategy_id)  built_at : $($metadata.Body.built_at)"
    if ($indexModel -and $indexModel -ne $currentModel) {
        Write-Warn "DESACCORD DE MODELE : index construit par $indexModel, service configure sur $currentModel."
        Write-Warn "Le service ne refusera rien (dimensions egales) : c'est la panne silencieuse. Reindexe."
    }
}

# ------------------------------------------------------------------ /embed
Write-Step "POST /embed (kind=query)"
$embed = Invoke-Api -Method POST -Path "/embed" -Body @{
    texts = @("Quels sont les horaires d'ouverture le samedi ?")
    kind  = "query"
}
$queryVector = $null
if ($embed.Ok) {
    $queryVector = $embed.Body.vectors[0]
    Write-Ok "vecteur obtenu : dimension $($embed.Body.dimension), modele $($embed.Body.model)"
}
elseif ($embed.Error.code -in @("OLLAMA_UNREACHABLE", "MODEL_NOT_FOUND")) {
    Write-Warn "code $($embed.Status) / $($embed.Error.code) : $($embed.Error.message)"
    Write-Warn "conseil : $($embed.Error.hint)"
}
else {
    Write-Ko "code $($embed.Status) / $($embed.Error.code) : $($embed.Error.message)"
}

# ----------------------------------------------------------- /index/search
Write-Step "POST /index/search"
if ($null -ne $queryVector) {
    $search = Invoke-Api -Method POST -Path "/index/search" -Body @{
        vector           = $queryVector
        top_k            = 4
        max_access_level = $null
    }
    if ($search.Ok) {
        $summary = ($search.Body.results | ForEach-Object { "$($_.chunk_id) ($([math]::Round($_.score, 3)))" }) -join ", "
        Write-Ok "resultats : $(if ($summary) { $summary } else { '(aucun)' })"
    }
    elseif ($search.Error.code -eq "INDEX_EMPTY") {
        Write-Warn "index vide : lance d'abord l'indexation du corpus (commande index du CLI)."
    }
    else {
        Write-Ko "code $($search.Status) / $($search.Error.code) : $($search.Error.message)"
    }
}
else {
    Write-Warn "ignore : pas de vecteur de requete disponible."
}

# --------------------------------------- ecriture (optionnelle, destructive)
if ($Write) {
    Write-Step "POST /index/reset + /index/upsert + /index/search  (MODE ECRITURE)"
    Write-Warn "l'index en place est REMPLACE par trois chunks factices ; il faudra reindexer."

    $reset = Invoke-Api -Method POST -Path "/index/reset" -Body @{
        model                = "smoke-test"
        dimension            = $dimension
        chunking_strategy_id = "smoke-test"
    }
    if ($reset.Ok) { Write-Ok "reset : ok" } else { Write-Ko "reset code $($reset.Status) : $($reset.Error.message)" }

    $items = @(
        (New-FakeChunk -ChunkId "secret#0"  -Level "confidential" -Vector (New-UnitVector -Dimension $dimension -Axes @(0))),
        (New-FakeChunk -ChunkId "interne#0" -Level "internal"     -Vector (New-UnitVector -Dimension $dimension -Axes @(0, 1))),
        (New-FakeChunk -ChunkId "public#0"  -Level "public"       -Vector (New-UnitVector -Dimension $dimension -Axes @(0, 1, 2)))
    )
    $upsert = Invoke-Api -Method POST -Path "/index/upsert" -Body @{ items = $items }
    if ($upsert.Ok) { Write-Ok "upsert : count = $($upsert.Body.count)" } else { Write-Ko "upsert code $($upsert.Status) : $($upsert.Error.message)" }

    $probe = New-UnitVector -Dimension $dimension -Axes @(0)

    $all = Invoke-Api -Method POST -Path "/index/search" -Body @{ vector = $probe; top_k = 2; max_access_level = $null }
    if ($all.Ok) {
        Write-Ok "sans filtre (top 2) : $((($all.Body.results | ForEach-Object { $_.chunk_id }) -join ', '))"
    }
    else { Write-Ko "search code $($all.Status) : $($all.Error.message)" }

    $filtered = Invoke-Api -Method POST -Path "/index/search" -Body @{ vector = $probe; top_k = 2; max_access_level = "public" }
    if ($filtered.Ok) {
        $ids = ($filtered.Body.results | ForEach-Object { $_.chunk_id }) -join ", "
        Write-Ok "pre-filtre public (top 2) : $ids"
        if ($ids -eq "public#0") {
            Write-Ok "le filtrage a bien eu lieu AVANT le top-k (sinon la liste serait vide)."
        }
        else {
            Write-Ko "resultat inattendu du pre-filtrage : $ids"
        }
    }
    else { Write-Ko "search filtre code $($filtered.Status) : $($filtered.Error.message)" }
}

Write-Host ""
if ($script:Failures -eq 0) {
    Write-Host "Contrat HTTP verifie sans echec." -ForegroundColor Green
}
else {
    Write-Host "$script:Failures verification(s) en echec." -ForegroundColor Red
}
exit $script:Failures
