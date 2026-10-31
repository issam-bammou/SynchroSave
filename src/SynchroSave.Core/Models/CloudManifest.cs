namespace SynchroSave.Core.Models;

/// <summary>Décrit l'état d'une sauvegarde de jeu stockée dans OneDrive (fichier manifest.json).</summary>
public sealed class CloudManifest
{
    public int FormatVersion { get; set; } = 1;
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string PathTemplate { get; set; } = string.Empty;

    /// <summary>Change à chaque envoi : permet de savoir si un autre PC a modifié la sauvegarde.</summary>
    public string Revision { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }
    public string SourceMachine { get; set; } = string.Empty;
    public List<FileEntry> Files { get; set; } = new();

    public long TotalSize => Files.Sum(f => f.Size);
    public DateTime? LatestFileWriteUtc => Files.Count == 0 ? null : Files.Max(f => f.LastWriteUtc);
}

/// <summary>Dernier état synchronisé connu par CE PC pour un jeu (stocké localement).</summary>
public sealed class SyncState
{
    public string GameId { get; set; } = string.Empty;
    public string CloudRevision { get; set; } = string.Empty;
    public List<FileEntry> LocalFiles { get; set; } = new();
    public DateTime SyncedUtc { get; set; }
}

/// <summary>Ancienne version archivée dans OneDrive.</summary>
public sealed class CloudVersion
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public string Machine { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsLatest { get; set; }

    public string DisplayName => IsLatest
        ? $"Dernière version ({CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}, {Machine})"
        : $"{CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss} — {Machine}{(string.IsNullOrEmpty(Reason) ? "" : " — " + Reason)}";
}

public enum SyncAction
{
    None,
    Uploaded,
    Downloaded,
    ConflictResolvedUpload,
    ConflictResolvedDownload,
    Skipped,
    Error
}

public sealed class SyncResult
{
    public GameEntry Game { get; init; } = null!;
    public SyncAction Action { get; init; }
    public string Message { get; init; } = string.Empty;
    public int FilesTransferred { get; init; }

    public bool IsError => Action == SyncAction.Error;
    public bool ChangedSomething => Action is SyncAction.Uploaded or SyncAction.Downloaded
        or SyncAction.ConflictResolvedUpload or SyncAction.ConflictResolvedDownload;
}
