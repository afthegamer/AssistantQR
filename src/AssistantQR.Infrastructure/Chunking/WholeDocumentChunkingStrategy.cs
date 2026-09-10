using AssistantQR.Application.Model;
using AssistantQR.Application.Ports;
using AssistantQR.Domain.Documents;

namespace AssistantQR.Infrastructure.Chunking;

/// <summary>
/// Adaptateur BASELINE de <see cref="IChunkingStrategy"/> : un morceau par document.
/// </summary>
/// <remarks>
/// Ne pas decouper est aussi une strategie de decoupage — et c'est la reference contre
/// laquelle les deux autres se mesurent. Sans elle, on ne saurait pas si le decoupage
/// ameliore quoi que ce soit ; on le supposerait, ce qui n'est pas la meme chose.
///
/// SES DEUX DEFAUTS SONT INSTRUCTIFS. D'abord un vecteur unique pour un document entier
/// moyenne tous ses sujets : le document devient mediocrement proche de tout et
/// franchement proche de rien. Ensuite le fragment injecte dans le prompt est le
/// document complet, ce qui consomme la fenetre de contexte et noie l'information utile
/// — le modele cite alors correctement une source dans laquelle le lecteur devra
/// chercher lui-meme.
///
/// En revanche elle ne coupe jamais une phrase en deux, et sur des documents courts
/// comme ceux de ce corpus, l'ecart avec « paragraph » est plus faible qu'on ne le
/// croit. C'est precisement ce genre d'intuition que les instantanes permettent de
/// verifier au lieu d'en discuter.
/// </remarks>
public sealed class WholeDocumentChunkingStrategy : IChunkingStrategy
{
    /// <inheritdoc />
    public string Id => "whole-document";

    /// <inheritdoc />
    public string Description => "Aucun découpage : un seul morceau contenant le document entier.";

    /// <inheritdoc />
    public IReadOnlyList<Chunk> Split(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new[]
        {
            new Chunk(
                Chunk.BuildId(document.Id, 0),
                document.Id,
                document.Title,
                document.AccessLevel,
                0,
                document.Content),
        };
    }
}
