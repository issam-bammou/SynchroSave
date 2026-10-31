namespace SynchroSave.Core;

public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string LevelText => Level switch
    {
        LogLevel.Success => "OK",
        LogLevel.Warning => "Attention",
        LogLevel.Error => "Erreur",
        _ => "Info",
    };
}

/// <summary>Journal simple : fichier texte + évènement pour l'interface.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _file;

    public static event Action<LogEntry>? EntryAdded;

    public static void Initialize(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _file = Path.Combine(logDirectory, "synchrosave.log");
        try
        {
            // Rotation simple à 2 Mo.
            var fi = new FileInfo(_file);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                File.Move(_file, Path.Combine(logDirectory, "synchrosave.old.log"), overwrite: true);
        }
        catch { /* best effort */ }
    }

    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Success(string m) => Write(LogLevel.Success, m);
    public static void Warn(string m) => Write(LogLevel.Warning, m);
    public static void Error(string m, Exception? ex = null) =>
        Write(LogLevel.Error, ex == null ? m : $"{m} : {ex.Message}");

    public static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        if (_file != null)
        {
            lock (Gate)
            {
                try { File.AppendAllText(_file, $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.LevelText}] {message}{Environment.NewLine}"); }
                catch { /* ne jamais planter pour un log */ }
            }
        }
        EntryAdded?.Invoke(entry);
    }
}
