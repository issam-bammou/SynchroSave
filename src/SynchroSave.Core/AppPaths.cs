namespace SynchroSave.Core;

/// <summary>Emplacements locaux utilisés par l'application.</summary>
public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null)
    {
        DataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SynchroSave");
    }

    public string DataRoot { get; }
    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string StateDirectory => Path.Combine(DataRoot, "state");
    public string LocalBackupsDirectory => Path.Combine(DataRoot, "LocalBackups");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    /// <summary>Base de jeux personnalisée optionnelle, fusionnée avec la base intégrée.</summary>
    public string CustomGamesFile => Path.Combine(DataRoot, "games.custom.json");
}
