namespace AssistantQR.Application.UseCases.Snapshots;

/// <summary>
/// Nature d'une divergence entre deux instantanes, de la plus grave a la plus benigne.
/// Toutes les derives ne se valent pas : une reformulation est du bruit attendu d'un
/// modele de langue, un refus qui devient une reponse est un changement de
/// comportement. Les distinguer evite de noyer le second dans le premier.
/// </summary>
public enum DifferenceKind
{
    /// <summary>Rien n'a bouge.</summary>
    Identical = 0,

    /// <summary>Mêmes sources, même decision, texte reformule. La derive la plus benigne.</summary>
    AnswerTextChanged = 1,

    /// <summary>Les sources citees ont change : la reponse ne s'appuie plus sur le même materiau.</summary>
    CitationsChanged = 2,

    /// <summary>On repondait et on refuse, ou l'inverse, ou le motif de refus a change. Le plus grave.</summary>
    RefusalChanged = 3,

    /// <summary>La question n'existait pas dans l'instantane de reference.</summary>
    MissingInBaseline = 4,

    /// <summary>La question a disparu de l'instantane candidat.</summary>
    MissingInCandidate = 5,
}
