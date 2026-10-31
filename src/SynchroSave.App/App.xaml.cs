using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SynchroSave.Core;
using SynchroSave.Core.Models;

namespace SynchroSave.App;

public partial class App : Application
{
    private const string MutexName = @"Local\SynchroSave.SingleInstance";
    private const string ShowEventName = @"Local\SynchroSave.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private MainWindow? _window;

    public static SyncService Service { get; private set; } = null!;
    public static bool IsExiting { get; private set; }
    public static bool IsFirstRun { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Une instance tourne déjà : on lui demande d'afficher sa fenêtre.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* ignore */ }
            IsExiting = true;
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (IsExiting) break;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        var paths = new AppPaths();
        AppLog.Initialize(paths.LogsDirectory);
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Erreur inattendue", args.Exception);
            MessageBox.Show(args.Exception.Message, "SynchroSave", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Erreur en tâche de fond", args.Exception);
            args.SetObserved();
        };

        Service = new SyncService(paths, new PathResolver(WindowsIntegration.BuildTokens()));
        var firstRun = !Service.Settings.FirstRunCompleted;
        IsFirstRun = firstRun;
        if (firstRun) FirstRunSetup();
        else WindowsIntegration.RefreshStartupEntry(Service.Settings.StartWithWindows);
        if (Service.Settings.StartWithWindows && WindowsIntegration.IsDisabledInTaskManager())
            AppLog.Warn("Le lancement au démarrage est désactivé dans le Gestionnaire des tâches (onglet Démarrage). Réactivez-le dans Paramètres.");

        AppLog.Info($"SynchroSave démarré sur {Service.Settings.MachineName}.");

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowMainWindow;
        _tray.SyncRequested += () => _ = Service.SyncAllAsync();
        _tray.PauseToggled += () => Service.IsPaused = !Service.IsPaused;
        _tray.StartupToggled += ToggleStartWithWindows;
        _tray.ExitRequested += ExitApplication;

        Service.SyncCompleted += OnSyncCompleted;
        Service.Start();

        var minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                        || (!firstRun && Service.Settings.StartMinimized);
        if (!minimized) ShowMainWindow();
        else _tray.ShowBalloon("SynchroSave", "SynchroSave fonctionne en arrière-plan.");

        if (Service.Settings.SyncOnStartup)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                await Service.SyncAllAsync(automatic: true);
            });
        }
    }

    private static void FirstRunSetup()
    {
        var s = Service.Settings;
        lock (Service.Gate)
        {
            if (string.IsNullOrWhiteSpace(s.OneDriveRoot))
                s.OneDriveRoot = WindowsIntegration.DetectOneDrive() ?? string.Empty;
            var added = Service.Catalog.MergeDetected(s);
            AppLog.Info($"Premier lancement : {added.Count} jeu(x) détecté(s).");
            s.FirstRunCompleted = true;
        }
        if (string.IsNullOrWhiteSpace(s.OneDriveRoot))
            AppLog.Warn("Dossier OneDrive non trouvé : choisissez-le dans l'onglet Paramètres.");
        WindowsIntegration.SetStartWithWindows(s.StartWithWindows);
        Service.SaveSettings();
    }

    private void OnSyncCompleted(System.Collections.Generic.IReadOnlyList<SyncResult> results)
    {
        if (!Service.Settings.ShowNotifications || _tray == null || results.Count == 0) return;
        var changed = results.Where(r => r.ChangedSomething).ToList();
        var errors = results.Where(r => r.IsError).ToList();
        if (errors.Count > 0)
        {
            _tray.ShowBalloon("SynchroSave - problème",
                string.Join("\n", errors.Take(3).Select(r => $"{r.Game.Name} : {r.Message}")), isError: true);
        }
        else if (changed.Count > 0)
        {
            _tray.ShowBalloon("Sauvegardes synchronisées",
                string.Join("\n", changed.Take(4).Select(r => $"{r.Game.Name} : {r.Message}")) +
                (changed.Count > 4 ? $"\n... et {changed.Count - 4} autre(s)" : ""));
        }
    }

    /// <summary>Bascule « Lancer au démarrage de Windows » (menu de l'icône).</summary>
    public static void ToggleStartWithWindows()
    {
        var enable = !WindowsIntegration.IsStartWithWindowsActive();
        lock (Service.Gate) Service.Settings.StartWithWindows = enable;
        WindowsIntegration.SetStartWithWindows(enable);
        Service.SaveSettings();
        StartupSettingChanged?.Invoke();
    }

    public static event Action? StartupSettingChanged;

    public void ShowMainWindow()
    {
        if (_window == null)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void ExitApplication()
    {
        if (Service.IsSyncing)
        {
            var r = MessageBox.Show("Une synchronisation est en cours. Quitter quand même ?", "SynchroSave",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        IsExiting = true;
        _showEvent?.Set();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Service?.SaveSettings();
        Service?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
