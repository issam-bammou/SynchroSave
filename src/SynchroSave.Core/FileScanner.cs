using System.Text.RegularExpressions;
using SynchroSave.Core.Models;

namespace SynchroSave.Core;

public static class FileScanner
{
    /// <summary>Liste récursivement les fichiers d'un dossier (chemins relatifs avec '/').</summary>
    public static List<FileEntry> Scan(string root, string? excludePatterns = null)
    {
        var result = new List<FileEntry>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;

        var excludes = BuildExcludes(excludePatterns);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var fi = new FileInfo(file);
            if (excludes.Any(r => r.IsMatch(fi.Name))) continue;
            result.Add(new FileEntry
            {
                Path = ToRelative(root, file),
                Size = fi.Length,
                LastWriteUtc = TruncateToSeconds(fi.LastWriteTimeUtc),
            });
        }
        result.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static string ToRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    public static string ToFull(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static DateTime TruncateToSeconds(DateTime dt) =>
        new(dt.Ticks - dt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>Deux listes décrivent-elles le même contenu (nom, taille, date à 2 s près) ?</summary>
    public static bool SameFiles(IReadOnlyList<FileEntry> a, IReadOnlyList<FileEntry> b)
    {
        if (a.Count != b.Count) return false;
        var map = b.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var f in a)
        {
            if (!map.TryGetValue(f.Path, out var o)) return false;
            if (!SameFile(f, o)) return false;
        }
        return true;
    }

    public static bool SameFile(FileEntry a, FileEntry b) =>
        a.Size == b.Size && Math.Abs((a.LastWriteUtc - b.LastWriteUtc).TotalSeconds) <= 2;

    internal static List<Regex> BuildExcludes(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return new List<Regex>();
        return patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToList();
    }
}
