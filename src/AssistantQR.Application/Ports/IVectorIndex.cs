using AssistantQR.Application.Model;

namespace AssistantQR.Application.Ports;

/// <summary>Stockage et interrogation des morceaux vectorises.</summary>
/// <remarks>
/// POURQUOI LA FRONTIERE EST ICI.
/// C'est la frontiere la plus classique du projet — un magasin de donnees — et
/// pourtant la plus chargee de sens. L'index recoit des morceaux avec leur vecteur et
/// rend des fragments du Domain deja scores : il ne rend pas des lignes, des
/// documents JSON ou des identifiants a rehydrater. La traduction vers le vocabulaire
/// metier se fait dans l'adaptateur, ce qui evite au cas d'usage de connaitre le
/// schema de la base.
///
/// LE POINT DELICAT : <c>SearchAsync</c> accepte un <see cref="SearchFilter"/>. Cette
/// signature fait deliberement entrer une notion metier — le controle d'acces — dans
/// le contrat d'un composant technique. C'est le dilemme central du cours, et il est
/// place ici plutot que masque : le pre-filtrage ameliore franchement la pertinence,
/// au prix d'une regle de securite desormais dupliquee hors du Domain. Le projet
/// laisse les deux modes disponibles pour que l'arbitrage se mesure au lieu de se
/// decreter.
///
/// POURQUOI <c>ResetAsync</c> PREND LE MODELE ET LA STRATEGIE. Un index sans memoire
/// de sa provenance est un piege : on peut l'interroger avec les vecteurs d'un autre
/// modele, les dimensions concordent, la recherche repond, et les resultats sont
/// faux. <c>GetMetadataAsync</c> existe uniquement pour rendre cette panne detectable
/// cote C#. Le service Python, lui, ne refuse volontairement rien : c'est le scenario
/// que la demonstration doit pouvoir reproduire.
/// </remarks>
public interface IVectorIndex
{
    /// <summary>Vide l'index et enregistre avec quel modele et quel decoupage il va être reconstruit.</summary>
    Task ResetAsync(
        EmbeddingModelDescriptor model,
        string chunkingStrategyId,
        CancellationToken cancellationToken = default);

    /// <summary>Ajoute ou remplace un lot de morceaux vectorises.</summary>
    Task UpsertAsync(IReadOnlyList<IndexedChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>Les <paramref name="topK"/> morceaux les plus proches, deja tries par score decroissant.</summary>
    Task<IReadOnlyList<ScoredFragment>> SearchAsync(
        EmbeddingVector query,
        int topK,
        SearchFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>Ce que l'index sait de sa propre construction.</summary>
    Task<IndexMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);
}
