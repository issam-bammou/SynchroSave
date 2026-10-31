using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SynchroSave.Core.Models;

namespace SynchroSave.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Ligne de l'onglet « Mes jeux ».</summary>
public sealed class GameRow : Observable
{
    public GameRow(GameEntry entry) => Entry = entry;

    public GameEntry Entry { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string LocalPath => Entry.LocalPath;
    public string Status => Entry.LastStatus;
    public string LastSyncText => Entry.LastSyncUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—";

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            lock (App.Service.Gate) Entry.Enabled = value;
            App.Service.ApplySettings();
            Raise();
        }
    }
}

/// <summary>Ligne de l'onglet « Restaurer ».</summary>
public sealed class CloudGameRow : Observable
{
    private bool _selected;
    private string _targetPath = string.Empty;

    public CloudGameRow(CloudManifest manifest) => Manifest = manifest;

    public CloudManifest Manifest { get; }
    public string Name => Manifest.GameName;
    public string SourceMachine => Manifest.SourceMachine;
    public string UpdatedText => Manifest.UpdatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string SizeText => FormatSize(Manifest.TotalSize);
    public string LocalState { get; set; } = string.Empty;

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Raise(); }
    }

    public string TargetPath
    {
        get => _targetPath;
        set { _targetPath = value; Raise(); }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "o", "Ko", "Mo", "Go" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} o" : $"{v:0.#} {units[i]}";
    }
}
