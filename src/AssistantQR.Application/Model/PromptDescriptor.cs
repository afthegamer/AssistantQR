namespace AssistantQR.Application.Model;

/// <summary>
/// Identite d'un gabarit sans son corps. C'est ce qui voyage dans les traces et les
/// instantanes : conserver le texte complet du prompt dans chaque enregistrement
/// serait illisible, ne conserver que le nom serait insuffisant (on modifie un prompt
/// sans toujours en changer la version). L'empreinte tranche le doute.
/// </summary>
public sealed record PromptDescriptor(string Name, string Version, string Fingerprint);
