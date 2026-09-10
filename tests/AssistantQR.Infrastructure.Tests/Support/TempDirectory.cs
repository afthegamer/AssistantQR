using System.Text;

namespace AssistantQR.Infrastructure.Tests.Support;

/// <summary>
/// Dossier temporaire, cree au constructeur et efface a la liberation.
/// </summary>
/// <remarks>
/// POURQUOI DES VRAIS FICHIERS DANS DES TESTS DITS « UNITAIRES ». Les adaptateurs de ce
/// dossier ont pour raison d'etre de parler au systeme de fichiers : les tester contre
/// une abstraction du disque reviendrait a tester l'abstraction. Ici, le systeme de
/// fichiers EST le sujet, pas une dependance genante.
///
/// La contrepartie — un test qui touche le disque est plus lent et peut echouer pour des
/// raisons etrangeres a son objet — est acceptee, mais bornee : chaque test recoit son
/// propre dossier tire au hasard, donc aucun ordre d'execution ni aucune execution
/// parallele ne peut les faire interferer.
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public TempDirectory(string label)
    {
        FullPath = Path.Combine(Path.GetTempPath(), "assistantqr-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(FullPath);
    }

    /// <summary>Chemin absolu du dossier.</summary>
    public string FullPath { get; }

    /// <summary>Ecrit un fichier UTF-8 sans BOM et rend son chemin absolu.</summary>
    public string Write(string relativeName, string content)
    {
        var full = Path.Combine(FullPath, relativeName);
        var parent = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(full, content, Utf8WithoutBom);
        return full;
    }

    /// <summary>Chemin absolu d'un element du dossier, existant ou non.</summary>
    public string Combine(string relativeName) => Path.Combine(FullPath, relativeName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Un verrou residuel sur un fichier ne doit pas faire echouer un test qui a
            // deja rendu son verdict : le dossier temporaire finira par etre nettoye.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
