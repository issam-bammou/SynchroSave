using SynchroSave.Core.Models;

namespace SynchroSave.Core;

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths) => _paths = paths;

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                var s = JsonFile.Read<AppSettings>(_paths.SettingsFile) ?? new AppSettings();
                Sanitize(s);
                return s;
            }
            catch (Exception ex)
            {
                AppLog.Error("Paramètres illisibles, valeurs par défaut utilisées", ex);
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Sanitize(settings);
            JsonFile.Write(_paths.SettingsFile, settings);
        }
    }

    private static void Sanitize(AppSettings s)
    {
        s.SyncIntervalMinutes = Math.Clamp(s.SyncIntervalMinutes, 1, 24 * 60);
        s.VersionsToKeep = Math.Clamp(s.VersionsToKeep, 0, 500);
        s.LocalSafetyBackupsToKeep = Math.Clamp(s.LocalSafetyBackupsToKeep, 0, 100);
        s.SyncOnChangeDelaySeconds = Math.Clamp(s.SyncOnChangeDelaySeconds, 5, 3600);
        s.OnlineDatabaseUpdateDays = Math.Clamp(s.OnlineDatabaseUpdateDays, 1, 365);
        if (string.IsNullOrWhiteSpace(s.CloudFolderName)) s.CloudFolderName = "SynchroSave";
        if (string.IsNullOrWhiteSpace(s.MachineName)) s.MachineName = Environment.MachineName;
        s.Games ??= new List<GameEntry>();
    }
}

public sealed class SyncStateStore
{
    private readonly AppPaths _paths;

    public SyncStateStore(AppPaths paths) => _paths = paths;

    private string FileFor(string gameId) => Path.Combine(_paths.StateDirectory, gameId + ".json");

    public SyncState? Load(string gameId)
    {
        try { return JsonFile.Read<SyncState>(FileFor(gameId)); }
        catch { return null; }
    }

    public void Save(SyncState state) => JsonFile.Write(FileFor(state.GameId), state);

    public void Delete(string gameId)
    {
        try { File.Delete(FileFor(gameId)); } catch { /* ignore */ }
    }
}
