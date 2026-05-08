using System;
using System.IO;
using System.Reflection;

namespace BCATracker.UI.Services;

/// <summary>
/// Computed file-system paths used throughout the app. Centralised here so
/// the rules for "where is X" are stated once.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Single roaming-app-data folder for everything: settings, crash logs,
    /// the reader's diag.log, and saved match JSONs. Sharing one folder with
    /// the legacy reader means you only ever look in one place.
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

    /// <summary>Folder containing the running WPF exe.</summary>
    public static string AppFolder
    {
        get
        {
            // AppContext.BaseDirectory is more reliable than Assembly.Location
            // under single-file publish.
            return AppContext.BaseDirectory;
        }
    }

    /// <summary>
    /// Path to the sibling Legacy console exe. This assumes Modern and Legacy
    /// are deployed side-by-side (the typical case after `dotnet publish` of
    /// the whole solution into a single output folder, or when the user
    /// extracts a release zip).
    /// </summary>
    public static string? LegacyExePath
    {
        get
        {
            // First, look right next to the WPF exe.
            string sibling = Path.Combine(AppFolder, "BCA-Tracker.Legacy.exe");
            if (File.Exists(sibling)) return sibling;

            // Dev convenience: when both projects are built side-by-side under
            // the solution, the legacy exe lives a few directories away.
            // .../BCA-Tracker/bin/Debug/net10.0-windows/BCA-Tracker.exe
            // .../BCA-Tracker.Legacy/bin/Debug/net10.0-windows/BCA-Tracker.Legacy.exe
            try
            {
                var dir = new DirectoryInfo(AppFolder);
                // Walk up to the solution root: bin → Debug → net10.0-windows → BCA-Tracker → solution
                DirectoryInfo? slnRoot = dir.Parent?.Parent?.Parent?.Parent;
                if (slnRoot != null)
                {
                    string devGuess = Path.Combine(
                        slnRoot.FullName,
                        "BCA-Tracker.Legacy", "bin", dir.Parent!.Name, dir.Name,
                        "BCA-Tracker.Legacy.exe");
                    if (File.Exists(devGuess)) return devGuess;
                }
            }
            catch
            {
                // best-effort
            }

            return sibling; // return the deployed-side-by-side path so the
                            // caller can show it in the "not found" message
        }
    }
}
