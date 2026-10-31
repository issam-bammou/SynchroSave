using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SynchroSave.Core;
using SynchroSave.Core.Models;

namespace SynchroSave.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<GameRow> _games = new();
    private readonly ObservableCollection<CloudGameRow> _cloudGames = new();
    private readonly ObservableCollection<LogEntry> _log = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _cloudLoadedOnce;
    private static bool _hideHintShown;

    private static SyncService Service => App.Service;

    public MainWindow()
    {
        InitializeComponent();
        GamesGrid.ItemsSource = _games;
        CloudGrid.ItemsSource = _cloudGames;
        LogList.ItemsSource = _log;

        RefreshGames();
        LoadSettingsView();
        LoadLogTail();

        Service.SyncStarted += OnServiceChanged;
        Service.SyncCompleted += OnSyncCompleted;
        Service.GameSynced += OnGameSynced;
        Service.ScheduleChanged += OnServiceChanged;
        AppLog.EntryAdded += OnLogEntry;
        App.StartupSettingChanged += OnStartupSettingChanged;
        Service.CatalogChanged += OnCatalogChanged;

        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == Tabs && Tabs.SelectedItem == RestoreTab && !_cloudLoadedOnce) _ = RefreshCloudAsync();
        };

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        UpdateStatus();

        if (App.IsFirstRun)
        {
            // Premier lancement : on propose tout de suite les sauvegardes trouvées par analyse.
            ContentRendered += (_, _) =>
            {
                if (!App.IsFirstRun) return;
                App.IsFirstRun = false;
                RunDetection();
            };
        }

        if (string.IsNullOrWhiteSpace(Service.Settings.OneDriveRoot))
        {
            Loaded += (_, _) =>
            {
                MessageBox.Show(this,
                    "Le dossier OneDrive n'a pas été trouvé automatiquement.\n\nVérifiez que OneDrive est installé et connecté à votre compte, puis choisissez le dossier dans l'onglet Paramètres.",
                    "SynchroSave", MessageBoxButton.OK, MessageBoxImage.Information);
                Tabs.SelectedIndex = 2;
            };
        }
    }

    // ---------------------------------------------------------------- Fenêtre

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting)
        {
            // Fermer la fenêtre = réduire dans la zone de notification ; la synchro continue.
            e.Cancel = true;
            Hide();
            if (!_hideHintShown)
            {
                _hideHintShown = true;
                AppLog.Info("SynchroSave continue en arrière-plan (icône près de l'horloge). Clic droit > Quitter pour l'arrêter.");
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Service.SyncStarted -= OnServiceChanged;
        Service.SyncCompleted -= OnSyncCompleted;
        Service.GameSynced -= OnGameSynced;
        Service.ScheduleChanged -= OnServiceChanged;
        AppLog.EntryAdded -= OnLogEntry;
        App.StartupSettingChanged -= OnStartupSettingChanged;
        Service.CatalogChanged -= OnCatalogChanged;
        _statusTimer.Stop();
        base.OnClosed(e);
    }

    private void OnStartupSettingChanged() => Dispatcher.BeginInvoke(() =>
    {
        StartWithWindowsCheck.IsChecked = WindowsIntegration.IsStartWithWindowsActive();
        UpdateStartupStatus();
    });

    private void OnCatalogChanged() => Dispatcher.BeginInvoke(UpdateOnlineStatus);

    private void UpdateOnlineStatus()
    {
        var info = Service.OnlineDatabase.Info;
        OnlineStatusText.Text = !Service.OnlineDatabase.IsAvailable
            ? "  Pas encore téléchargée (elle le sera automatiquement à la prochaine détection)."
            : $"  {Service.Catalog.OnlineCount:N0} jeux chargés — mise à jour du {info.LastUpdateUtc?.ToLocalTime():dd/MM/yyyy}, vérifiée le {info.LastCheckUtc?.ToLocalTime():dd/MM/yyyy HH:mm}.";
    }

    private async void UpdateOnline_Click(object sender, RoutedEventArgs e)
    {
        UpdateOnlineButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(m => OnlineStatusText.Text = "  " + m);
            var updated = await Service.UpdateOnlineDatabaseAsync(force: true, progress);
            UpdateOnlineStatus();
            if (updated && MessageBox.Show(this,
                    $"Base de jeux en ligne à jour : {Service.Catalog.OnlineCount:N0} jeux.\n\nLancer une détection des sauvegardes maintenant ?",
                    "SynchroSave", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                RunDetection();
        }
        finally
        {
            UpdateOnlineButton.IsEnabled = true;
        }
    }

    private void OnServiceChanged() => Dispatcher.BeginInvoke(UpdateStatus);

    private void OnSyncCompleted(IReadOnlyList<SyncResult> results) => Dispatcher.BeginInvoke(() =>
    {
        UpdateStatus();
        foreach (var row in _games) row.RaiseAll();
    });

    private void OnGameSynced(SyncResult r) => Dispatcher.BeginInvoke(() =>
    {
        _games.FirstOrDefault(g => g.Id == r.Game.Id)?.RaiseAll();
    });

    private void OnLogEntry(LogEntry entry) => Dispatcher.BeginInvoke(() =>
    {
        _log.Insert(0, entry);
        while (_log.Count > 1000) _log.RemoveAt(_log.Count - 1);
    });

    private void LoadLogTail()
    {
        try
        {
            var file = Path.Combine(Service.Paths.LogsDirectory, "synchrosave.log");
            if (!File.Exists(file)) return;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(200);
            foreach (var line in lines.Reverse())
            {
                var l = line.TrimEnd('\r');
                if (l.Length < 21 || !DateTime.TryParse(l[..19], out var t)) continue;
                var rest = l[20..];
                var level = rest.StartsWith("[Erreur]") ? LogLevel.Error
                    : rest.StartsWith("[Attention]") ? LogLevel.Warning
                    : rest.StartsWith("[OK]") ? LogLevel.Success : LogLevel.Info;
                var msg = rest.Contains("] ") ? rest[(rest.IndexOf("] ", StringComparison.Ordinal) + 2)..] : rest;
                _log.Add(new LogEntry(t, level, msg));
            }
        }
        catch { /* ignore */ }
    }

    private void UpdateStatus()
    {
        var s = Service.Settings;
        string text;
        if (Service.IsSyncing) text = "⟳ Synchronisation en cours…";
        else if (Service.IsPaused) text = "⏸ Synchronisation automatique en pause.";
        else if (!s.AutoSyncEnabled) text = "Synchronisation automatique désactivée (voir Paramètres).";
        else if (Service.NextRunUtc is { } next) text = $"Prochaine synchro automatique à {next.ToLocalTime():HH:mm} (toutes les {FormatInterval(s.SyncIntervalMinutes)}).";
        else text = "Prêt.";
        if (Service.LastRunUtc is { } last) text += $"   •   Dernière synchro : {last.ToLocalTime():HH:mm}";
        StatusText.Text = text;
        PauseButton.Content = Service.IsPaused ? "▶ Reprendre" : "⏸ Pause";
        SyncAllButton.IsEnabled = !Service.IsSyncing;

        int enabled;
        lock (Service.Gate) enabled = s.Games.Count(g => g.Enabled);
        HeaderSubtitle.Text = string.IsNullOrWhiteSpace(s.OneDriveRoot)
            ? "⚠ Dossier OneDrive non configuré — voir Paramètres."
            : $"{enabled} jeu(x) synchronisé(s) vers {Path.Combine(s.OneDriveRoot, s.CloudFolderName)}";
    }

    private static string FormatInterval(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes} min";

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        Service.IsPaused = !Service.IsPaused;
        UpdateStatus();
    }

    // ---------------------------------------------------------------- Mes jeux

    private void RefreshGames()
    {
        _games.Clear();
        List<GameEntry> list;
        lock (Service.Gate) list = Service.Settings.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var g in list) _games.Add(new GameRow(g));
        UpdateStatus();
    }

    private List<GameRow> SelectedGames() => GamesGrid.SelectedItems.OfType<GameRow>().ToList();

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckOneDrive()) return;
        SyncAllButton.IsEnabled = false;
        var results = await Service.SyncAllAsync();
        if (results.Count == 0 && !_games.Any(g => g.Enabled))
            MessageBox.Show(this, "Aucun jeu n'est coché pour la synchronisation.", "SynchroSave");
        UpdateStatus();
    }

    private async void SyncSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckOneDrive()) return;
        var sel = SelectedGames();
        if (sel.Count == 0)
        {
            MessageBox.Show(this, "Sélectionnez un ou plusieurs jeux dans la liste.", "SynchroSave");
            return;
        }
        var disabled = sel.Where(g => !g.Enabled).ToList();
        if (disabled.Count > 0)
            MessageBox.Show(this, "Les jeux non cochés ne sont pas synchronisés : " + string.Join(", ", disabled.Select(d => d.Name)), "SynchroSave");
        await Service.SyncAllAsync(onlyIds: sel.Select(g => g.Id));
    }

    private void Detect_Click(object sender, RoutedEventArgs e) => RunDetection();

    /// <summary>Ouvre la fenêtre de détection (jeux connus + analyse des dossiers AppData, Documents…).</summary>
    public void RunDetection()
    {
        var dlg = new DetectWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Added.Count == 0) return;
        Service.ApplySettings();
        RefreshGames();
        AppLog.Success($"{dlg.Added.Count} jeu(x) ajouté(s) : {string.Join(", ", dlg.Added.Select(a => a.Name))}.");
        _ = Service.SyncAllAsync(onlyIds: dlg.Added.Select(a => a.Id));
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GameEditWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;

        GameEntry entry;
        lock (Service.Gate)
        {
            var settings = Service.Settings;
            var def = dlg.SelectedDefinition;
            if (def != null && !settings.Games.Any(g => g.Id == def.Id))
            {
                var template = def.Paths.FirstOrDefault(p =>
                                   Service.Resolver.ResolveExisting(p).Any(x => SamePath(x, dlg.FolderPath)))
                               ?? Service.Resolver.ToTemplate(dlg.FolderPath);
                entry = new GameEntry
                {
                    Id = def.Id, Name = dlg.GameName, LocalPath = dlg.FolderPath, PathTemplate = template,
                    Enabled = true, LastStatus = "Ajouté",
                };
                settings.Games.Add(entry);
            }
            else
            {
                entry = Service.Catalog.CreateCustom(dlg.GameName, dlg.FolderPath, settings);
            }
        }
        Service.ApplySettings();
        RefreshGames();
        GamesGrid.SelectedItem = _games.FirstOrDefault(g => g.Id == entry.Id);
        _ = Service.SyncAllAsync(onlyIds: new[] { entry.Id });
    }

    private void GamesGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: GameRow } && GamesGrid.CurrentColumn?.DisplayIndex != 0)
            EditGame_Click(sender, e);
    }

    private void EditGame_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameRow row) return;
        var dlg = new GameEditWindow(row.Entry) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        lock (Service.Gate)
        {
            var pathChanged = !SamePath(row.Entry.LocalPath, dlg.FolderPath);
            row.Entry.Name = dlg.GameName;
            if (pathChanged)
            {
                row.Entry.LocalPath = dlg.FolderPath;
                row.Entry.PathTemplate = Service.Resolver.ToTemplate(dlg.FolderPath);
                Service.States.Delete(row.Entry.Id);
            }
        }
        Service.ApplySettings();
        RefreshGames();
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedGames();
        if (sel.Count == 0) return;
        var r = MessageBox.Show(this,
            $"Retirer {sel.Count} jeu(x) de la liste ?\n\nLes fichiers de ce PC ne sont pas touchés et la copie dans OneDrive est conservée (supprimable depuis l'onglet Restaurer).",
            "SynchroSave", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        lock (Service.Gate)
        {
            foreach (var g in sel)
            {
                Service.Settings.Games.Remove(g.Entry);
                Service.States.Delete(g.Id);
            }
        }
        Service.ApplySettings();
        RefreshGames();
    }

    private void OpenLocal_Click(object sender, RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is GameRow row) OpenFolder(row.LocalPath);
    }

    private void OpenCloud_Click(object sender, RoutedEventArgs e)
    {
        var repo = Service.CloudRepository;
        if (repo == null) return;
        OpenFolder(GamesGrid.SelectedItem is GameRow row ? repo.GameDir(row.Id) : repo.Root);
    }

    // ---------------------------------------------------------------- Restaurer

    private void RefreshCloud_Click(object sender, RoutedEventArgs e) => _ = RefreshCloudAsync();

    private async Task RefreshCloudAsync()
    {
        _cloudLoadedOnce = true;
        _cloudGames.Clear();
        VersionsList.ItemsSource = null;
        var repo = Service.CloudRepository;
        if (repo == null || !Directory.Exists(Service.Settings.OneDriveRoot))
        {
            VersionsTitle.Text = "Dossier OneDrive non configuré (voir Paramètres).";
            return;
        }
        VersionsTitle.Text = "Lecture de OneDrive…";
        List<CloudManifest> manifests;
        try
        {
            manifests = await Task.Run(repo.ListGames);
        }
        catch (Exception ex)
        {
            VersionsTitle.Text = "Erreur : " + ex.Message;
            return;
        }

        foreach (var m in manifests)
        {
            var row = new CloudGameRow(m);
            lock (Service.Gate)
            {
                row.TargetPath = Service.Restore.SuggestTarget(m, Service.Settings);
                var tracked = Service.Settings.Games.FirstOrDefault(g => g.Id == m.GameId);
                row.LocalState = tracked != null ? (tracked.Enabled ? "Synchronisé" : "Suivi (non coché)")
                    : string.IsNullOrEmpty(row.TargetPath) ? "Destination inconnue"
                    : Directory.Exists(row.TargetPath) ? "Dossier présent" : "Pas encore sur ce PC";
                row.Selected = tracked == null && !string.IsNullOrEmpty(row.TargetPath) && !Directory.Exists(row.TargetPath);
            }
            _cloudGames.Add(row);
        }
        VersionsTitle.Text = manifests.Count == 0
            ? "Aucune sauvegarde dans OneDrive pour l'instant. (Si vous venez d'installer OneDrive, attendez la fin de sa synchronisation.)"
            : $"{manifests.Count} jeu(x) dans OneDrive — sélectionnez un jeu pour voir ses versions.";
    }

    private void SelectAllCloud_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _cloudGames) r.Selected = true;
    }

    private void SelectNoneCloud_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _cloudGames) r.Selected = false;
    }

    private void ChangeTarget_Click(object sender, RoutedEventArgs e)
    {
        if (CloudGrid.SelectedItem is not CloudGameRow row)
        {
            MessageBox.Show(this, "Sélectionnez d'abord un jeu dans la liste.", "SynchroSave");
            return;
        }
        var dlg = new OpenFolderDialog { Title = $"Où restaurer les sauvegardes de {row.Name} ?" };
        if (Directory.Exists(row.TargetPath)) dlg.InitialDirectory = row.TargetPath;
        if (dlg.ShowDialog(this) == true)
        {
            row.TargetPath = dlg.FolderName;
            row.Selected = true;
        }
    }

    private void CloudGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloudGrid.SelectedItem is not CloudGameRow row || Service.CloudRepository is not { } repo)
        {
            VersionsList.ItemsSource = null;
            return;
        }
        var versions = new List<CloudVersion>
        {
            new() { IsLatest = true, CreatedUtc = row.Manifest.UpdatedUtc, Machine = row.Manifest.SourceMachine, Size = row.Manifest.TotalSize },
        };
        try { versions.AddRange(repo.ListVersions(row.Manifest.GameId)); }
        catch (Exception ex) { AppLog.Warn("Lecture des versions impossible : " + ex.Message); }
        VersionsList.ItemsSource = versions;
        VersionsList.SelectedIndex = 0;
        VersionsTitle.Text = $"Versions de {row.Name} ({versions.Count})";
    }

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var rows = _cloudGames.Where(r => r.Selected).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Cochez au moins un jeu à récupérer.", "SynchroSave");
            return;
        }
        var missing = rows.Where(r => string.IsNullOrWhiteSpace(r.TargetPath)).ToList();
        if (missing.Count > 0)
        {
            MessageBox.Show(this, "Choisissez un dossier de destination pour : " + string.Join(", ", missing.Select(m => m.Name)) +
                                  "\n(sélectionnez la ligne puis « Changer la destination… »)", "SynchroSave", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = MessageBox.Show(this,
            "Restaurer la dernière version de :\n\n" + string.Join("\n", rows.Select(r => $"• {r.Name}  →  {r.TargetPath}")) +
            "\n\nFermez ces jeux avant de continuer. Le contenu actuel de ces dossiers sera d'abord copié dans les sauvegardes de sécurité locales.",
            "Restaurer", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        await RestoreAsync(rows.Select(r => (r, (CloudVersion?)null)).ToList());
    }

    private async void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (CloudGrid.SelectedItem is not CloudGameRow row || VersionsList.SelectedItem is not CloudVersion version)
        {
            MessageBox.Show(this, "Sélectionnez un jeu puis une version.", "SynchroSave");
            return;
        }
        if (string.IsNullOrWhiteSpace(row.TargetPath))
        {
            MessageBox.Show(this, "Choisissez d'abord le dossier de destination (« Changer la destination… »).", "SynchroSave");
            return;
        }
        var confirm = MessageBox.Show(this,
            $"Restaurer {row.Name} — {version.DisplayName}\ndans {row.TargetPath} ?\n\nFermez le jeu avant de continuer. Le contenu actuel sera d'abord copié dans les sauvegardes de sécurité locales.",
            "Restaurer une version", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        await RestoreAsync(new List<(CloudGameRow, CloudVersion?)> { (row, version) });
    }

    private async Task RestoreAsync(List<(CloudGameRow Row, CloudVersion? Version)> items)
    {
        IsEnabled = false;
        var ok = new List<string>();
        var errors = new List<string>();
        try
        {
            foreach (var (row, version) in items)
            {
                try
                {
                    await Service.RunExclusiveAsync(() =>
                    {
                        lock (Service.Gate)
                            return Service.Restore.Restore(row.Manifest, version, row.TargetPath, Service.Settings);
                    });
                    AppLog.Success($"{row.Name} : restauré dans {row.TargetPath}.");
                    ok.Add(row.Name);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"{row.Name} : échec de la restauration", ex);
                    errors.Add($"{row.Name} : {ex.Message}");
                }
            }
        }
        finally
        {
            IsEnabled = true;
        }
        Service.ApplySettings();
        RefreshGames();
        await RefreshCloudAsync();

        var msg = ok.Count > 0 ? "Restauré avec succès :\n" + string.Join("\n", ok.Select(o => "• " + o)) : "";
        if (errors.Count > 0) msg += (msg.Length > 0 ? "\n\n" : "") + "Erreurs :\n" + string.Join("\n", errors);
        if (ok.Count > 0) msg += "\n\nCes jeux sont maintenant synchronisés automatiquement depuis ce PC.";
        MessageBox.Show(this, msg, "Restauration", MessageBoxButton.OK,
            errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private async void DeleteCloud_Click(object sender, RoutedEventArgs e)
    {
        if (CloudGrid.SelectedItem is not CloudGameRow row || Service.CloudRepository is not { } repo) return;
        var r = MessageBox.Show(this,
            $"Supprimer définitivement de OneDrive toutes les sauvegardes de « {row.Name} » (y compris l'historique) ?\n\n(La corbeille OneDrive permet de les récupérer pendant un temps limité.)",
            "Supprimer de OneDrive", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        try
        {
            await Service.RunExclusiveAsync(() => { repo.DeleteGame(row.Manifest.GameId); return true; });
            Service.States.Delete(row.Manifest.GameId);
            AppLog.Info($"{row.Name} : supprimé de OneDrive.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await RefreshCloudAsync();
    }

    // ---------------------------------------------------------------- Paramètres

    private void LoadSettingsView()
    {
        var s = Service.Settings;
        OneDriveBox.Text = s.OneDriveRoot;
        CloudFolderBox.Text = s.CloudFolderName;
        MachineBox.Text = s.MachineName;
        AutoSyncCheck.IsChecked = s.AutoSyncEnabled;
        if (s.SyncIntervalMinutes >= 60 && s.SyncIntervalMinutes % 60 == 0)
        {
            IntervalBox.Text = (s.SyncIntervalMinutes / 60).ToString();
            IntervalUnitCombo.SelectedIndex = 1;
        }
        else
        {
            IntervalBox.Text = s.SyncIntervalMinutes.ToString();
            IntervalUnitCombo.SelectedIndex = 0;
        }
        SyncOnChangeCheck.IsChecked = s.SyncOnChange;
        ChangeDelayBox.Text = s.SyncOnChangeDelaySeconds.ToString();
        SyncOnStartupCheck.IsChecked = s.SyncOnStartup;
        ModeBackupRadio.IsChecked = s.Mode == SyncMode.BackupOnly;
        ModeTwoWayRadio.IsChecked = s.Mode == SyncMode.TwoWay;
        VersionsBox.Text = s.VersionsToKeep.ToString();
        LocalBackupsBox.Text = s.LocalSafetyBackupsToKeep.ToString();
        ExcludeBox.Text = s.ExcludePatterns;
        StartWithWindowsCheck.IsChecked = s.StartWithWindows && !WindowsIntegration.IsDisabledInTaskManager();
        UpdateStartupStatus();
        StartMinimizedCheck.IsChecked = s.StartMinimized;
        NotificationsCheck.IsChecked = s.ShowNotifications;
        OnlineDbCheck.IsChecked = s.OnlineDatabaseEnabled;
        OnlineDaysBox.Text = s.OnlineDatabaseUpdateDays.ToString();
        UpdateOnlineStatus();
    }

    private void IntervalPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var minutes)) return;
        if (minutes >= 60 && minutes % 60 == 0)
        {
            IntervalBox.Text = (minutes / 60).ToString();
            IntervalUnitCombo.SelectedIndex = 1;
        }
        else
        {
            IntervalBox.Text = minutes.ToString();
            IntervalUnitCombo.SelectedIndex = 0;
        }
        AutoSyncCheck.IsChecked = true;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalBox.Text.Trim(), out var interval) || interval < 1)
        {
            Warn("La fréquence doit être un nombre entier positif.");
            return;
        }
        var minutes = IntervalUnitCombo.SelectedIndex == 1 ? interval * 60 : interval;
        if (minutes > 24 * 60)
        {
            Warn("La fréquence maximale est de 24 heures.");
            return;
        }
        if (!int.TryParse(VersionsBox.Text.Trim(), out var versions) || versions < 0)
        {
            Warn("Nombre de versions invalide.");
            return;
        }
        if (!int.TryParse(LocalBackupsBox.Text.Trim(), out var localBackups) || localBackups < 0)
        {
            Warn("Nombre de copies de sécurité invalide.");
            return;
        }
        if (!int.TryParse(ChangeDelayBox.Text.Trim(), out var delay) || delay < 5)
        {
            Warn("Le délai après modification doit être d'au moins 5 secondes.");
            return;
        }
        if (!int.TryParse(OnlineDaysBox.Text.Trim(), out var onlineDays) || onlineDays < 1)
        {
            Warn("La fréquence de mise à jour de la base en ligne doit être d'au moins 1 jour.");
            return;
        }
        var oneDrive = OneDriveBox.Text.Trim();
        if (!Directory.Exists(oneDrive))
        {
            Warn("Le dossier OneDrive indiqué n'existe pas.");
            return;
        }
        var folderName = CloudFolderBox.Text.Trim();
        if (string.IsNullOrEmpty(folderName) || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Warn("Nom de dossier OneDrive invalide.");
            return;
        }

        var s = Service.Settings;
        lock (Service.Gate)
        {
            s.OneDriveRoot = oneDrive;
            s.CloudFolderName = folderName;
            s.MachineName = string.IsNullOrWhiteSpace(MachineBox.Text) ? Environment.MachineName : MachineBox.Text.Trim();
            s.AutoSyncEnabled = AutoSyncCheck.IsChecked == true;
            s.SyncIntervalMinutes = minutes;
            s.SyncOnChange = SyncOnChangeCheck.IsChecked == true;
            s.SyncOnChangeDelaySeconds = delay;
            s.SyncOnStartup = SyncOnStartupCheck.IsChecked == true;
            s.Mode = ModeTwoWayRadio.IsChecked == true ? SyncMode.TwoWay : SyncMode.BackupOnly;
            s.VersionsToKeep = versions;
            s.LocalSafetyBackupsToKeep = localBackups;
            s.ExcludePatterns = ExcludeBox.Text.Trim();
            s.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
            s.StartMinimized = StartMinimizedCheck.IsChecked == true;
            s.ShowNotifications = NotificationsCheck.IsChecked == true;
            s.OnlineDatabaseEnabled = OnlineDbCheck.IsChecked == true;
            s.OnlineDatabaseUpdateDays = onlineDays;
        }
        if (s.OnlineDatabaseEnabled) _ = Service.InitializeOnlineDatabaseAsync();
        else Service.Catalog.SetOnlineDefinitions(Array.Empty<GameDefinition>());
        WindowsIntegration.SetStartWithWindows(s.StartWithWindows);
        Service.ApplySettings();
        LoadSettingsView();
        UpdateStatus();
        _cloudLoadedOnce = false;
        AppLog.Info($"Paramètres enregistrés (synchro toutes les {FormatInterval(minutes)}, mode {(s.Mode == SyncMode.TwoWay ? "bidirectionnel" : "sauvegarde")}).");
        MessageBox.Show(this, "Paramètres enregistrés.", "SynchroSave", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateStartupStatus()
    {
        if (WindowsIntegration.IsStartWithWindowsActive())
        {
            StartupStatusText.Text = "✔ Actif : SynchroSave démarrera réduit près de l'horloge à chaque démarrage du PC.";
            StartupStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
        }
        else if (WindowsIntegration.HasRunEntry() && WindowsIntegration.IsDisabledInTaskManager())
        {
            StartupStatusText.Text = "⚠ Désactivé dans le Gestionnaire des tâches (onglet Démarrage). Cochez la case et enregistrez pour le réactiver.";
            StartupStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
        }
        else
        {
            StartupStatusText.Text = "Inactif : SynchroSave ne se lancera pas tout seul au démarrage.";
            StartupStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Subtle");
        }
    }

    private void ResetSettingsView_Click(object sender, RoutedEventArgs e) => LoadSettingsView();

    private void DetectOneDrive_Click(object sender, RoutedEventArgs e)
    {
        var found = WindowsIntegration.DetectOneDrive();
        if (found != null) OneDriveBox.Text = found;
        else Warn("OneDrive n'a pas été trouvé. Installez/connectez OneDrive ou choisissez le dossier manuellement.");
    }

    private void BrowseOneDrive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier OneDrive" };
        if (Directory.Exists(OneDriveBox.Text)) dlg.InitialDirectory = OneDriveBox.Text;
        if (dlg.ShowDialog(this) == true) OneDriveBox.Text = dlg.FolderName;
    }

    private void OpenCloudRoot_Click(object sender, RoutedEventArgs e)
    {
        if (Service.CloudRepository is { } repo)
        {
            Directory.CreateDirectory(repo.Root);
            OpenFolder(repo.Root);
        }
    }

    private void OpenLocalBackups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Service.Paths.LocalBackupsDirectory);
        OpenFolder(Service.Paths.LocalBackupsDirectory);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Service.Paths.DataRoot);

    // ---------------------------------------------------------------- Journal

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(Service.Paths.LogsDirectory, "synchrosave.log");
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    // ---------------------------------------------------------------- Utilitaires

    private bool CheckOneDrive()
    {
        if (Directory.Exists(Service.Settings.OneDriveRoot)) return true;
        Warn("Le dossier OneDrive n'est pas configuré ou n'existe pas. Réglez-le dans l'onglet Paramètres.");
        Tabs.SelectedIndex = 2;
        return false;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "SynchroSave", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Warn("Ce dossier n'existe pas : " + path);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
