namespace SynchroSave.Core.Models;

public enum SyncMode
{
    /// <summary>Le PC envoie ses sauvegardes vers OneDrive (OneDrive n'écrase jamais le PC automatiquement).</summary>
    BackupOnly,
    /// <summary>Synchronisation dans les deux sens : la version la plus récente gagne, l'autre est archivée.</summary>
    TwoWay
}

public sealed class AppSettings
{
    /// <summary>Dossier racine OneDrive local (ex: C:\Users\Moi\OneDrive).</summary>
    public string OneDriveRoot { get; set; } = string.Empty;

    /// <summary>Nom du dossier créé dans OneDrive pour stocker les sauvegardes.</summary>
    public string CloudFolderName { get; set; } = "SynchroSave";

    /// <summary>Intervalle entre deux synchronisations automatiques (minutes).</summary>
    public int SyncIntervalMinutes { get; set; } = 15;

    public bool AutoSyncEnabled { get; set; } = true;

    /// <summary>Déclenche aussi une synchro quelques secondes après qu'un fichier de sauvegarde a changé.</summary>
    public bool SyncOnChange { get; set; } = true;

    public int SyncOnChangeDelaySeconds { get; set; } = 60;

    public SyncMode Mode { get; set; } = SyncMode.BackupOnly;

    /// <summary>Nombre d'anciennes versions conservées dans OneDrive par jeu (0 = aucune).</summary>
    public int VersionsToKeep { get; set; } = 10;

    /// <summary>Nombre de sauvegardes locales de sécurité (avant restauration/écrasement) gardées par jeu.</summary>
    public int LocalSafetyBackupsToKeep { get; set; } = 5;

    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool SyncOnStartup { get; set; } = true;

    /// <summary>Motifs de fichiers ignorés, séparés par ';' (ex: *.tmp;*.log).</summary>
    public string ExcludePatterns { get; set; } = "*.tmp;*.temp;*.lock;desktop.ini;Thumbs.db";

    /// <summary>Nom de ce PC tel qu'il apparaît dans OneDrive.</summary>
    public string MachineName { get; set; } = Environment.MachineName;

    public bool FirstRunCompleted { get; set; }

    public List<GameEntry> Games { get; set; } = new();
}
