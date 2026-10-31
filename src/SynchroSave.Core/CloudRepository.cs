using System.Globalization;
using SynchroSave.Core.Models;

namespace SynchroSave.Core;

/// <summary>
/// Organisation des sauvegardes dans le dossier OneDrive :
/// <code>
/// OneDrive\SynchroSave\Games\&lt;id-du-jeu&gt;\manifest.json   (description de la dernière version)
/// OneDrive\SynchroSave\Games\&lt;id-du-jeu&gt;\Current\...       (copie à jour des fichiers)
/// OneDrive\SynchroSave\Games\&lt;id-du-jeu&gt;\Versions\*.zip     (historique)
/// </code>
/// Le client OneDrive de Windows se charge d'envoyer ces fichiers dans le cloud.
/// </summary>
public sealed class CloudRepository
{
    private const string StampFormat = "yyyyMMdd-HHmmss";

    public CloudRepository(string oneDriveRoot, string cloudFolderName)
    {
        Root = Path.Combine(oneDriveRoot, cloudFolderName);
    }

    public string Root { get; }
    public string GamesRoot => Path.Combine(Root, "Games");

    public string GameDir(string id) => Path.Combine(GamesRoot, id);
    public string CurrentDir(string id) => Path.Combine(GameDir(id), "Current");
    public string VersionsDir(string id) => Path.Combine(GameDir(id), "Versions");
    public string ManifestFile(string id) => Path.Combine(GameDir(id), "manifest.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(GamesRoot);
        var readme = Path.Combine(Root, "LISEZ-MOI.txt");
        if (!File.Exists(readme))
        {
            File.WriteAllText(readme,
                "Ce dossier est géré par SynchroSave.\r\n" +
                "Chaque sous-dossier de 'Games' contient les sauvegardes d'un jeu :\r\n" +
                "  - Current  : la dernière version des fichiers de sauvegarde\r\n" +
                "  - Versions : les anciennes versions (fichiers .zip)\r\n" +
                "  - manifest.json : informations utilisées par l'application\r\n" +
                "Ne modifiez pas ces fichiers à la main.\r\n");
        }
    }

    public CloudManifest? ReadManifest(string id)
    {
        try { return JsonFile.Read<CloudManifest>(ManifestFile(id)); }
        catch (Exception ex)
        {
            AppLog.Warn($"Manifest illisible pour {id} : {ex.Message}");
            return null;
        }
    }

    public void WriteManifest(CloudManifest manifest) => JsonFile.Write(ManifestFile(manifest.GameId), manifest);

    /// <summary>Tous les jeux présents dans OneDrive (déposés par n'importe quel PC).</summary>
    public List<CloudManifest> ListGames()
    {
        var list = new List<CloudManifest>();
        if (!Directory.Exists(GamesRoot)) return list;
        foreach (var dir in Directory.GetDirectories(GamesRoot))
        {
            var m = ReadManifest(Path.GetFileName(dir));
            if (m != null) list.Add(m);
        }
        return list.OrderBy(m => m.GameName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Archive le contenu actuel de Current dans Versions (zip), puis applique la rotation.</summary>
    public string? ArchiveCurrent(string id, string machine, string reason, int keep)
    {
        if (keep <= 0) return null;
        var current = CurrentDir(id);
        if (!Directory.Exists(current) || !Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories).Any())
            return null;

        var manifest = ReadManifest(id);
        var when = manifest?.UpdatedUtc ?? DateTime.UtcNow;
        var sourceMachine = manifest?.SourceMachine ?? machine;
        var zip = Path.Combine(VersionsDir(id), BuildVersionName(when, sourceMachine, reason));
        FileOps.ZipDirectory(current, zip);
        PruneVersions(id, keep);
        return zip;
    }

    /// <summary>Ajoute un dossier local comme version (ex: perdant d'un conflit).</summary>
    public string? ArchiveFolder(string id, string folder, string machine, string reason, int keep)
    {
        if (keep <= 0 || !Directory.Exists(folder)) return null;
        var zip = Path.Combine(VersionsDir(id), BuildVersionName(DateTime.UtcNow, machine, reason));
        FileOps.ZipDirectory(folder, zip);
        PruneVersions(id, keep);
        return zip;
    }

    public List<CloudVersion> ListVersions(string id)
    {
        var result = new List<CloudVersion>();
        var dir = VersionsDir(id);
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.GetFiles(dir, "*.zip"))
        {
            var v = ParseVersionName(f);
            if (v != null) result.Add(v);
        }
        return result.OrderByDescending(v => v.CreatedUtc).ToList();
    }

    public void PruneVersions(string id, int keep)
    {
        foreach (var old in ListVersions(id).Skip(Math.Max(0, keep)))
        {
            try { FileOps.DeleteFile(old.FilePath); }
            catch (Exception ex) { AppLog.Warn($"Impossible de supprimer l'ancienne version {old.FilePath} : {ex.Message}"); }
        }
    }

    public void DeleteGame(string id)
    {
        var dir = GameDir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    internal static string BuildVersionName(DateTime utc, string machine, string reason) =>
        $"{utc.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture)}__{FileOps.SafeName(machine)}__{FileOps.SafeName(reason)}.zip";

    internal static CloudVersion? ParseVersionName(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var parts = name.Split("__");
        if (parts.Length < 1) return null;
        if (!DateTime.TryParseExact(parts[0], StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            return null;
        long size = 0;
        try { size = new FileInfo(file).Length; } catch { /* ignore */ }
        return new CloudVersion
        {
            FilePath = file,
            CreatedUtc = DateTime.SpecifyKind(when, DateTimeKind.Utc),
            Machine = parts.Length > 1 ? parts[1] : "",
            Reason = parts.Length > 2 ? parts[2] : "",
            Size = size,
        };
    }
}
