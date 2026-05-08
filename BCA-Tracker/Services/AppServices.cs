using System;

namespace BCATracker.UI.Services;

/// <summary>
/// Tiny service locator. Lets pages reach shared services without us
/// setting up a full DI container. Initialise once at startup; pages
/// read via the static accessors.
/// </summary>
public static class AppServices
{
    static MatchStore?              _matches;
    static LiveMatchService?        _liveMatch;
    static DiscordPresenceService?  _discord;

    public static MatchStore Matches =>
        _matches ?? throw new InvalidOperationException("AppServices not initialised");

    public static LiveMatchService LiveMatch =>
        _liveMatch ?? throw new InvalidOperationException("AppServices not initialised");

    public static DiscordPresenceService Discord =>
        _discord ?? throw new InvalidOperationException("AppServices not initialised");

    public static AppSettings Settings { get; private set; } = new();

    public static void Initialise()
    {
        // Initialise the diagnostic logger first thing so any subsequent
        // setup errors are captured. Without this, every DiagLog.Write call
        // silently no-ops because _path is null — that's why diag.log was
        // never appearing for the GUI build (only the legacy console called Init).
        BCATracker.Core.DiagLog.Init();

        Settings = AppSettings.Load();

        // Resolve matches folder. Precedence:
        //   1. Explicit override in settings.json (if set)
        //   2. New default %AppData%\BCA-Tracker\matches
        //   3. Legacy %AppData%\BCA-Hub\matches (alpha builds wrote there).
        //      If we're falling through to legacy, persist the choice so the
        //      user's existing data shows up without an empty Match History.
        string root = Settings.MatchesFolder ?? AppPaths.DefaultMatchesFolder;
        if (Settings.MatchesFolder is null && !System.IO.Directory.Exists(root))
        {
            string legacy = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BCA-Hub", "matches");
            if (System.IO.Directory.Exists(legacy))
            {
                root = legacy;
                Settings.MatchesFolder = legacy;
                Settings.Save();
            }
        }
        _matches = new MatchStore(root);

        _liveMatch = new LiveMatchService();
        _liveMatch.Start();

        _discord = new DiscordPresenceService(_liveMatch);
        if (Settings.DiscordRpcEnabled && !string.IsNullOrEmpty(Settings.DiscordClientId))
            _discord.Start(Settings.DiscordClientId);
    }

    public static void Shutdown()
    {
        try { _discord?.Dispose(); } catch { }
        try { _liveMatch?.Dispose(); } catch { }
    }
}
