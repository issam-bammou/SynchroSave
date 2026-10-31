using SynchroSave.Core;
using SynchroSave.Core.Models;
using Xunit;

namespace SynchroSave.Tests;

public sealed class PathAndCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssync-path-" + Guid.NewGuid().ToString("N"));
    public PathAndCatalogTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private PathResolver Resolver() => new(new Dictionary<string, string>
    {
        ["UserProfile"] = Path.Combine(_root, "user"),
        ["Documents"] = Path.Combine(_root, "user", "Documents"),
        ["AppData"] = Path.Combine(_root, "user", "AppData", "Roaming"),
        ["Steam"] = Path.Combine(_root, "steam"),
    });

    [Fact]
    public void Template_roundtrip_prefers_most_specific_token()
    {
        var r = Resolver();
        var abs = Path.Combine(_root, "user", "Documents", "My Games", "Terraria");
        var t = r.ToTemplate(abs);
        Assert.Equal("{Documents}" + Path.DirectorySeparatorChar + "My Games" + Path.DirectorySeparatorChar + "Terraria", t);
        Assert.Equal(abs, r.Expand(t));
    }

    [Fact]
    public void Wildcards_are_expanded()
    {
        var r = Resolver();
        var p = Path.Combine(_root, "steam", "userdata", "12345", "570", "remote");
        Directory.CreateDirectory(p);
        Assert.Equal(p, r.ResolveExisting("{Steam}\\userdata\\*\\570\\remote").Single());
    }

    [Fact]
    public void Catalog_detects_existing_games()
    {
        var r = Resolver();
        Directory.CreateDirectory(Path.Combine(_root, "user", "AppData", "Roaming", "EldenRing"));
        var catalog = new GameCatalog(r);
        Assert.True(catalog.Definitions.Count > 30);
        var settings = new AppSettings();
        var added = catalog.MergeDetected(settings);
        Assert.Contains(added, g => g.Id == "elden-ring" && g.PathTemplate == "{AppData}\\EldenRing");
        Assert.Empty(catalog.MergeDetected(settings)); // pas de doublon
    }

    [Fact]
    public void Custom_ids_are_stable_and_slugged()
    {
        Assert.Equal(GameCatalog.MakeCustomId("Mon Jeu Préféré!", "{Documents}\\X"), GameCatalog.MakeCustomId("Mon Jeu Préféré!", "{documents}\\x"));
        Assert.StartsWith("custom-mon-jeu-prefere-", GameCatalog.MakeCustomId("Mon Jeu Préféré!", "a"));
    }

    [Fact]
    public void Version_names_roundtrip()
    {
        var when = new DateTime(2026, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        var name = CloudRepository.BuildVersionName(when, "MY_PC", "conflit");
        var v = CloudRepository.ParseVersionName(Path.Combine(_root, name))!;
        Assert.Equal(when, v.CreatedUtc);
        Assert.Equal("MY-PC", v.Machine);
        Assert.Equal("conflit", v.Reason);
    }
}

public sealed class SaveFolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssync-scan-" + Guid.NewGuid().ToString("N"));
    public SaveFolderScannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Local => Path.Combine(_root, "AppData", "Local");
    private string Docs => Path.Combine(_root, "Documents");

    private PathResolver Resolver() => new(new Dictionary<string, string>
    {
        ["UserProfile"] = _root,
        ["LocalAppData"] = Local,
        ["AppData"] = Path.Combine(_root, "AppData", "Roaming"),
        ["LocalLow"] = Path.Combine(_root, "AppData", "LocalLow"),
        ["Documents"] = Docs,
        ["SavedGames"] = Path.Combine(_root, "Saved Games"),
    });

    private static void Touch(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Finds_unreal_and_named_save_folders_and_skips_noise()
    {
        Touch(Path.Combine(Local, "MortalShell2", "Saved", "SaveGames", "slot0.sav"));
        Touch(Path.Combine(Local, "MortalShell2", "Saved", "Logs", "game.log"));
        Touch(Path.Combine(Local, "SomeIndie", "profile1.sl2"));
        Touch(Path.Combine(Docs, "My Games", "CoolGame", "Saves", "a.dat"));
        Touch(Path.Combine(_root, "AppData", "LocalLow", "Studio", "LowGame", "save.es3"));
        Touch(Path.Combine(Local, "Google", "Chrome", "Saves", "x.sav")); // exclu
        Touch(Path.Combine(Local, "FortniteGame", "Saved", "Config", "x.ini")); // pas de sauvegarde
        Directory.CreateDirectory(Path.Combine(Local, "EmptyGame", "Saved", "SaveGames")); // vide

        var found = new SaveFolderScanner(Resolver()).Scan();
        var names = found.Select(f => f.Name).ToList();

        Assert.Contains("Mortal Shell 2", names);
        Assert.Contains("Some Indie", names);
        Assert.Contains("Cool Game", names);
        Assert.Contains("Low Game", names);
        Assert.DoesNotContain(found, f => f.Path.Contains("Google"));
        Assert.DoesNotContain(found, f => f.Path.Contains("FortniteGame"));
        Assert.DoesNotContain(found, f => f.Path.Contains("EmptyGame"));
        Assert.Single(found, f => f.Name == "Mortal Shell 2");
        Assert.Equal("{LocalAppData}" + Path.DirectorySeparatorChar + Path.Combine("MortalShell2", "Saved", "SaveGames"),
            found.Single(f => f.Name == "Mortal Shell 2").Template);
    }

    [Fact]
    public void Candidates_exclude_tracked_and_known_games()
    {
        Touch(Path.Combine(Local, "EA SPORTS FC 26", "settings", "Career-20261001.sav"));
        Touch(Path.Combine(Local, "Tracked", "Saved", "SaveGames", "s.sav"));
        Touch(Path.Combine(Local, "NewOne", "Saved", "SaveGames", "s.sav"));

        var r = Resolver();
        var catalog = new GameCatalog(r);
        var settings = new SynchroSave.Core.Models.AppSettings();
        settings.Games.Add(new SynchroSave.Core.Models.GameEntry { Id = "t", Name = "Tracked", LocalPath = Path.Combine(Local, "Tracked") });

        var c = catalog.FindNewCandidates(settings);
        Assert.Single(c, x => x.KnownGameId == "ea-fc-26");
        Assert.Single(c, x => x.Name == "New One");
        Assert.DoesNotContain(c, x => x.Path.Contains("Tracked"));
        Assert.DoesNotContain(c, x => x.KnownGameId == null && x.Path.Contains("EA SPORTS"));

        var added = catalog.AddCandidates(c, settings);
        Assert.Equal(c.Count, added.Count);
        Assert.Empty(catalog.FindNewCandidates(settings));
    }
}
