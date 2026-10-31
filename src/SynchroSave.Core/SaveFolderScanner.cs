using System.Text;
using System.Text.RegularExpressions;
using SynchroSave.Core.Models;

namespace SynchroSave.Core;

/// <summary>Dossier de sauvegarde trouvé par l'analyse heuristique.</summary>
public sealed class SaveCandidate
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Template { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public long Size { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    /// <summary>Id de la base de jeux si le dossier correspond à un jeu connu.</summary>
    public string? KnownGameId { get; init; }
}

/// <summary>
/// Parcourt AppData (Local, Roaming, LocalLow), Documents, « My Games » et « Saved Games » pour trouver
/// les dossiers de sauvegarde de jeux qui ne sont pas dans la base : dossiers nommés SaveGames / Saves /
/// SaveData… (dont le schéma Unreal Engine &lt;Jeu&gt;\Saved\SaveGames) et dossiers contenant des fichiers
/// de sauvegarde typiques (.sav, .sl2, .ess…).
/// </summary>
public sealed class SaveFolderScanner
{
    private const int MaxDepth = 5;
    private const int MaxDirectories = 60_000;

    private static readonly HashSet<string> SaveFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SaveGames", "SaveGame", "Saves", "Save", "Savegames", "Savegame", "SavedGames", "Saved Games",
        "SaveData", "Save Data", "SavesDir", "GameSaves", "gamesaves", "PlayerProfiles", "Save_Data", "UserSaves",
    };

    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sav", ".save", ".savegame", ".sl2", ".ess", ".fos", ".sfs", ".lsv", ".rvdata2", ".rpgsave", ".es3", ".gamesave",
    };

    /// <summary>Dossiers (1er niveau) qui ne sont jamais des jeux.</summary>
    private static readonly string[] ExcludedPrefixes =
    {
        "Microsoft", "Packages", "Temp", "Google", "Mozilla", "NVIDIA", "AMD", "Intel", "Discord", "Docker", "npm",
        "pip", "Crash", "D3DSCache", "ConnectedDevicesPlatform", "Comms", "cache", "CEF", "ASP.NET", "IBM",
        "javasharedresources", "Programs", "Package Cache", "Adobe", "JetBrains", "Apple", "Spotify", "Slack",
        "Zoom", "Code", "Claude", "claude", "copilot", "Microsoft Edge", "BraveSoftware", "Opera", "Vivaldi",
        "Postman", "GitHub", "SquirrelTemp", "Jedi", "Python", "pypa", "Yarn", "node", "NuGet", "Unity", "UnrealEngine",
        "EpicGamesLauncher", "Steam", "EADesktop", "EALaunchHelper", "Electron", "Logitech", "Razer", "Corsair",
        "SteelSeries", "Overwolf", "mobalytics", "WhatsApp", "Telegram", "OneDrive", "Dropbox", "Battle.net",
        "Blizzard Entertainment", "Riot Games", "Ubisoft Game Launcher", "GOG.com", "Origin", "Visual Studio",
        "VirtualStore", "Comms", "PeerDistRepub", "PlaceholderTileLogoFolder", "Publishers", "SynchroSave",
        "WindowsApps", "LuaTools", "Downloaded Installations", "My Music", "My Pictures", "My Videos", "Zoom",
        "Outlook", "Sublime", "Notepad++", "VLC", "obs-studio", "OBS", "7-Zip", "WinRAR",
    };

    /// <summary>Dossiers « conteneurs » dans lesquels le nom du jeu est le niveau suivant.</summary>
    private static readonly HashSet<string> ContainerFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "My Games", "Saved Games", "Games", "SavedGames", "Saves",
    };

    /// <summary>Sous-dossiers à ne jamais explorer.</summary>
    private static readonly HashSet<string> SkipFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Logs", "Log", "Crashes", "CrashReport", "Cache", "Caches", "ShaderCache", "Shaders", "GPUCache", "Code Cache",
        "Temp", "tmp", "webcache", "Webcache", "D3DCache", "PipelineCaches", "Screenshots", "Replays", "Demos",
        "node_modules", ".git", "Mods", "Workshop", "Downloads", "Updates", "Installer", "Plugins", "Binaries",
    };

    private readonly PathResolver _resolver;

    public SaveFolderScanner(PathResolver resolver) => _resolver = resolver;

    public static IReadOnlyList<string> DefaultRootTokens { get; } = new[]
    {
        "{LocalAppData}", "{AppData}", "{LocalLow}", "{SavedGames}", "{Documents}", "{Documents}\\My Games", "{PublicDocuments}",
    };

    public List<SaveCandidate> Scan(IEnumerable<string>? rootTemplates = null, CancellationToken ct = default)
    {
        var found = new Dictionary<string, SaveCandidate>(StringComparer.OrdinalIgnoreCase);
        var visited = 0;
        foreach (var rootTemplate in rootTemplates ?? DefaultRootTokens)
        {
            var root = _resolver.Expand(rootTemplate);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            ScanDirectory(root, root, 0, found, ref visited, ct);
        }

        // Si un dossier trouvé est inclus dans un autre trouvé, on garde le plus haut (un seul jeu = une entrée).
        var list = found.Values.OrderBy(c => c.Path.Length).ToList();
        var result = new List<SaveCandidate>();
        foreach (var c in list)
        {
            if (result.Any(r => IsSameOrInside(c.Path, r.Path))) continue;
            result.Add(c);
        }
        return result.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void ScanDirectory(string root, string dir, int depth, Dictionary<string, SaveCandidate> found,
        ref int visited, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > MaxDepth || ++visited > MaxDirectories) return;

        string[] subDirs;
        try
        {
            var info = new DirectoryInfo(dir);
            if (depth > 0 && (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Hidden)))
                return;
            subDirs = Directory.GetDirectories(dir);
        }
        catch
        {
            return;
        }

        if (depth > 0)
        {
            var name = System.IO.Path.GetFileName(dir);
            if (SkipFolderNames.Contains(name)) return;
            if (depth == 1 && IsExcluded(name)) return;

            string? reason = null;
            if (SaveFolderNames.Contains(name) && !(depth == 1 && ContainerFolders.Contains(name)))
                reason = name.Equals("SaveGames", StringComparison.OrdinalIgnoreCase) && dir.Contains(System.IO.Path.DirectorySeparatorChar + "Saved" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? "Dossier Unreal Engine Saved\\SaveGames"
                    : $"Dossier « {name} »";
            else if (HasSaveFiles(dir))
                reason = "Contient des fichiers de sauvegarde";

            if (reason != null)
            {
                var candidate = BuildCandidate(root, dir, reason);
                if (candidate != null)
                {
                    found[dir] = candidate;
                    return; // pas besoin de descendre plus bas
                }
            }
        }

        foreach (var sub in subDirs)
            ScanDirectory(root, sub, depth + 1, found, ref visited, ct);
    }

    private SaveCandidate? BuildCandidate(string root, string dir, string reason)
    {
        var files = FileScanner.Scan(dir);
        if (files.Count == 0) return null;
        // Les vrais dossiers de sauvegarde restent raisonnables ; au-delà c'est sûrement autre chose.
        var size = files.Sum(f => f.Size);
        if (files.Count > 5000 || size > 4L * 1024 * 1024 * 1024) return null;

        return new SaveCandidate
        {
            Name = GuessGameName(root, dir),
            Path = dir,
            Template = _resolver.ToTemplate(dir),
            Reason = reason,
            FileCount = files.Count,
            Size = size,
            LastWriteUtc = files.Max(f => f.LastWriteUtc),
        };
    }

    private static bool HasSaveFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Any(f => SaveExtensions.Contains(System.IO.Path.GetExtension(f)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExcluded(string firstLevelName) =>
        ExcludedPrefixes.Any(p => firstLevelName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Devine le nom du jeu à partir du chemin (ex: MortalShell2\Saved\SaveGames → « Mortal Shell 2 »).</summary>
    internal static string GuessGameName(string root, string dir)
    {
        var segments = System.IO.Path.GetRelativePath(root, dir)
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // Retire les conteneurs génériques en tête (My Games, Saved Games...).
        while (segments.Count > 1 && ContainerFolders.Contains(segments[0])) segments.RemoveAt(0);

        // Retire les dossiers « techniques » en fin (Saved, SaveGames, Saves, identifiants numériques...).
        while (segments.Count > 1 && (SaveFolderNames.Contains(segments[^1])
                                      || segments[^1].Equals("Saved", StringComparison.OrdinalIgnoreCase)
                                      || segments[^1].Equals("settings", StringComparison.OrdinalIgnoreCase)
                                      || Regex.IsMatch(segments[^1], @"^[0-9a-fA-F\-_]{6,}$")))
            segments.RemoveAt(segments.Count - 1);

        // Structure Éditeur\Jeu (typique de LocalLow) : on préfère le nom du jeu.
        var name = root.EndsWith("LocalLow", StringComparison.OrdinalIgnoreCase) && segments.Count >= 2
            ? segments[1]
            : segments[0];
        return Prettify(name);
    }

    internal static string Prettify(string raw)
    {
        var s = raw.Replace('_', ' ').Trim();
        if (s.Contains(' ')) return s;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0)
            {
                var p = s[i - 1];
                var splitCase = char.IsLower(p) && char.IsUpper(c);
                var splitDigit = char.IsLetter(p) && char.IsDigit(c) || char.IsDigit(p) && char.IsLetter(c);
                if (splitCase || splitDigit) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsSameOrInside(string path, string parent)
    {
        var p = path.TrimEnd('\\', '/');
        var r = parent.TrimEnd('\\', '/');
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vrai si le dossier est déjà suivi (identique, parent ou enfant d'un jeu suivi).</summary>
    public static bool IsAlreadyTracked(string path, IEnumerable<GameEntry> games) =>
        games.Any(g => !string.IsNullOrEmpty(g.LocalPath) && (IsSameOrInside(path, g.LocalPath) || IsSameOrInside(g.LocalPath, path)));
}
