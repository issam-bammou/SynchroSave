using SynchroSave.Core.Models;

namespace SynchroSave.Core;

/// <summary>Synchronise un jeu entre son dossier local et OneDrive.</summary>
public sealed class SyncEngine
{
    private readonly AppPaths _paths;
    private readonly SyncStateStore _states;

    public SyncEngine(AppPaths paths, SyncStateStore states)
    {
        _paths = paths;
        _states = states;
    }

    public SyncResult SyncGame(GameEntry game, AppSettings settings, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(settings.OneDriveRoot) || !Directory.Exists(settings.OneDriveRoot))
                return Result(game, SyncAction.Error, "Dossier OneDrive introuvable. Vérifiez les paramètres.");

            var repo = new CloudRepository(settings.OneDriveRoot, settings.CloudFolderName);
            repo.EnsureCreated();

            var local = FileScanner.Scan(game.LocalPath, settings.ExcludePatterns);
            var cloud = repo.ReadManifest(game.Id);
            var state = _states.Load(game.Id);

            // Rien en local
            if (local.Count == 0)
            {
                if (cloud == null)
                    return Result(game, SyncAction.None, Directory.Exists(game.LocalPath)
                        ? "Aucun fichier de sauvegarde pour l'instant."
                        : "Dossier de sauvegarde introuvable sur ce PC.");

                if (settings.Mode == SyncMode.TwoWay && cloud.Files.Count > 0)
                    return Download(game, settings, repo, cloud, "Sauvegarde récupérée depuis OneDrive.", SyncAction.Downloaded);

                // On ne vide jamais OneDrive à cause d'un dossier local vide.
                return Result(game, SyncAction.Skipped, "Dossier local vide : la copie OneDrive est conservée.");
            }

            if (cloud == null)
                return Upload(game, settings, repo, local, null, "Première sauvegarde envoyée vers OneDrive.", SyncAction.Uploaded);

            if (FileScanner.SameFiles(local, cloud.Files))
            {
                SaveState(game.Id, local, cloud.Revision);
                return Result(game, SyncAction.None, "À jour.");
            }

            var localChanged = state == null || !FileScanner.SameFiles(local, state.LocalFiles);
            var cloudChanged = state == null || state.CloudRevision != cloud.Revision;

            if (settings.Mode == SyncMode.BackupOnly)
            {
                // Un autre PC a envoyé une version plus récente et ce PC n'a rien de nouveau :
                // on n'écrase pas OneDrive avec une sauvegarde plus ancienne.
                var cloudIsNewer = (cloud.LatestFileWriteUtc ?? cloud.UpdatedUtc) > local.Max(f => f.LastWriteUtc);
                if (cloudChanged && (!localChanged || (state == null && cloudIsNewer)))
                {
                    return Result(game, SyncAction.Skipped,
                        $"OneDrive contient une version plus récente (de {cloud.SourceMachine}). " +
                        "Utilisez l'onglet « Restaurer » pour la récupérer sur ce PC.");
                }

                var msg = cloudChanged && cloud.SourceMachine != settings.MachineName
                    ? $"Envoyé vers OneDrive (la version de {cloud.SourceMachine} a été archivée)."
                    : "Modifications envoyées vers OneDrive.";
                return Upload(game, settings, repo, local, cloud, msg, SyncAction.Uploaded);
            }

            // Mode bidirectionnel
            if (localChanged && !cloudChanged)
                return Upload(game, settings, repo, local, cloud, "Modifications envoyées vers OneDrive.", SyncAction.Uploaded);

            if (!localChanged && cloudChanged)
                return Download(game, settings, repo, cloud,
                    $"Version plus récente récupérée depuis OneDrive ({cloud.SourceMachine}).", SyncAction.Downloaded);

            // Conflit : les deux côtés ont changé. Le plus récent gagne, l'autre est archivé.
            var localLatest = local.Max(f => f.LastWriteUtc);
            var cloudLatest = cloud.LatestFileWriteUtc ?? cloud.UpdatedUtc;
            if (localLatest >= cloudLatest)
            {
                return Upload(game, settings, repo, local, cloud,
                    $"Conflit : la version de ce PC est plus récente, celle de {cloud.SourceMachine} a été archivée dans les versions.",
                    SyncAction.ConflictResolvedUpload, archiveReason: "conflit");
            }

            repo.ArchiveFolder(game.Id, game.LocalPath, settings.MachineName, "conflit", Math.Max(1, settings.VersionsToKeep));
            return Download(game, settings, repo, cloud,
                $"Conflit : la version de {cloud.SourceMachine} est plus récente, celle de ce PC a été archivée dans les versions.",
                SyncAction.ConflictResolvedDownload);
        }
        catch (OperationCanceledException)
        {
            return Result(game, SyncAction.Skipped, "Annulé.");
        }
        catch (IOException ex)
        {
            return Result(game, SyncAction.Error, $"Fichier inaccessible (jeu en cours ?), nouvel essai plus tard : {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result(game, SyncAction.Error, ex.Message);
        }
    }

    private SyncResult Upload(GameEntry game, AppSettings settings, CloudRepository repo, List<FileEntry> local,
        CloudManifest? previous, string message, SyncAction action, string archiveReason = "avant-mise-a-jour")
    {
        var current = repo.CurrentDir(game.Id);
        Directory.CreateDirectory(current);

        if (previous != null)
            repo.ArchiveCurrent(game.Id, settings.MachineName, archiveReason,
                archiveReason == "conflit" ? Math.Max(1, settings.VersionsToKeep) : settings.VersionsToKeep);

        var cloudFiles = FileScanner.Scan(current).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        foreach (var f in local)
        {
            if (cloudFiles.TryGetValue(f.Path, out var existing) && FileScanner.SameFile(existing, f)) continue;
            FileOps.CopyFile(FileScanner.ToFull(game.LocalPath, f.Path), FileScanner.ToFull(current, f.Path), f.LastWriteUtc);
            copied++;
        }

        var localSet = local.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in cloudFiles.Keys.Where(p => !localSet.Contains(p)))
            FileOps.DeleteFile(FileScanner.ToFull(current, extra));
        FileOps.DeleteEmptyDirectories(current);

        var manifest = new CloudManifest
        {
            GameId = game.Id,
            GameName = game.Name,
            PathTemplate = game.PathTemplate,
            Revision = Guid.NewGuid().ToString("N"),
            UpdatedUtc = DateTime.UtcNow,
            SourceMachine = settings.MachineName,
            Files = local,
        };
        repo.WriteManifest(manifest);
        SaveState(game.Id, local, manifest.Revision);
        return Result(game, action, message, copied);
    }

    private SyncResult Download(GameEntry game, AppSettings settings, CloudRepository repo, CloudManifest cloud,
        string message, SyncAction action)
    {
        var copied = RestoreFolderFromCloud(repo, cloud, game.LocalPath, settings, game.Id);
        var local = FileScanner.Scan(game.LocalPath, settings.ExcludePatterns);
        SaveState(game.Id, local, cloud.Revision);
        return Result(game, action, message, copied);
    }

    /// <summary>Copie Current (OneDrive) vers le dossier local, après une sauvegarde de sécurité locale.</summary>
    internal int RestoreFolderFromCloud(CloudRepository repo, CloudManifest cloud, string target, AppSettings settings, string gameId)
    {
        var current = repo.CurrentDir(cloud.GameId);
        if (!Directory.Exists(current))
            throw new DirectoryNotFoundException("Les fichiers de la sauvegarde sont introuvables dans OneDrive (synchronisation OneDrive en cours ?).");

        // Vérifie que OneDrive a bien fini de télécharger les fichiers attendus.
        var available = FileScanner.Scan(current);
        var missing = cloud.Files.Where(f => !available.Any(a => a.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count > 0)
            throw new IOException($"{missing.Count} fichier(s) pas encore synchronisé(s) par OneDrive sur ce PC.");

        CreateLocalSafetyBackup(gameId, target, settings.LocalSafetyBackupsToKeep);

        Directory.CreateDirectory(target);
        var localFiles = FileScanner.Scan(target, settings.ExcludePatterns).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        foreach (var f in cloud.Files)
        {
            if (localFiles.TryGetValue(f.Path, out var existing) && FileScanner.SameFile(existing, f)) continue;
            FileOps.CopyFile(FileScanner.ToFull(current, f.Path), FileScanner.ToFull(target, f.Path), f.LastWriteUtc);
            copied++;
        }
        var cloudSet = cloud.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in localFiles.Keys.Where(p => !cloudSet.Contains(p)))
            FileOps.DeleteFile(FileScanner.ToFull(target, extra));
        return copied;
    }

    /// <summary>Zip du dossier local avant de l'écraser (dans %LOCALAPPDATA%\SynchroSave\LocalBackups).</summary>
    public string? CreateLocalSafetyBackup(string gameId, string folder, int keep)
    {
        if (keep <= 0 || !Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
            return null;
        var dir = Path.Combine(_paths.LocalBackupsDirectory, gameId);
        var zip = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        FileOps.ZipDirectory(folder, zip);
        foreach (var old in Directory.GetFiles(dir, "*.zip").OrderByDescending(f => f).Skip(keep))
        {
            try { File.Delete(old); } catch { /* ignore */ }
        }
        return zip;
    }

    private void SaveState(string gameId, List<FileEntry> local, string revision) =>
        _states.Save(new SyncState { GameId = gameId, CloudRevision = revision, LocalFiles = local, SyncedUtc = DateTime.UtcNow });

    private static SyncResult Result(GameEntry game, SyncAction action, string message, int files = 0) =>
        new() { Game = game, Action = action, Message = message, FilesTransferred = files };
}
