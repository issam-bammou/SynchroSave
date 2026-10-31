using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SynchroSave.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SynchroSave.Core;

public sealed class OnlineDatabaseInfo
{
    public string? ETag { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public DateTime? LastUpdateUtc { get; set; }
    public int GameCount { get; set; }
    public int CompilerVersion { get; set; }
}

/// <summary>
/// Base communautaire des emplacements de sauvegarde (manifeste Ludusavi, issu de PCGamingWiki) :
/// des dizaines de milliers de jeux. Téléchargée depuis internet, mise à jour régulièrement,
/// puis convertie en <see cref="GameDefinition"/> utilisables par la détection.
/// </summary>
public sealed class OnlineGameDatabase
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
    private const int CurrentCompilerVersion = 1;

    private readonly string _dir;

    public OnlineGameDatabase(AppPaths paths) => _dir = Path.Combine(paths.DataRoot, "online");

    private string CompiledFile => Path.Combine(_dir, "online-games.json");
    private string InfoFile => Path.Combine(_dir, "info.json");

    public OnlineDatabaseInfo Info => JsonFileSafe<OnlineDatabaseInfo>(InfoFile) ?? new OnlineDatabaseInfo();

    public bool IsAvailable => File.Exists(CompiledFile) && Info.CompilerVersion == CurrentCompilerVersion;

    public List<GameDefinition> Load() => JsonFileSafe<List<GameDefinition>>(CompiledFile) ?? new List<GameDefinition>();

    public bool IsUpdateDue(int days) =>
        !IsAvailable || Info.LastCheckUtc == null || DateTime.UtcNow - Info.LastCheckUtc.Value > TimeSpan.FromDays(Math.Max(1, days));

    /// <summary>Télécharge la base si elle a changé (ETag). Retourne vrai si une nouvelle version a été installée.</summary>
    public async Task<bool> UpdateAsync(bool force = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var info = Info;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SynchroSave", "1.0"));
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        if (!force && IsAvailable && !string.IsNullOrEmpty(info.ETag))
            request.Headers.TryAddWithoutValidation("If-None-Match", info.ETag);

        progress?.Report("Téléchargement de la base de jeux en ligne…");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            info.LastCheckUtc = DateTime.UtcNow;
            JsonFile.Write(InfoFile, info);
            progress?.Report("Base de jeux en ligne déjà à jour.");
            return false;
        }
        response.EnsureSuccessStatusCode();

        var yamlFile = Path.Combine(_dir, "manifest.yaml");
        var tmp = yamlFile + ".download";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            await response.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        File.Move(tmp, yamlFile, overwrite: true);

        progress?.Report("Analyse de la base de jeux…");
        List<GameDefinition> games;
        using (var reader = new StreamReader(yamlFile))
            games = await Task.Run(() => Compile(reader), ct).ConfigureAwait(false);
        JsonFile.Write(CompiledFile, games);

        info.ETag = response.Headers.ETag?.Tag;
        info.LastCheckUtc = info.LastUpdateUtc = DateTime.UtcNow;
        info.GameCount = games.Count;
        info.CompilerVersion = CurrentCompilerVersion;
        JsonFile.Write(InfoFile, info);
        try { File.Delete(yamlFile); } catch { /* la version compilée suffit */ }
        progress?.Report($"Base de jeux en ligne mise à jour : {games.Count:N0} jeux.");
        return true;
    }

    // ------------------------------------------------------------------ Conversion du manifeste

    private sealed class ManifestGame
    {
        public Dictionary<string, ManifestFile?>? Files { get; set; }
        public Dictionary<string, object?>? InstallDir { get; set; }
        public ManifestSteam? Steam { get; set; }
    }

    private sealed class ManifestFile
    {
        public List<string>? Tags { get; set; }
        public List<ManifestWhen>? When { get; set; }
    }

    private sealed class ManifestWhen
    {
        public string? Os { get; set; }
        public string? Store { get; set; }
    }

    private sealed class ManifestSteam
    {
        public long? Id { get; set; }
    }

    private static readonly HashSet<string> TooGeneric = new(StringComparer.OrdinalIgnoreCase)
    {
        "{UserProfile}", "{UserProfile}\\Documents", "{Documents}", "{Documents}\\My Games", "{UserProfile}\\Documents\\My Games",
        "{UserProfile}\\Saved Games", "{SavedGames}", "{UserProfile}\\AppData", "{UserProfile}\\AppData\\Local",
        "{UserProfile}\\AppData\\LocalLow", "{UserProfile}\\AppData\\Roaming", "{AppData}", "{LocalAppData}", "{LocalLow}",
        "{Steam}", "{Steam}\\userdata", "{Steam}\\userdata\\*", "{Public}", "{Public}\\Documents", "{ProgramData}", "{WinDir}",
        "{LocalAppData}\\Packages", "{UserProfile}\\AppData\\Local\\Packages", "{Documents}\\Saved Games", "{UserProfile}\\Documents\\Saved Games",
    };

    public static List<GameDefinition> Compile(TextReader yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var manifest = deserializer.Deserialize<Dictionary<string, ManifestGame?>>(yaml) ?? new();

        var result = new List<GameDefinition>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, game) in manifest)
        {
            if (game?.Files == null || game.Files.Count == 0) continue;
            var installDirs = game.InstallDir?.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? new List<string>();
            if (installDirs.Count == 0) installDirs.Add(name);

            var paths = new List<string>();
            foreach (var (rawPath, file) in game.Files)
            {
                if (file?.Tags != null && file.Tags.Count > 0 && !file.Tags.Contains("save")) continue;
                if (file?.When != null && file.When.Count > 0 &&
                    !file.When.Any(w => w.Os == null || w.Os.Equals("windows", StringComparison.OrdinalIgnoreCase)))
                    continue;
                foreach (var p in ConvertPath(rawPath, installDirs, game.Steam?.Id))
                    if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
            }
            // Un dossier inclus dans un autre du même jeu est inutile.
            paths = paths.Where(p => !paths.Any(o => !ReferenceEquals(o, p) &&
                                                     p.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
            if (paths.Count == 0) continue;

            // Les dossiers « classiques » d'abord, ceux du dossier d'installation ensuite.
            paths = paths.OrderBy(p => p.StartsWith("{Base:", StringComparison.Ordinal) ? 1 : 0).ToList();

            var id = GameCatalog.Slug(name);
            if (!usedIds.Add(id))
            {
                id = game.Steam?.Id is { } sid ? $"{id}-{sid}" : $"{id}-{usedIds.Count}";
                usedIds.Add(id);
            }
            result.Add(new GameDefinition { Id = id, Name = name, Paths = paths, AutoEnable = true });
        }
        return result;
    }

    /// <summary>Convertit un chemin du manifeste (avec &lt;jetons&gt; et jokers) en modèle(s) de dossier SynchroSave.</summary>
    internal static IEnumerable<string> ConvertPath(string rawPath, IReadOnlyList<string> installDirs, long? steamId)
    {
        var segments = rawPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count == 0) yield break;

        // On s'arrête au premier segment contenant un joker de fichier : on synchronise le dossier parent.
        var cut = segments.FindIndex(s => s.IndexOfAny(new[] { '*', '?', '[', ']', '{', '}' }) >= 0);
        var cutHappened = cut >= 0;
        if (cutHappened) segments = segments.Take(cut).ToList();
        // Dernier segment ressemblant à un nom de fichier (ex: save.dat) : on garde le dossier.
        else if (segments.Count > 1 && LooksLikeFile(segments[^1])) segments.RemoveAt(segments.Count - 1);
        if (segments.Count < 2) yield break;

        var first = segments[0];
        string? root = first switch
        {
            "<winAppData>" => "{AppData}",
            "<winLocalAppData>" => "{LocalAppData}",
            "<winLocalAppDataLow>" => "{LocalLow}",
            "<winDocuments>" => "{Documents}",
            "<home>" => "{UserProfile}",
            "<winPublic>" => "{Public}",
            "<winProgramData>" => "{ProgramData}",
            "<winDir>" => "{WinDir}",
            "<root>" => segments.Count > 1 && segments[1].Equals("userdata", StringComparison.OrdinalIgnoreCase) ? "{Steam}" : null,
            "<base>" => "BASE",
            _ => null,
        };
        if (root == null) yield break;

        var dirs = first is "<base>" || rawPath.Contains("<game>") ? installDirs : new[] { "" };
        foreach (var dir in dirs.Take(3))
        {
            var mapped = new List<string> { root == "BASE" ? "{Base:" + dir + "}" : root };
            var ok = true;
            foreach (var seg in segments.Skip(1))
            {
                var s = seg.Replace("<game>", dir)
                    .Replace("<storeUserId>", "*")
                    .Replace("<osUserName>", "{OsUserName}")
                    .Replace("<storeGameId>", steamId?.ToString() ?? "*");
                if (s.Contains('<') || s.Contains('>')) { ok = false; break; }
                mapped.Add(s);
            }
            if (!ok) continue;
            // Identifiant (Steam…) en dernier segment : on garde le dossier parent, qui contient tous les comptes.
            while (mapped.Count > 1 && mapped[^1] == "*") mapped.RemoveAt(mapped.Count - 1);
            var template = string.Join('\\', mapped);
            if (TooGeneric.Contains(template)) continue;
            if (root == "BASE" && mapped.Count < 2) continue; // tout le dossier d'installation : trop gros
            yield return template;
        }
    }

    private static bool LooksLikeFile(string segment)
    {
        var ext = Path.GetExtension(segment);
        return ext.Length is >= 2 and <= 9 && ext.Skip(1).All(char.IsLetterOrDigit) && !segment.StartsWith('.');
    }

    private static T? JsonFileSafe<T>(string path) where T : class
    {
        try { return JsonFile.Read<T>(path); }
        catch { return null; }
    }
}
