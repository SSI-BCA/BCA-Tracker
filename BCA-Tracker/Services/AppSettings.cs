using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCATracker.UI.Services;

/// <summary>
/// Kept around so existing settings.json files don't fail to deserialize.
/// As of beta2 we ship only the modern UI; this enum has no effect.
/// </summary>
public enum UIChoice
{
    None    = 0,
    Modern  = 1,
    Legacy  = 2,
}

/// <summary>
/// User-facing settings persisted to %AppData%\BCA-Tracker\settings.json.
/// Kept deliberately small and JSON-friendly — this file is hand-editable
/// for users who want to tweak it.
/// </summary>
public class AppSettings
{
    // ── Legacy launcher fields (unused after beta2) ──────────────────────────
    // These are still serialized so older settings.json files round-trip
    // cleanly and so users who flip back to an older build don't lose data.

    [JsonPropertyName("preferredUI")]
    public UIChoice PreferredUI { get; set; } = UIChoice.Modern;

    [JsonPropertyName("rememberLauncherChoice")]
    public bool RememberLauncherChoice { get; set; } = true;

    // ── Paths ────────────────────────────────────────────────────────────────

    /// <summary>Folder that match JSON files are read from. Defaults to the
    /// same folder the legacy tracker writes to.</summary>
    [JsonPropertyName("matchesFolder")]
    public string? MatchesFolder { get; set; }

    // ── Discord RPC ──────────────────────────────────────────────────────────

    /// <summary>Whether to push Discord Rich Presence updates while the app
    /// is running. Off by default; flip on in Settings.</summary>
    [JsonPropertyName("discordRpcEnabled")]
    public bool DiscordRpcEnabled { get; set; } = false;

    /// <summary>Discord application Client ID. Get yours at
    /// https://discord.com/developers/applications. Empty string disables RPC
    /// even if <see cref="DiscordRpcEnabled"/> is true.</summary>
    [JsonPropertyName("discordClientId")]
    public string DiscordClientId { get; set; } = "";

    // ── UX preferences ───────────────────────────────────────────────────────

    /// <summary>If true, the tracker automatically navigates to the Live
    /// Match page when a match starts. If false, the user stays on whatever
    /// page they were viewing and has to switch manually.</summary>
    [JsonPropertyName("autoJumpToLiveMatch")]
    public bool AutoJumpToLiveMatch { get; set; } = true;

    /// <summary>
    /// Behavior when the user clicks the title-bar close button.
    ///   - "quit"      : default. Application shuts down.
    ///   - "tray"      : Window hides; app keeps running in the system tray
    ///                   so background services (lobby publisher, Discord
    ///                   RPC, match watcher) stay alive. Right-click tray
    ///                   icon to quit.
    /// </summary>
    [JsonPropertyName("closeBehavior")]
    public string CloseBehavior { get; set; } = "quit";

    /// <summary>Accent color name. The Palette.axaml resource for
    /// "Accent.*" gets swapped at runtime to match. Values:
    /// "purple" (default), "red", "blue", "green", "orange", "teal",
    /// "yellow". Unknown values fall back to purple.</summary>
    [JsonPropertyName("accentColor")]
    public string AccentColor { get; set; } = "purple";

    // ── Profile ──────────────────────────────────────────────────────────────

    /// <summary>Optional path to an image file used as the profile picture
    /// on the Home page identity card. If null/empty or the file is missing,
    /// the avatar falls back to the first letter of the player's handle.</summary>
    [JsonPropertyName("profilePicturePath")]
    public string? ProfilePicturePath { get; set; }

    // ── Anonymous match data submission ──────────────────────────────────────
    //
    // The tracker can optionally upload completed match files to a backend
    // server for community-wide stats (map/weapon pickrates, currently-
    // running matches, leaderboards). This is OPT-IN — defaults to off,
    // user is asked on first run.
    //
    // What gets uploaded: the raw match_*.json files we already write to
    // disk. They contain player handles, loadouts, kill feed entries,
    // and stat counters — same info anyone watching the kill feed in-game
    // would see. They do NOT contain Steam/Epic IDs, IP addresses, or
    // the user's Windows account name.
    //
    // The AnonymousAccountId GUID lets the backend group a single user's
    // matches together for "your history" features without learning who
    // the user is. Generated once on first launch, never leaves this
    // machine if data submission is off.

    /// <summary>Set to true once the user has been shown the data-submission
    /// consent dialog. Until then, the dialog appears at startup. Independent
    /// of the answer the user gave (their answer goes in
    /// <see cref="DataSubmissionEnabled"/>).</summary>
    [JsonPropertyName("dataSubmissionAsked")]
    public bool DataSubmissionAsked { get; set; } = false;

    /// <summary>If true, finished match JSON files are queued for upload to
    /// <see cref="DataSubmissionEndpoint"/>. Off by default.</summary>
    [JsonPropertyName("dataSubmissionEnabled")]
    public bool DataSubmissionEnabled { get; set; } = false;

    /// <summary>Base URL of the upload backend. Empty/null means the feature
    /// is dormant even if <see cref="DataSubmissionEnabled"/> is true (so
    /// builds without a configured backend can ship safely). The uploader
    /// POSTs to {endpoint}/v1/matches.
    /// 
    /// The default points at the official community server. Users can
    /// override this in Settings; that takes precedence (and is the only
    /// way to point at a private instance or a local dev backend).
    /// </summary>
    [JsonPropertyName("dataSubmissionEndpoint")]
    public string DataSubmissionEndpoint { get; set; } = "https://api-bca.puppetino.dev";

    /// <summary>Stable random GUID generated on first launch. Identifies the
    /// installation to the backend without revealing the underlying account.
    /// The user can rotate this from Settings to start a fresh stats history.
    /// </summary>
    [JsonPropertyName("anonymousAccountId")]
    public string AnonymousAccountId { get; set; } = "";

    // ── Lobby advertising ────────────────────────────────────────────────────
    //
    // When the user hosts a custom BCA lobby and toggles this on, the
    // tracker asks the backend to provision a NetBird group + setup key
    // for it, enrolls the local NetBird agent into that group, and
    // posts the lobby (with the host's NetBird IP) to the directory
    // backend. Other tracker users see it in the Lobbies tab, click
    // join, and their NetBird agent enrolls into the same group — at
    // which point both peers are on the same encrypted overlay
    // network and BCA's direct-connect "just works" over the NetBird IP.
    //
    // The "advertised name" is what shows up in the browser. It's
    // tracker-side because reading the in-game lobby name (UE FText) is
    // brittle across versions.

    /// <summary>Master toggle for lobby advertising. Off by default.</summary>
    [JsonPropertyName("lobbyAdvertisingEnabled")]
    public bool LobbyAdvertisingEnabled { get; set; } = false;

    /// <summary>
    /// Override the automatic "am I the host?" memory read. Until the
    /// IsGameLeader offset is verified across game builds, this lets
    /// users manually claim host status — useful for testing, and for
    /// patches where the offset shifts. When false, hosting is gated on
    /// the memory-read result.
    /// </summary>
    [JsonPropertyName("lobbyForceHost")]
    public bool LobbyForceHost { get; set; } = false;

    /// <summary>What the lobby is called in the browser. Empty falls back
    /// to "{hostName}'s lobby".</summary>
    [JsonPropertyName("lobbyAdvertisedName")]
    public string LobbyAdvertisedName { get; set; } = "";

    /// <summary>
    /// Optional password for the hosted lobby. When set, only joiners
    /// who supply the matching password get a NetBird setup key from
    /// the backend; the lobby still appears in the public list but
    /// with a lock icon.
    /// 
    /// NOT persisted to settings.json: it's a per-session secret, the
    /// host re-enters it each time they advertise. Survives across
    /// app navigation but not a restart. The [JsonIgnore] attribute
    /// keeps it out of the saved file even if the JSON serializer
    /// were ever pointed at this class directly.
    /// </summary>
    [JsonIgnore]
    public string LobbyPassword { get; set; } = "";

    /// <summary>
    /// True if the hosted lobby should be hidden from the public list.
    /// The backend still has it on file (so joiners can find it by
    /// group id), it's just not returned in GET /v1/lobbies. Not
    /// persisted; like LobbyPassword, the host re-enables it per
    /// session if they want a hidden lobby.
    /// </summary>
    [JsonIgnore]
    public bool LobbyHidden { get; set; } = false;

    /// <summary>
    /// Manually-entered display name. Used as a fallback when the
    /// tracker can't read a name from BCA (game closed, or no match
    /// played yet so the scoreboard memory is empty). The in-game
    /// read still wins when it's available - this is only the
    /// "we have nothing else" fallback so hosts can advertise without
    /// having to launch a match first.
    /// </summary>
    [JsonPropertyName("playerNameOverride")]
    public string PlayerNameOverride { get; set; } = "";

    // ── Persistence ──────────────────────────────────────────────────────────

    static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            string path = AppPaths.SettingsFilePath;
            if (!File.Exists(path))
            {
                settings = new AppSettings();
            }
            else
            {
                string json = File.ReadAllText(path);
                settings = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOpts)
                           ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable — fall back to defaults.
            settings = new AppSettings();
        }

        // Backfill the anonymous ID for installations that pre-date this
        // field. We do this even if data submission isn't enabled, so the
        // ID is stable from the very first launch onward — toggling
        // submission on later won't reset the user's history.
        if (string.IsNullOrEmpty(settings.AnonymousAccountId))
        {
            settings.AnonymousAccountId = Guid.NewGuid().ToString("N");
            settings.Save();
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            string path = AppPaths.SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = JsonSerializer.Serialize(this, s_jsonOpts);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Settings persistence is best-effort.
        }
    }
}
