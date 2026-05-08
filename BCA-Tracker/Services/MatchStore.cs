using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BCATracker.Core;

namespace BCATracker.UI.Services;

/// <summary>
/// Loads saved match JSON files from disk. Cheap to call repeatedly: results
/// are cached by file mtime, so unchanged files are not re-parsed.
///
/// Folder layout produced by the legacy reader / MatchSaver:
///   matchesRoot\YYYY-MM\YYYY-MM-DD\match_HH-mm-ss_Map_Mode.json
/// We don't depend on that exact layout — we just recursively scan for
/// "match_*.json" files. That makes the store resilient to folder
/// reorganisation, dropping in old archives, etc.
/// </summary>
public class MatchStore
{
    readonly string _root;
    readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public MatchStore(string root)
    {
        _root = root;
    }

    /// <summary>The folder being scanned.</summary>
    public string Root => _root;

    /// <summary>True if the matches folder exists at all.</summary>
    public bool RootExists => Directory.Exists(_root);

    /// <summary>
    /// Loads every match record under the root folder. Returns them sorted
    /// newest-first by PlayedAt. Files that fail to parse are skipped (and
    /// logged via DiagLog so they don't disappear silently).
    /// </summary>
    public List<MatchRecord> LoadAll()
    {
        if (!Directory.Exists(_root))
            return new List<MatchRecord>();

        var results = new List<MatchRecord>();
        foreach (string path in Directory.EnumerateFiles(_root, "match_*.json", SearchOption.AllDirectories))
        {
            MatchRecord? rec = TryLoad(path);
            if (rec != null) results.Add(rec);
        }

        results.Sort((a, b) => b.PlayedAt.CompareTo(a.PlayedAt));
        return results;
    }

    MatchRecord? TryLoad(string path)
    {
        try
        {
            DateTime mtime = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out CacheEntry cached) && cached.MTime == mtime)
                return cached.Record;

            string json = File.ReadAllText(path);
            MatchRecord? rec = JsonSerializer.Deserialize<MatchRecord>(json, s_jsonOpts);
            if (rec is null) return null;

            _cache[path] = new CacheEntry(rec, mtime);
            return rec;
        }
        catch (Exception ex)
        {
            // Don't crash the whole load over one bad file.
            DiagLog.Write($"[MatchStore] Failed to load {path}: {ex.Message}");
            return null;
        }
    }

    static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly struct CacheEntry
    {
        public readonly MatchRecord Record;
        public readonly DateTime    MTime;
        public CacheEntry(MatchRecord r, DateTime t) { Record = r; MTime = t; }
    }
}
