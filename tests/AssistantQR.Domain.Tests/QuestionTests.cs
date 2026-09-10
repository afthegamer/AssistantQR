using AssistantQR.Domain.Questions;
using Xunit;

namespace AssistantQR.Domain.Tests;

/// <summary>
/// Question est un record a constructeur prive : la fabrique est la seule porte
/// d'entree. Avec un record positionnel, un appelant pourrait fabriquer une question
/// vide a partir d'une question valide via <c>with</c>, et la validation ne vaudrait
/// plus rien. Le test de bornes ci-dessous documente ce choix autant qu'il le verifie.
/// </summary>
public sealed class QuestionTests
{
    [Fact]
    public void From_TexteNormal_ConserveLeTexte()
    {
        var question = Question.From("Quels sont les horaires du samedi ?");

        Assert.Equal("Quels sont les horaires du samedi ?", question.Text);
        Assert.Equal("Quels sont les horaires du samedi ?", question.ToString());
    }

    [Fact]
    public void From_TexteEntoureDeBlancs_Trime()
    {
        Assert.Equal("Une question", Question.From("\n\t  Une question   \n").Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  ")]
    public void From_TexteVideOuBlanc_LeveDomainException(string text)
    {
        Assert.Throws<DomainException>(() => Question.From(text));
    }

    [Fact]
    public void From_TexteNul_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => Question.From(null!));
    }

    [Fact]
    public void From_DeuxMilleCaracteres_EstAccepte()
    {
        var limite = new string('q', 2000);

        Assert.Equal(2000, Question.From(limite).Text.Length);
    }

    [Fact]
    public void From_PlusDeDeuxMilleCaracteres_LeveDomainException()
    {
        Assert.Throws<DomainException>(() => Question.From(new string('q', 2001)));
    }

    /// <summary>Les blancs sont retires avant la mesure : la borne porte sur le texte utile.</summary>
    [Fact]
    public void From_LimiteAtteinteApresTrim_EstAccepte()
    {
        var question = Question.From("   " + new string('q', 2000) + "   ");

        Assert.Equal(2000, question.Text.Length);
    }

    /// <summary>Value object : deux questions de meme texte normalise sont la meme question.</summary>
    [Fact]
    public void Egalite_MemeTexteApresNormalisation_QuestionsEgales()
    {
        Assert.Equal(
            Question.From("Combien coûte l'abonnement ?"),
            Question.From("  Combien coûte l'abonnement ?  "));

        Assert.NotEqual(
            Question.From("Combien coûte l'abonnement ?"),
            Question.From("Où se trouve l'espace jeunesse ?"));
    }
}
