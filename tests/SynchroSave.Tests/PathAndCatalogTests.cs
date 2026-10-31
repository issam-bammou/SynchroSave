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
