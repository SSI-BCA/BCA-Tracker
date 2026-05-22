using System;
using System.IO;

namespace BCATracker.UI.Services;

/// <summary>
/// Computed file-system paths used throughout the app. Centralised here so
/// the rules for "where is X" are stated once.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Single roaming-app-data folder for everything: settings, crash logs,
    /// the reader's diag.log, and saved match JSONs.
    /// </summary>
    public static string AppDataFolder
    {
        get
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(roaming, "BCA-Tracker");
        }
    }

    /// <summary>%AppData%\BCA-Tracker\settings.json</summary>
    public static string SettingsFilePath
        => Path.Combine(AppDataFolder, "settings.json");

    /// <summary>The folder match-record JSONs are loaded from.
    /// %AppData%\BCA-Tracker\matches\.</summary>
    public static string DefaultMatchesFolder
        => Path.Combine(AppDataFolder, "matches");

    /// <summary>Folder containing the running exe. AppContext.BaseDirectory
    /// is more reliable than Assembly.Location under single-file publish.</summary>
    public static string AppFolder => AppContext.BaseDirectory;
}
