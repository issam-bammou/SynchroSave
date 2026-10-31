namespace SynchroSave.Core.Models;

/// <summary>Jeu suivi sur ce PC.</summary>
public sealed class GameEntry
{
    /// <summary>Identifiant stable (= nom du dossier dans OneDrive). Identique sur tous les PC.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Chemin local résolu sur ce PC.</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>Chemin portable avec jetons ({Documents}, {AppData}...) pour retrouver le dossier sur un autre PC.</summary>
    public string PathTemplate { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool IsCustom { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    public string LastStatus { get; set; } = string.Empty;
}
