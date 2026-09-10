using System.Text;

namespace AssistantQR.Application.Model;

/// <summary>
/// Mise en forme du bloc d'extraits injecte dans le prompt.
/// Ce formateur est le jumeau de <see cref="ModelResponseParser"/> : l'un dit au
/// modele quels identifiants existent, l'autre relit ceux qu'il a cites. Les deux
/// doivent bouger ensemble, donc ils vivent cote a cote.
/// Le separateur est un « \n » explicite et non <c>Environment.NewLine</c> : un
/// instantane enregistre sous Windows doit se comparer a un instantane enregistre
/// sous Linux, sinon la detection de derive signale des differences qui n'en sont pas.
/// </summary>
public static class EvidenceFormatter
{
    private const string Separator = "\n\n";

    /// <summary>
    /// Un bloc par fragment, separes par une ligne vide :
    /// <code>
    /// [identifiant] Titre du document (niveau)
    /// texte du fragment
    /// </code>
    /// </summary>
    public static string Format(IReadOnlyList<ScoredFragment> fragments)
    {
        if (fragments is null || fragments.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        for (var i = 0; i < fragments.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(Separator);
            }

            var fragment = fragments[i].Fragment;

            builder.Append('[').Append(fragment.DocumentId).Append("] ")
                   .Append(fragment.DocumentTitle)
                   .Append(" (").Append(fragment.AccessLevel).Append(')')
                   .Append('\n')
                   .Append(fragment.Text.Trim());
        }

        return builder.ToString();
    }
}
