using AssistantQR.Application.Ports;
using AssistantQR.Infrastructure.Time;

using Xunit;

namespace AssistantQR.Infrastructure.Tests.Time;

/// <summary>
/// Tests des deux adaptateurs du port <c>IClock</c>.
/// </summary>
/// <remarks>
/// TESTER UNE HORLOGE PARAIT DERISOIRE ; CE QUI EST TESTE ICI NE L'EST PAS. Un
/// instantane d'evaluation porte une date de creation : avec l'horloge du systeme, deux
/// enregistrements du meme jeu de questions differeraient toujours d'au moins un champ,
/// et la comparaison — dont c'est l'unique raison d'etre — signalerait une derive
/// permanente qui n'en est pas une. Le port existe pour ce seul cas, et
/// <c>FixedClock</c> est la piece qui le justifie.
///
/// <c>SystemClock</c> est par ailleurs le SEUL endroit du depot ou
/// <c>DateTimeOffset.UtcNow</c> a le droit d'apparaitre : le Domain et l'Application
/// n'y ont pas acces, parce qu'un appel a l'horloge est une entree-sortie et que les
/// entrees-sorties ne franchissent la frontiere que par un port.
/// </remarks>
public sealed class ClockTests
{
    [Fact]
    public void SystemClock_Instance_EstPartageeEtNonNulle()
    {
        Assert.NotNull(SystemClock.Instance);
        Assert.Same(SystemClock.Instance, SystemClock.Instance);
    }

    [Fact]
    public void SystemClock_UtcNow_EstProcheDeLHeureCouranteEtEnUtc()
    {
        var before = DateTimeOffset.UtcNow;
        var read = SystemClock.Instance.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(read, before, after);
        Assert.Equal(TimeSpan.Zero, read.Offset);
    }

    [Fact]
    public void FixedClock_UtcNow_NAvancePasToutSeul()
    {
        var moment = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
        IClock clock = new FixedClock(moment);

        Assert.Equal(moment, clock.UtcNow);
        Assert.Equal(moment, clock.UtcNow);
    }

    [Fact]
    public void FixedClock_UtcNow_EstModifiableParLeTest()
    {
        var moment = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
        var clock = new FixedClock(moment);

        clock.UtcNow = moment.AddHours(2);

        // C'est le test qui decide quand l'heure change, et de combien. Un double qui
        // prendrait cette decision tout seul reintroduirait le non-determinisme qu'on
        // cherchait a supprimer.
        Assert.Equal(moment.AddHours(2), clock.UtcNow);
    }
}
