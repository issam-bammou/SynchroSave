namespace SynchroSave.Core.Models;

/// <summary>Entrée de la base de jeux connus (emplacements de sauvegarde typiques).</summary>
public sealed class GameDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Emplacements candidats (avec jetons). Le premier qui existe est utilisé.</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>Si faux, le jeu n'est pas activé automatiquement lorsqu'il est détecté (dossiers génériques volumineux).</summary>
    public bool AutoEnable { get; set; } = true;

    /// <summary>Origine de la définition (base intégrée, base en ligne, base perso) — non enregistrée.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Source { get; set; } = string.Empty;
}
