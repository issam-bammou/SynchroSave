using SynchroSave.Core;
using SynchroSave.Core.Models;
using Xunit;

namespace SynchroSave.Tests;

/// <summary>Simule deux PC (A et B) partageant le même dossier OneDrive.</summary>
public sealed class SyncEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ssync-tests-" + Guid.NewGuid().ToString("N"));
    private string OneDrive => Path.Combine(_root, "OneDrive");

    public SyncEngineTests() => Directory.CreateDirectory(OneDrive);
    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record Pc(string Name, string SavePath, AppSettings Settings, SyncEngine Engine, SyncStateStore States, AppPaths Paths)
    {
        public GameEntry Game { get; } = new() { Id = "test-game", Name = "Test Game", LocalPath = SavePath, PathTemplate = "{Documents}\\Test" };
        public SyncResult Sync() => Engine.SyncGame(Game, Settings);
    }

    private Pc MakePc(string name, SyncMode mode = SyncMode.TwoWay)
    {
        var data = Path.Combine(_root, name, "data");
        var save = Path.Combine(_root, name, "saves");
        Directory.CreateDirectory(save);
        var paths = new AppPaths(data);
        var states = new SyncStateStore(paths);
        var settings = new AppSettings { OneDriveRoot = OneDrive, MachineName = name, Mode = mode, VersionsToKeep = 3 };
        return new Pc(name, save, settings, new SyncEngine(paths, states), states, paths);
    }

    private static void WriteSave(Pc pc, string rel, string content, DateTime? when = null)
    {
        var p = Path.Combine(pc.SavePath, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, when ?? DateTime.UtcNow);
    }

    private static string ReadSave(Pc pc, string rel) => File.ReadAllText(Path.Combine(pc.SavePath, rel));

    [Fact]
    public void First_sync_uploads_files_and_manifest()
    {
        var a = MakePc("PC-A");
        WriteSave(a, "slot1.sav", "hello");
        WriteSave(a, "sub/slot2.sav", "world");

        var r = a.Sync();

        Assert.Equal(SyncAction.Uploaded, r.Action);
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.True(File.Exists(Path.Combine(repo.CurrentDir("test-game"), "sub", "slot2.sav")));
        var m = repo.ReadManifest("test-game")!;
        Assert.Equal(2, m.Files.Count);
        Assert.Equal("PC-A", m.SourceMachine);

        Assert.Equal(SyncAction.None, a.Sync().Action);
    }

    [Fact]
    public void Change_is_uploaded_and_previous_version_archived()
    {
        var a = MakePc("PC-A");
        WriteSave(a, "slot1.sav", "v1", DateTime.UtcNow.AddMinutes(-10));
        a.Sync();
        WriteSave(a, "slot1.sav", "v2-longer");
        Assert.Equal(SyncAction.Uploaded, a.Sync().Action);

        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.Equal("v2-longer", File.ReadAllText(Path.Combine(repo.CurrentDir("test-game"), "slot1.sav")));
        Assert.Single(repo.ListVersions("test-game"));
    }

    [Fact]
    public void Versions_are_pruned()
    {
        var a = MakePc("PC-A");
        for (var i = 0; i < 6; i++)
        {
            WriteSave(a, "slot1.sav", new string('x', i + 1), DateTime.UtcNow.AddMinutes(-100 + i));
            a.Sync();
            Thread.Sleep(1100); // noms de version horodatés à la seconde
        }
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.Equal(3, repo.ListVersions("test-game").Count);
    }

    [Fact]
    public void Deleted_local_file_is_removed_from_cloud_current()
    {
        var a = MakePc("PC-A");
        WriteSave(a, "a.sav", "1");
        WriteSave(a, "b.sav", "2");
        a.Sync();
        File.Delete(Path.Combine(a.SavePath, "b.sav"));
        a.Sync();
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.False(File.Exists(Path.Combine(repo.CurrentDir("test-game"), "b.sav")));
    }

    [Fact]
    public void Empty_local_folder_never_wipes_cloud_in_backup_mode()
    {
        var a = MakePc("PC-A", SyncMode.BackupOnly);
        WriteSave(a, "a.sav", "1");
        a.Sync();
        File.Delete(Path.Combine(a.SavePath, "a.sav"));
        Assert.Equal(SyncAction.Skipped, a.Sync().Action);
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.Single(repo.ReadManifest("test-game")!.Files);
    }

    [Fact]
    public void TwoWay_other_pc_downloads_newer_version()
    {
        var a = MakePc("PC-A");
        var b = MakePc("PC-B");
        WriteSave(a, "slot.sav", "from A", DateTime.UtcNow.AddMinutes(-5));
        a.Sync();

        // B n'a rien : il récupère.
        Assert.Equal(SyncAction.Downloaded, b.Sync().Action);
        Assert.Equal("from A", ReadSave(b, "slot.sav"));

        // B joue puis synchronise ; A récupère.
        WriteSave(b, "slot.sav", "from B, later");
        Assert.Equal(SyncAction.Uploaded, b.Sync().Action);
        Assert.Equal(SyncAction.Downloaded, a.Sync().Action);
        Assert.Equal("from B, later", ReadSave(a, "slot.sav"));

        // Une sauvegarde de sécurité locale a été faite sur A avant d'écraser.
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(a.Paths.LocalBackupsDirectory, "test-game")));
    }

    [Fact]
    public void TwoWay_conflict_newest_wins_and_loser_is_archived()
    {
        var a = MakePc("PC-A");
        var b = MakePc("PC-B");
        WriteSave(a, "slot.sav", "base", DateTime.UtcNow.AddMinutes(-30));
        a.Sync();
        b.Sync();

        WriteSave(a, "slot.sav", "A edit", DateTime.UtcNow.AddMinutes(-10));
        a.Sync();
        WriteSave(b, "slot.sav", "B edit newer", DateTime.UtcNow);

        var r = b.Sync();
        Assert.Equal(SyncAction.ConflictResolvedUpload, r.Action);
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.Contains(repo.ListVersions("test-game"), v => v.Reason == "conflit");

        Assert.Equal(SyncAction.Downloaded, a.Sync().Action);
        Assert.Equal("B edit newer", ReadSave(a, "slot.sav"));
    }

    [Fact]
    public void BackupOnly_does_not_overwrite_newer_cloud_version_from_other_pc()
    {
        var a = MakePc("PC-A", SyncMode.BackupOnly);
        var b = MakePc("PC-B", SyncMode.BackupOnly);
        WriteSave(a, "slot.sav", "old", DateTime.UtcNow.AddMinutes(-60));
        a.Sync();
        WriteSave(b, "slot.sav", "newer from B", DateTime.UtcNow);
        b.Sync();

        // A n'a pas rejoué : il ne doit pas remettre l'ancienne version.
        Assert.Equal(SyncAction.Skipped, a.Sync().Action);
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.Equal("PC-B", repo.ReadManifest("test-game")!.SourceMachine);
        Assert.Equal("old", ReadSave(a, "slot.sav"));
    }

    [Fact]
    public void Restore_latest_and_older_version_on_new_pc()
    {
        var a = MakePc("PC-A", SyncMode.BackupOnly);
        WriteSave(a, "slot.sav", "v1", DateTime.UtcNow.AddMinutes(-60));
        a.Sync();
        Thread.Sleep(1100);
        WriteSave(a, "slot.sav", "v2!", DateTime.UtcNow);
        a.Sync();

        var b = MakePc("PC-B", SyncMode.BackupOnly);
        var restore = new RestoreService(b.Engine, b.States, new PathResolver(new Dictionary<string, string> { ["Documents"] = Path.Combine(_root, "PC-B", "docs") }));
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        var manifest = repo.ListGames().Single();

        var target = restore.SuggestTarget(manifest, b.Settings);
        Assert.Equal(Path.Combine(_root, "PC-B", "docs", "Test"), target);

        var game = restore.Restore(manifest, null, target, b.Settings);
        Assert.Equal("v2!", File.ReadAllText(Path.Combine(target, "slot.sav")));
        Assert.Contains(b.Settings.Games, g => g.Id == "test-game" && g.Enabled);

        // Après restauration de la dernière version, la synchro de B ne fait rien.
        Assert.Equal(SyncAction.None, b.Engine.SyncGame(game, b.Settings).Action);

        // Restauration d'une ancienne version : elle devient la version courante à la synchro suivante.
        var old = repo.ListVersions("test-game").Single();
        restore.Restore(manifest, old, target, b.Settings);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(target, "slot.sav")));
        Assert.Equal(SyncAction.Uploaded, b.Engine.SyncGame(game, b.Settings).Action);
    }

    [Fact]
    public void Exclude_patterns_are_respected()
    {
        var a = MakePc("PC-A");
        WriteSave(a, "slot.sav", "1");
        WriteSave(a, "cache.tmp", "junk");
        a.Sync();
        var repo = new CloudRepository(OneDrive, "SynchroSave");
        Assert.Single(repo.ReadManifest("test-game")!.Files);
    }
}
