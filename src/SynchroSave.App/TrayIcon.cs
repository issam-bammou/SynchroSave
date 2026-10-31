using System;
using System.Drawing;
using System.Windows.Forms;

namespace SynchroSave.App;

/// <summary>Icône dans la zone de notification (à côté de l'horloge).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;

    public event Action? OpenRequested;
    public event Action? SyncRequested;
    public event Action? PauseToggled;
    public event Action? StartupToggled;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir SynchroSave", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Synchroniser maintenant", null, (_, _) => SyncRequested?.Invoke());
        _pauseItem = new ToolStripMenuItem("Mettre en pause la synchro auto", null, (_, _) => PauseToggled?.Invoke());
        menu.Items.Add(_pauseItem);
        _startupItem = new ToolStripMenuItem("Lancer au démarrage de Windows", null, (_, _) => StartupToggled?.Invoke());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitRequested?.Invoke());
        menu.Opening += (_, _) =>
        {
            _pauseItem.Text = App.Service.IsPaused ? "Reprendre la synchro auto" : "Mettre en pause la synchro auto";
            _startupItem.Checked = WindowsIntegration.IsStartWithWindowsActive();
        };

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "SynchroSave - sauvegardes de jeux vers OneDrive",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = WindowsIntegration.ExecutablePath;
            return Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    public void ShowBalloon(string title, string text, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _icon.ShowBalloonTip(5000, title, text.Length > 250 ? text[..250] + "..." : text,
            isError ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
