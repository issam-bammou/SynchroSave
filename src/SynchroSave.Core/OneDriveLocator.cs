namespace SynchroSave.Core;

public static class OneDriveLocator
{
    /// <summary>
    /// Cherche le dossier OneDrive local. Le client OneDrive de Windows définit les variables
    /// d'environnement OneDriveConsumer (compte perso), OneDriveCommercial (pro) et OneDrive.
    /// Des candidats supplémentaires (ex. lus dans le registre) peuvent être fournis.
    /// </summary>
    public static string? Detect(IEnumerable<string?>? extraCandidates = null)
    {
        var candidates = new List<string?>();
        if (extraCandidates != null) candidates.AddRange(extraCandidates);
        candidates.Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        candidates.Add(Environment.GetEnvironmentVariable("OneDrive"));
        candidates.Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) candidates.Add(Path.Combine(profile, "OneDrive"));

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && Directory.Exists(c));
    }
}
