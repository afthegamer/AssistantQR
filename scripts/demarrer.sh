#!/usr/bin/env bash
# La porte d'entree unique du depot — miroir de scripts/demarrer.ps1 pour Git
# Bash et WSL. Meme sept etapes, meme ordre, memes garanties.
#
# CE SCRIPT N'EST PAS UN INSTALLEUR ET N'EST PAS UN MODE DEGRADE. Il ne
# telecharge que les modeles Ollama manquants, il n'en retire jamais un, et si
# une etape echoue il s'arrete avec un message qui dit quoi faire. Il ne bascule
# JAMAIS de lui-meme sur le profil « offline » : il peut le suggerer, c'est tout.
# Un repli silencieux est exactement la panne que ce depot enseigne a reperer.
#
# IDEMPOTENCE. Chaque etape constate avant d'agir. Ollama repond ? On ne le
# relance pas. Les cinq modeles sont la ? On ne tire rien. Le binaire est plus
# recent que les sources ? On ne reconstruit pas. L'index contient des morceaux
# construits avec le modele qui tourne ? On ne reindexe pas — c'est le point qui
# compte, parce que reindexer coute environ deux minutes et demie.
#
# Usage :
#   ./scripts/demarrer.sh
#   ./scripts/demarrer.sh --model bge-m3
#   ./scripts/demarrer.sh --reindex
#   ./scripts/demarrer.sh --skip-pull
#   ./scripts/demarrer.sh --help
#
# Pour que les variables d'environnement restent dans TON shell, source-le :
#   source ./scripts/demarrer.sh

# Volontairement sans « set -e » : chaque etape juge elle-meme son code de
# retour et redige son message. Un « set -e » ferait sortir le script au milieu
# d'un test, sans rien expliquer — et, quand le script est source, il tuerait le
# terminal de l'utilisateur.

# --------------------------------------------------------------- constantes

URL_OLLAMA="http://localhost:11434"
URL_SERVICE="http://127.0.0.1:8088"

CODE_PREREQUIS=1
CODE_OLLAMA=2
CODE_MODELES=3
CODE_SERVICE=4
CODE_BUILD=5
CODE_INDEX=6

# Les cinq modeles dont la visite guidee a besoin — ni plus, ni moins —, au
# format « nom|taille|role ». Les tailles servent a prevenir AVANT de lancer un
# telechargement de plusieurs gigaoctets. La commande 5 de la visite en compare
# trois d'un coup : elle exige donc llama3.2:3b, qui n'etait pas requis tant que
# le test opposait seulement granite4.2:3b a llama3.2:1b.
MODELES_REQUIS=(
  "granite4.2:3b|~2.2 Go|generation, 3B, reference de la visite"
  "llama3.2:3b|~2.0 Go|generation, 3B, l'appariement de granite au test 5"
  "llama3.2:1b|~1.3 Go|generation, 1B, le desequilibre du test 5"
  "qwen3-embedding:0.6b|~640 Mo|embeddings, dimension 1024"
  "bge-m3|~1.2 Go|embeddings, dimension 1024"
)

# Modeles que ce script ne telecharge PAS, mais dont il signale la presence, au
# format « nom|raison ». qwen3:4b tourne sur cette machine ; il ne tient
# simplement pas le prompt RAG du depot, et un modele qu'on garde par erreur
# coute plus cher qu'un modele absent. Ce script ne retire jamais rien : il
# constate et il chiffre.
MODELES_ECARTES=(
  "qwen3:4b|1029.8 s (17 min 10) pour UNE question du corpus, budget de 2000 jetons epuise sans jamais fermer « </think> » : la reponse est perdue, la sortie est du raisonnement."
)

# ---------------------------------------------------------------- affichage

etape() { printf '\n=== Etape %s — %s\n' "$1" "$2"; }
ok()    { printf '  [ok]   %s\n' "$1"; }
info()  { printf '  [..]   %s\n' "$1"; }
agir()  { printf '  [->]   %s\n' "$1"; }
ko()    { printf '  [KO]   %s\n' "$1" >&2; }

erreur_finale() {
  # $1 : message. Le reste : lignes d'action, affichees telles quelles pour
  # etre copiees.
  local message="$1"; shift
  printf '\n' >&2
  ko "$message"
  local ligne
  for ligne in "$@"; do printf '%s\n' "$ligne" >&2; done
  printf '\n' >&2
  printf "  Arret a l'etape en cours. Aucun repli automatique : si tu veux travailler\n" >&2
  printf "  sans services exterieurs, c'est un choix a faire a la main, avec\n" >&2
  printf '  « --profile offline » sur la commande de ton choix.\n\n' >&2
}

# ----------------------------------------------------------------- sondages

json_distant() {
  # Ecrit le corps sur stdout et rend 0, ou rend 1 si le service ne repond pas.
  # Une absence de reponse est une information attendue ici, pas un incident.
  curl --silent --show-error --fail --max-time "${2:-5}" "$1" 2>/dev/null
}

champ_json() {
  # Extraction d'un champ scalaire sans dependance a jq : le service rend un
  # objet plat, une expression reguliere suffit et evite d'imposer un outil de
  # plus a l'etudiant.
  local corps="$1" cle="$2" valeur
  valeur="$(printf '%s' "$corps" | sed -n "s/.*\"$cle\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p")"
  if [ -z "$valeur" ]; then
    valeur="$(printf '%s' "$corps" | sed -n "s/.*\"$cle\"[[:space:]]*:[[:space:]]*\([^,}]*\).*/\1/p" | tr -d ' ')"
  fi
  printf '%s' "$valeur"
}

resoudre_cli() {
  # Le binaire s'appelle « assistantqr.exe » sous Windows et « assistantqr »
  # ailleurs. On resout au moment ou on en a besoin, pas une fois pour toutes :
  # au premier lancement, il n'existe encore ni l'un ni l'autre.
  local base="$REPO_ROOT/src/AssistantQR.Cli/bin/Debug/net8.0/assistantqr"
  if [ -f "$base.exe" ]; then CHEMIN_CLI="$base.exe"; else CHEMIN_CLI="$base"; fi
}

nom_modele_normalise() {
  # « bge-m3 » et « bge-m3:latest » designent le meme modele : « ollama list »
  # affiche le second, le sujet nomme le premier.
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed 's/:latest$//'
}

attendre_service() {
  # $1 url, $2 delai max en secondes, $3 libelle.
  local url="$1" delai="$2" quoi="$3" debut ecoule dernier=0
  debut=$(date +%s)
  while :; do
    if json_distant "$url" 3 >/dev/null 2>&1; then
      ecoule=$(( $(date +%s) - debut ))
      ok "$quoi repond apres ${ecoule} s."
      return 0
    fi
    sleep 2
    ecoule=$(( $(date +%s) - debut ))
    if [ "$ecoule" -ge "$delai" ]; then return 1; fi
    if [ $(( ecoule - dernier )) -ge 10 ]; then
      dernier=$ecoule
      info "toujours en attente de $quoi (${ecoule} s / ${delai} s)"
    fi
  done
}

# --------------------------------------------------------------------- aide

aide() {
  cat <<'AIDE'

demarrer.sh — porte d'entree du depot AssistantQR

  Amene la machine d'un depot clone a une session utilisable en profil « local » :
  prerequis, serveur Ollama, modeles, service Python d'embeddings, construction,
  index. Chaque etape constate avant d'agir : relancer le script ne refait rien.

Usage :
  ./scripts/demarrer.sh [--model <nom>] [--skip-pull] [--reindex] [--help]

Options :
  --model <nom>   Modele d'embeddings du service Python et de l'index.
                  Defaut : qwen3-embedding:0.6b. Autre valeur prevue : bge-m3.
  --skip-pull     Ne telecharge aucun modele manquant. Le script constate
                  l'absence et s'arrete plutot que de tirer des gigaoctets.
  --reindex       Force la reindexation du corpus meme si l'index est coherent.
  --help          Cette page.

Variables d'environnement :
  Le script positionne les variables du profil local dans son propre processus,
  et les affiche a la fin. Pour qu'elles restent dans TON shell, source-le :
      source ./scripts/demarrer.sh

Codes de retour :
  0  Tout est en place.
  1  Un prerequis manque (dotnet, python, ollama).
  2  Le serveur Ollama ne repond pas.
  3  Un modele Ollama manque et n'a pas pu etre installe.
  4  Le service Python d'embeddings ne repond pas.
  5  La construction .NET a echoue.
  6  L'indexation a echoue.

Ce script ne bascule jamais tout seul sur le profil « offline ». Si tu veux
travailler sans Ollama ni service Python, c'est une decision a prendre a la main :
      assistantqr ask "..." --profile offline

AIDE
}

# ------------------------------------------------------------------- etapes

etape1_prerequis() {
  etape "1/7" "Prerequis"
  local manquants=()

  # --- dotnet
  if ! command -v dotnet >/dev/null 2>&1; then
    ko "dotnet : introuvable dans le PATH."
    manquants+=("  SDK .NET 8 : https://dotnet.microsoft.com/download/dotnet/8.0 (Windows : winget install --id Microsoft.DotNet.SDK.8)")
  else
    local sdks majeur_max=0 majeur
    sdks="$(dotnet --list-sdks 2>/dev/null)"
    while read -r ligne; do
      [ -z "$ligne" ] && continue
      majeur="${ligne%%.*}"
      case "$majeur" in
        ''|*[!0-9]*) continue ;;
      esac
      [ "$majeur" -gt "$majeur_max" ] && majeur_max="$majeur"
    done <<< "$sdks"

    if [ "$majeur_max" -eq 0 ]; then
      ko "dotnet : present, mais aucun SDK installe (seulement le runtime ?)."
      manquants+=("  SDK .NET 8 : https://dotnet.microsoft.com/download/dotnet/8.0")
    elif [ "$majeur_max" -lt 8 ]; then
      ko "dotnet : SDK ${majeur_max}.x seulement, il en faut au moins 8."
      manquants+=("  SDK .NET 8 : https://dotnet.microsoft.com/download/dotnet/8.0")
    else
      ok "dotnet : SDK $(printf '%s' "$sdks" | awk 'NF {printf "%s%s", sep, $1; sep=", "}')"
    fi
  fi

  # --- python
  # Sous Windows, « python3 » peut exister sans etre un interpreteur : c'est le
  # raccourci du Microsoft Store. On verifie donc qu'il s'execute vraiment,
  # comme le fait deja start-embeddings.sh.
  local candidat prefere="${PYTHON_BIN:-}"
  PYTHON_BIN=""
  for candidat in "$prefere" python3 python py; do
    [ -z "$candidat" ] && continue
    if command -v "$candidat" >/dev/null 2>&1 && "$candidat" -c "import sys" >/dev/null 2>&1; then
      PYTHON_BIN="$candidat"
      break
    fi
  done

  if [ -z "$PYTHON_BIN" ]; then
    ko "python : introuvable (ni python3, ni python, ni py)."
    manquants+=("  Python 3.11+ : https://www.python.org/downloads/ (Windows : winget install --id Python.Python.3.12)")
  else
    local version majeur mineur
    version="$("$PYTHON_BIN" --version 2>&1)"
    majeur="$("$PYTHON_BIN" -c 'import sys; print(sys.version_info[0])' 2>/dev/null)"
    mineur="$("$PYTHON_BIN" -c 'import sys; print(sys.version_info[1])' 2>/dev/null)"
    if [ -z "$majeur" ] || [ "$majeur" -lt 3 ] || { [ "$majeur" -eq 3 ] && [ "$mineur" -lt 11 ]; }; then
      ko "python : $version, il en faut au moins 3.11."
      manquants+=("  Python 3.11+ : https://www.python.org/downloads/")
    else
      ok "python : $version ($(command -v "$PYTHON_BIN"))"
    fi
  fi

  # --- ollama
  # Ollama s'installe sous Windows sans forcement rafraichir le PATH de la
  # session en cours. LOCALAPPDATA arrive avec des antislashs, que bash ne sait
  # pas suivre : on les retourne avant de tester le chemin.
  OLLAMA_BIN=""
  local local_appdata
  local_appdata="$(printf '%s' "${LOCALAPPDATA:-}" | tr '\\' '/')"
  if command -v ollama >/dev/null 2>&1; then
    OLLAMA_BIN="$(command -v ollama)"
  elif [ -n "$local_appdata" ] && [ -x "$local_appdata/Programs/Ollama/ollama.exe" ]; then
    OLLAMA_BIN="$local_appdata/Programs/Ollama/ollama.exe"
  elif [ -x "/c/Users/${USERNAME:-$USER}/AppData/Local/Programs/Ollama/ollama.exe" ]; then
    OLLAMA_BIN="/c/Users/${USERNAME:-$USER}/AppData/Local/Programs/Ollama/ollama.exe"
  fi

  if [ -z "$OLLAMA_BIN" ]; then
    ko "ollama : introuvable (ni dans le PATH, ni dans LOCALAPPDATA/Programs/Ollama)."
    manquants+=("  Ollama : winget install --id'Ollama.Ollama  (ou https://ollama.com/download)")
  else
    ok "ollama : $("$OLLAMA_BIN" --version 2>&1 | tail -n 1) ($OLLAMA_BIN)"
  fi

  if [ "${#manquants[@]}" -gt 0 ]; then
    erreur_finale "Il manque ${#manquants[@]} prerequis. Installe-les puis relance ce script." \
      "${manquants[@]}" "" "  Puis rouvre un terminal pour que le PATH soit rafraichi."
    return $CODE_PREREQUIS
  fi
  return 0
}

etape2_ollama() {
  etape "2/7" "Serveur Ollama"

  if json_distant "$URL_OLLAMA/api/tags" 3 >/dev/null 2>&1; then
    ok "deja en ecoute sur $URL_OLLAMA — rien a demarrer."
    return 0
  fi

  agir "aucune reponse sur $URL_OLLAMA : demarrage de « ollama serve »."
  mkdir -p "$DOSSIER_JOURNAUX"
  ( nohup "$OLLAMA_BIN" serve >"$DOSSIER_JOURNAUX/ollama.log" 2>&1 & ) >/dev/null 2>&1

  if ! attendre_service "$URL_OLLAMA/api/tags" 60 "Ollama"; then
    erreur_finale "Ollama n'a pas repondu sur $URL_OLLAMA au bout de 60 s." \
      "  Lis le journal :" \
      "      tail -n 40 '$DOSSIER_JOURNAUX/ollama.log'" \
      "  Ou lance-le dans un terminal a part pour voir ce qu'il dit :" \
      "      '$OLLAMA_BIN' serve"
    return $CODE_OLLAMA
  fi
  return 0
}

etape3_modeles() {
  etape "3/7" "Modeles Ollama"

  local sortie
  if ! sortie="$("$OLLAMA_BIN" list 2>&1)"; then
    erreur_finale "« ollama list » a echoue : $sortie" \
      "  Verifie que le serveur tourne : '$OLLAMA_BIN' serve"
    return $CODE_MODELES
  fi

  local installes
  installes="$(printf '%s\n' "$sortie" | tail -n +2 | awk 'NF {print $1}' | tr '[:upper:]' '[:lower:]' | sed 's/:latest$//')"

  local a_tirer=() entree nom taille role
  for entree in "${MODELES_REQUIS[@]}"; do
    nom="${entree%%|*}"
    taille="$(printf '%s' "$entree" | cut -d'|' -f2)"
    role="$(printf '%s' "$entree" | cut -d'|' -f3)"
    if printf '%s\n' "$installes" | grep -Fxq "$(nom_modele_normalise "$nom")"; then
      ok "$nom — present ($role)"
    else
      agir "$nom — ABSENT, $taille a telecharger ($role)"
      a_tirer+=("$entree")
    fi
  done

  local ecarte
  for ecarte in "${MODELES_ECARTES[@]}"; do
    if printf '%s\n' "$installes" | grep -Fxq "$(nom_modele_normalise "${ecarte%%|*}")"; then
      info "${ecarte%%|*} — installe mais ECARTE de la visite, et conserve : ${ecarte#*|}"
    fi
  done

  if [ "${#a_tirer[@]}" -eq 0 ]; then
    ok "les ${#MODELES_REQUIS[@]} modeles sont la — rien a telecharger."
    return 0
  fi

  if [ "$SKIP_PULL" = "1" ]; then
    local lignes=("  Relance sans --skip-pull, ou tire-les a la main :")
    for entree in "${a_tirer[@]}"; do lignes+=("      '$OLLAMA_BIN' pull ${entree%%|*}"); done
    erreur_finale "${#a_tirer[@]} modele(s) manquent et --skip-pull interdit de les telecharger." "${lignes[@]}"
    return $CODE_MODELES
  fi

  local total=""
  for entree in "${a_tirer[@]}"; do
    total="${total:+$total + }$(printf '%s' "$entree" | cut -d'|' -f2)"
  done
  info "telechargement de ${#a_tirer[@]} modele(s) : $total. Ce script ne retire jamais un modele."

  for entree in "${a_tirer[@]}"; do
    nom="${entree%%|*}"
    taille="$(printf '%s' "$entree" | cut -d'|' -f2)"
    agir "ollama pull $nom ($taille)"
    if ! "$OLLAMA_BIN" pull "$nom"; then
      erreur_finale "Le telechargement de $nom a echoue." \
        "  Reessaie a la main pour lire l'erreur complete :" \
        "      '$OLLAMA_BIN' pull $nom"
      return $CODE_MODELES
    fi
    ok "$nom installe."
  done
  return 0
}

etape4_service() {
  etape "4/7" "Service Python d'embeddings"

  local sante qui voulu
  if sante="$(json_distant "$URL_SERVICE/health" 5)"; then
    qui="$(nom_modele_normalise "$(champ_json "$sante" embedding_model)")"
    voulu="$(nom_modele_normalise "$MODEL")"

    if [ "$qui" != "$voulu" ]; then
      # On ne tue pas un processus qu'on n'a pas demarre, et on ne fait pas
      # semblant que le modele demande est celui qui tourne. On s'arrete.
      erreur_finale "Le service tourne deja avec « $(champ_json "$sante" embedding_model) », or tu demandes « $MODEL »." \
        "  Soit tu gardes celui qui tourne :" \
        "      ./scripts/demarrer.sh --model $(champ_json "$sante" embedding_model)" \
        "  Soit tu arretes le service en place, puis tu relances :" \
        "      # Windows : Get-NetTCPConnection -LocalPort 8088 -State Listen | ForEach-Object { Stop-Process -Id \$_.OwningProcess -Force }" \
        "      # Linux   : fuser -k 8088/tcp" \
        "      ./scripts/demarrer.sh --model $MODEL"
      return $CODE_SERVICE
    fi

    ok "deja en ecoute sur $URL_SERVICE — rien a demarrer."
    info "modele : $(champ_json "$sante" embedding_model) · dimension $(champ_json "$sante" dimension) · ollama joignable : $(champ_json "$sante" ollama_reachable)"
    SANTE="$sante"
    return 0
  fi

  agir "aucune reponse sur $URL_SERVICE : demarrage via scripts/start-embeddings.sh (modele $MODEL)."
  info "au tout premier lancement, cette etape cree le venv et installe les dependances : compte quelques minutes."

  mkdir -p "$DOSSIER_JOURNAUX"
  local journal="$DOSSIER_JOURNAUX/embeddings.log"
  # Le journal va hors du depot : ce script n'ecrit rien dans le depot en dehors
  # de bin/obj et de l'index du service.
  info "journal : $journal"

  ( cd "$REPO_ROOT" && EMBEDDING_MODEL="$MODEL" nohup "$SCRIPT_DIR/start-embeddings.sh" >"$journal" 2>&1 & ) >/dev/null 2>&1

  if ! attendre_service "$URL_SERVICE/health" 300 "le service d'embeddings"; then
    erreur_finale "Le service d'embeddings n'a pas repondu sur $URL_SERVICE au bout de 300 s." \
      "  Lis le journal, la cause y est :" \
      "      tail -n 40 '$journal'" \
      "  Ou relance-le au premier plan pour voir la trace complete :" \
      "      EMBEDDING_MODEL=$MODEL ./scripts/start-embeddings.sh"
    return $CODE_SERVICE
  fi

  if ! sante="$(json_distant "$URL_SERVICE/health" 10)"; then
    erreur_finale "Le service a repondu puis s'est tu." "  tail -n 40 '$journal'"
    return $CODE_SERVICE
  fi

  info "modele : $(champ_json "$sante" embedding_model) · dimension $(champ_json "$sante" dimension) · ollama joignable : $(champ_json "$sante" ollama_reachable)"
  if [ "$(champ_json "$sante" ollama_reachable)" != "true" ]; then
    erreur_finale "Le service tourne mais ne joint pas Ollama : aucun embedding ne sera calculable." \
      "  Verifie Ollama : '$OLLAMA_BIN' serve"
    return $CODE_SERVICE
  fi

  SANTE="$sante"
  return 0
}

etape5_construction() {
  etape "5/7" "Construction .NET"

  resoudre_cli

  local besoin=0 raison="" plus_recente="" dossier_sortie reference
  if [ ! -f "$CHEMIN_CLI" ]; then
    besoin=1
    raison="le binaire de la CLI est absent"
  else
    # LA REFERENCE N'EST PAS assistantqr.exe MAIS LE PLUS RECENT DES ARTEFACTS DE
    # SORTIE. Le .exe n'est qu'un lanceur : MSBuild ne le recopie pas forcement
    # quand seul du code change, et sa date resterait alors eternellement
    # anterieure a celle des sources — le script reconstruirait a chaque appel.
    dossier_sortie="$(dirname "$CHEMIN_CLI")"
    reference="$dossier_sortie/$(ls -tp "$dossier_sortie" 2>/dev/null | grep -v '/$' | head -n 1)"
    [ -f "$reference" ] || reference="$CHEMIN_CLI"

    # On ne cherche pas LA source la plus recente : la premiere plus recente que
    # la sortie suffit a decider, et « -print -quit » s'arrete des qu'il en
    # trouve une.
    plus_recente="$(find "$REPO_ROOT/src" -type f \
        \( -name '*.cs' -o -name '*.csproj' -o -name '*.json' \) \
        -not -path '*/bin/*' -not -path '*/obj/*' \
        -newer "$reference" -print -quit 2>/dev/null)"
    if [ -z "$plus_recente" ]; then
      plus_recente="$(find "$REPO_ROOT" -maxdepth 1 -type f \
          \( -name 'Directory.Build.props' -o -name 'global.json' \) \
          -newer "$reference" -print -quit 2>/dev/null)"
    fi
    if [ -n "$plus_recente" ]; then
      besoin=1
      raison="sources modifiees depuis la derniere construction ($(basename "$plus_recente"))"
    fi
  fi

  if [ "$besoin" -eq 0 ]; then
    ok "binaire a jour ($(basename "$CHEMIN_CLI")) — rien a construire."
    return 0
  fi

  agir "$raison : construction."
  local debut fin
  debut=$(date +%s)
  if ! dotnet build "$REPO_ROOT/src/AssistantQR.Cli" -v q --nologo; then
    erreur_finale "« dotnet build » a echoue." \
      "  Relance-le sans filtre pour lire les erreurs :" \
      "      dotnet build '$REPO_ROOT/src/AssistantQR.Cli'" \
      "  Si l'erreur parle d'un fichier verrouille (MSB3027), c'est qu'un assistantqr" \
      "  tourne encore. Attends qu'il finisse, ou regarde qui c'est :" \
      "      # Windows : Get-Process assistantqr" \
      "      # Linux   : pgrep -a assistantqr"
    return $CODE_BUILD
  fi
  fin=$(date +%s)
  resoudre_cli
  ok "construction reussie en $(( fin - debut )) s."
  return 0
}

etape6_index() {
  etape "6/7" "Index vectoriel"

  local meta
  if ! meta="$(json_distant "$URL_SERVICE/index/metadata" 10)"; then
    erreur_finale "Le service ne rend pas /index/metadata alors qu'il repondait a /health." \
      "  curl $URL_SERVICE/index/metadata"
    return $CODE_INDEX
  fi

  local modele_service modele_index morceaux raison=""
  modele_service="$(nom_modele_normalise "$(champ_json "$SANTE" embedding_model)")"
  modele_index="$(nom_modele_normalise "$(champ_json "$meta" embedding_model)")"
  morceaux="$(champ_json "$meta" chunk_count)"
  case "$morceaux" in
    ''|*[!0-9]*) morceaux=0 ;;
  esac

  if [ "$REINDEX" = "1" ]; then
    raison="--reindex demande explicitement."
  elif [ "$morceaux" -eq 0 ]; then
    raison="l'index est vide."
  elif [ "$modele_index" != "$modele_service" ]; then
    # C est la panne silencieuse du cours : meme dimension, produit scalaire
    # calculable, resultats plausibles et faux. On refuse de la laisser en place.
    raison="l'index a ete construit avec « $(champ_json "$meta" embedding_model) » alors que le service tourne avec « $(champ_json "$SANTE" embedding_model) »."
  fi

  if [ -z "$raison" ]; then
    ok "index deja construit avec « $(champ_json "$meta" embedding_model) » : $morceaux morceaux, decoupage « $(champ_json "$meta" chunking_strategy_id) »."
    info "construit le $(champ_json "$meta" built_at) — rien a reindexer (c'est la ~2 min 30 qu'on ne repaie pas)."
    return 0
  fi

  agir "reindexation : $raison"
  local debut fin
  debut=$(date +%s)
  if ! ( cd "$REPO_ROOT" && "$CHEMIN_CLI" index ); then
    fin=$(date +%s)
    erreur_finale "« assistantqr index » a echoue apres $(( fin - debut )) s." \
      "  Diagnostic complet :" \
      "      '$CHEMIN_CLI' doctor"
    return $CODE_INDEX
  fi
  fin=$(date +%s)

  local apres
  apres="$(json_distant "$URL_SERVICE/index/metadata" 10)"
  ok "reindexation terminee en $(( fin - debut )) s — $(champ_json "$apres" chunk_count) morceaux avec « $(champ_json "$apres" embedding_model) »."
  return 0
}

environnement_local() {
  # Positionne les variables du profil local dans CE processus. Si le script est
  # source, elles restent dans le shell de l'utilisateur ; sinon elles servent au
  # moins aux commandes que ce script lance lui-meme, et sont affichees pour
  # etre copiees.
  export ASSISTANTQR_PROFILE="local"
  export ASSISTANTQR_EMBEDDINGS__MODEL="$(champ_json "$SANTE" embedding_model)"
  export ASSISTANTQR_EMBEDDINGS__DIMENSION="$(champ_json "$SANTE" dimension)"
  export ASSISTANTQR_LANGUAGEMODEL__MODEL="granite4.2:3b"
}

montrer_environnement() {
  printf '\nVariables du profil local\n'
  if [ "$SOURCE" = "1" ]; then
    printf '  (script source : elles sont deja actives dans ce shell)\n'
  else
    printf "  (script execute : copie ces quatre lignes dans ton shell, ou source le script\n"
    printf "   avec « source ./scripts/demarrer.sh » pour qu'il les pose lui-meme)\n"
  fi
  printf '\n'
  printf '    export ASSISTANTQR_PROFILE=local\n'
  printf '    export ASSISTANTQR_EMBEDDINGS__MODEL="%s"\n' "$(champ_json "$SANTE" embedding_model)"
  printf '    export ASSISTANTQR_EMBEDDINGS__DIMENSION=%s\n' "$(champ_json "$SANTE" dimension)"
  printf '    export ASSISTANTQR_LANGUAGEMODEL__MODEL="granite4.2:3b"\n'
  printf '\n'
  printf "  Et l'alias qui rend copiables les commandes de la visite guidee :\n\n"
  printf '    alias assistantqr='"'"'%s'"'"'\n' "$CHEMIN_CLI"
}

montrer_visite_guidee() {
  etape "7/7" "Visite guidee"

  # VISITE-GUIDEE-DEBUT
  # ---------------------------------------------------------------------------
  # LES SIX COMMANDES ONT ETE JOUEES SUR CETTE MACHINE AVANT D'ETRE ECRITES ICI.
  # Les chiffres cites dans « ce que tu vas voir » sont des sorties relevees, pas
  # des ordres de grandeur inventes. L'ordre raconte quelque chose : deux fois le
  # systeme rassure (un diagnostic vert, une reponse citee), deux fois il refuse
  # pour proteger (un demandeur qui n'a pas l'habilitation, une etiquette
  # d'embedding fausse), deux fois il derange (7 reponses qui basculent en refus
  # quand le modele n'honore pas la consigne de citation, 41.7 % des reponses qui
  # bougent sans qu'aucune erreur ne soit levee).
  #
  # « --prompt-version 1.2.0 » N'EST PAS DECORATIF. Le gabarit par defaut du
  # depot est le 1.0.0 ; avec granite4.2:3b, il fait recopier au modele
  # l'identifiant fictif de son propre exemple, et la reponse est rejetee sur
  # « ModelCitedUnknownDocument ». Mesure : 1.0.0 -> refus, 1.2.0 -> reponse
  # citee, meme question, meme index, meme minute.
  #
  # CE QUI A CHANGE, ET POURQUOI LE TEXTE DE LA COMMANDE 5 A ETE CORRIGE. La
  # version precedente opposait granite4.2:3b a llama3.2:1b — 3B contre 1B — et
  # concluait de 9 refus sur 12 que le produit s'effondrait. Le test a ete refait
  # a trois modeles, llama3.2:3b intercale entre les deux : entre les deux 3B,
  # 0/12 chaines de citations differentes. La mesure d'avant portait sur un ecart
  # de taille de modele ; l'attribuer a la substitution de modele en general
  # etait une lecture trop large. La commande 5 dit desormais ce que chacune des
  # deux paires dit, separement.
  #
  # qwen3:4b N'APPARAIT PAS ICI, expres. Il reste installe et l'etape 3 le
  # signale : 1029.8 s (17 min 10) pour UNE question, 2000 jetons consommes sans
  # jamais fermer « </think> », reponse perdue. Ce n'est pas une question de
  # taille — 4B est plus gros que les trois modeles de la commande 5 : ce
  # modele-la depense son budget en raisonnement et n'atteint pas la reponse.
  #
  # Format d'une entree : « commande|ce que tu vas voir|ce que ca demontre ».
  # Une entree peut tenir sur PLUSIEURS lignes physiques : la premiere porte les
  # trois champs, les suivantes ont la forme « |suite du deuxieme champ| », les
  # premier et troisieme champs y restant vides. « cut » lit chaque ligne, la
  # substitution de commande retire les lignes vides de fin, et le deuxieme
  # champ ressort donc multiligne. Les cinq espaces d'indentation des lignes de
  # continuation sont poses a la main, pour tomber sous le libelle.
  # ---------------------------------------------------------------------------
  local VISITE=(
    "assistantqr doctor|(~5 s : 4.8 s mesurees) Profil « local », embeddings qwen3-embedding:0.6b/1024|Que rien n'est simule ici, et qu'une panne s'affichera au lieu d'etre absorbee.
|     sur :8088, generation granite4.2:3b sur :11434, quatre sondes OK : 23|
|     documents, 4 gabarits, 120 morceaux indexes, modele installe (6 modeles|
|     au total sur cette machine). Puis la phrase qui engage : si un service|
|     tombe, la commande s'arrete — aucune doublure.|"

    "assistantqr ask \"Quels sont les horaires d'ouverture le samedi ?\" --prompt-version 1.2.0 --trace|(10 s a 2 min selon que granite4.2:3b est deja charge en memoire ; 92.4 s|Que chaque phrase rendue se remonte a un morceau nomme, et que ce morceau ne l'emporte ici que de 0.0133 de cosinus.
|     mesurees a froid) « Les horaires d'ouverture le samedi sont de 10h00 a|
|     18h00 [horaires-ouverture] », puis la trace : 4 candidats bruts, 3|
|     retenus, planning-agents#2 (0.727, internal) ecarte par l'acces. Le|
|     premier morceau, horaires-ouverture#1, sort a 0.740 : 0.0133 devant le|
|     meilleur morceau d'un AUTRE document — la 2e plus petite marge des 12|
|     questions du jeu, dont la mediane est 0.1197.|"

    "assistantqr ask \"Quelle est la remuneration d'un agent d'accueil ?\" --user agent-accueil --clearance internal --trace|(~5 s, dont 3.20 s de recuperation, et zero appel a Ollama) Refus|Que le modele ne peut pas divulguer ce qu'il n'a jamais recu.
|     « NoEvidenceReadableByRequester ». Les quatre morceaux rendus sont|
|     grille-remuneration #1, #2, #4 et #5, scores 0.839 / 0.750 / 0.733 /|
|     0.717, tous « confidential » : 4 candidats bruts, 0 retenu, 0 extrait|
|     soumis au modele. Le montant est dans l'index et ne sort pas.|
|     Variante, pour voir la meme regle appliquee une couche plus tot|
|     (~2 min, deux generations) :|
|       assistantqr demo access-filter \"Quels sont les horaires d'ouverture le samedi ?\" --prompt-version 1.2.0|
|       -> post-filtrage : 3 extraits atteignent le modele, planning-agents#2|
|          ayant consomme une place du topK avant d'en etre retire ;|
|          pre-filtrage : 4, dont horaires-ouverture#4 (0.634).|"

    "assistantqr ask \"Quels sont les horaires d'ouverture le samedi ?\" --embedding bge-m3 --prompt-version 1.2.0|(~3 s, code de retour 2, et pas un seul vecteur calcule) Le service tourne|Qu'une etiquette d'embedding fausse est arretee a la seconde ou elle est prononcee, au lieu d'etre recopiee dans les metadonnees de l'index et dans l'empreinte des instantanes.
|     sous qwen3-embedding:0.6b — l'etat que ce script vient de poser — et tu|
|     declares bge-m3. Avant le premier /embed, l'adaptateur lit GET /health,|
|     confronte « nom declare : bge-m3 » a « nom reellement servi :|
|     qwen3-embedding:0.6b » et s'arrete. Les dimensions, elles, concordent|
|     (1024 contre 1024) : c'est le nom qui tranche. Le message donne les deux|
|     seules issues, aligner la declaration ou redemarrer le service.|"

    "assistantqr demo llm-swap --models granite4.2:3b,llama3.2:3b,llama3.2:1b --prompt-version 1.2.0|(~31 min : 1854 s mesurees — granite4.2:3b 854.90 s soit 71.24 s par appel,|Qu'entre deux modeles apparies les chaines de citations tiennent — 0/12 differentes — et que ce sont les 9 refus de citation du 1B, dont 7 sur des questions ou granite4.2:3b citait, qui font basculer les reponses en refus, la recuperation restant identique dans les deux comparaisons.
|     llama3.2:3b 636.25 s soit 53.02 s, llama3.2:1b 358.46 s soit 29.87 s.|
|     Lance-la et va faire autre chose.) 12 questions, un seul index, jamais|
|     reconstruit : 12/12 recuperations identiques pour les trois modeles,|
|     memes morceaux, memes scores, meme ordre. Puis deux paires.|
|     granite4.2:3b contre llama3.2:3b — 3B contre 3B : 5/12 reponses|
|     differentes, 0/12 chaines de citations differentes, 7/12 chaines|
|     identiques NON VIDES, 0 passage reponse -> refus.|
|     granite4.2:3b contre llama3.2:1b — 3B contre 1B : 9/12 reponses|
|     differentes, 7/12 chaines de citations differentes, 0/12 chaine|
|     identique non vide, 7 passages reponse -> refus, tous motives par|
|     « ModelProducedNoCitation ».|"

    "assistantqr snapshot compare reel-qwen3 reel-bge-m3|(moins d'une seconde : 0.31 s mesurees, les deux instantanes sont au disque)|Qu'une ligne de configuration deplace 41.7 % des reponses sans changer une seule citation ni lever une erreur.
|     Une seule difference de configuration, EmbeddingModel|
|     qwen3-embedding:0.6b -> bge-m3, et 5 reponses sur 12 modifiees : 41.7 %|
|     de derive, 5 « AnswerTextChanged » et 7 « Identical » — donc 0 citation|
|     changee, 0 exception, 0 test qui change d'issue. Sur « horaires », les|
|     scores passent de 0.740/0.656/0.643 a 0.692/0.631/0.616/0.593.|
|     Pourquoi si peu suffit : sur les 12 questions, la marge entre le|
|     meilleur morceau et le meilleur morceau d'un AUTRE document vaut 0.1197|
|     en mediane, et 5 questions sur 12 sont sous 0.10 — dont #1 a 0.0133 et|
|     #11, « velos », a 0.0017.|
|     La refaire de bout en bout, ~25 min chronometrees, dans cet ordre :|
|       1. assistantqr snapshot record reel-qwen3 --prompt-version 1.2.0|
|            (96 s ; ecrase l'instantane livre)|
|       2. fuser -k 8088/tcp   (arrete le service d'embeddings en place)|
|       3. ./scripts/demarrer.sh --model bge-m3|
|            (redemarre le service et reindexe : 82.9 s, dont 80.4 s d'embeddings)|
|       4. assistantqr snapshot record reel-bge-m3 --embedding bge-m3|
|            --prompt-version 1.2.0   (944 s ; sans --embedding, c'est le|
|            controle de la commande 4 qui t'arrete, au lieu de laisser|
|            partir l'etiquette qwen3 sur des vecteurs bge-m3)|
|       5. cette commande-ci, de nouveau.|"
  )
  # VISITE-GUIDEE-FIN

  printf "\n  Six commandes, dans cet ordre. Pose d'abord l'alias affiche plus haut\n"
  printf '  (alias assistantqr=...), sinon prefixe-les par le chemin complet.\n\n'

  local numero=1 entree
  for entree in "${VISITE[@]}"; do
    printf '  %d. %s\n' "$numero" "$(printf '%s' "$entree" | cut -d'|' -f1)"
    printf '     ce que tu vas voir  : %s\n' "$(printf '%s' "$entree" | cut -d'|' -f2)"
    printf '     ce que ca demontre  : %s\n\n' "$(printf '%s' "$entree" | cut -d'|' -f3)"
    numero=$(( numero + 1 ))
  done
}

# ---------------------------------------------------------------------- main

main() {
  MODEL="qwen3-embedding:0.6b"
  SKIP_PULL=0
  REINDEX=0
  SANTE=""
  OLLAMA_BIN=""

  while [ "$#" -gt 0 ]; do
    case "$1" in
      --model)
        # « shift 2 » sur un seul argument restant echoue SANS decaler : la
        # boucle tournerait alors indefiniment sur « --model ». On refuse avant.
        if [ "$#" -lt 2 ]; then
          ko "L'option --model attend une valeur, par exemple « --model bge-m3 »."
          return $CODE_PREREQUIS
        fi
        MODEL="$2"; shift 2 ;;
      --model=*)    MODEL="${1#--model=}"; shift ;;
      --skip-pull)  SKIP_PULL=1; shift ;;
      --reindex)    REINDEX=1; shift ;;
      --help|-h)    aide; return 0 ;;
      *)
        ko "Option inconnue : « $1 »."
        aide
        return $CODE_PREREQUIS
        ;;
    esac
  done

  if [ -z "$MODEL" ]; then
    ko "L'option --model attend'une valeur."
    return $CODE_PREREQUIS
  fi

  printf '\nAssistantQR — mise en route\n'
  printf '  depot          : %s\n' "$REPO_ROOT"
  printf '  modele vise    : %s (embeddings)\n' "$MODEL"
  printf '  options        : --skip-pull=%s · --reindex=%s\n' "$SKIP_PULL" "$REINDEX"

  local debut fin code
  debut=$(date +%s)

  etape1_prerequis; code=$?; [ "$code" -ne 0 ] && return "$code"
  etape2_ollama;    code=$?; [ "$code" -ne 0 ] && return "$code"
  etape3_modeles;   code=$?; [ "$code" -ne 0 ] && return "$code"
  etape4_service;   code=$?; [ "$code" -ne 0 ] && return "$code"
  environnement_local
  etape5_construction; code=$?; [ "$code" -ne 0 ] && return "$code"
  etape6_index;        code=$?; [ "$code" -ne 0 ] && return "$code"

  montrer_environnement
  montrer_visite_guidee

  fin=$(date +%s)
  printf 'Pret en %s s. Relance ce script autant de fois que tu veux : il ne refera que ce qui manque.\n\n' "$(( fin - debut ))"
  return 0
}

# Source ou execution ? La difference n'est pas cosmetique : dans un script
# source, « exit » tuerait le terminal de l'utilisateur.
SOURCE=0
if [ "${BASH_SOURCE[0]}" != "$0" ]; then SOURCE=1; fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CHEMIN_CLI=""
resoudre_cli
DOSSIER_JOURNAUX="${TMPDIR:-${TEMP:-/tmp}}/assistantqr-demarrer"

main "$@"
CODE_FINAL=$?

if [ "$SOURCE" = "1" ]; then
  return "$CODE_FINAL" 2>/dev/null || true
else
  exit "$CODE_FINAL"
fi
