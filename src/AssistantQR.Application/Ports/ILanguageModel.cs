using AssistantQR.Application.Model;

namespace AssistantQR.Application.Ports;

/// <summary>Appel a un modele de langue.</summary>
/// <remarks>
/// POURQUOI LA FRONTIERE EST ICI — le port le plus important du projet.
/// Elle est etroite a dessein : une chaine entre, une chaine sort. Pas de
/// « conversation », pas de « chaine d'agents », pas de « memoire », pas d'outils.
/// Chacune de ces abstractions plus riches ferait entrer dans l'Application des
/// concepts appartenant a un fournisseur donne, et le jour ou l'on change de
/// fournisseur, c'est le pipeline entier qu'il faudrait reecrire.
///
/// CE QUE CETTE ETROITESSE PROTEGE. Le modele de langue est le seul composant du
/// systeme dont on ne peut pas garantir la sortie. Le confiner derriere une interface
/// a une methode, c'est se donner le droit de ne jamais lui faire confiance : ce qu'il
/// rend est une PROPOSITION, analysee par le parseur, puis jugee par la politique du
/// Domain. Toutes les regles de securite du systeme passent APRES ce port, jamais
/// dedans. C'est pour cela qu'on peut tester exhaustivement le comportement de
/// l'assistant — y compris ses refus — sans jamais appeler un modele.
///
/// POURQUOI <c>ModelId</c> EST EXPOSE. Il entre dans l'empreinte de configuration des
/// instantanes. Une derive de reponses apres un changement de modele doit être
/// attribuable a ce changement, pas laissee au diagnostic a vue.
/// </remarks>
public interface ILanguageModel
{
    /// <summary>Identifiant du modele, tel qu'il figurera dans les traces et les instantanes.</summary>
    string ModelId { get; }

    /// <summary>Une requete, une completion. Rien d'autre ne traverse cette frontiere.</summary>
    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
