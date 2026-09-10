using AssistantQR.Application.Model;

namespace AssistantQR.Application.Ports;

/// <summary>Acces aux gabarits de prompt versionnes.</summary>
/// <remarks>
/// POURQUOI CE PORT EXISTE ALORS QU'UNE CONSTANTE SUFFIRAIT.
/// Techniquement, on pourrait ecrire le prompt en dur dans le cas d'usage. Le projet
/// refuse ce raccourci parce qu'un prompt determine le comportement du systeme au
/// même titre que du code, tout en se modifiant sans recompilation. En faire un
/// artefact charge depuis l'exterieur, nomme et versionne, produit trois effets qu'une
/// constante ne donne pas : on peut le relire sans lire le code, on peut comparer deux
/// versions sur le même jeu de questions, et son empreinte entre dans les instantanes.
///
/// POURQUOI LA FRONTIERE EST A CE NIVEAU. Le port rend des <see cref="PromptTemplate"/>
/// deja analyses : nom, version, corps, emplacements requis. L'en-tete YAML, la
/// convention de nommage <c>nom@version.md</c> et la lecture disque restent dans
/// l'adaptateur. Le cas d'usage demande « le gabarit repondre-en-citant, version
/// 1.0.0 » ; il n'a aucune raison de savoir que cela correspond a un fichier.
///
/// L'interface est SYNCHRONE, contrairement aux autres ports d'entree-sortie. C'est
/// deliberé : le catalogue charge tout au demarrage. Un gabarit manquant est une
/// erreur de configuration qui doit exploser tot et bruyamment, pas une latence a
/// gerer a chaque question.
/// </remarks>
public interface IPromptCatalog
{
    /// <summary>Un gabarit precis.</summary>
    /// <exception cref="InvalidOperationException">Si le couple nom/version est absent du catalogue.</exception>
    PromptTemplate Get(string name, string version);

    /// <summary>La version la plus recente d'un gabarit.</summary>
    /// <exception cref="InvalidOperationException">Si aucun gabarit ne porte ce nom.</exception>
    PromptTemplate GetLatest(string name);

    /// <summary>Tout ce que le catalogue contient, sans les corps.</summary>
    IReadOnlyList<PromptDescriptor> List();
}
