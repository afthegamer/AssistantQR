namespace AssistantQR.Infrastructure.LanguageModels;

/// <summary>
/// Une entree du jeu de rejeu : si <see cref="Match"/> apparait dans le prompt, le
/// modele sert <see cref="Response"/> sans rien calculer.
/// </summary>
/// <remarks>
/// Le format volontairement pauvre — une sous-chaine, une reponse — est un choix
/// pedagogique : un jeu de rejeu doit rester lisible et modifiable a la main par un
/// etudiant qui veut voir l'effet d'une reponse mal citee sur la politique metier.
/// Un appariement plus savant (expressions regulieres, empreinte du prompt complet)
/// serait plus fidele mais illisible dans un fichier JSON de dix lignes.
/// </remarks>
public sealed record ReplayEntry(string Match, string Response);
