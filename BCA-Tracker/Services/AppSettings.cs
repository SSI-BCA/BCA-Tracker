using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCATracker.UI.Services;

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
    /// <summary>Last choice made in the launcher dialog (Modern or Legacy).</summary>
    [JsonPropertyName("preferredUI")]
    public UIChoice PreferredUI { get; set; } = UIChoice.Modern;

    /// <summary>If true, skip the launcher dialog on subsequent starts and
    /// go directly to <see cref="PreferredUI"/>.</summary>
    [JsonPropertyName("rememberLauncherChoice")]
    public bool RememberLauncherChoice { get; set; } = false;

    /// <summary>Folder that match JSON files are read from. Defaults to the
    /// same folder the legacy tracker writes to.</summary>
    [JsonPropertyName("matchesFolder")]
    public string? MatchesFolder { get; set; }

    /// <summary>Whether to push Discord Rich Presence updates while the app
    /// is running. Off by default; flip on in Settings.</summary>
    [JsonPropertyName("discordRpcEnabled")]
    public bool DiscordRpcEnabled { get; set; } = false;

    /// <summary>Discord application Client ID. Get yours at
    /// https://discord.com/developers/applications. Empty string disables RPC
    /// even if <see cref="DiscordRpcEnabled"/> is true.</summary>
    [JsonPropertyName("discordClientId")]
    public string DiscordClientId { get; set; } = "";

    /// <summary>Optional path to an image file used as the profile picture
    /// on the Home page identity card. If null/empty or the file is missing,
    /// the avatar falls back to the first letter of the player's handle.</summary>
    [JsonPropertyName("profilePicturePath")]
    public string? ProfilePicturePath { get; set; }

    static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        try
        {
            string path = AppPaths.SettingsFilePath;
            if (!File.Exists(path)) return new AppSettings();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, s_jsonOpts) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults.
            // We deliberately don't surface this to the user; the launcher will
            // show on next start and they can re-pick.
            return new AppSettings();
        }
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
            // Settings persistence is best-effort. Failure here is not worth
            // interrupting the user — they'll just see the launcher again.
        }
    }
}
