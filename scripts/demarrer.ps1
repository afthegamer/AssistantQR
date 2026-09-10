# La porte d'entree unique du depot. Un seul script, relancable autant de fois
# qu'on veut, qui amene la machine d'un depot fraichement clone a une session
# utilisable en profil « local » — puis affiche la visite guidee.
#
# CE QUE CE SCRIPT N'EST PAS. Ce n'est pas un installeur : il ne telecharge rien
# d'autre que les modeles Ollama manquants, et il ne retire jamais un modele. Ce
# n'est pas non plus un mode degrade : si une etape echoue, il s'arrete avec un
# message qui dit quoi faire. Il ne bascule JAMAIS de lui-meme sur le profil
# « offline » — il peut le suggerer, c'est tout. Un repli silencieux est
# exactement la panne que ce depot enseigne a reperer ; ce serait malvenu de la
# commettre dans le script d'accueil.
#
# IDEMPOTENCE. Chaque etape commence par constater. Ollama repond deja ? On ne
# le relance pas. Les quatre modeles sont la ? On ne tire rien. Le binaire est
# plus recent que les sources ? On ne reconstruit pas. L'index contient des
# morceaux construits avec le modele qui tourne ? On ne reindexe pas — c'est le
# point qui compte, parce que reindexer coute environ deux minutes et demie.
#
# Usage :
#   .\scripts\demarrer.ps1
#   .\scripts\demarrer.ps1 -Model bge-m3
#   .\scripts\demarrer.ps1 -Reindex
#   .\scripts\demarrer.ps1 -SkipPull
#   .\scripts\demarrer.ps1 -Help
#
# Pour que les variables d'environnement restent dans TA session, source-le :
#   . .\scripts\demarrer.ps1

[CmdletBinding()]
param(
    # Modele d'embeddings vise pour le service Python et pour l'index.
    [string]$Model = "qwen3-embedding:0.6b",

    # Ne tire aucun modele manquant : constate et echoue s'il en manque un.
    [switch]$SkipPull,

    # Force la reindexation meme si l'index en place est deja coherent.
    [switch]$Reindex,

    # Affiche l'aide et sort.
    [switch]$Help
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Dot-source ou execution normale ? La difference n'est pas cosmetique : dans un
# script source, « exit » tuerait le terminal de l'utilisateur.
$script:Source = ($MyInvocation.InvocationName -eq ".")

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:CodeSortie = 0
$script:ActionsErreur = @()
$script:OllamaExe = $null
$script:Sante = $null
$script:CodeNatif = 0

$script:UrlOllama = "http://localhost:11434"
$script:UrlService = "http://127.0.0.1:8088"
$script:CheminCli = Join-Path $script:RepoRoot "src\AssistantQR.Cli\bin\Debug\net8.0\assistantqr.exe"
$script:ProjetCli = Join-Path $script:RepoRoot "src\AssistantQR.Cli"
$script:DossierJournaux = Join-Path $env:TEMP "assistantqr-demarrer"

# Les quatre modeles imposes par le sujet. Les tailles servent a prevenir AVANT
# de lancer un telechargement de plusieurs gigaoctets.
$script:ModelesRequis = @(
    [pscustomobject]@{ Nom = "granite4.2:3b";        Taille = "~2.2 Go"; Role = "generation, modele a raisonnement" }
    [pscustomobject]@{ Nom = "llama3.2:1b";          Taille = "~1.3 Go"; Role = "generation, sans raisonnement" }
    [pscustomobject]@{ Nom = "qwen3-embedding:0.6b"; Taille = "~640 Mo"; Role = "embeddings, dimension 1024" }
    [pscustomobject]@{ Nom = "bge-m3";               Taille = "~1.2 Go"; Role = "embeddings, dimension 1024" }
)

# Codes de retour, alignes sur l'esprit de ceux de la CLI : un code par famille
# de panne, pour qu'un script appelant puisse distinguer « rien d'installe » de
# « le service ne repond pas ».
$CODE_PREREQUIS = 1
$CODE_OLLAMA = 2
$CODE_MODELES = 3
$CODE_SERVICE = 4
$CODE_BUILD = 5
$CODE_INDEX = 6

# ------------------------------------------------------------------ affichage

function Write-Etape {
    param([string]$Numero, [string]$Titre)
    Write-Host ""
    Write-Host "=== Etape $Numero — $Titre" -ForegroundColor Cyan
}

function Write-Ok    { param([string]$Message) Write-Host "  [ok]   $Message" -ForegroundColor Green }
function Write-Info  { param([string]$Message) Write-Host "  [..]   $Message" }
function Write-Agir  { param([string]$Message) Write-Host "  [->]   $Message" -ForegroundColor Yellow }
function Write-Ko    { param([string]$Message) Write-Host "  [KO]   $Message" -ForegroundColor Red }

function Stop-Demarrage {
    <#
        Interrompt le script avec un message actionnable. Les actions proposees
        sont affichees telles quelles : elles doivent etre copiables.
    #>
    param([string]$Message, [string[]]$Actions = @(), [int]$Code = 1)
    $script:CodeSortie = $Code
    $script:ActionsErreur = $Actions
    throw $Message
}

function Invoke-Natif {
    <#
        Lance un executable et rend ses lignes, code de retour dans
        $script:CodeNatif.

        POURQUOI CE DETOUR. Sous Windows PowerShell 5.1, « commande 2>&1 » emet
        des ErrorRecord ; avec $ErrorActionPreference a « Stop », la moindre
        ligne de stderr — un avertissement de telechargement, une barre de
        progression — ferait lever le script. On abaisse donc la preference le
        temps de l'appel, et on juge sur le code de retour, qui est la seule
        chose que l'executable ait vraiment voulu dire.
    #>
    param([scriptblock]$Bloc)
    $ancien = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $lignes = @(& $Bloc 2>&1 | ForEach-Object { "$_" })
        $script:CodeNatif = $LASTEXITCODE
        return $lignes
    }
    finally {
        $ErrorActionPreference = $ancien
    }
}

function Invoke-NatifVisible {
    <#
        Meme chose, mais la sortie reste a l'ecran : une construction .NET ou une
        indexation de deux minutes doit se regarder pendant qu'elle se deroule.
    #>
    param([scriptblock]$Bloc)
    $ancien = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Bloc
        $script:CodeNatif = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $ancien
    }
}

# ------------------------------------------------------------------- sondages

function Get-JsonDistant {
    <#
        Retourne l'objet JSON, ou $null si le service ne repond pas. Une absence
        de reponse est une information attendue ici, pas un incident : elle
        signifie « pas encore demarre ».
    #>
    param([string]$Url, [int]$TimeoutSec = 5)
    try {
        $reponse = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
        return ($reponse.Content | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

function Get-NomModeleNormalise {
    <#
        « bge-m3 » et « bge-m3:latest » designent le meme modele : « ollama list »
        affiche le second, le sujet nomme le premier.
    #>
    param([string]$Nom)
    $normalise = "$Nom".Trim().ToLowerInvariant()
    if ($normalise.EndsWith(":latest")) { $normalise = $normalise.Substring(0, $normalise.Length - 7) }
    return $normalise
}

function Wait-Service {
    <#
        Attend qu'une URL reponde. Rend $true, ou $false au bout du delai — c'est
        l'appelant qui redige le message d'echec, parce que lui seul sait quoi
        proposer.
    #>
    param([string]$Url, [int]$DelaiMaxSecondes, [string]$Quoi)
    $chrono = [System.Diagnostics.Stopwatch]::StartNew()
    $dernierPoint = 0
    while ($chrono.Elapsed.TotalSeconds -lt $DelaiMaxSecondes) {
        if ($null -ne (Get-JsonDistant -Url $Url -TimeoutSec 3)) {
            Write-Ok "$Quoi repond apres $([int]$chrono.Elapsed.TotalSeconds) s."
            return $true
        }
        Start-Sleep -Seconds 2
        if ($chrono.Elapsed.TotalSeconds - $dernierPoint -ge 10) {
            $dernierPoint = [int]$chrono.Elapsed.TotalSeconds
            Write-Info "toujours en attente de $Quoi ($dernierPoint s / $DelaiMaxSecondes s)"
        }
    }
    return $false
}

# ---------------------------------------------------------------------- aide

function Show-Aide {
    Write-Host ""
    Write-Host "demarrer.ps1 — porte d'entree du depot AssistantQR" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Amene la machine d'un depot clone a une session utilisable en profil « local » :"
    Write-Host "  prerequis, serveur Ollama, modeles, service Python d'embeddings, construction,"
    Write-Host "  index. Chaque etape constate avant d'agir : relancer le script ne refait rien."
    Write-Host ""
    Write-Host "Usage :" -ForegroundColor Cyan
    Write-Host "  .\scripts\demarrer.ps1 [-Model <nom>] [-SkipPull] [-Reindex] [-Help]"
    Write-Host ""
    Write-Host "Parametres :" -ForegroundColor Cyan
    Write-Host "  -Model <nom>   Modele d'embeddings du service Python et de l'index."
    Write-Host "                 Defaut : qwen3-embedding:0.6b. Autre valeur prevue : bge-m3."
    Write-Host "  -SkipPull      Ne telecharge aucun modele manquant. Le script constate"
    Write-Host "                 l'absence et s'arrete plutot que de tirer des gigaoctets."
    Write-Host "  -Reindex       Force la reindexation du corpus meme si l'index est coherent."
    Write-Host "  -Help          Cette page."
    Write-Host ""
    Write-Host "Variables d'environnement :" -ForegroundColor Cyan
    Write-Host "  Le script positionne les variables du profil local dans son propre processus,"
    Write-Host "  et les affiche a la fin. Pour qu'elles restent dans TA session, source-le :"
    Write-Host "      . .\scripts\demarrer.ps1"
    Write-Host ""
    Write-Host "Codes de retour :" -ForegroundColor Cyan
    Write-Host "  0  Tout est en place."
    Write-Host "  1  Un prerequis manque (dotnet, python, ollama)."
    Write-Host "  2  Le serveur Ollama ne repond pas."
    Write-Host "  3  Un modele Ollama manque et n'a pas pu etre installe."
    Write-Host "  4  Le service Python d'embeddings ne repond pas."
    Write-Host "  5  La construction .NET a echoue."
    Write-Host "  6  L'indexation a echoue."
    Write-Host ""
    Write-Host "Ce script ne bascule jamais tout seul sur le profil « offline ». Si tu veux"
    Write-Host "travailler sans Ollama ni service Python, c'est une decision a prendre a la main :"
    Write-Host "      assistantqr ask ""...""  --profile offline"
    Write-Host ""
}

# ------------------------------------------------------------------- etapes

function Invoke-Etape1Prerequis {
    Write-Etape "1/7" "Prerequis"

    $manquants = @()

    # --- dotnet
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Write-Ko "dotnet : introuvable dans le PATH."
        $manquants += "  SDK .NET 8 : winget install --id Microsoft.DotNet.SDK.8"
    }
    else {
        $sdks = Invoke-Natif { & $dotnet.Source --list-sdks }
        $majeurs = @()
        foreach ($ligne in $sdks) {
            if ("$ligne" -match '^\s*(\d+)\.') { $majeurs += [int]$Matches[1] }
        }
        if ($majeurs.Count -eq 0) {
            Write-Ko "dotnet : present, mais aucun SDK installe (seulement le runtime ?)."
            $manquants += "  SDK .NET 8 : winget install --id Microsoft.DotNet.SDK.8"
        }
        elseif (($majeurs | Measure-Object -Maximum).Maximum -lt 8) {
            Write-Ko "dotnet : SDK $(($majeurs | Measure-Object -Maximum).Maximum).x seulement, il en faut au moins 8."
            $manquants += "  SDK .NET 8 : winget install --id Microsoft.DotNet.SDK.8"
        }
        else {
            $liste = ($sdks | ForEach-Object { ("$_" -split ' ')[0] }) -join ", "
            Write-Ok "dotnet : SDK $liste"
        }
    }

    # --- python
    # start-embeddings.ps1 appelle « python » : c'est donc « python » qu'on
    # verifie, pas « py » ni « python3 », sinon on validerait autre chose que ce
    # qui servira.
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) {
        Write-Ko "python : introuvable dans le PATH."
        $manquants += "  Python 3.11+ : winget install --id Python.Python.3.12"
    }
    else {
        $version = ((Invoke-Natif { & $python.Source --version }) -join " ").Trim()
        if ($version -match '(\d+)\.(\d+)') {
            $majeur = [int]$Matches[1]
            $mineur = [int]$Matches[2]
            if ($majeur -lt 3 -or ($majeur -eq 3 -and $mineur -lt 11)) {
                Write-Ko "python : $version, il en faut au moins 3.11."
                $manquants += "  Python 3.11+ : winget install --id Python.Python.3.12"
            }
            else {
                Write-Ok "python : $version ($($python.Source))"
            }
        }
        else {
            Write-Ko "python : version illisible (« $version »). Sur Windows, c'est souvent le"
            Write-Ko "         raccourci du Microsoft Store plutot qu'un interpreteur."
            $manquants += "  Python 3.11+ : winget install --id Python.Python.3.12"
        }
    }

    # --- ollama
    $ollama = Get-Command ollama -ErrorAction SilentlyContinue
    if ($ollama) {
        $script:OllamaExe = $ollama.Source
    }
    else {
        # Chemin d'installation par defaut sous Windows : Ollama s'installe sans
        # forcement rafraichir le PATH de la session en cours.
        $candidat = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
        if (Test-Path $candidat) { $script:OllamaExe = $candidat }
    }

    if (-not $script:OllamaExe) {
        Write-Ko "ollama : introuvable (ni dans le PATH, ni dans %LOCALAPPDATA%\Programs\Ollama)."
        $manquants += "  Ollama : winget install --id Ollama.Ollama"
    }
    else {
        $versionOllama = ((Invoke-Natif { & $script:OllamaExe --version }) | Select-Object -Last 1)
        Write-Ok "ollama : $versionOllama ($script:OllamaExe)"
    }

    if ($manquants.Count -gt 0) {
        Stop-Demarrage `
            -Message "Il manque $($manquants.Count) prerequis. Installe-les puis relance ce script." `
            -Actions ($manquants + @("", "  Puis rouvre un terminal pour que le PATH soit rafraichi.")) `
            -Code $CODE_PREREQUIS
    }
}

function Invoke-Etape2Ollama {
    Write-Etape "2/7" "Serveur Ollama"

    if ($null -ne (Get-JsonDistant -Url "$script:UrlOllama/api/tags" -TimeoutSec 3)) {
        Write-Ok "deja en ecoute sur $script:UrlOllama — rien a demarrer."
        return
    }

    Write-Agir "aucune reponse sur $script:UrlOllama : demarrage de « ollama serve »."
    Start-Process -FilePath $script:OllamaExe -ArgumentList "serve" -WindowStyle Hidden | Out-Null

    if (-not (Wait-Service -Url "$script:UrlOllama/api/tags" -DelaiMaxSecondes 60 -Quoi "Ollama")) {
        Stop-Demarrage `
            -Message "Ollama n'a pas repondu sur $script:UrlOllama au bout de 60 s." `
            -Actions @(
                "  Lance-le dans un terminal a part pour voir ce qu'il dit :",
                "      & '$script:OllamaExe' serve",
                "  Si le port 11434 est deja pris par autre chose :",
                "      Get-NetTCPConnection -LocalPort 11434 | Select-Object OwningProcess"
            ) `
            -Code $CODE_OLLAMA
    }
}

function Invoke-Etape3Modeles {
    Write-Etape "3/7" "Modeles Ollama"

    $sortie = Invoke-Natif { & $script:OllamaExe list }
    if ($script:CodeNatif -ne 0) {
        Stop-Demarrage `
            -Message "« ollama list » a echoue : $($sortie -join ' ')" `
            -Actions @("  Verifie que le serveur tourne : & '$script:OllamaExe' serve") `
            -Code $CODE_MODELES
    }

    $installes = @()
    foreach ($ligne in $sortie | Select-Object -Skip 1) {
        $texte = "$ligne".Trim()
        if ($texte.Length -eq 0) { continue }
        $installes += Get-NomModeleNormalise (($texte -split '\s+')[0])
    }

    $aTirer = @()
    foreach ($modele in $script:ModelesRequis) {
        if ($installes -contains (Get-NomModeleNormalise $modele.Nom)) {
            Write-Ok "$($modele.Nom) — present ($($modele.Role))"
        }
        else {
            Write-Agir "$($modele.Nom) — ABSENT, $($modele.Taille) a telecharger ($($modele.Role))"
            $aTirer += $modele
        }
    }

    if ($aTirer.Count -eq 0) {
        Write-Ok "les $($script:ModelesRequis.Count) modeles sont la — rien a telecharger."
        return
    }

    if ($SkipPull) {
        Stop-Demarrage `
            -Message "$($aTirer.Count) modele(s) manquent et -SkipPull interdit de les telecharger." `
            -Actions (@("  Relance sans -SkipPull, ou tire-les a la main :") +
                      ($aTirer | ForEach-Object { "      & '$script:OllamaExe' pull $($_.Nom)" })) `
            -Code $CODE_MODELES
    }

    $total = ($aTirer | ForEach-Object { $_.Taille }) -join " + "
    Write-Info "telechargement de $($aTirer.Count) modele(s) : $total. Ce script ne retire jamais un modele."

    foreach ($modele in $aTirer) {
        Write-Agir "ollama pull $($modele.Nom) ($($modele.Taille))"
        Invoke-NatifVisible { & $script:OllamaExe pull $modele.Nom }
        if ($script:CodeNatif -ne 0) {
            Stop-Demarrage `
                -Message "Le telechargement de $($modele.Nom) a echoue (code $($script:CodeNatif))." `
                -Actions @(
                    "  Reessaie a la main pour lire l'erreur complete :",
                    "      & '$script:OllamaExe' pull $($modele.Nom)"
                ) `
                -Code $CODE_MODELES
        }
        Write-Ok "$($modele.Nom) installe."
    }
}

function Invoke-Etape4Service {
    Write-Etape "4/7" "Service Python d'embeddings"

    $sante = Get-JsonDistant -Url "$script:UrlService/health" -TimeoutSec 5

    if ($null -ne $sante) {
        $qui = Get-NomModeleNormalise $sante.embedding_model
        $voulu = Get-NomModeleNormalise $Model

        if ($qui -ne $voulu) {
            # On ne tue pas un processus qu'on n'a pas demarre, et on ne fait pas
            # semblant que le modele demande est celui qui tourne. On s'arrete.
            Stop-Demarrage `
                -Message "Le service tourne deja avec « $($sante.embedding_model) », or tu demandes « $Model »." `
                -Actions @(
                    "  Soit tu gardes celui qui tourne :",
                    "      .\scripts\demarrer.ps1 -Model $($sante.embedding_model)",
                    "  Soit tu arretes le service en place, puis tu relances :",
                    "      Get-NetTCPConnection -LocalPort 8088 -State Listen | ForEach-Object { Stop-Process -Id `$_.OwningProcess -Force }",
                    "      .\scripts\demarrer.ps1 -Model $Model"
                ) `
                -Code $CODE_SERVICE
        }

        Write-Ok "deja en ecoute sur $script:UrlService — rien a demarrer."
        Write-Info "modele : $($sante.embedding_model) · dimension $($sante.dimension) · ollama joignable : $($sante.ollama_reachable)"
        $script:Sante = $sante
        return
    }

    Write-Agir "aucune reponse sur $script:UrlService : demarrage via scripts\start-embeddings.ps1 -Model $Model."
    Write-Info "au tout premier lancement, cette etape cree le venv et installe les dependances : compte quelques minutes."

    if (-not (Test-Path $script:DossierJournaux)) {
        New-Item -ItemType Directory -Path $script:DossierJournaux -Force | Out-Null
    }
    $journalSortie = Join-Path $script:DossierJournaux "embeddings.out.log"
    $journalErreur = Join-Path $script:DossierJournaux "embeddings.err.log"

    # Les journaux vont dans %TEMP% : ce script n'ecrit rien dans le depot en
    # dehors de bin/obj et de l'index du service.
    Write-Info "journaux : $journalSortie et $journalErreur"

    $hote = (Get-Process -Id $PID).Path
    $scriptService = Join-Path $PSScriptRoot "start-embeddings.ps1"

    Start-Process -FilePath $hote `
        -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptService, "-Model", $Model) `
        -WorkingDirectory $script:RepoRoot `
        -RedirectStandardOutput $journalSortie `
        -RedirectStandardError $journalErreur `
        -WindowStyle Hidden | Out-Null

    if (-not (Wait-Service -Url "$script:UrlService/health" -DelaiMaxSecondes 300 -Quoi "le service d'embeddings")) {
        Stop-Demarrage `
            -Message "Le service d'embeddings n'a pas repondu sur $script:UrlService au bout de 300 s." `
            -Actions @(
                "  Lis les journaux, la cause y est :",
                "      Get-Content '$journalErreur' -Tail 40",
                "      Get-Content '$journalSortie' -Tail 40",
                "  Ou relance-le au premier plan pour voir la trace complete :",
                "      .\scripts\start-embeddings.ps1 -Model $Model"
            ) `
            -Code $CODE_SERVICE
    }

    $sante = Get-JsonDistant -Url "$script:UrlService/health" -TimeoutSec 10
    if ($null -eq $sante) {
        Stop-Demarrage -Message "Le service a repondu puis s'est tu. Lis $journalErreur." -Code $CODE_SERVICE
    }

    Write-Info "modele : $($sante.embedding_model) · dimension $($sante.dimension) · ollama joignable : $($sante.ollama_reachable)"
    if (-not $sante.ollama_reachable) {
        Stop-Demarrage `
            -Message "Le service tourne mais ne joint pas Ollama : aucun embedding ne sera calculable." `
            -Actions @("  Verifie Ollama : & '$script:OllamaExe' serve") `
            -Code $CODE_SERVICE
    }
    $script:Sante = $sante
}

function Invoke-Etape5Construction {
    Write-Etape "5/7" "Construction .NET"

    $sources = Get-ChildItem -Path (Join-Path $script:RepoRoot "src") -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Where-Object { $_.Extension -eq ".cs" -or $_.Extension -eq ".csproj" -or $_.Extension -eq ".json" }

    foreach ($racine in @("Directory.Build.props", "global.json")) {
        $chemin = Join-Path $script:RepoRoot $racine
        if (Test-Path $chemin) { $sources = @($sources) + @(Get-Item $chemin) }
    }

    $plusRecente = ($sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)

    # LA REFERENCE N'EST PAS assistantqr.exe MAIS LE PLUS RECENT DES ARTEFACTS DE
    # SORTIE. Le .exe n'est qu'un lanceur : MSBuild ne le recopie pas forcement
    # quand seul du code change, et sa date resterait alors eternellement
    # anterieure a celle des sources — le script reconstruirait a chaque appel en
    # annoncant a chaque fois que les sources ont bouge. Le .dll, lui, est
    # reecrit des qu'une ligne de C# change.
    $dossierSortie = Split-Path -Parent $script:CheminCli
    $sortieRecente = Get-ChildItem -Path $dossierSortie -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

    if (-not (Test-Path $script:CheminCli)) {
        Write-Agir "le binaire de la CLI est absent : construction."
    }
    elseif ($null -ne $plusRecente -and $null -ne $sortieRecente -and
            $sortieRecente.LastWriteTimeUtc -lt $plusRecente.LastWriteTimeUtc) {
        Write-Agir "sources modifiees depuis la derniere construction ($($plusRecente.Name)) : reconstruction."
    }
    else {
        Write-Ok "binaire a jour ($([System.IO.Path]::GetFileName($script:CheminCli)), $($sortieRecente.LastWriteTime)) — rien a construire."
        return
    }

    $chrono = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-NatifVisible { & dotnet build $script:ProjetCli -v q --nologo }
    $code = $script:CodeNatif
    $chrono.Stop()

    if ($code -ne 0) {
        Stop-Demarrage `
            -Message "« dotnet build » a echoue (code $code)." `
            -Actions @(
                "  Relance-le sans filtre pour lire les erreurs :",
                "      dotnet build $script:ProjetCli",
                "  Si l'erreur parle d'un fichier verrouille (MSB3027), c'est qu'un assistantqr.exe",
                "  tourne encore. Attends qu'il finisse, ou regarde qui c'est :",
                "      Get-Process assistantqr -ErrorAction SilentlyContinue"
            ) `
            -Code $CODE_BUILD
    }

    Write-Ok "construction reussie en $([math]::Round($chrono.Elapsed.TotalSeconds, 1)) s."
}

function Invoke-Etape6Index {
    Write-Etape "6/7" "Index vectoriel"

    $meta = Get-JsonDistant -Url "$script:UrlService/index/metadata" -TimeoutSec 10
    if ($null -eq $meta) {
        Stop-Demarrage `
            -Message "Le service ne rend pas /index/metadata alors qu'il repondait a /health." `
            -Actions @("  curl $script:UrlService/index/metadata") `
            -Code $CODE_INDEX
    }

    $modeleService = Get-NomModeleNormalise $script:Sante.embedding_model
    $modeleIndex = Get-NomModeleNormalise $meta.embedding_model
    $morceaux = [int]$meta.chunk_count

    $raison = $null
    if ($Reindex) {
        $raison = "-Reindex demande explicitement."
    }
    elseif ($morceaux -eq 0) {
        $raison = "l'index est vide."
    }
    elseif ($modeleIndex -ne $modeleService) {
        # C'est la panne silencieuse du cours : meme dimension, produit scalaire
        # calculable, resultats plausibles et faux. On refuse de la laisser en place.
        $raison = "l'index a ete construit avec « $($meta.embedding_model) » alors que le service tourne avec « $($script:Sante.embedding_model) »."
    }

    if ($null -eq $raison) {
        Write-Ok "index deja construit avec « $($meta.embedding_model) » : $morceaux morceaux, decoupage « $($meta.chunking_strategy_id) »."
        Write-Info "construit le $($meta.built_at) — rien a reindexer (c'est la ~2 min 30 qu'on ne repaie pas)."
        return
    }

    Write-Agir "reindexation : $raison"
    $chrono = [System.Diagnostics.Stopwatch]::StartNew()

    Push-Location $script:RepoRoot
    try {
        Invoke-NatifVisible { & $script:CheminCli index }
        $code = $script:CodeNatif
    }
    finally {
        Pop-Location
    }
    $chrono.Stop()

    if ($code -ne 0) {
        Stop-Demarrage `
            -Message "« assistantqr index » a echoue (code $code) apres $([math]::Round($chrono.Elapsed.TotalSeconds, 1)) s." `
            -Actions @(
                "  Diagnostic complet :",
                "      $script:CheminCli doctor"
            ) `
            -Code $CODE_INDEX
    }

    $apres = Get-JsonDistant -Url "$script:UrlService/index/metadata" -TimeoutSec 10
    Write-Ok "reindexation terminee en $([math]::Round($chrono.Elapsed.TotalSeconds, 1)) s — $([int]$apres.chunk_count) morceaux avec « $($apres.embedding_model) »."
}

function Set-EnvironnementLocal {
    <#
        Positionne les variables du profil local dans CE processus. Si le script
        est source, elles restent dans la session de l'utilisateur ; sinon elles
        servent au moins aux commandes que ce script lance lui-meme, et sont
        affichees pour etre copiees.
    #>
    $env:ASSISTANTQR_PROFILE = "local"
    $env:ASSISTANTQR_EMBEDDINGS__MODEL = $script:Sante.embedding_model
    $env:ASSISTANTQR_EMBEDDINGS__DIMENSION = "$($script:Sante.dimension)"
    $env:ASSISTANTQR_LANGUAGEMODEL__MODEL = "granite4.2:3b"
}

function Show-Environnement {
    # Sans effet visible quand le script est execute (l'alias meurt avec le
    # processus), utile quand il est source : les six commandes de la visite
    # guidee deviennent copiables telles quelles.
    if (Test-Path $script:CheminCli) {
        Set-Alias -Name assistantqr -Value $script:CheminCli -Scope Global -Force
    }

    Write-Host ""
    Write-Host "Variables du profil local" -ForegroundColor Cyan
    if ($script:Source) {
        Write-Host "  (script source : elles sont deja actives dans cette session)"
    }
    else {
        Write-Host "  (script execute : copie ces quatre lignes dans ton terminal, ou source le script"
        Write-Host "   avec « . .\scripts\demarrer.ps1 » pour qu'il les pose lui-meme)"
    }
    Write-Host ""
    Write-Host "    `$env:ASSISTANTQR_PROFILE = ""local"""
    Write-Host "    `$env:ASSISTANTQR_EMBEDDINGS__MODEL = ""$($script:Sante.embedding_model)"""
    Write-Host "    `$env:ASSISTANTQR_EMBEDDINGS__DIMENSION = ""$($script:Sante.dimension)"""
    Write-Host "    `$env:ASSISTANTQR_LANGUAGEMODEL__MODEL = ""granite4.2:3b"""
    Write-Host ""
    Write-Host "  Et l'alias qui rend copiables les commandes de la visite guidee :"
    Write-Host ""
    Write-Host "    Set-Alias assistantqr '$script:CheminCli'"
}

function Show-VisiteGuidee {
    Write-Etape "7/7" "Visite guidee"

    # VISITE-GUIDEE-DEBUT
    # -------------------------------------------------------------------------
    # LES SIX COMMANDES ONT ETE JOUEES SUR CETTE MACHINE AVANT D'ETRE ECRITES
    # ICI. Les chiffres cites dans « Voir » sont des sorties relevees, pas des
    # ordres de grandeur inventes. L'ordre raconte quelque chose : on part de ce
    # qui rassure (un diagnostic vert), on finit par ce qui derange (41.7 % des
    # reponses qui bougent sans qu'aucune erreur ne soit levee).
    #
    # « --prompt-version 1.2.0 » N'EST PAS DECORATIF. Le gabarit par defaut du
    # depot est le 1.0.0 ; avec granite4.2:3b, il fait recopier au modele
    # l'identifiant fictif de son propre exemple, et la reponse est rejetee sur
    # « ModelCitedUnknownDocument ». Mesure : 1.0.0 -> refus, 1.2.0 -> reponse
    # citee, meme question, meme index, meme minute.
    #
    # Le champ « Voir » accepte plusieurs lignes. Les lignes de continuation
    # portent leurs cinq espaces d'indentation a la main, pour tomber sous le
    # libelle affiche par la boucle qui suit ce bloc.
    # -------------------------------------------------------------------------
    $visite = @(
        [pscustomobject]@{
            Commande = "assistantqr doctor"
            Voir     = "(~5 s) Profil « local », embeddings qwen3-embedding:0.6b/1024 sur :8088,`n" +
                       "     generation granite4.2:3b sur :11434, quatre sondes OK : 23 documents,`n" +
                       "     4 gabarits, 120 morceaux indexes, modele installe. Puis la phrase qui`n" +
                       "     engage : si un service tombe, la commande s'arrete — aucune doublure."
            Demontre = "Que rien n'est simule ici, et qu'une panne s'affichera au lieu d'etre absorbee."
        }
        [pscustomobject]@{
            Commande = "assistantqr ask ""Quels sont les horaires d'ouverture le samedi ?"" --prompt-version 1.2.0 --trace"
            Voir     = "(10 s a 2 min, selon que granite4.2:3b est deja charge en memoire) « Les`n" +
                       "     horaires d'ouverture le samedi sont de 10h00 a 18h00 [horaires-ouverture] »,`n" +
                       "     puis la trace : 4 candidats bruts, 3 retenus, planning-agents#2 (0.727,`n" +
                       "     internal) ecarte par l'acces, et l'empreinte de configuration complete."
            Demontre = "Que chaque phrase rendue se remonte jusqu'a un morceau de document nomme."
        }
        [pscustomobject]@{
            Commande = "assistantqr ask ""Quelle est la remuneration d'un agent d'accueil ?"" --user agent-accueil --clearance internal --trace"
            Voir     = "(~3 s, et zero appel a Ollama) Refus « NoEvidenceReadableByRequester » en`n" +
                       "     619 ms. Les quatre meilleurs morceaux sont grille-remuneration #1, #2, #4`n" +
                       "     et #5, scores 0.839 a 0.717, tous « confidential » : 4 candidats bruts,`n" +
                       "     0 retenu, 0 extrait soumis au modele. Le montant existe et ne sort pas."
            Demontre = "Que le modele ne peut pas divulguer ce qu'il n'a jamais recu."
        }
        [pscustomobject]@{
            Commande = "assistantqr demo access-filter ""Quels sont les horaires d'ouverture le samedi ?"" --prompt-version 1.2.0"
            Voir     = "(~2 min : deux generations) La question de l'etape 2, rejouee des deux`n" +
                       "     cotes du meme choix. Post-filtrage : planning-agents#2 (0.727, internal)`n" +
                       "     occupe une place du topK avant d'en etre retire, 3 extraits atteignent`n" +
                       "     le modele. Pre-filtrage : 4, dont horaires-ouverture#4 (0.634), inconnu`n" +
                       "     de l'autre mode."
            Demontre = "Que la couche ou vit une regle metier change ce que le modele recoit."
        }
        [pscustomobject]@{
            Commande = "assistantqr demo llm-swap --models granite4.2:3b,llama3.2:1b --prompt-version 1.2.0"
            Voir     = "(~20 min : 1228 s mesurees, dont 1158 s de pure generation — lance-la et`n" +
                       "     va faire autre chose) 12 questions, 12 recuperations « identique » sur`n" +
                       "     12, et 9 reponses sur 12 differentes : granite cite ses sources,`n" +
                       "     llama3.2:1b se fait refuser neuf fois sur « ModelProducedNoCitation ».`n" +
                       "     L'index n'est reconstruit a aucun moment."
            Demontre = "Que la recuperation ne bouge pas d'un iota — et que le produit s'effondre quand meme : 9 refus sur 12."
        }
        [pscustomobject]@{
            Commande = "assistantqr snapshot compare reel-qwen3 reel-bge-m3"
            Voir     = "(~2 s : les deux instantanes sont deja au disque) Une seule difference de`n" +
                       "     configuration, EmbeddingModel qwen3-embedding:0.6b -> bge-m3, et 5`n" +
                       "     reponses sur 12 modifiees : 41.7 % de derive, sans une exception et sans`n" +
                       "     un test qui change d'issue. Sur « horaires », les scores tombent de`n" +
                       "     0.740/0.656/0.643 a 0.692/0.631/0.616/0.593.`n" +
                       "     La refaire de bout en bout, ~25 min chronometrees, dans cet ordre :`n" +
                       "       1. assistantqr snapshot record reel-qwen3 --prompt-version 1.2.0`n" +
                       "            (96 s ; ecrase l'instantane livre)`n" +
                       "       2. Get-NetTCPConnection -LocalPort 8088 -State Listen |`n" +
                       "            ForEach-Object { Stop-Process -Id `$_.OwningProcess -Force }`n" +
                       "       3. .\scripts\demarrer.ps1 -Model bge-m3`n" +
                       "            (redemarre le service et reindexe : 82.9 s, dont 80.4 s d'embeddings)`n" +
                       "       4. assistantqr snapshot record reel-bge-m3 --embedding bge-m3`n" +
                       "            --prompt-version 1.2.0   (944 s ; sans --embedding, l'instantane`n" +
                       "            porterait l'etiquette qwen3 sur des vecteurs bge-m3)`n" +
                       "       5. cette commande-ci, de nouveau."
            Demontre = "Qu'une ligne de configuration deplace 41.7 % des reponses sans lever d'erreur."
        }
    )
    # VISITE-GUIDEE-FIN

    Write-Host ""
    Write-Host "  Six commandes, dans cet ordre. Pose d'abord l'alias affiche plus haut" -ForegroundColor Cyan
    Write-Host "  (Set-Alias assistantqr ...), sinon prefixe-les par le chemin complet." -ForegroundColor Cyan
    Write-Host ""

    $numero = 1
    foreach ($etape in $visite) {
        Write-Host ("  {0}. {1}" -f $numero, $etape.Commande) -ForegroundColor Cyan
        Write-Host "     ce que tu vas voir  : $($etape.Voir)"
        Write-Host "     ce que ca demontre  : $($etape.Demontre)"
        Write-Host ""
        $numero++
    }
}

# ------------------------------------------------------------------ execution

if ($Help) {
    Show-Aide
    if (-not $script:Source) { exit 0 }
    return
}

$chronoTotal = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host ""
Write-Host "AssistantQR — mise en route" -ForegroundColor Cyan
Write-Host "  depot          : $script:RepoRoot"
Write-Host "  modele vise    : $Model (embeddings)"
Write-Host "  options        : -SkipPull=$($SkipPull.IsPresent) · -Reindex=$($Reindex.IsPresent)"

try {
    Invoke-Etape1Prerequis
    Invoke-Etape2Ollama
    Invoke-Etape3Modeles
    Invoke-Etape4Service
    Set-EnvironnementLocal
    Invoke-Etape5Construction
    Invoke-Etape6Index
    Show-Environnement
    Show-VisiteGuidee

    $chronoTotal.Stop()
    Write-Host "Pret en $([math]::Round($chronoTotal.Elapsed.TotalSeconds, 1)) s. Relance ce script autant de fois que tu veux : il ne refera que ce qui manque." -ForegroundColor Green
    Write-Host ""
}
catch {
    $chronoTotal.Stop()
    if ($script:CodeSortie -eq 0) { $script:CodeSortie = 1 }

    Write-Host ""
    Write-Ko $_.Exception.Message
    foreach ($action in $script:ActionsErreur) {
        Write-Host $action -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "  Arret a l'etape en cours, code de retour $script:CodeSortie. Aucun repli automatique :" -ForegroundColor Yellow
    Write-Host "  si tu veux travailler sans services exterieurs, c'est un choix a faire a la main," -ForegroundColor Yellow
    Write-Host "  avec « --profile offline » sur la commande de ton choix." -ForegroundColor Yellow
    Write-Host ""
}

if (-not $script:Source) { exit $script:CodeSortie }
