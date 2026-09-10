using System.Text.RegularExpressions;

using AssistantQR.Domain;
using AssistantQR.Domain.Answers;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Application.Model;

/// <summary>
/// TENSION 3 (frontiere floue) : ce parseur est le pendant exact du gabarit de
/// prompt. Il est en Application parce qu'il fait partie du contrat que nous
/// imposons au modele. On aurait pu le mettre en Infrastructure — le README
/// expose l'argument des deux camps.
///
/// L'argument « Application » (celui retenu) : le format de citation
/// <c>[identifiant]</c> est impose par NOTRE prompt, pas par le fournisseur. Un
/// changement de fournisseur ne le change pas ; un changement de gabarit, si. Le
/// parseur doit donc vivre au même endroit que la decision qu'il reflete.
/// L'argument « Infrastructure » : analyser la sortie d'un systeme externe est,
/// litteralement, de l'adaptation. Si demain un fournisseur rend les citations dans
/// un champ JSON structure, c'est ici qu'il faudra ecrire une seconde implementation
/// — et l'absence d'interface se fera sentir.
/// </summary>
public static class ModelResponseParser
{
    /// <summary>Marqueur que le prompt impose au modele quand les extraits ne suffisent pas.</summary>
    public const string RefusalMarker = "AUCUNE_REPONSE";

    private static readonly Regex CitationPattern =
        new(@"\[([A-Za-z0-9][A-Za-z0-9_\-]{0,127})\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Le prompt impose des citations en ligne de la forme <c>[identifiant-du-document]</c>.
    /// On extrait les identifiants (ordre d'apparition, dedoublonnes) et on retourne
    /// le texte tel quel. Rien n'est valide ici : la sortie est une PROPOSITION, et c'est
    /// <c>AnswerPolicy</c> qui juge. Ce parseur ne fait que traduire du texte en donnees.
    ///
    /// <para>
    /// DEUX SILENCES QUI NE SE RESSEMBLENT PAS, et que ce parseur ne confond plus :
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     Le marqueur <see cref="RefusalMarker"/> seul : le modele a repondu, et sa
    ///     reponse est « ces extraits ne permettent pas de repondre ». Il a fait
    ///     exactement ce que le gabarit lui ordonnait. On rend
    ///     <c>DraftAnswer.DeclinedByModel</c> — une proposition de refus DECLARE.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     Une reponse nulle, vide ou entierement blanche : le modele n'a rien rendu du
    ///     tout. C'est un incident technique. On rend <c>DraftAnswer.Empty</c>.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    /// Les deux propositions portent un texte vide : sans le drapeau <c>Declined</c>,
    /// le Domain n'aurait aucun moyen de les distinguer, et classerait l'obeissance
    /// du modele comme une panne.
    /// </para>
    /// </summary>
    public static DraftAnswer Parse(string rawResponse)
    {
        // Rien rendu : incident technique.
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return DraftAnswer.Empty;
        }

        // Le marqueur, et rien d'autre : refus declare, donc obeissance a la regle metier.
        if (IsRefusal(rawResponse))
        {
            return DraftAnswer.DeclinedByModel;
        }

        var citedIds = new List<DocumentId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in CitationPattern.Matches(rawResponse))
        {
            var candidate = match.Groups[1].Value;
            if (!seen.Add(candidate))
            {
                continue;
            }

            try
            {
                citedIds.Add(DocumentId.From(candidate));
            }
            catch (DomainException)
            {
                // Un identifiant mal forme est du bruit de generation, pas une erreur du
                // systeme : on l'ignore. Le refus viendra de la politique si, au final,
                // aucune citation exploitable ne subsiste.
            }
        }

        return new DraftAnswer(rawResponse, citedIds);
    }

    /// <summary>
    /// Le modele a-t-il refuse ? On enleve les marqueurs de citation et tous les blancs,
    /// puis on compare au marqueur : un modele bavard qui ecrit « AUCUNE_REPONSE. »
    /// avec un point final reste, lui, un cas de refus mal forme et sera traite plus loin.
    /// </summary>
    private static bool IsRefusal(string rawResponse)
    {
        var withoutCitations = CitationPattern.Replace(rawResponse, string.Empty);

        var compact = new System.Text.StringBuilder(withoutCitations.Length);
        foreach (var c in withoutCitations)
        {
            if (!char.IsWhiteSpace(c))
            {
                compact.Append(c);
            }
        }

        return string.Equals(compact.ToString(), RefusalMarker, StringComparison.OrdinalIgnoreCase);
    }
}
