using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using SynchroSave.Core;

namespace SynchroSave.App;

/// <summary>Accès au registre Windows : démarrage automatique, OneDrive, Steam, dossiers connus.</summary>
internal static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SynchroSave";

    public static string ExecutablePath => Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "SynchroSave.exe");

    public static void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(AppName, $"\"{ExecutablePath}\" --minimized");
            else if (key.GetValue(AppName) != null) key.DeleteValue(AppName);
        }
        catch (Exception ex)
        {
            AppLog.Error("Impossible de modifier le démarrage automatique", ex);
        }
    }

    public static string? DetectOneDrive()
    {
        var candidates = new List<string?>();
        try
        {
            using var personal = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal");
            candidates.Add(personal?.GetValue("UserFolder") as string);
            using var od = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive");
            candidates.Add(od?.GetValue("UserFolder") as string);
        }
        catch { /* ignore */ }
        return OneDriveLocator.Detect(candidates);
    }

    /// <summary>Jetons de chemin enrichis avec les vraies valeurs Windows (dossier Saved Games, Steam).</summary>
    public static Dictionary<string, string> BuildTokens()
    {
        var tokens = PathResolver.DefaultTokens();
        try
        {
            using var shell = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            // {4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4} = Saved Games
            if (shell?.GetValue("{4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4}") is string saved && saved.Length > 0)
                tokens["SavedGames"] = Environment.ExpandEnvironmentVariables(saved);
        }
        catch { /* ignore */ }
        try
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is string sp && Directory.Exists(sp))
                tokens["Steam"] = sp.Replace('/', '\\');
        }
        catch { /* ignore */ }
        return tokens;
    }
}
