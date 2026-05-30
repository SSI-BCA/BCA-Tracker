using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace BCATracker.UI.Services;

/// <summary>
/// Per-metric "trending up" and "trending down" colors for the Trends
/// page line charts. Persisted in %AppData%\BCA-Tracker\chart-colors.json
/// so power users can edit colors directly without going through the
/// Settings UI; the file is read on every Trends page refresh, so edits
/// take effect the next time the page is shown.
///
/// Layout of the file:
/// {
///   "metrics": {
///     "kd":      { "up": "#22C55E", "down": "#EF4444" },
///     "acc":     { "up": "#22C55E", "down": "#EF4444" },
///     "dmg":     { "up": "#22C55E", "down": "#EF4444" },
///     "winRate": { "up": "#22C55E", "down": "#EF4444" }
///   }
/// }
///
/// Unknown keys are ignored. Missing keys fall back to defaults.
/// Invalid hex strings fall back to the corresponding default.
/// </summary>
public static class ChartColors
{
    public sealed class MetricColors
    {
        [JsonPropertyName("up")]   public string Up   { get; set; } = "";
        [JsonPropertyName("down")] public string Down { get; set; } = "";
    }

    public sealed class FileShape
    {
        [JsonPropertyName("metrics")]
        public System.Collections.Generic.Dictionary<string, MetricColors> Metrics { get; set; } = new();
    }

    /// <summary>Resolved up/down brushes for one metric.</summary>
    public sealed record Resolved(IBrush Up, IBrush Down);

    static readonly object _gate = new();
    static FileShape _file = new();

    /// <summary>Built-in defaults. Used when the file doesn't exist or
    /// a metric isn't present in it.</summary>
    static readonly System.Collections.Generic.Dictionary<string, (string up, string down)> _defaults =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "kd",      ("#22C55E", "#EF4444") },
        { "acc",     ("#22C55E", "#EF4444") },
        { "dmg",     ("#22C55E", "#EF4444") },
        { "winRate", ("#22C55E", "#EF4444") },
        { "delta",   ("#22C55E", "#EF4444") },
        { "alive",   ("#22C55E", "#EF4444") },
    };

    /// <summary>
    /// Load (or reload) the config file from disk. Safe to call on every
    /// chart refresh; the cost is a tiny json parse. If the file doesn't
    /// exist, write the default file so users can find it and edit.
    /// </summary>
    public static void Reload()
    {
        string path = ConfigPath();
        try
        {
            if (!File.Exists(path))
            {
                WriteDefaultFile(path);
                lock (_gate) _file = BuildDefaultShape();
                return;
            }
            string json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<FileShape>(json, _jsonOpts);
            lock (_gate) _file = parsed ?? new();
        }
        catch
        {
            // Bad json or IO error - fall back to defaults silently. We
            // don't want a syntax slip in a hand-edited file to crash the
            // chart render.
            lock (_gate) _file = new();
        }
    }

    /// <summary>
    /// Resolve up/down brushes for a metric key, falling back to defaults
    /// for missing or invalid entries.
    /// </summary>
    public static Resolved For(string metricKey)
    {
        MetricColors? entry = null;
        lock (_gate)
        {
            _file.Metrics.TryGetValue(metricKey, out entry);
        }
        (string upDef, string downDef) = _defaults.TryGetValue(metricKey, out var d)
            ? d : ("#22C55E", "#EF4444");
        IBrush up   = ParseBrush(entry?.Up,   upDef);
        IBrush down = ParseBrush(entry?.Down, downDef);
        return new Resolved(up, down);
    }

    /// <summary>Absolute path to the config file. Public so the Settings
    /// page can show it to the user / open the containing folder.</summary>
    public static string ConfigPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir  = Path.Combine(root, "BCA-Tracker");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "chart-colors.json");
    }

    // ── Internals ────────────────────────────────────────────────────────

    static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    static FileShape BuildDefaultShape()
    {
        var f = new FileShape();
        foreach (var kv in _defaults)
            f.Metrics[kv.Key] = new MetricColors { Up = kv.Value.up, Down = kv.Value.down };
        return f;
    }

    static void WriteDefaultFile(string path)
    {
        try
        {
            // Header comment is a nice-to-have for users opening the
            // file. JSON proper doesn't allow comments, so we ship a
            // sibling .md file with explanation instead. That keeps
            // the JSON strict so any editor's validator is happy.
            string json = JsonSerializer.Serialize(BuildDefaultShape(), _jsonOpts);
            File.WriteAllText(path, json);

            string readmePath = Path.Combine(Path.GetDirectoryName(path)!, "chart-colors.README.md");
            if (!File.Exists(readmePath))
            {
                File.WriteAllText(readmePath,
                    "# chart-colors.json\n\n" +
                    "Per-metric colors for the Trends page line charts. Each metric has two\n" +
                    "colors: `up` for when the recent value is trending higher than earlier values,\n" +
                    "and `down` for when it's trending lower.\n\n" +
                    "Edit the JSON file and the changes apply the next time you open the Trends page.\n\n" +
                    "Color format is `#RRGGBB` (standard hex). For example:\n" +
                    "- `#22C55E` = green\n" +
                    "- `#EF4444` = red\n" +
                    "- `#3B82F6` = blue\n" +
                    "- `#8B5CF6` = purple\n\n" +
                    "Metric keys are: kd, acc, dmg, winRate, delta (damage delta), and alive (time alive per match).\n");
            }
        }
        catch
        {
            // Best-effort. If we can't write the default file the
            // resolver still works with the in-memory defaults.
        }
    }

    static IBrush ParseBrush(string? hex, string fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) hex = fallback;
        try
        {
            return new SolidColorBrush(Color.Parse(hex!));
        }
        catch
        {
            try   { return new SolidColorBrush(Color.Parse(fallback)); }
            catch { return Brushes.MediumPurple; }
        }
    }
}
