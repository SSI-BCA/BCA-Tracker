using System;
using BCATracker.Core;

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
    static MatchUploadService?      _uploader;
    static LobbyHostingService?     _lobby;
    static UpdateChecker?           _updateChecker;

    public static MatchStore Matches =>
        _matches ?? throw new InvalidOperationException("AppServices not initialised");

    public static LiveMatchService LiveMatch =>
        _liveMatch ?? throw new InvalidOperationException("AppServices not initialised");

    public static DiscordPresenceService Discord =>
        _discord ?? throw new InvalidOperationException("AppServices not initialised");

    public static MatchUploadService Uploader =>
        _uploader ?? throw new InvalidOperationException("AppServices not initialised");

    public static LobbyHostingService Lobby =>
        _lobby ?? throw new InvalidOperationException("AppServices not initialised");

    /// <summary>
    /// Polls GitHub Releases for newer tracker versions. Null between
    /// app start and Initialise(). Always non-null after Initialise.
    /// </summary>
    public static UpdateChecker UpdateChecker =>
        _updateChecker ?? throw new InvalidOperationException("AppServices not initialised");

    public static AppSettings Settings { get; private set; } = new();

    public static void Initialise()
    {
        // Initialise the diagnostic logger first thing so any subsequent
        // setup errors are captured.
        BCATracker.Core.DiagLog.Init();

        Settings = AppSettings.Load();

        // Resolve matches folder. Precedence:
        //   1. Explicit override in settings.json (if set)
        //   2. New default %AppData%\BCA-Tracker\matches
        //   3. Legacy %AppData%\BCA-Hub\matches (alpha builds wrote there).
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

        // Match-upload service. Constructed and started unconditionally —
        // a disabled-by-config uploader is harmless (it just sits idle).
        // This way users who flip the toggle on later don't have to
        // restart the app to get uploading working.
        _uploader = new MatchUploadService();
        ApplyUploaderConfig();
        _uploader.Start();

        // The live match service builds its own MatchSaver internally; we
        // pass the uploader through so saved matches get enqueued.
        _liveMatch = new LiveMatchService(_uploader);
        _liveMatch.Start();

        // Lobby hosting orchestrator. Constructed unconditionally; the
        // user toggles advertising on/off from Settings, which gates
        // whether it actually opens ports and publishes.
        _lobby = new LobbyHostingService(Settings);
        _liveMatch.AttachLobbyHosting(_lobby);

        _discord = new DiscordPresenceService(_liveMatch);
        if (Settings.DiscordRpcEnabled && !string.IsNullOrEmpty(Settings.DiscordClientId))
            _discord.Start(Settings.DiscordClientId);

        // NetBird prerequisite: if the user wants to host or join
        // lobbies, the NetBird agent must be installed. Check now and,
        // if missing, kick off the bundled MSI install in the
        // background. One UAC prompt; we don't block app startup on it.
        _ = System.Threading.Tasks.Task.Run(EnsureNetBirdInstalled);

        // Background update check. One immediate poll on startup; the UI
        // will display the banner if a newer release is available. We
        // could schedule a periodic re-poll, but a single startup check
        // is enough — users typically launch the tracker often.
        _updateChecker = new BCATracker.Core.UpdateChecker();
        _ = System.Threading.Tasks.Task.Run(() => _updateChecker!.CheckAsync());
    }

    /// <summary>
    /// If NetBird isn't installed, run the bundled MSI silently. The
    /// MSI is expected to sit next to the tracker executable as
    /// "netbird_installer.msi" (or one of the alternate names below).
    /// </summary>
    static async System.Threading.Tasks.Task EnsureNetBirdInstalled()
    {
        try
        {
            var nb = _lobby?.NetBird;
            if (nb is null || nb.IsInstalled) return;

            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                System.IO.Path.Combine(baseDir, "netbird_installer.msi"),
                System.IO.Path.Combine(baseDir, "netbird.msi"),
                System.IO.Path.Combine(baseDir, "NetBird.msi"),
            };
            string? msi = null;
            foreach (var c in candidates)
                if (System.IO.File.Exists(c)) { msi = c; break; }

            if (msi is null)
            {
                BCATracker.Core.DiagLog.Write(
                    "[NB] MSI not found alongside tracker. Lobby hosting/joining won't work.");
                return;
            }

            BCATracker.Core.DiagLog.Write($"[NB] Installing from {msi}...");
            bool ok = await nb.InstallAsync(msi);
            BCATracker.Core.DiagLog.Write(ok ? "[NB] Install succeeded." : "[NB] Install failed.");
        }
        catch (Exception ex)
        {
            BCATracker.Core.DiagLog.Write($"[NB] Auto-install error: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-apply the data-submission settings to the running uploader. Call
    /// this after the user changes the toggle or endpoint in Settings.
    /// </summary>
    public static void ApplyUploaderConfig()
    {
        _uploader?.Configure(
            enabled:   Settings.DataSubmissionEnabled,
            endpoint:  Settings.DataSubmissionEndpoint ?? "",
            accountId: Settings.AnonymousAccountId    ?? "");
        // Same endpoint feeds the lobby publisher.
        _lobby?.ApplyConfig();
    }

    public static void Shutdown()
    {
        try { _discord?.Dispose(); } catch { }
        try { _lobby?.Dispose(); } catch { }
        try { _liveMatch?.Dispose(); } catch { }
        try { _uploader?.Dispose(); } catch { }
    }
}
