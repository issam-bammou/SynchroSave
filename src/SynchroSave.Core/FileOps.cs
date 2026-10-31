using System.IO.Compression;

namespace SynchroSave.Core;

internal static class FileOps
{
    /// <summary>Copie un fichier même s'il est ouvert par le jeu, via un fichier temporaire, en conservant la date.</summary>
    public static void CopyFile(string source, string destination, DateTime? lastWriteUtc = null)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = destination + ".ssync-tmp";

        Retry(() =>
        {
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                src.CopyTo(dst);
            }
            var ts = lastWriteUtc ?? File.GetLastWriteTimeUtc(source);
            File.SetLastWriteTimeUtc(tmp, ts);
            if (File.Exists(destination))
            {
                var attrs = File.GetAttributes(destination);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(destination, attrs & ~FileAttributes.ReadOnly);
            }
            File.Move(tmp, destination, overwrite: true);
        });
    }

    public static void DeleteFile(string path)
    {
        Retry(() =>
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        });
    }

    /// <summary>Supprime les dossiers vides restant sous root (root est conservé).</summary>
    public static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>Compresse un dossier en zip (fichiers verrouillés lus en partage).</summary>
    public static void ZipDirectory(string sourceDir, string zipPath, IEnumerable<string>? relativeFiles = null)
    {
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = zipPath + ".tmp";
        var files = relativeFiles?.ToList() ?? FileScanner.Scan(sourceDir).Select(f => f.Path).ToList();

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var rel in files)
            {
                var full = FileScanner.ToFull(sourceDir, rel);
                if (!File.Exists(full)) continue;
                var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                var lw = File.GetLastWriteTime(full);
                if (lw.Year >= 1980 && lw.Year <= 2107) entry.LastWriteTime = lw;
                using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        File.Move(tmp, zipPath, overwrite: true);
    }

    /// <summary>Extrait un zip dans un dossier (protection contre les chemins sortant du dossier).</summary>
    public static List<string> ExtractZip(string zipPath, string targetDir)
    {
        var written = new List<string>();
        var rootFull = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dossier
            var rel = entry.FullName.Replace('\\', '/');
            var dest = Path.GetFullPath(FileScanner.ToFull(targetDir, rel));
            if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Entrée de zip invalide : {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".ssync-tmp";
            Retry(() =>
            {
                entry.ExtractToFile(tmp, overwrite: true);
                File.SetLastWriteTime(tmp, entry.LastWriteTime.DateTime);
                File.Move(tmp, dest, overwrite: true);
            });
            written.Add(rel);
        }
        return written;
    }

    public static void Retry(Action action, int attempts = 4)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (i < attempts)
            {
                Thread.Sleep(250 * i);
            }
            catch (UnauthorizedAccessException) when (i < attempts)
            {
                Thread.Sleep(250 * i);
            }
        }
    }

    public static string SafeName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var chars = s.Select(c => invalid.Contains(c) ? '-' : c).ToArray();
        return new string(chars).Replace('_', '-').Trim();
    }
}
