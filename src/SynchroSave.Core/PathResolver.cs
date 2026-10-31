namespace SynchroSave.Core;

/// <summary>
/// Convertit des chemins "portables" contenant des jetons ({Documents}, {AppData}, ...) en chemins réels
/// et inversement. C'est ce qui permet de restaurer une sauvegarde sur un autre PC où le nom
/// d'utilisateur ou l'emplacement de Documents est différent.
/// </summary>
public sealed class PathResolver
{
    private readonly Dictionary<string, string> _tokens;
    private readonly Func<IEnumerable<string>>? _installRootsProvider;
    private List<(string Root, HashSet<string> Children)>? _installRoots;
    private readonly object _rootsGate = new();
    private static readonly System.Text.RegularExpressions.Regex BaseToken =
        new(@"\{Base:([^}]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <param name="installRootsProvider">
    /// Dossiers contenant des jeux installés, pour le jeton {Base:NomDuDossier} (sauvegardes dans le dossier du jeu).
    /// </param>
    public PathResolver(IDictionary<string, string>? tokens = null, Func<IEnumerable<string>>? installRootsProvider = null)
    {
        _installRootsProvider = installRootsProvider;
        _tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in tokens ?? DefaultTokens())
        {
            if (!string.IsNullOrWhiteSpace(kv.Value))
                _tokens[kv.Key] = kv.Value.TrimEnd('\\', '/');
        }
    }

    public IReadOnlyDictionary<string, string> Tokens => _tokens;

    public static Dictionary<string, string> DefaultTokens()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserProfile"] = profile,
            ["Documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ["AppData"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["LocalAppData"] = local,
            ["LocalLow"] = string.IsNullOrEmpty(local) ? "" : Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            ["SavedGames"] = string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, "Saved Games"),
            ["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["PublicDocuments"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            ["ProgramFiles"] = pf,
            ["ProgramFilesX86"] = string.IsNullOrEmpty(pf86) ? pf : pf86,
        };
        d["Steam"] = string.IsNullOrEmpty(d["ProgramFilesX86"]) ? "" : Path.Combine(d["ProgramFilesX86"], "Steam");
        d["Public"] = Environment.GetEnvironmentVariable("PUBLIC") ?? "";
        d["WinDir"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        d["OsUserName"] = Environment.UserName;
        return d;
    }

    /// <summary>Relit la liste des dossiers d'installation de jeux (nouveaux jeux installés...).</summary>
    public void RefreshInstallRoots()
    {
        lock (_rootsGate) _installRoots = null;
    }

    private List<(string Root, HashSet<string> Children)> GetInstallRoots()
    {
        lock (_rootsGate)
        {
            if (_installRoots != null) return _installRoots;
            var list = new List<(string, HashSet<string>)>();
            if (_installRootsProvider != null)
            {
                IEnumerable<string> roots;
                try { roots = _installRootsProvider().ToList(); }
                catch { roots = Array.Empty<string>(); }
                foreach (var root in roots)
                {
                    try
                    {
                        var children = Directory.GetDirectories(root).Select(Path.GetFileName)
                            .Where(n => n != null).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        list.Add((root, children));
                    }
                    catch { /* inaccessible */ }
                }
            }
            return _installRoots = list;
        }
    }

    public IReadOnlyList<string> InstallRootPaths => GetInstallRoots().Select(r => r.Root).ToList();

    private string ExpandBase(string template)
    {
        if (!template.Contains("{Base:", StringComparison.Ordinal)) return template;
        return BaseToken.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            foreach (var (root, children) in GetInstallRoots())
                if (children.Contains(name)) return Path.Combine(root, name);
            return m.Value; // jeu non installé : jeton laissé tel quel (non résolu)
        });
    }

    /// <summary>Remplace les jetons {X} et les variables d'environnement %X%.</summary>
    public string Expand(string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;
        var result = ExpandBase(template);
        foreach (var kv in _tokens)
            result = result.Replace("{" + kv.Key + "}", kv.Value, StringComparison.OrdinalIgnoreCase);
        result = Environment.ExpandEnvironmentVariables(result);
        return NormalizeSeparators(result);
    }

    /// <summary>Résout un modèle en dossiers existants (gère les jokers '*' dans les segments).</summary>
    public IEnumerable<string> ResolveExisting(string template)
    {
        var expanded = Expand(template);
        if (string.IsNullOrEmpty(expanded) || expanded.Contains('{')) yield break;

        if (!expanded.Contains('*') && !expanded.Contains('?'))
        {
            if (Directory.Exists(expanded)) yield return expanded;
            yield break;
        }

        foreach (var p in ExpandWildcards(expanded))
            yield return p;
    }

    private static IEnumerable<string> ExpandWildcards(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var segments = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = new[] { string.IsNullOrEmpty(root) ? "." : root };
        foreach (var seg in segments)
        {
            var s = seg;
            current = current.SelectMany(dir =>
            {
                try
                {
                    if (s.Contains('*') || s.Contains('?'))
                        return Directory.Exists(dir) ? Directory.GetDirectories(dir, s) : Array.Empty<string>();
                    var next = Path.Combine(dir, s);
                    return Directory.Exists(next) ? new[] { next } : Array.Empty<string>();
                }
                catch (Exception)
                {
                    return Array.Empty<string>();
                }
            }).ToList();
        }
        return current;
    }

    /// <summary>Transforme un chemin absolu en modèle portable (le jeton le plus spécifique gagne).</summary>
    public string ToTemplate(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return string.Empty;
        var full = NormalizeSeparators(absolutePath).TrimEnd(Path.DirectorySeparatorChar);
        // Jetons dans un ordre déterministe : plus long d'abord.
        foreach (var kv in _tokens.OrderByDescending(k => k.Value.Length).ThenBy(k => k.Key))
        {
            var value = NormalizeSeparators(kv.Value);
            if (value.Length == 0 || !Path.IsPathRooted(value)) continue;
            if (full.Equals(value, StringComparison.OrdinalIgnoreCase))
                return "{" + kv.Key + "}";
            if (full.StartsWith(value + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "{" + kv.Key + "}" + full[value.Length..];
        }
        return full;
    }

    private static string NormalizeSeparators(string p) =>
        p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
}
