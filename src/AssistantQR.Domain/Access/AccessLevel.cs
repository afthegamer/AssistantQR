namespace AssistantQR.Domain.Access;

/// <summary>
/// Niveau d'habilitation, exprime comme un ordre total (public &lt; interne &lt; confidentiel).
/// C'est un value object : deux niveaux de meme rang sont le meme niveau, il n'y a
/// rien a identifier. Le rang est modelise ici, et pas dans une enum, pour que la
/// comparaison « lisible avec » soit une operation du Domain et non une convention
/// dispersee dans le code appelant.
/// </summary>
public readonly record struct AccessLevel : IComparable<AccessLevel>
{
    private readonly int _rank;

    private AccessLevel(int rank) => _rank = rank;

    /// <summary>Rang croissant : 0 public, 1 interne, 2 confidentiel.</summary>
    public int Rank => _rank;

    /// <summary>Nom canonique anglais. La traduction francaise appartient a l'Infrastructure.</summary>
    public string Name => _rank switch
    {
        1 => "internal",
        2 => "confidential",
        _ => "public",
    };

    /// <summary>Niveau le plus ouvert. C'est aussi <c>default(AccessLevel)</c> : le defaut sur.</summary>
    public static AccessLevel Public { get; } = new(0);

    /// <summary>Reserve aux agents de la structure.</summary>
    public static AccessLevel Internal { get; } = new(1);

    /// <summary>Le plus restrictif.</summary>
    public static AccessLevel Confidential { get; } = new(2);

    /// <summary>Les trois niveaux, du plus ouvert au plus restrictif.</summary>
    public static IReadOnlyList<AccessLevel> All { get; } = new[] { Public, Internal, Confidential };

    /// <summary>Reconstruit un niveau depuis son rang.</summary>
    /// <exception cref="DomainException">Si le rang sort de l'intervalle [0..2].</exception>
    public static AccessLevel FromRank(int rank)
    {
        if (rank is < 0 or > 2)
        {
            throw new DomainException($"Rang de niveau d'accès invalide : {rank}. Attendu 0, 1 ou 2.");
        }

        return new AccessLevel(rank);
    }

    /// <summary>Analyse un nom anglais ("public", "internal", "confidential"), insensible a la casse.</summary>
    /// <exception cref="DomainException">Si le nom est inconnu.</exception>
    public static AccessLevel Parse(string value)
    {
        if (!TryParse(value, out var level))
        {
            throw new DomainException(
                $"Niveau d'accès inconnu : « {value} ». Attendu : public, internal ou confidential.");
        }

        return level;
    }

    /// <summary>Variante non levante de <see cref="Parse"/>.</summary>
    public static bool TryParse(string? value, out AccessLevel level)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "public":
                level = Public;
                return true;
            case "internal":
                level = Internal;
                return true;
            case "confidential":
                level = Confidential;
                return true;
            default:
                level = default;
                return false;
        }
    }

    /// <summary>Ce niveau est-il lisible par quelqu'un dont l'habilitation est <paramref name="clearance"/> ?</summary>
    public bool IsReadableWith(AccessLevel clearance) => _rank <= clearance._rank;

    /// <inheritdoc />
    public int CompareTo(AccessLevel other) => _rank.CompareTo(other._rank);

    /// <summary>Compare deux niveaux par rang.</summary>
    public static bool operator <(AccessLevel left, AccessLevel right) => left._rank < right._rank;

    /// <summary>Compare deux niveaux par rang.</summary>
    public static bool operator >(AccessLevel left, AccessLevel right) => left._rank > right._rank;

    /// <summary>Compare deux niveaux par rang.</summary>
    public static bool operator <=(AccessLevel left, AccessLevel right) => left._rank <= right._rank;

    /// <summary>Compare deux niveaux par rang.</summary>
    public static bool operator >=(AccessLevel left, AccessLevel right) => left._rank >= right._rank;

    /// <inheritdoc />
    public override string ToString() => Name;
}
