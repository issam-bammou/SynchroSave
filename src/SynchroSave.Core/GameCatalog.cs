using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SynchroSave.Core.Models;

namespace SynchroSave.Core;

public sealed class DetectedGame
{
    public GameDefinition Definition { get; init; } = null!;
    public string LocalPath { get; init; } = string.Empty;
    public string PathTemplate { get; init; } = string.Empty;
}

/// <summary>Base des emplacements de sauvegarde connus + détection automatique.</summary>
public sealed class GameCatalog
{
    private readonly PathResolver _resolver;

    private readonly List<GameDefinition> _local;
    private List<GameDefinition> _all;
    private readonly object _gate = new();

    public GameCatalog(PathResolver resolver, string? customDatabaseFile = null)
    {
        _resolver = resolver;
        _local = LoadBuiltIn();
        foreach (var d in _local) d.Source = "Jeu connu";
        if (customDatabaseFile != null && File.Exists(customDatabaseFile))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<List<GameDefinition>>(File.ReadAllText(customDatabaseFile), JsonFile.Options) ?? new();
                foreach (var c in custom.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
                {
                    _local.RemoveAll(d => d.Id.Equals(c.Id, StringComparison.OrdinalIgnoreCase));
                    c.Source = "Base perso";
                    _local.Add(c);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Base de jeux personnalisée ignorée ({customDatabaseFile}) : {ex.Message}");
            }
        }
        _all = _local.ToList();
    }

    /// <summary>Toutes les définitions : base intégrée/perso puis base en ligne.</summary>
    public List<GameDefinition> Definitions
    {
        get { lock (_gate) return _all; }
    }

    public int OnlineCount { get; private set; }

    /// <summary>Ajoute la base en ligne ; la base intégrée et la base perso restent prioritaires.</summary>
    public void SetOnlineDefinitions(IEnumerable<GameDefinition> online)
    {
        var ids = _local.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = _local.Select(d => NormalizeName(d.Name)).ToHashSet();
        var merged = _local.ToList();
        var count = 0;
        foreach (var d in online)
        {
            if (ids.Contains(d.Id) || names.Contains(NormalizeName(d.Name))) continue;
            d.Source = "Base en ligne (PCGamingWiki)";
            merged.Add(d);
            ids.Add(d.Id);
            count++;
        }
        lock (_gate)
        {
            _all = merged;
            OnlineCount = count;
        }
    }

    private static string NormalizeName(string n) => Slug(n).Replace("-", "");

    private static List<GameDefinition> LoadBuiltIn()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("SynchroSave.Core.games.json")
                           ?? throw new InvalidOperationException("Base de jeux intégrée manquante.");
        return JsonSerializer.Deserialize<List<GameDefinition>>(stream, JsonFile.Options) ?? new();
    }

    /// <summary>Jeux de la base dont le dossier de sauvegarde existe sur ce PC.</summary>
    public List<DetectedGame> Detect()
    {
        _resolver.RefreshInstallRoots();
        var found = new List<DetectedGame>();
        foreach (var def in Definitions)
        {
            // Plusieurs emplacements existants : on préfère celui qui ressemble à un dossier de sauvegarde.
            DetectedGame? best = null;
            foreach (var template in def.Paths)
            {
                var path = _resolver.ResolveExisting(template).FirstOrDefault();
                if (path == null) continue;
                var candidate = new DetectedGame { Definition = def, LocalPath = path, PathTemplate = template };
                if (best == null) best = candidate;
                if (template.Contains("save", StringComparison.OrdinalIgnoreCase)) { best = candidate; break; }
            }
            if (best != null) found.Add(best);
        }
        return found.OrderBy(f => f.Definition.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Ajoute à la liste des jeux suivis ceux qui viennent d'être détectés. Retourne les nouveaux.</summary>
    public List<GameEntry> MergeDetected(AppSettings settings)
    {
        var added = new List<GameEntry>();
        foreach (var d in Detect())
        {
            if (settings.Games.Any(g => g.Id.Equals(d.Definition.Id, StringComparison.OrdinalIgnoreCase))) continue;
            if (settings.Games.Any(g => SamePath(g.LocalPath, d.LocalPath))) continue;
            var entry = new GameEntry
            {
                Id = d.Definition.Id,
                Name = d.Definition.Name,
                LocalPath = d.LocalPath,
                PathTemplate = d.PathTemplate,
                Enabled = d.Definition.AutoEnable,
                LastStatus = "Détecté",
            };
            settings.Games.Add(entry);
            added.Add(entry);
        }
        return added;
    }

    /// <summary>
    /// Recherche complète : jeux connus de la base + analyse heuristique des dossiers (AppData, Documents...).
    /// Ne renvoie que ce qui n'est pas encore suivi.
    /// </summary>
    public List<SaveCandidate> FindNewCandidates(AppSettings settings, CancellationToken ct = default)
    {
        var tracked = settings.Games.ToList();
        var result = new List<SaveCandidate>();

        foreach (var d in Detect())
        {
            if (tracked.Any(g => g.Id.Equals(d.Definition.Id, StringComparison.OrdinalIgnoreCase))) continue;
            if (SaveFolderScanner.IsAlreadyTracked(d.LocalPath, tracked)) continue;
            if (result.Any(r => string.Equals(r.Path, d.LocalPath, StringComparison.OrdinalIgnoreCase))) continue;
            if (ct.IsCancellationRequested) break;
            var files = FileScanner.Scan(d.LocalPath);
            if (files.Count == 0) continue; // dossier présent mais aucune sauvegarde
            var size = files.Sum(f => f.Size);
            var big = files.Count > 5000 || size > 1024L * 1024 * 1024;
            result.Add(new SaveCandidate
            {
                Name = d.Definition.Name,
                Path = d.LocalPath,
                Template = d.PathTemplate,
                Reason = !d.Definition.AutoEnable ? "Dossier global (volumineux)"
                    : big ? d.Definition.Source + " — volumineux, à vérifier"
                    : d.Definition.Source,
                KnownGameId = d.Definition.Id,
                FileCount = files.Count,
                Size = size,
                LastWriteUtc = files.Max(f => f.LastWriteUtc),
                Preselect = d.Definition.AutoEnable && !big,
            });
        }

        var known = result.ToList();
        foreach (var c in new SaveFolderScanner(_resolver).Scan(ct: ct))
        {
            if (SaveFolderScanner.IsAlreadyTracked(c.Path, tracked)) continue;
            // Déjà couvert par un jeu connu détecté ?
            if (known.Any(k => SaveFolderScanner.IsAlreadyTracked(c.Path, new[] { new GameEntry { LocalPath = k.Path } }))) continue;
            result.Add(c);
        }
        return result;
    }

    /// <summary>Ajoute les candidats choisis par l'utilisateur à la liste des jeux suivis.</summary>
    public List<GameEntry> AddCandidates(IEnumerable<SaveCandidate> candidates, AppSettings settings)
    {
        var added = new List<GameEntry>();
        foreach (var c in candidates)
        {
            GameEntry entry;
            if (c.KnownGameId != null)
            {
                if (settings.Games.Any(g => g.Id == c.KnownGameId)) continue;
                entry = new GameEntry { Id = c.KnownGameId, Name = c.Name, LocalPath = c.Path, PathTemplate = c.Template, LastStatus = "Détecté" };
                settings.Games.Add(entry);
            }
            else
            {
                entry = CreateCustom(string.IsNullOrWhiteSpace(c.Name) ? System.IO.Path.GetFileName(c.Path) : c.Name.Trim(), c.Path, settings);
                entry.LastStatus = "Détecté";
            }
            entry.Enabled = true;
            added.Add(entry);
        }
        return added;
    }

    /// <summary>Crée une entrée pour un dossier choisi manuellement.</summary>
    public GameEntry CreateCustom(string name, string folder, AppSettings settings)
    {
        var template = _resolver.ToTemplate(folder);
        var id = MakeCustomId(name, template);
        var existing = settings.Games.FirstOrDefault(g => g.Id == id);
        if (existing != null) return existing;
        var entry = new GameEntry
        {
            Id = id,
            Name = name,
            LocalPath = folder,
            PathTemplate = template,
            Enabled = true,
            IsCustom = true,
            LastStatus = "Ajouté",
        };
        settings.Games.Add(entry);
        return entry;
    }

    internal static string MakeCustomId(string name, string template)
    {
        var slug = Slug(name);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template.ToLowerInvariant())))[..8].ToLowerInvariant();
        return $"custom-{slug}-{hash}";
    }

    public static string Slug(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-' && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "jeu" : (slug.Length > 40 ? slug[..40].Trim('-') : slug);
    }

    private static bool SamePath(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
