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

    // Désactivation faite par l'utilisateur dans le Gestionnaire des tâches > Démarrage.
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static string StartupCommand => $"\"{ExecutablePath}\" --minimized";

    /// <summary>
    /// Active/désactive le lancement automatique à l'ouverture de session Windows (clé Run de l'utilisateur,
    /// sans droits administrateur). Activer lève aussi une éventuelle désactivation faite dans le Gestionnaire des tâches.
    /// </summary>
    public static void SetStartWithWindows(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(AppName, StartupCommand);
                using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
                if (approved?.GetValue(AppName) != null) approved.DeleteValue(AppName);
                AppLog.Info("Lancement au démarrage de Windows activé.");
            }
            else if (key.GetValue(AppName) != null)
            {
                key.DeleteValue(AppName);
                AppLog.Info("Lancement au démarrage de Windows désactivé.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Impossible de modifier le démarrage automatique", ex);
        }
    }

    /// <summary>
    /// À chaque lancement : remet à jour le chemin de l'exécutable dans la clé Run (si le .exe a été déplacé),
    /// sans passer outre une désactivation volontaire faite dans le Gestionnaire des tâches.
    /// </summary>
    public static void RefreshStartupEntry(bool enabled)
    {
        if (!enabled) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (!string.Equals(key.GetValue(AppName) as string, StartupCommand, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(AppName, StartupCommand);
                AppLog.Info("Entrée de démarrage Windows mise à jour : " + ExecutablePath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Impossible de mettre à jour le démarrage automatique", ex);
        }
    }

    /// <summary>Vrai si Windows lancera réellement l'application à l'ouverture de session.</summary>
    public static bool IsStartWithWindowsActive() =>
        HasRunEntry() && !IsDisabledInTaskManager();

    public static bool HasRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        catch { return false; }
    }

    /// <summary>Le premier octet de la valeur StartupApproved vaut 0x02 (activé) ou 0x03 (désactivé).</summary>
    public static bool IsDisabledInTaskManager()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            return approved?.GetValue(AppName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
        catch { return false; }
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
