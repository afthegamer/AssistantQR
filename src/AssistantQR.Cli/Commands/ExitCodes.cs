namespace AssistantQR.Cli.Commands;

/// <summary>
/// Les trois issues possibles d'une invocation, telles que les lit un script.
/// </summary>
/// <remarks>
/// Un refus de repondre n'est PAS une erreur et rend donc <see cref="Success"/>. C'est
/// une decision du systeme, prise par la politique du Domain, et le cas nominal d'un
/// assistant qui doit citer ses sources : confondre « je n'ai pas le droit de repondre »
/// avec « quelque chose a casse » ferait echouer un script de demonstration sur le
/// comportement que la demonstration cherche justement a montrer.
/// </remarks>
internal static class ExitCodes
{
    /// <summary>La commande a fait ce qu'on lui demandait, refus de reponse compris.</summary>
    public const int Success = 0;

    /// <summary>La commande tapee n'a pas de sens.</summary>
    public const int Usage = 1;

    /// <summary>La commande avait du sens mais n'a pas pu aboutir.</summary>
    public const int Execution = 2;
}
