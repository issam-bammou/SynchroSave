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

    public GameCatalog(PathResolver resolver, string? customDatabaseFile = null)
    {
        _resolver = resolver;
        Definitions = LoadBuiltIn();
        if (customDatabaseFile != null && File.Exists(customDatabaseFile))
        {
            try
            {
                var custom = JsonSerializer.Deserialize<List<GameDefinition>>(File.ReadAllText(customDatabaseFile), JsonFile.Options) ?? new();
                foreach (var c in custom.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
                {
                    Definitions.RemoveAll(d => d.Id.Equals(c.Id, StringComparison.OrdinalIgnoreCase));
                    Definitions.Add(c);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Base de jeux personnalisée ignorée ({customDatabaseFile}) : {ex.Message}");
            }
        }
    }

    public List<GameDefinition> Definitions { get; }

    private static List<GameDefinition> LoadBuiltIn()
    {
        using var stream = typeof(GameCatalog).Assembly.GetManifestResourceStream("SynchroSave.Core.games.json")
                           ?? throw new InvalidOperationException("Base de jeux intégrée manquante.");
        return JsonSerializer.Deserialize<List<GameDefinition>>(stream, JsonFile.Options) ?? new();
    }

    /// <summary>Jeux de la base dont le dossier de sauvegarde existe sur ce PC.</summary>
    public List<DetectedGame> Detect()
    {
        var found = new List<DetectedGame>();
        foreach (var def in Definitions)
        {
            foreach (var template in def.Paths)
            {
                var path = _resolver.ResolveExisting(template).FirstOrDefault();
                if (path == null) continue;
                found.Add(new DetectedGame { Definition = def, LocalPath = path, PathTemplate = template });
                break;
            }
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
