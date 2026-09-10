namespace AssistantQR.Domain.Access;

/// <summary>
/// Celui qui pose la question, et jusqu'ou il a le droit de lire.
/// L'habilitation voyage AVEC la demande : aucune politique du Domain n'a besoin
/// d'aller interroger un annuaire, donc aucune n'a besoin d'une dependance.
/// </summary>
public sealed record Requester(UserId Id, AccessLevel Clearance)
{
    /// <summary>Le demandeur par defaut : identifie mais sans habilitation particuliere.</summary>
    public static Requester Anonymous { get; } = new(UserId.From("anonymous"), AccessLevel.Public);

    /// <summary>Confort d'appel depuis les couches externes, qui manipulent des chaines.</summary>
    /// <exception cref="DomainException">Si l'identifiant ou le niveau est invalide.</exception>
    public static Requester Create(string userId, string clearance) =>
        new(UserId.From(userId), AccessLevel.Parse(clearance));
}
