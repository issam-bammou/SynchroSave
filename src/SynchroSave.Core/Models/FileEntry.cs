namespace SynchroSave.Core.Models;

public sealed class FileEntry
{
    /// <summary>Chemin relatif, séparateur '/'.</summary>
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
}
