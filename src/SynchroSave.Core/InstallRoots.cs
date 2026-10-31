using System.Text.Json;
using System.Text.RegularExpressions;

namespace SynchroSave.Core;

/// <summary>
/// Dossiers contenant des jeux installés (bibliothèques Steam sur tous les disques, Epic, GOG, Xbox, dossiers
/// « Games » à la racine des disques…). Utilisés pour les sauvegardes stockées dans le dossier d'installation.
/// </summary>
public static class InstallRoots
{
    private static readonly string[] DriveRelativeRoots =
    {
        "Games", "Jeux", "Jeux PC", "PC Games", "Epic Games", "GOG Games", "GOG Galaxy\\Games", "SteamLibrary\\steamapps\\common",
        "Steam\\steamapps\\common", "XboxGames", "EA Games", "Riot Games", "Ubisoft Games", "Amazon Games\\Library",
        "Program Files\\Epic Games", "Program Files\\EA Games", "Program Files\\Electronic Arts", "Program Files\\Steam\\steamapps\\common",
        "Program Files (x86)\\Steam\\steamapps\\common", "Program Files (x86)\\GOG Galaxy\\Games", "Program Files (x86)\\Origin Games",
        "Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\games", "Program Files (x86)\\EA Games", "Program Files\\Games",
        "Program Files (x86)\\Games",
    };

    public static List<string> Find(string? steamPath, IEnumerable<string>? extra = null)
    {
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);
                if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
            }
            catch { /* chemin invalide */ }
        }

        // Steam : bibliothèque principale + autres bibliothèques déclarées dans libraryfolders.vdf
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            Add(Path.Combine(steamPath, "steamapps", "common"));
            foreach (var lib in SteamLibraries(steamPath)) Add(Path.Combine(lib, "steamapps", "common"));
        }

        // Epic Games : un manifeste .item par jeu avec InstallLocation
        foreach (var loc in EpicInstallLocations()) Add(Path.GetDirectoryName(loc));

        if (extra != null) foreach (var e in extra) Add(e);

        // Dossiers usuels à la racine de chaque disque fixe
        foreach (var drive in SafeDrives())
            foreach (var rel in DriveRelativeRoots)
                Add(Path.Combine(drive, rel));

        return roots;
    }

    internal static IEnumerable<string> SteamLibraries(string steamPath)
    {
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }
        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            yield return m.Groups[1].Value.Replace("\\\\", "\\");
    }

    private static IEnumerable<string> EpicInstallLocations()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        string[] files;
        try { files = Directory.GetFiles(dir, "*.item"); } catch { yield break; }
        foreach (var f in files)
        {
            string? loc = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("InstallLocation", out var p)) loc = p.GetString();
            }
            catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(loc)) yield return loc;
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
