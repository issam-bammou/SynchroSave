using System.Diagnostics;
using SynchroSave.Core;
using Xunit;
using Xunit.Abstractions;

namespace SynchroSave.Tests;

public sealed class OnlineDatabaseTests
{
    private readonly ITestOutputHelper _out;
    public OnlineDatabaseTests(ITestOutputHelper output) => _out = output;

    private static List<string> Convert(string raw, params string[] installDirs) =>
        OnlineGameDatabase.ConvertPath(raw, installDirs.Length == 0 ? new[] { "Game" } : installDirs, 1234).ToList();

    [Fact]
    public void Converts_manifest_paths_to_folder_templates()
    {
        Assert.Equal(new[] { "{AppData}\\EldenRing" }, Convert("<winAppData>/EldenRing/*/ER0000.sl2"));
        Assert.Equal(new[] { "{Documents}\\My Games\\Skyrim\\Saves" }, Convert("<winDocuments>/My Games/Skyrim/Saves"));
        Assert.Equal(new[] { "{Base:Hades II}\\Saves" }, Convert("<base>/Saves/*.sav", "Hades II"));
        Assert.Equal(new[] { "{Steam}\\userdata\\*\\1234\\remote" }, Convert("<root>/userdata/<storeUserId>/<storeGameId>/remote"));
        Assert.Equal(new[] { "{UserProfile}\\Saved Games\\Game" }, Convert("<home>/Saved Games/<game>"));
        Assert.Equal(new[] { "{LocalAppData}\\anyway" }, Convert("<winLocalAppData>/anyway/save.dat"));
    }

    [Fact]
    public void Rejects_too_generic_or_unsupported_paths()
    {
        Assert.Empty(Convert("<winAppData>/*.sav"));
        Assert.Empty(Convert("<base>/*.sav"));
        Assert.Empty(Convert("<base>/save.dat"));
        Assert.Empty(Convert("<winDocuments>/My Games/*"));
        Assert.Empty(Convert("<xdgData>/game/saves"));
        Assert.Empty(Convert("<root>/userdata/<storeUserId>/*"));
        Assert.Equal(new[] { "{AppData}\\EldenRing" }, Convert("<winAppData>/EldenRing/<storeUserId>/*.sl2"));
        Assert.Empty(Convert("<regHkcu>/Software/X"));
    }

    [Fact]
    public void Compiles_yaml_with_os_and_tag_filters()
    {
        const string yaml = """
            "Win Game":
              files:
                "<winAppData>/WinGame/Saves":
                  tags: [save]
                  when:
                    - os: windows
                "<home>/.config/wingame":
                  tags: [save]
                  when:
                    - os: linux
                "<winAppData>/WinGame/Config":
                  tags: [config]
              installDir:
                WinGame: {}
              steam:
                id: 42
            "No Files":
              steam:
                id: 1
            "Base Game":
              files:
                "<base>/Profiles/*.dat":
                  tags: [save]
              installDir:
                BaseGameDir: {}
            """;
        var defs = OnlineGameDatabase.Compile(new StringReader(yaml));
        Assert.Equal(2, defs.Count);
        Assert.Equal(new[] { "{AppData}\\WinGame\\Saves" }, defs.Single(d => d.Name == "Win Game").Paths);
        Assert.Equal(new[] { "{Base:BaseGameDir}\\Profiles" }, defs.Single(d => d.Name == "Base Game").Paths);
    }

    [Fact]
    public void Base_token_resolves_through_install_roots()
    {
        var root = Path.Combine(Path.GetTempPath(), "ssync-base-" + Guid.NewGuid().ToString("N"));
        var lib = Path.Combine(root, "D", "SteamLibrary", "steamapps", "common");
        Directory.CreateDirectory(Path.Combine(lib, "Cool Game", "Saves"));
        try
        {
            var r = new PathResolver(new Dictionary<string, string>(), () => new[] { Path.Combine(root, "nope"), lib });
            Assert.Equal(Path.Combine(lib, "Cool Game", "Saves"), r.ResolveExisting("{Base:Cool Game}\\Saves").Single());
            Assert.Empty(r.ResolveExisting("{Base:Not Installed}\\Saves"));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Contrôle sur le vrai manifeste si disponible localement (SSYNC_MANIFEST=chemin).</summary>
    [Fact]
    public void Real_manifest_compiles_quickly()
    {
        var path = Environment.GetEnvironmentVariable("SSYNC_MANIFEST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var sw = Stopwatch.StartNew();
        using var reader = new StreamReader(path);
        var defs = OnlineGameDatabase.Compile(reader);
        _out.WriteLine($"{defs.Count} jeux, {defs.Sum(d => d.Paths.Count)} chemins en {sw.ElapsedMilliseconds} ms");
        foreach (var name in new[] { "Elden Ring", "Mortal Shell 2", "EA Sports FC 25", "Hades II", "Hollow Knight", "Celeste", "Slay the Spire" })
        {
            var d = defs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _out.WriteLine($"  {name}: {(d == null ? "-" : string.Join(" | ", d.Paths))}");
        }
        Assert.True(defs.Count > 5000);
    }
}

public sealed class OnlineDownloadTests
{
    private readonly ITestOutputHelper _out;
    public OnlineDownloadTests(ITestOutputHelper output) => _out = output;

    /// <summary>Téléchargement réel (seulement si SSYNC_ONLINE=1).</summary>
    [Fact]
    public async Task Downloads_compiles_and_reuses_etag()
    {
        if (Environment.GetEnvironmentVariable("SSYNC_ONLINE") != "1") return;
        var dir = Path.Combine(Path.GetTempPath(), "ssync-online-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new OnlineGameDatabase(new AppPaths(dir));
            var sw = Stopwatch.StartNew();
            Assert.True(await db.UpdateAsync());
            _out.WriteLine($"Téléchargement + compilation : {sw.ElapsedMilliseconds} ms, {db.Info.GameCount} jeux");
            sw.Restart();
            var defs = db.Load();
            _out.WriteLine($"Chargement : {sw.ElapsedMilliseconds} ms, fichier {new FileInfo(Path.Combine(dir, "online", "online-games.json")).Length / 1024} Ko");
            Assert.Equal(db.Info.GameCount, defs.Count);
            Assert.False(await db.UpdateAsync()); // 304 Not Modified grâce à l'ETag
            var catalog = new GameCatalog(new PathResolver());
            catalog.SetOnlineDefinitions(defs);
            _out.WriteLine($"Catalogue : {catalog.Definitions.Count} définitions ({catalog.OnlineCount} en ligne)");
            sw.Restart();
            catalog.Detect();
            _out.WriteLine($"Détection complète : {sw.ElapsedMilliseconds} ms");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
