using AssistantQR.Domain.Access;
using AssistantQR.Domain.Documents;
using AssistantQR.Domain.Evidence;
using AssistantQR.Domain.Policies;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// REGLE METIER 3. L'index vectoriel peut pre-filtrer par efficacite, mais c'est
/// cette fonction-ci qui fait autorite : un pre-filtrage est une optimisation, la
/// politique reste la verite. Ces tests sont donc la reference contre laquelle
/// l'Infrastructure sera jugee, jamais l'inverse.
/// </summary>
public sealed class AccessPolicyTests
{
    private static EvidenceFragment Fragment(string id, AccessLevel level, int ordinal = 0) => new(
        DocumentId.From(id),
        $"Titre de {id}",
        $"Texte de {id}, fragment {ordinal}.",
        level,
        ordinal);

    private static readonly EvidenceFragment Publique =
        Fragment("retards-amendes", AccessLevel.Public);

    private static readonly EvidenceFragment Interne =
        Fragment("gestion-retards-interne", AccessLevel.Internal);

    private static readonly EvidenceFragment Confidentielle =
        Fragment("contentieux-usagers", AccessLevel.Confidential);

    private static IReadOnlyList<EvidenceFragment> LesTrois() =>
        new[] { Publique, Interne, Confidentielle };

    // --- IsReadable ----------------------------------------------------------

    [Fact]
    public void IsReadable_DemandeurPublic_NeVoitQueLeFragmentPublic()
    {
        var demandeur = Requester.Anonymous;

        Assert.True(AccessPolicy.IsReadable(Publique, demandeur));
        Assert.False(AccessPolicy.IsReadable(Interne, demandeur));
        Assert.False(AccessPolicy.IsReadable(Confidentielle, demandeur));
    }

    [Fact]
    public void IsReadable_DemandeurInterne_VoitPublicEtInterne()
    {
        var demandeur = Requester.Create("agent", "internal");

        Assert.True(AccessPolicy.IsReadable(Publique, demandeur));
        Assert.True(AccessPolicy.IsReadable(Interne, demandeur));
        Assert.False(AccessPolicy.IsReadable(Confidentielle, demandeur));
    }

    [Fact]
    public void IsReadable_DemandeurConfidentiel_VoitTout()
    {
        var demandeur = Requester.Create("direction", "confidential");

        Assert.True(AccessPolicy.IsReadable(Publique, demandeur));
        Assert.True(AccessPolicy.IsReadable(Interne, demandeur));
        Assert.True(AccessPolicy.IsReadable(Confidentielle, demandeur));
    }

    // --- Readable ------------------------------------------------------------

    [Fact]
    public void Readable_DemandeurPublic_NeConserveQueLePublic()
    {
        var lisibles = AccessPolicy.Readable(LesTrois(), Requester.Anonymous);

        Assert.Equal(new[] { Publique }, lisibles);
    }

    [Fact]
    public void Readable_DemandeurInterne_ConservePublicEtInterneDansLOrdreDEntree()
    {
        var lisibles = AccessPolicy.Readable(LesTrois(), Requester.Create("agent", "internal"));

        Assert.Equal(new[] { Publique, Interne }, lisibles);
    }

    [Fact]
    public void Readable_DemandeurConfidentiel_ConserveTout()
    {
        var lisibles = AccessPolicy.Readable(LesTrois(), Requester.Create("direction", "confidential"));

        Assert.Equal(LesTrois(), lisibles);
    }

    [Fact]
    public void Readable_ListeVide_RendUneListeVide()
    {
        Assert.Empty(AccessPolicy.Readable(Array.Empty<EvidenceFragment>(), Requester.Anonymous));
    }

    [Fact]
    public void Readable_ListeNulle_RendUneListeVideSansLever()
    {
        Assert.Empty(AccessPolicy.Readable(null!, Requester.Anonymous));
    }

    [Fact]
    public void Readable_AucunFragmentAccessible_RendUneListeVide()
    {
        var fragments = new[] { Interne, Confidentielle };

        Assert.Empty(AccessPolicy.Readable(fragments, Requester.Anonymous));
    }

    // --- Forbidden -----------------------------------------------------------

    /// <summary>
    /// Forbidden sert a EXPLIQUER un refus (« trois documents existent, aucun ne vous
    /// est accessible »), jamais a construire une réponse. Le nommer separement rend
    /// cette intention lisible dans le code appelant.
    /// </summary>
    [Fact]
    public void Forbidden_DemandeurPublic_RendLesFragmentsEcartes()
    {
        var interdits = AccessPolicy.Forbidden(LesTrois(), Requester.Anonymous);

        Assert.Equal(new[] { Interne, Confidentielle }, interdits);
    }

    [Fact]
    public void Forbidden_DemandeurConfidentiel_RendUneListeVide()
    {
        Assert.Empty(AccessPolicy.Forbidden(LesTrois(), Requester.Create("direction", "confidential")));
    }

    [Fact]
    public void Forbidden_ListeVideOuNulle_RendUneListeVide()
    {
        Assert.Empty(AccessPolicy.Forbidden(Array.Empty<EvidenceFragment>(), Requester.Anonymous));
        Assert.Empty(AccessPolicy.Forbidden(null!, Requester.Anonymous));
    }

    /// <summary>Readable et Forbidden partitionnent l'ensemble : rien ne se perd, rien ne se duplique.</summary>
    [Fact]
    public void ReadableEtForbidden_QuelQueSoitLeDemandeur_PartitionnentLesFragments()
    {
        foreach (var clearance in AccessLevel.All)
        {
            var demandeur = new Requester(UserId.From("testeur"), clearance);
            var lisibles = AccessPolicy.Readable(LesTrois(), demandeur);
            var interdits = AccessPolicy.Forbidden(LesTrois(), demandeur);

            Assert.Equal(LesTrois().Count, lisibles.Count + interdits.Count);
            Assert.Empty(lisibles.Intersect(interdits));
        }
    }
}
