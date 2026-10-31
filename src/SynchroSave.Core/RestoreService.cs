using SynchroSave.Core.Models;

namespace SynchroSave.Core;

/// <summary>Récupère des sauvegardes depuis OneDrive vers ce PC (dernière version ou version archivée).</summary>
public sealed class RestoreService
{
    private readonly SyncEngine _engine;
    private readonly SyncStateStore _states;
    private readonly PathResolver _resolver;

    public RestoreService(SyncEngine engine, SyncStateStore states, PathResolver resolver)
    {
        _engine = engine;
        _states = states;
        _resolver = resolver;
    }

    /// <summary>Emplacement proposé sur ce PC pour une sauvegarde venant de OneDrive.</summary>
    public string SuggestTarget(CloudManifest manifest, AppSettings settings)
    {
        var known = settings.Games.FirstOrDefault(g => g.Id == manifest.GameId);
        if (known != null && !string.IsNullOrWhiteSpace(known.LocalPath)) return known.LocalPath;

        var existing = _resolver.ResolveExisting(manifest.PathTemplate).FirstOrDefault();
        if (existing != null) return existing;

        var expanded = _resolver.Expand(manifest.PathTemplate);
        return expanded.Contains('*') || expanded.Contains('{') ? string.Empty : expanded;
    }

    /// <summary>
    /// Restaure un jeu. <paramref name="version"/> = null pour la dernière version.
    /// Le dossier local actuel est d'abord sauvegardé en zip. Le jeu est ensuite ajouté/mis à jour dans la liste suivie.
    /// </summary>
    public GameEntry Restore(CloudManifest manifest, CloudVersion? version, string targetFolder, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(targetFolder))
            throw new ArgumentException("Aucun dossier de destination choisi.");

        var repo = new CloudRepository(settings.OneDriveRoot, settings.CloudFolderName);

        if (version == null || version.IsLatest)
        {
            _engine.RestoreFolderFromCloud(repo, manifest, targetFolder, settings, manifest.GameId);
        }
        else
        {
            _engine.CreateLocalSafetyBackup(manifest.GameId, targetFolder, settings.LocalSafetyBackupsToKeep);
            Directory.CreateDirectory(targetFolder);
            var written = FileOps.ExtractZip(version.FilePath, targetFolder)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in FileScanner.Scan(targetFolder, settings.ExcludePatterns).Where(f => !written.Contains(f.Path)))
                FileOps.DeleteFile(FileScanner.ToFull(targetFolder, f.Path));
        }

        var local = FileScanner.Scan(targetFolder, settings.ExcludePatterns);
        // Dernière version : ce PC est à jour. Ancienne version : elle deviendra la version courante à la prochaine synchro.
        _states.Save(new SyncState
        {
            GameId = manifest.GameId,
            CloudRevision = manifest.Revision,
            LocalFiles = version == null || version.IsLatest ? local : new List<FileEntry>(),
            SyncedUtc = DateTime.UtcNow,
        });

        var game = settings.Games.FirstOrDefault(g => g.Id == manifest.GameId);
        if (game == null)
        {
            game = new GameEntry { Id = manifest.GameId, IsCustom = true };
            settings.Games.Add(game);
        }
        game.Name = manifest.GameName;
        game.LocalPath = targetFolder;
        game.PathTemplate = string.IsNullOrWhiteSpace(manifest.PathTemplate) ? _resolver.ToTemplate(targetFolder) : manifest.PathTemplate;
        game.Enabled = true;
        game.LastSyncUtc = DateTime.UtcNow;
        game.LastStatus = version == null || version.IsLatest ? "Restauré depuis OneDrive" : $"Version du {version.CreatedUtc.ToLocalTime():g} restaurée";
        return game;
    }
}
