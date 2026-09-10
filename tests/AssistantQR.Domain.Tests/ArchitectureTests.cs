using System.Reflection;
using AssistantQR.Domain.Access;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// Les tests d'architecture sont les seuls qui echouent pour une bonne raison sans
/// qu'aucun comportement n'ait change. Ils ne verifient pas ce que le code FAIT, ils
/// verifient ce dont il DEPEND — c'est-a-dire ce qu'un diagramme de couches promet et
/// qu'une revue de code oublie de tenir au bout de six mois.
///
/// Ici, la promesse est brutale : le Domain ne depend de rien. Pas d'une base, pas
/// d'un client HTTP, pas d'un serialiseur, pas d'un SDK de modele de langue. C'est
/// cette promesse qui rend AnswerPolicy testable hors ligne, donc verifiable. Une
/// dependance ajoutee « juste pour depanner » la casse silencieusement, et personne
/// ne s'en apercoit avant le jour ou les tests unitaires reclament une cle d'API.
/// Ces tests-la sont le detecteur de fumee.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(DomainException).Assembly;

    /// <summary>Prefixes des seules assemblies que le Domain a le droit de referencer.</summary>
    private static readonly string[] PrefixesAutorises = { "System", "netstandard", "mscorlib" };

    /// <summary>Fragments de noms de types qui trahissent une fuite technique dans le Domain.</summary>
    private static readonly string[] FragmentsInterdits = { "Http", "Json", "Vector" };

    // =====================================================================
    // 1. Le Domain ne reference rien
    // =====================================================================

    [Fact]
    public void Domain_QuellesQueSoientSesReferences_NeDependQueDeLaBibliothequeStandard()
    {
        var references = Domain.GetReferencedAssemblies();

        // Garde-fou : un test d'architecture qui n'inspecte rien passerait toujours.
        Assert.NotEmpty(references);

        var intruses = references
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !PrefixesAutorises.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            intruses.Count == 0,
            $"Le Domain a acquis une dépendance : {string.Join(", ", intruses)}. "
            + "C'est exactement ce que ce dépôt cherche à rendre visible. "
            + "Le cœur métier doit rester exécutable sans réseau, sans base et sans clé d'API : "
            + "la dépendance ajoutée doit repasser derrière un port de l'Application.");
    }

    // =====================================================================
    // 2. Aucun type public du Domain n'expose de mecanique technique
    // =====================================================================

    [Fact]
    public void Domain_QuelQueSoitLeTypePublic_NExposeAucunMembreTechnique()
    {
        var types = Domain.GetExportedTypes();
        var fuites = types.SelectMany(FuitesTechniquesDe).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        var signaturesInspectees = types.Sum(SignaturesPubliquesDe);

        // Garde-fou : si le parcours par reflexion se vidait, le test passerait a vide.
        Assert.True(
            signaturesInspectees > 100,
            $"Seules {signaturesInspectees} signatures publiques ont été inspectées : le parcours "
            + "par réflexion ne couvre plus le Domain, ce test ne prouve donc plus rien.");

        Assert.True(
            fuites.Count == 0,
            "Un type public du Domain expose un détail technique dans sa signature :"
            + Environment.NewLine
            + string.Join(Environment.NewLine, fuites)
            + Environment.NewLine
            + "Un vecteur d'embeddings, un client HTTP ou un document JSON sont des mécaniques "
            + "choisies, pas des concepts du métier « répondre en citant ses sources ». Leur place "
            + "est en Application ou en Infrastructure : le Domain doit ignorer qu'ils existent.");
    }

    /// <summary>
    /// Controle negatif. Un detecteur qui ne detecte jamais rien est indiscernable d'un
    /// detecteur en panne : on lui soumet donc un temoin volontairement fautif.
    /// </summary>
    [Fact]
    public void Detecteur_TypeTemoinExposantUneMecanique_SignaleBienLaFuite()
    {
        var fuites = FuitesTechniquesDe(typeof(TemoinFautif)).ToList();

        Assert.NotEmpty(fuites);
        Assert.Contains(fuites, fuite => fuite.Contains("Vector", StringComparison.Ordinal));
        Assert.Contains(fuites, fuite => fuite.Contains("Json", StringComparison.Ordinal));
    }

    // =====================================================================
    // 3. Corollaire : pas d'horloge dans le Domain
    // =====================================================================

    /// <summary>
    /// Une politique qui lirait l'heure courante cesserait d'etre une fonction pure,
    /// et ses tests dependraient du jour ou on les execute. Le temps entre par IClock,
    /// qui est un port de l'Application.
    /// </summary>
    [Fact]
    public void Domain_QuelQueSoitLeTypePublic_NExposeAucuneHorloge()
    {
        var fuites = Domain.GetExportedTypes()
            .SelectMany(type => SignaturesDe(type).SelectMany(signature => NomsImpliques(signature.Type))
                .Where(nom => nom is "DateTime" or "DateTimeOffset" or "Stopwatch" or "TimeProvider")
                .Select(nom => $"{type.FullName} expose « {nom} »"))
            .Distinct()
            .ToList();

        Assert.True(
            fuites.Count == 0,
            "Le Domain manipule le temps courant, il n'est donc plus déterministe :"
            + Environment.NewLine
            + string.Join(Environment.NewLine, fuites)
            + Environment.NewLine
            + "Le temps doit entrer par le port IClock de l'Application.");
    }

    [Fact]
    public void NomsImpliques_TypeGenerique_DeplieLesArgumentsDeType()
    {
        var noms = NomsImpliques(typeof(IReadOnlyList<AccessLevel>)).ToList();

        Assert.Contains("AccessLevel", noms);
        Assert.Contains(noms, nom => nom.StartsWith("IReadOnlyList", StringComparison.Ordinal));
    }

    // =====================================================================
    // Mecanique de detection
    // =====================================================================

    private static IEnumerable<string> FuitesTechniquesDe(Type type)
    {
        foreach (var (membre, typeExpose, origine) in SignaturesDe(type))
        {
            foreach (var nom in NomsImpliques(typeExpose))
            {
                var fragment = FragmentsInterdits.FirstOrDefault(f => nom.Contains(f, StringComparison.Ordinal));

                if (fragment is not null)
                {
                    yield return
                        $"{type.FullName}.{membre.Name} ({origine} de type « {nom} », fragment interdit « {fragment} »)";
                }
            }
        }
    }

    private static int SignaturesPubliquesDe(Type type) => SignaturesDe(type).Count();

    /// <summary>Toutes les paires (membre public declare, type apparaissant dans sa signature).</summary>
    private static IEnumerable<(MemberInfo Membre, Type Type, string Origine)> SignaturesDe(Type type)
    {
        const BindingFlags Publics =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var membre in type.GetMembers(Publics))
        {
            switch (membre)
            {
                case FieldInfo field:
                    yield return (membre, field.FieldType, "champ");
                    break;

                case PropertyInfo property:
                    yield return (membre, property.PropertyType, "propriété");
                    break;

                case EventInfo eventInfo when eventInfo.EventHandlerType is not null:
                    yield return (membre, eventInfo.EventHandlerType, "événement");
                    break;

                case MethodInfo method:
                    yield return (membre, method.ReturnType, "type de retour");
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return (membre, parameter.ParameterType, $"paramètre « {parameter.Name} »");
                    }

                    break;

                case ConstructorInfo constructor:
                    foreach (var parameter in constructor.GetParameters())
                    {
                        yield return (membre, parameter.ParameterType, $"paramètre « {parameter.Name} »");
                    }

                    break;
            }
        }
    }

    /// <summary>Deplie un type et tous ses arguments generiques, tableaux et references compris.</summary>
    private static IEnumerable<string> NomsImpliques(Type type)
    {
        var actuel = type;

        while (actuel.HasElementType && actuel.GetElementType() is { } element)
        {
            actuel = element;
        }

        yield return actuel.Name;

        foreach (var argument in actuel.GetGenericArguments())
        {
            foreach (var nom in NomsImpliques(argument))
            {
                yield return nom;
            }
        }
    }

    /// <summary>Temoin volontairement fautif, uniquement destine au controle negatif ci-dessus.</summary>
    private sealed class TemoinFautif
    {
        public EmbeddingVectorFactice Vecteur { get; } = new();

        public void Enregistrer(JsonDocumentFactice document) => _ = document;

        internal sealed record EmbeddingVectorFactice;

        internal sealed record JsonDocumentFactice;
    }
}
