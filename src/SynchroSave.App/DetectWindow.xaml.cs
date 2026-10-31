using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SynchroSave.Core;
using SynchroSave.Core.Models;

namespace SynchroSave.App;

public sealed class CandidateRow : Observable
{
    private bool _selected;
    private string _name;

    public CandidateRow(SaveCandidate c)
    {
        Candidate = c;
        _name = c.Name;
        // Les dossiers globaux ou volumineux ne sont pas cochés d'office.
        _selected = c.Preselect;
    }

    public SaveCandidate Candidate { get; }
    public string Path => Candidate.Path;
    public string Reason => Candidate.Reason;
    public int FileCount => Candidate.FileCount;
    public string SizeText => CloudGameRow.FormatSize(Candidate.Size);
    public string LastWriteText => Candidate.LastWriteUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "—";

    public bool Selected { get => _selected; set { _selected = value; Raise(); } }
    public string Name { get => _name; set { _name = value; Raise(); } }
}

public partial class DetectWindow : Window
{
    private readonly ObservableCollection<CandidateRow> _rows = new();
    private readonly CancellationTokenSource _cts = new();

    public DetectWindow()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
        AddButton.IsEnabled = false;
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    public List<GameEntry> Added { get; } = new();

    private async Task ScanAsync()
    {
        List<SaveCandidate> candidates;
        try
        {
            var progress = new Progress<string>(m => StatusText.Text = m);
            await App.Service.EnsureOnlineDatabaseAsync(progress);
            StatusText.Text = $"Analyse en cours… ({App.Service.Catalog.Definitions.Count:N0} jeux connus + recherche dans les dossiers)";
            AppSettings settings;
            lock (App.Service.Gate) settings = new AppSettings { Games = App.Service.Settings.Games.ToList() };
            candidates = await Task.Run(() => App.Service.Catalog.FindNewCandidates(settings, _cts.Token));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Erreur pendant l'analyse : " + ex.Message;
            return;
        }
        if (_cts.IsCancellationRequested) return;

        foreach (var c in candidates
                     .OrderBy(c => c.KnownGameId == null)
                     .ThenByDescending(c => c.LastWriteUtc ?? DateTime.MinValue))
            _rows.Add(new CandidateRow(c));

        AddButton.IsEnabled = _rows.Count > 0;
        var known = candidates.Count(c => c.KnownGameId != null);
        var onlineHint = App.Service.Settings.OnlineDatabaseEnabled && App.Service.Catalog.OnlineCount == 0
            ? "  ⚠ Base en ligne non disponible (pas d'internet ?) : seule la base intégrée a été utilisée."
            : "";
        StatusText.Text = _rows.Count == 0
            ? "Aucun nouveau dossier de sauvegarde trouvé. Tous vos jeux détectables sont déjà suivis. Pour un jeu qui enregistre ailleurs (ex. dans son dossier d'installation), utilisez « Ajouter un jeu / dossier »."
            : $"{_rows.Count} dossier(s) trouvé(s) : {known} jeu(x) connu(s) et {_rows.Count - known} trouvé(s) par analyse. Décochez ce qui n'est pas une sauvegarde de jeu." + onlineHint;
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = true;
    }

    private void None_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = false;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is CandidateRow row && Directory.Exists(row.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{row.Path}\"") { UseShellExecute = true });
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit();
        var chosen = _rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Cochez au moins un dossier.", Title);
            return;
        }
        foreach (var r in chosen) r.Candidate.Name = string.IsNullOrWhiteSpace(r.Name) ? r.Candidate.Name : r.Name.Trim();
        lock (App.Service.Gate)
            Added.AddRange(App.Service.Catalog.AddCandidates(chosen.Select(r => r.Candidate), App.Service.Settings));
        DialogResult = true;
    }
}
