using System.Text;
using System.Text.RegularExpressions;

using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Domain.Text;
using AssistantQR.Infrastructure.Embeddings;

namespace AssistantQR.Infrastructure.LanguageModels;

/// <summary>
/// Adaptateur FACTICE de <see cref="ILanguageModel"/> : il n'engendre rien, il extrait.
/// Il relit le bloc d'extraits que le prompt lui a fourni, ecarte ceux qui ne recoupent
/// pas la question, garde les deux premiers qui restent, en prend la premiere phrase et
/// les recite avec leur identifiant. Si aucun extrait ne recoupe la question, il emet le
/// marqueur de refus.
/// </summary>
/// <remarks>
/// C'EST CE TYPE QUI REND LA LIGNE DE COMMANDE UTILISABLE SANS RESEAU. Sans lui, il
/// faudrait Ollama pour voir la moindre reponse, et le depot deviendrait une promesse
/// plutot qu'une demonstration.
///
/// IL IGNORE DELIBEREMENT LA TEMPERATURE ET LA GRAINE — et c'est exactement ce qui le
/// rend utile. Un vrai modele, meme a temperature nulle, peut varier : ordre de
/// reduction en virgule flottante sur GPU, changement de version du modele, mise a jour
/// du serveur. Ici, la meme requete rend octet pour octet la meme reponse, aujourd'hui
/// et dans six mois. Un test qui echoue accuse donc le code, jamais le modele. C'est la
/// seule facon de faire d'un instantane une reference : la ligne de base doit etre
/// stable pour que la derive du vrai modele soit lisible par contraste.
///
/// CRITERE DE PERTINENCE, EN TROIS PHRASES. Un, on reduit la question a ses mots
/// significatifs : minuscules, accents supprimes, au moins quatre lettres, mots vides
/// francais ecartes. Deux, un extrait n'est retenu que si DEUX de ces mots se retrouvent
/// AU MEME ENDROIT de son bloc — soit dans l'en-tete (identifiant et titre, qui disent de
/// quoi le document parle), soit dans le texte de l'extrait (qui dit ce qu'il contient) —
/// jamais en additionnant un mot de l'un et un mot de l'autre. Trois, si aucun extrait ne
/// passe ce filtre, on emet <see cref="ModelResponseParser.RefusalMarker"/> au lieu de
/// citer les deux premiers venus.
///
/// POURQUOI CE CRITERE EXISTE. Le gabarit de prompt ORDONNE au modele d'ecrire le
/// marqueur de refus quand les extraits ne permettent pas de repondre. Ce faux-la simule
/// donc l'OBEISSANCE AU PROMPT, grossierement mais visiblement : recoupement lexical, pas
/// comprehension. C'est precisement l'interet du port — un vrai modele, lui, obeit *ou
/// pas*, et rien dans le contrat <see cref="ILanguageModel"/> ne l'y contraint. La
/// garantie metier ne vient toujours pas d'ici : <c>AnswerPolicy</c> reste le filet qui
/// verifie que les identifiants cites existent et sont lisibles. Ce critere ne remplace
/// pas la politique, il rend la doublure representative du comportement qu'on attend.
///
/// CE QUE CE FAUX NE SAIT TOUJOURS PAS FAIRE : synthetiser deux sources, reformuler,
/// juger qu'un extrait parle du bon sujet avec d'autres mots. Un recoupement lexical
/// n'est pas une comprehension : il laissera passer un extrait qui partage le vocabulaire
/// sans repondre, et refusera une question posee avec des synonymes. C'est une doublure,
/// pas un modele.
///
/// Il connait le format produit par <c>EvidenceFormatter</c> — l'en-tete
/// « [identifiant] Titre (niveau) » suivi du texte. Ce couplage est assume : les deux
/// types doivent bouger ensemble.
/// </remarks>
public sealed class ExtractiveLanguageModel : ILanguageModel
{
    private const int MaxCitedFragments = 2;
    private const int MaxFragmentLength = 400;
    private const int MinSentenceLength = 20;
    private const int MinWordLength = 4;
    private const int MinMatchingWords = 2;
    private const string Preamble = "D'après les documents consultés : ";

    private static readonly Regex HeaderPattern = new(
        @"^\[(?<id>[A-Za-z0-9][A-Za-z0-9_\-]{0,127})\]\s+(?<title>.+?)\s+\((?<level>public|internal|confidential)\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Mots vides francais de quatre lettres ou plus. Les plus courts sont deja ecartes
    /// par <see cref="MinWordLength"/>, inutile de les lister. On y ajoute les mots
    /// interrogatifs (« quels », « comment », « combien ») : ils ouvrent la question sans
    /// rien dire de son sujet, et les garder ferait passer n'importe quel extrait
    /// contenant « comment » pour pertinent.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "afin", "alors", "ainsi", "apres", "aucun", "aucune", "aussi", "autre", "autres",
        "avant", "avec", "avoir", "bien", "cela", "celle", "celles", "celui", "cette",
        "ceux", "chaque", "chez", "combien", "comme", "comment", "dans", "depuis", "donc",
        "dont", "elle", "elles", "encore", "etre", "faire", "fait", "faut", "leur",
        "leurs", "lors", "lorsque", "mais", "meme", "memes", "moins", "notre", "nous",
        "parce", "peut", "peuvent", "peux", "plus", "pour", "pourquoi", "pouvez", "puis",
        "quand", "quel", "quelle", "quelles", "quels", "quoi", "sans", "sera", "seront",
        "seulement", "soit", "sont", "sous", "suis", "tous", "tout", "toute", "toutes",
        "tres", "votre", "vous",
    };

    public string ModelId => "extractive-fake";

    public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var fragments = ReadEvidence(request.Prompt);
        var questionWords = ReadQuestionWords(request.Prompt);
        var relevant = SelectRelevant(fragments, questionWords);

        // Aucun extrait exploitable : soit le prompt n'en contenait pas, soit aucun ne
        // recoupe la question. Dans les deux cas on emet le marqueur de refus plutot que
        // d'inventer. Le parseur d'Application le traduira en brouillon vide, et la
        // politique metier en refus argumente.
        if (relevant.Count == 0)
        {
            return Task.FromResult(new LlmCompletion(ModelResponseParser.RefusalMarker, ModelId));
        }

        var builder = new StringBuilder(Preamble);

        for (var i = 0; i < relevant.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(FirstSentence(relevant[i].Text))
                   .Append(" [")
                   .Append(relevant[i].Id)
                   .Append(']');
        }

        // Pas de comptage de jetons invente : ce faux ne consomme rien, et afficher un
        // chiffre plausible mais faux serait pire que de n'en afficher aucun.
        return Task.FromResult(new LlmCompletion(builder.ToString(), ModelId));
    }

    /// <summary>
    /// Retient, dans l'ordre du prompt (donc par score decroissant), les deux premiers
    /// extraits qui recoupent la question.
    /// </summary>
    /// <remarks>
    /// DEGRADATION VOLONTAIRE : si la question est introuvable dans le prompt ou ne
    /// contient aucun mot significatif (« Combien ? »), le critere n'a pas de matiere et
    /// on revient au comportement d'origine — les deux premiers extraits. Refuser tout
    /// en bloc serait la pire panne possible pour une doublure : silencieuse, totale, et
    /// imputee au corpus alors qu'elle viendrait du gabarit.
    /// </remarks>
    private static List<EvidenceBlock> SelectRelevant(
        List<EvidenceBlock> fragments,
        HashSet<string> questionWords)
    {
        var kept = new List<EvidenceBlock>(MaxCitedFragments);
        var required = RequiredMatches(questionWords.Count);

        foreach (var fragment in fragments)
        {
            if (kept.Count == MaxCitedFragments)
            {
                break;
            }

            // DEUX COMPTES SEPARES, JAMAIS ADDITIONNES. L'en-tete dit de quoi le document
            // PARLE, le texte dit ce que l'extrait CONTIENT : ce sont deux affirmations
            // differentes, et il faut qu'une des deux tienne seule. Additionner les deux
            // laissait passer le pire cas — un mot ici, un mot la, aucun rapport avec la
            // question. « Quelle est la remuneration d'un agent d'accueil ? » retenait
            // ainsi « Procedure d'accueil au comptoir » : « accueil » venait du titre,
            // « agent » du texte, « remuneration » de nulle part, et le faux citait un
            // document qui ne dit pas un mot des salaires. Depuis, il refuse.
            if (required == 0 ||
                CountMatches(fragment.Header, questionWords) >= required ||
                CountMatches(fragment.Text, questionWords) >= required)
            {
                kept.Add(fragment);
            }
        }

        return kept;
    }

    /// <summary>
    /// Combien de mots significatifs un fragment doit recouper pour etre juge pertinent :
    /// deux, sauf si la question en offre moins.
    /// </summary>
    /// <remarks>
    /// UN NOMBRE FIXE, ET PAS UNE PROPORTION — la tentation est pourtant forte. Exiger la
    /// moitie des mots de la question parait plus fin : deux mots communs sur trois, c'est
    /// un sujet partage ; deux sur six, une coincidence. Mais ce seuil-la casse les
    /// questions composees. « Quels sont les horaires d'ouverture et les regles de pret
    /// des documents ? » porte cinq mots significatifs et appelle DEUX documents, dont
    /// chacun ne couvre par construction que sa moitie : reclamer trois recoupements les
    /// refuse tous les deux et fait taire le systeme sur une question a laquelle le corpus
    /// repond parfaitement. Un test du depot fixe ce bord exact. On garde donc le plancher
    /// a deux, et on assume la contrepartie : sur une question longue, deux mots suffisent
    /// encore a retenir un extrait qui ne repond pas.
    ///
    /// LE PLAFOND protege les questions d'un seul mot significatif, qui sans lui seraient
    /// toujours refusees.
    /// </remarks>
    private static int RequiredMatches(int questionWordCount) =>
        Math.Min(MinMatchingWords, questionWordCount);

    /// <summary>
    /// Nombre de mots significatifs DISTINCTS de la question presents dans un texte.
    /// Un extrait qui repete dix fois « retard » ne recoupe pas mieux la question qu'un
    /// extrait qui l'ecrit une fois.
    /// </summary>
    private static int CountMatches(string text, HashSet<string> questionWords)
    {
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in Words(text))
        {
            if (questionWords.Contains(word))
            {
                matched.Add(word);
            }
        }

        return matched.Count;
    }

    /// <summary>
    /// Relit le bloc d'extraits injecte dans le prompt : un en-tete
    /// « [identifiant] Titre (niveau) », puis le texte jusqu'a la ligne vide suivante.
    /// </summary>
    private static List<EvidenceBlock> ReadEvidence(string? prompt)
    {
        var blocks = new List<EvidenceBlock>();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return blocks;
        }

        // Decoupage sur '\n' puis nettoyage des blancs de bord (dont le '\r') : le gabarit
        // vient d'un fichier qui peut etre en CRLF alors que le bloc d'extraits est
        // toujours en LF, et rien n'interdit au gabarit d'indenter son placeholder.
        var lines = prompt.Split('\n');
        var index = 0;

        // On lit TOUS les blocs, plus seulement les deux premiers : le tri par pertinence
        // vient apres, et un extrait pertinent peut arriver en troisieme position.
        while (index < lines.Length)
        {
            var headerLine = lines[index].Trim();
            var header = HeaderPattern.Match(headerLine);
            index++;

            if (!header.Success)
            {
                continue;
            }

            var text = new StringBuilder();

            while (index < lines.Length)
            {
                var line = lines[index].Trim();

                if (line.Length == 0 || HeaderPattern.IsMatch(line))
                {
                    break;
                }

                if (text.Length > 0)
                {
                    text.Append(' ');
                }

                text.Append(line);
                index++;
            }

            if (text.Length > 0)
            {
                blocks.Add(new EvidenceBlock(header.Groups["id"].Value, headerLine, text.ToString()));
            }
        }

        return blocks;
    }

    /// <summary>
    /// Extrait les mots significatifs de la question posee dans le prompt.
    /// </summary>
    /// <remarks>
    /// FRAGILITE ASSUMEE, ET POURQUOI C'EST LE MOINDRE MAL. Le port
    /// <see cref="ILanguageModel"/> ne transporte qu'une chaine : la question a deja
    /// fondu dans le gabarit quand elle arrive ici, exactement comme pour un vrai
    /// fournisseur. Il faut donc la relire dans le prompt, et le seul repere disponible
    /// est le titre de section qui precede <c>{{question}}</c> dans les gabarits
    /// (« ## Question posée »). Elargir le contrat du port pour transporter la question
    /// a part serait plus solide, mais deformerait le port pour les besoins d'une
    /// doublure — le vrai fournisseur, lui, ne recevra jamais qu'un prompt. On accepte
    /// donc le couplage, on tolere les variantes (« ## Question », « Question : ... »),
    /// et on retombe sur le comportement d'origine si rien n'est reconnu.
    /// </remarks>
    private static HashSet<string> ReadQuestionWords(string? prompt)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return words;
        }

        var lines = prompt.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var marker = QuestionMarker(line);

            if (marker is null)
            {
                continue;
            }

            // Forme en ligne : « Question : ... ». Le texte suit sur la meme ligne.
            if (marker.Length > 0)
            {
                Collect(words, marker);
                return words;
            }

            // Forme en titre de section : le texte suit, jusqu'a la ligne vide ou au
            // titre suivant.
            for (var next = index + 1; next < lines.Length; next++)
            {
                var candidate = lines[next].Trim();

                if (candidate.Length == 0)
                {
                    if (words.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (candidate.StartsWith('#'))
                {
                    break;
                }

                Collect(words, candidate);
            }

            return words;
        }

        return words;
    }

    /// <summary>
    /// La ligne annonce-t-elle la question ? Renvoie le texte qui suit l'annonce sur la
    /// meme ligne (chaine vide si l'annonce est un titre de section seul), ou
    /// <c>null</c> si la ligne n'annonce rien.
    /// </summary>
    private static string? QuestionMarker(string line)
    {
        var withoutHashes = line.TrimStart('#', ' ', '\t');
        var normalized = TextNormalization.Fold(withoutHashes);

        if (!normalized.StartsWith("question", StringComparison.Ordinal))
        {
            return null;
        }

        var colon = withoutHashes.IndexOf(':', StringComparison.Ordinal);

        return colon >= 0 ? withoutHashes[(colon + 1)..].Trim() : string.Empty;
    }

    private static void Collect(HashSet<string> words, string text)
    {
        foreach (var word in Words(text))
        {
            if (!StopWords.Contains(word))
            {
                words.Add(word);
            }
        }
    }

    /// <summary>
    /// Mots retenus d'un texte : minuscules, accents supprimes, quatre lettres au moins.
    /// Meme repliement des deux cotes de la comparaison — sinon « médiathèque » dans le
    /// corpus ne recouperait jamais « mediatheque » tape au clavier.
    ///
    /// On reutilise <c>TextNormalization</c>, deja partage par l'embedding factice et le
    /// modele de rejeu, plutot que la recette « FormD puis on jette les NonSpacingMark » :
    /// le depot compile avec <c>InvariantGlobalization=true</c>, ou <c>string.Normalize</c>
    /// ne decompose rien et echoue en silence. Trois doublures qui replient le texte de
    /// trois facons differentes, ce serait trois seuils de pertinence differents.
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        var normalized = TextNormalization.Fold(text);
        var builder = new StringBuilder();

        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length >= MinWordLength)
            {
                yield return builder.ToString();
            }

            builder.Clear();
        }

        if (builder.Length >= MinWordLength)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>
    /// Premiere phrase du fragment. La coupe exige une ponctuation forte suivie d'un
    /// blanc et un minimum de caracteres avant elle : sans ces deux garde-fous,
    /// « 3.50 » ou « art. 4 » couperaient la phrase en plein milieu.
    /// </summary>
    private static string FirstSentence(string text)
    {
        // On reutilise l'utilitaire du Domain : il normalise les blancs et borne la
        // longueur. Aucune raison d'en ecrire un second ici.
        var normalized = TextExcerpt.Shorten(text, MaxFragmentLength);

        for (var i = MinSentenceLength; i < normalized.Length; i++)
        {
            if (normalized[i] is not ('.' or '!' or '?'))
            {
                continue;
            }

            if (i + 1 >= normalized.Length || char.IsWhiteSpace(normalized[i + 1]))
            {
                return normalized[..(i + 1)];
            }
        }

        return normalized;
    }

    private sealed record EvidenceBlock(string Id, string Header, string Text);
}
