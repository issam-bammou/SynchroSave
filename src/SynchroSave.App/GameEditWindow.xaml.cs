using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SynchroSave.Core.Models;

namespace SynchroSave.App;

public partial class GameEditWindow : Window
{
    private readonly bool _isEdit;

    public GameEditWindow(GameEntry? existing = null)
    {
        InitializeComponent();
        _isEdit = existing != null;
        Title = _isEdit ? "Modifier le jeu" : "Ajouter un jeu";
        KnownCombo.ItemsSource = App.Service.Catalog.Definitions.OrderBy(d => d.Name).ToList();
        if (existing != null)
        {
            NameBox.Text = existing.Name;
            PathBox.Text = existing.LocalPath;
            KnownCombo.IsEnabled = false;
        }
    }

    public string GameName => NameBox.Text.Trim();
    public string FolderPath => App.Service.Resolver.Expand(PathBox.Text.Trim());
    public GameDefinition? SelectedDefinition => KnownCombo.SelectedItem as GameDefinition;

    private void KnownCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KnownCombo.SelectedItem is not GameDefinition def) return;
        NameBox.Text = def.Name;
        var resolver = App.Service.Resolver;
        var found = def.Paths.SelectMany(resolver.ResolveExisting).FirstOrDefault();
        PathBox.Text = found ?? resolver.Expand(def.Paths.FirstOrDefault() ?? string.Empty);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Dossier des sauvegardes du jeu" };
        if (Directory.Exists(FolderPath)) dlg.InitialDirectory = FolderPath;
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileName(dlg.FolderName.TrimEnd('\\'));
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameName))
        {
            MessageBox.Show(this, "Indiquez le nom du jeu.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(FolderPath))
        {
            MessageBox.Show(this, "Choisissez le dossier des sauvegardes.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(FolderPath))
        {
            var r = MessageBox.Show(this, "Ce dossier n'existe pas (encore). L'ajouter quand même ?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
    }
}
