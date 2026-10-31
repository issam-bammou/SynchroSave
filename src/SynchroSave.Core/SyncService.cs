using SynchroSave.Core.Models;

namespace SynchroSave.Core;

/// <summary>
/// Cœur de l'application : détient les paramètres, lance la synchro périodique
/// (fréquence paramétrable) et la synchro déclenchée par les changements de fichiers.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly SettingsStore _store;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingGames = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private Timer? _debounce;
    private bool _paused;

    public SyncService(AppPaths paths, PathResolver? resolver = null)
    {
        Paths = paths;
        _store = new SettingsStore(paths);
        States = new SyncStateStore(paths);
        Resolver = resolver ?? new PathResolver();
        Engine = new SyncEngine(paths, States);
        Restore = new RestoreService(Engine, States, Resolver);
        Catalog = new GameCatalog(Resolver, paths.CustomGamesFile);
        OnlineDatabase = new OnlineGameDatabase(paths);
        Settings = _store.Load();
    }

    public OnlineGameDatabase OnlineDatabase { get; }

    /// <summary>Déclenché quand la base de jeux en ligne a été chargée ou mise à jour.</summary>
    public event Action? CatalogChanged;

    private readonly SemaphoreSlim _onlineLock = new(1, 1);

    /// <summary>Charge la base en ligne déjà téléchargée, puis la met à jour si nécessaire (en arrière-plan).</summary>
    public async Task InitializeOnlineDatabaseAsync()
    {
        if (!Settings.OnlineDatabaseEnabled) return;
        try
        {
            if (OnlineDatabase.IsAvailable)
            {
                await Task.Run(() => Catalog.SetOnlineDefinitions(OnlineDatabase.Load())).ConfigureAwait(false);
                CatalogChanged?.Invoke();
            }
            if (OnlineDatabase.IsUpdateDue(Settings.OnlineDatabaseUpdateDays))
                await UpdateOnlineDatabaseAsync(force: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Base de jeux en ligne indisponible : " + ex.Message);
        }
    }

    /// <summary>Télécharge la base en ligne si elle n'a jamais été téléchargée (avant une détection).</summary>
    public async Task EnsureOnlineDatabaseAsync(IProgress<string>? progress = null)
    {
        if (!Settings.OnlineDatabaseEnabled || (OnlineDatabase.IsAvailable && Catalog.OnlineCount > 0)) return;
        if (OnlineDatabase.IsAvailable)
        {
            await Task.Run(() => Catalog.SetOnlineDefinitions(OnlineDatabase.Load())).ConfigureAwait(false);
            CatalogChanged?.Invoke();
            return;
        }
        await UpdateOnlineDatabaseAsync(force: false, progress).ConfigureAwait(false);
    }

    /// <summary>Vérifie/télécharge la dernière version de la base en ligne. Ne lève pas d'exception.</summary>
    public async Task<bool> UpdateOnlineDatabaseAsync(bool force, IProgress<string>? progress = null)
    {
        await _onlineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var updated = await OnlineDatabase.UpdateAsync(force, progress).ConfigureAwait(false);
            if (updated || Catalog.OnlineCount == 0)
            {
                await Task.Run(() => Catalog.SetOnlineDefinitions(OnlineDatabase.Load())).ConfigureAwait(false);
                CatalogChanged?.Invoke();
            }
            if (updated) AppLog.Success($"Base de jeux en ligne mise à jour : {Catalog.OnlineCount:N0} jeux avec emplacements de sauvegarde.");
            return updated;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Mise à jour de la base de jeux en ligne impossible (pas d'internet ?) : " + ex.Message);
            progress?.Report("Base en ligne indisponible : " + ex.Message);
            return false;
        }
        finally
        {
            _onlineLock.Release();
        }
    }

    /// <summary>Verrou à prendre pour modifier <see cref="Settings"/> depuis l'interface.</summary>
    public object Gate { get; } = new();

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public SyncStateStore States { get; }
    public PathResolver Resolver { get; }
    public SyncEngine Engine { get; }
    public RestoreService Restore { get; }
    public GameCatalog Catalog { get; }

    public bool IsSyncing { get; private set; }
    public DateTime? NextRunUtc { get; private set; }
    public DateTime? LastRunUtc { get; private set; }

    public bool IsPaused
    {
        get => _paused;
        set
        {
            _paused = value;
            AppLog.Info(value ? "Synchronisation automatique en pause." : "Synchronisation automatique reprise.");
            Reschedule();
        }
    }

    public event Action? SyncStarted;
    public event Action<IReadOnlyList<SyncResult>>? SyncCompleted;
    public event Action<SyncResult>? GameSynced;
    public event Action? ScheduleChanged;

    public CloudRepository? CloudRepository =>
        string.IsNullOrWhiteSpace(Settings.OneDriveRoot) ? null : new CloudRepository(Settings.OneDriveRoot, Settings.CloudFolderName);

    public void SaveSettings()
    {
        lock (Gate) _store.Save(Settings);
    }

    /// <summary>À appeler après une modification des paramètres ou de la liste des jeux.</summary>
    public void ApplySettings()
    {
        SaveSettings();
        Reschedule();
        RefreshWatchers();
    }

    public void Start()
    {
        Reschedule();
        RefreshWatchers();
        _ = Task.Run(InitializeOnlineDatabaseAsync);
    }

    private void Reschedule()
    {
        _timer?.Dispose();
        _timer = null;
        NextRunUtc = null;
        if (Settings.AutoSyncEnabled && !_paused)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, Settings.SyncIntervalMinutes));
            NextRunUtc = DateTime.UtcNow + interval;
            _timer = new Timer(_ => _ = RunScheduledAsync(), null, interval, interval);
        }
        ScheduleChanged?.Invoke();
    }

    private async Task RunScheduledAsync()
    {
        NextRunUtc = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(1, Settings.SyncIntervalMinutes));
        ScheduleChanged?.Invoke();
        await SyncAllAsync(automatic: true).ConfigureAwait(false);
    }

    /// <summary>Synchronise tous les jeux activés (ou seulement ceux indiqués).</summary>
    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(bool automatic = false, IEnumerable<string>? onlyIds = null,
        CancellationToken ct = default)
    {
        if (automatic && _paused) return Array.Empty<SyncResult>();
        if (!await _syncLock.WaitAsync(automatic ? 0 : Timeout.Infinite, ct).ConfigureAwait(false))
            return Array.Empty<SyncResult>();

        var results = new List<SyncResult>();
        try
        {
            IsSyncing = true;
            SyncStarted?.Invoke();
            List<GameEntry> games;
            AppSettings snapshot;
            lock (Gate)
            {
                var filter = onlyIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
                games = Settings.Games.Where(g => g.Enabled && (filter == null || filter.Contains(g.Id))).ToList();
                snapshot = Settings;
            }

            if (games.Count > 0)
                AppLog.Info(automatic ? $"Synchronisation automatique de {games.Count} jeu(x)..." : $"Synchronisation de {games.Count} jeu(x)...");

            foreach (var game in games)
            {
                if (ct.IsCancellationRequested) break;
                var r = await Task.Run(() => Engine.SyncGame(game, snapshot, ct), ct).ConfigureAwait(false);
                lock (Gate)
                {
                    game.LastStatus = r.Message;
                    if (!r.IsError && r.Action != SyncAction.Skipped) game.LastSyncUtc = DateTime.UtcNow;
                }
                Log(r);
                results.Add(r);
                GameSynced?.Invoke(r);
            }
            LastRunUtc = DateTime.UtcNow;
            SaveSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("Erreur pendant la synchronisation", ex);
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
            SyncCompleted?.Invoke(results);
        }
        return results;
    }

    /// <summary>Exécute une opération (ex: restauration) sans qu'une synchro tourne en même temps.</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<T> work)
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            IsSyncing = true;
            return await Task.Run(work).ConfigureAwait(false);
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
        }
    }

    private static void Log(SyncResult r)
    {
        var text = $"{r.Game.Name} : {r.Message}" + (r.FilesTransferred > 0 ? $" ({r.FilesTransferred} fichier(s))" : "");
        switch (r.Action)
        {
            case SyncAction.Error: AppLog.Error(text); break;
            case SyncAction.Skipped: AppLog.Warn(text); break;
            case SyncAction.None: AppLog.Info(text); break;
            default: AppLog.Success(text); break;
        }
    }

    // ---- Surveillance des dossiers de sauvegarde ----

    private void RefreshWatchers()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
            if (!Settings.SyncOnChange) return;

            List<GameEntry> games;
            lock (Gate) games = Settings.Games.Where(g => g.Enabled).ToList();
            foreach (var g in games)
            {
                if (!Directory.Exists(g.LocalPath)) continue;
                try
                {
                    var w = new FileSystemWatcher(g.LocalPath)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    };
                    var id = g.Id;
                    FileSystemEventHandler h = (_, _) => OnGameFilesChanged(id);
                    w.Changed += h;
                    w.Created += h;
                    w.Deleted += h;
                    w.Renamed += (_, _) => OnGameFilesChanged(id);
                    w.EnableRaisingEvents = true;
                    _watchers[g.Id] = w;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Surveillance impossible pour {g.Name} : {ex.Message}");
                }
            }
        }
    }

    private void OnGameFilesChanged(string gameId)
    {
        if (_paused) return;
        lock (_pendingGames)
        {
            _pendingGames.Add(gameId);
            var delay = TimeSpan.FromSeconds(Math.Max(5, Settings.SyncOnChangeDelaySeconds));
            // On attend que le jeu ait fini d'écrire (aucun changement pendant "delay").
            _debounce ??= new Timer(_ => _ = FlushPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task FlushPendingAsync()
    {
        List<string> ids;
        lock (_pendingGames)
        {
            ids = _pendingGames.ToList();
            _pendingGames.Clear();
        }
        if (ids.Count == 0) return;
        var wasBusy = IsSyncing;
        var results = await SyncAllAsync(automatic: true, onlyIds: ids).ConfigureAwait(false);
        if (results.Count == 0 && wasBusy && !_paused)
        {
            // Une synchro était déjà en cours : on réessaie plus tard.
            foreach (var id in ids) OnGameFilesChanged(id);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _debounce?.Dispose();
        lock (_watchers)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }
}
