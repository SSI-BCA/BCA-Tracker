using System;
using System.Collections.Generic;
using System.Linq;
using BCATracker.Core;

namespace BCATracker.UI.Services;

/// <summary>
/// Aggregation summary across some set of matches, viewed from the
/// perspective of the local player. All numbers are derived; nothing is
/// pre-stored.
/// </summary>
public class StatsSummary
{
    public int    Matches      { get; set; }
    public int    Wins         { get; set; }
    public int    Losses       { get; set; }
    public double WinRatePct   { get; set; }   // 0..100

    public int    Kills        { get; set; }
    public int    Deaths       { get; set; }
    public int    Assists      { get; set; }
    public double KDRatio      { get; set; }   // Kills / max(Deaths, 1)
    public double KDARatio     { get; set; }   // (Kills + Assists) / max(Deaths, 1)

    public double AvgAccuracyPct { get; set; } // simple average across matches
    public double AvgAbilityAccuracyPct { get; set; }

    public double AvgDamage   { get; set; }    // damage per match
    public double TotalDamage { get; set; }
    public double TotalHeal   { get; set; }

    public double AvgMatchDurationSecs { get; set; }
    public double TotalPlayTimeSecs    { get; set; }

    public string? FavouriteMap     { get; set; }
    public string? FavouriteWeapon  { get; set; }
    public string? FavouriteAbility { get; set; }

    public double AvgKillsPerMatch  { get; set; }
    public double AvgDeathsPerMatch { get; set; }

    // ── New stats (this turn) ────────────────────────────────────────
    /// <summary>Best single-match kill count.</summary>
    public int    BestMatchKills        { get; set; }
    /// <summary>Best single-match damage figure.</summary>
    public double BestMatchDamage       { get; set; }
    /// <summary>Best single-match K/D ratio (matches with at least 5 kills count).</summary>
    public double BestMatchKD           { get; set; }
    /// <summary>Total wins minus total losses (signed match streak).</summary>
    public int    NetWinDelta           { get; set; }
    /// <summary>Number of consecutive wins ending at the most-recent match,
    /// or 0 if the most recent match was a loss.</summary>
    public int    CurrentWinStreak      { get; set; }
    /// <summary>Longest sequence of consecutive wins ever recorded.</summary>
    public int    LongestWinStreak      { get; set; }
    /// <summary>Number of matches in which the local player died zero times (Flawless).</summary>
    public int    FlawlessMatches       { get; set; }
    /// <summary>Number of matches the local player ended as match MVP
    /// (highest score across both teams).</summary>
    public int    MatchMvpCount         { get; set; }
    /// <summary>Number of matches the local player ended as their team's MVP.</summary>
    public int    TeamMvpCount          { get; set; }
    /// <summary>Average damage taken per match across the local player.</summary>
    public double AvgDamageTaken        { get; set; }
    /// <summary>Average damage delta (dealt - taken) per match.</summary>
    public double AvgDamageDelta        { get; set; }
}

/// <summary>
/// Per-match highlights derived from match data only. Cheap to compute, no
/// schema bump required. Surfaced on Match Detail.
/// </summary>
public class MatchHighlights
{
    /// <summary>Local player's outcome.</summary>
    public bool? Won;
    /// <summary>True if the local player died zero times.</summary>
    public bool Flawless;
    /// <summary>True if the local player got the first kill of the match.</summary>
    public bool FirstBlood;
    /// <summary>True if the local player suffered the first death of the match.</summary>
    public bool FirstDeath;
    /// <summary>True if the local player led their team in score.</summary>
    public bool TeamMvp;
    /// <summary>True if the local player led ALL players in score.</summary>
    public bool MatchMvp;
    /// <summary>Damage dealt minus damage taken. Positive = good.</summary>
    public double DamageDelta;
    /// <summary>Score-based ranking on the local player's team (1 = best).</summary>
    public int TeamRank;
    /// <summary>Score-based ranking across both teams (1 = best).</summary>
    public int OverallRank;
}

/// <summary>
/// Per-bucket summary used for "stats by map" or "stats by weapon" tables.
/// </summary>
public class BucketStats
{
    public string Key       { get; set; } = "";  // map name, weapon name, etc.
    public int    Matches   { get; set; }
    public int    Wins      { get; set; }
    public double WinPct    { get; set; }
    public int    Kills     { get; set; }
    public int    Deaths    { get; set; }
    public double KD        { get; set; }
    public double AvgDmg    { get; set; }
    public double AvgAcc    { get; set; }   // weapon accuracy, %
}

public static class Stats
{
    /// <summary>
    /// Pulls the local player's record from a match.
    ///
    /// Identity precedence:
    ///   1. If <paramref name="knownAccountId"/> is supplied AND any player
    ///      in this match has a matching non-null AccountId, return that
    ///      player. This is the stable cross-match identity.
    ///   2. Otherwise return the player flagged IsLocalPlayer = true.
    ///   3. Otherwise return null.
    ///
    /// Old matches and matches saved before AccountId was populated all
    /// fall through to (2), which preserves backward compatibility.
    /// </summary>
    public static PlayerRecord? Local(MatchRecord m, string? knownAccountId = null)
    {
        if (m.Players is null) return null;

        if (!string.IsNullOrEmpty(knownAccountId))
        {
            foreach (PlayerRecord p in m.Players)
                if (!string.IsNullOrEmpty(p.AccountId) &&
                    string.Equals(p.AccountId, knownAccountId, StringComparison.OrdinalIgnoreCase))
                    return p;
        }

        foreach (PlayerRecord p in m.Players)
            if (p.IsLocalPlayer) return p;

        return null;
    }

    /// <summary>
    /// Find the local user's most recent stable AccountId, if any. Returns
    /// null if no match has populated the field yet (which is the current
    /// state of all saved matches — it's a forward-compat field).
    /// </summary>
    public static string? FindKnownAccountId(IEnumerable<MatchRecord> matches)
    {
        foreach (MatchRecord m in matches.OrderByDescending(m => m.PlayedAt))
        {
            if (m.Players is null) continue;
            foreach (PlayerRecord p in m.Players)
                if (p.IsLocalPlayer && !string.IsNullOrEmpty(p.AccountId))
                    return p.AccountId;
        }
        return null;
    }

    /// <summary>
    /// Did the local player win this match? Falls back to the
    /// PlayerRecord.IsWinner flag when WinningTeam isn't set.
    /// </summary>
    public static bool? DidLocalWin(MatchRecord m, string? knownAccountId = null)
    {
        PlayerRecord? me = Local(m, knownAccountId);
        if (me is null) return null;

        if (!string.IsNullOrEmpty(m.WinningTeam))
        {
            string mine = "Team" + me.Team;
            return string.Equals(m.WinningTeam, mine, StringComparison.OrdinalIgnoreCase);
        }

        return me.IsWinner;
    }

    /// <summary>
    /// Compute the lifetime summary across the given matches, from the local
    /// player's perspective. If <paramref name="knownAccountId"/> is null we
    /// auto-detect it from the most recent match that has it populated.
    /// </summary>
    public static StatsSummary ComputeLifetime(IEnumerable<MatchRecord> matches, string? knownAccountId = null)
    {
        var matchList = matches as IList<MatchRecord> ?? matches.ToList();
        knownAccountId ??= FindKnownAccountId(matchList);

        var list = matchList.Where(m => Local(m, knownAccountId) != null).ToList();

        var s = new StatsSummary { Matches = list.Count };
        if (list.Count == 0) return s;

        double totalAcc = 0, totalAbilAcc = 0;
        int    accCount = 0, abilAccCount = 0;

        var mapCounts     = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var weaponCounts  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var abilityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // For win-streak calculation we need matches in chronological order.
        var chronological = list.OrderBy(m => m.PlayedAt).ToList();
        var winSequence = new List<bool?>(chronological.Count);

        double totalDmgTaken = 0;

        foreach (MatchRecord m in list)
        {
            PlayerRecord me = Local(m, knownAccountId)!;

            bool? win = DidLocalWin(m, knownAccountId);
            if (win == true)  s.Wins++;
            if (win == false) s.Losses++;

            s.Kills   += me.Kills;
            s.Deaths  += me.Deaths;
            s.Assists += me.Assists;

            s.TotalDamage += me.Damage;
            s.TotalHeal   += me.Heal;
            totalDmgTaken += me.ReceivedShieldDmg;

            s.TotalPlayTimeSecs += m.DurationSecs;

            if (me.NbHitsCaused > 0 || me.Accuracy > 0)
            {
                totalAcc += me.Accuracy;
                accCount++;
            }
            if (me.NbAbilitiesHit > 0 || me.AbilityAccuracy > 0)
            {
                totalAbilAcc += me.AbilityAccuracy;
                abilAccCount++;
            }

            if (!string.IsNullOrEmpty(m.Map))     Bump(mapCounts,     m.Map);
            if (!string.IsNullOrEmpty(me.Weapon)) Bump(weaponCounts,  me.Weapon);
            if (!string.IsNullOrEmpty(me.Ability))Bump(abilityCounts, me.Ability);

            // Per-match maxima
            if (me.Kills  > s.BestMatchKills)  s.BestMatchKills  = me.Kills;
            if (me.Damage > s.BestMatchDamage) s.BestMatchDamage = me.Damage;
            if (me.Kills >= 5)
            {
                double kd = me.Deaths > 0 ? (double)me.Kills / me.Deaths : me.Kills;
                if (kd > s.BestMatchKD) s.BestMatchKD = kd;
            }

            // Flawless / MVP — derived from in-match data
            var hl = ComputeHighlights(m, knownAccountId);
            if (hl.Flawless) s.FlawlessMatches++;
            if (hl.TeamMvp)  s.TeamMvpCount++;
            if (hl.MatchMvp) s.MatchMvpCount++;
        }

        // Build the win-sequence for streak calculation, in chronological order.
        foreach (MatchRecord m in chronological)
            winSequence.Add(DidLocalWin(m, knownAccountId));

        s.LongestWinStreak = LongestRun(winSequence, true);
        s.CurrentWinStreak = TrailingRun(winSequence, true);

        s.NetWinDelta = s.Wins - s.Losses;

        s.WinRatePct = s.Matches > 0 ? 100.0 * s.Wins / s.Matches : 0;
        s.KDRatio    = s.Deaths > 0 ? (double)s.Kills / s.Deaths : s.Kills;
        s.KDARatio   = s.Deaths > 0 ? (double)(s.Kills + s.Assists) / s.Deaths : s.Kills + s.Assists;

        s.AvgAccuracyPct        = accCount     > 0 ? totalAcc     / accCount     : 0;
        s.AvgAbilityAccuracyPct = abilAccCount > 0 ? totalAbilAcc / abilAccCount : 0;

        s.AvgDamage            = s.Matches > 0 ? s.TotalDamage / s.Matches : 0;
        s.AvgDamageTaken       = s.Matches > 0 ? totalDmgTaken / s.Matches : 0;
        s.AvgDamageDelta       = s.AvgDamage - s.AvgDamageTaken;
        s.AvgMatchDurationSecs = s.Matches > 0 ? s.TotalPlayTimeSecs / s.Matches : 0;
        s.AvgKillsPerMatch     = s.Matches > 0 ? (double)s.Kills  / s.Matches : 0;
        s.AvgDeathsPerMatch    = s.Matches > 0 ? (double)s.Deaths / s.Matches : 0;

        s.FavouriteMap     = TopKey(mapCounts);
        s.FavouriteWeapon  = TopKey(weaponCounts);
        s.FavouriteAbility = TopKey(abilityCounts);

        return s;
    }

    /// <summary>
    /// Per-match highlights derived from match data alone. Cheap; no schema
    /// change required.
    /// </summary>
    public static MatchHighlights ComputeHighlights(MatchRecord m, string? knownAccountId = null)
    {
        var h = new MatchHighlights();
        knownAccountId ??= FindKnownAccountId(new[] { m });

        PlayerRecord? me = Local(m, knownAccountId);
        h.Won = DidLocalWin(m, knownAccountId);
        if (me is null) return h;

        h.Flawless    = me.Deaths == 0 && (me.Kills > 0 || me.Damage > 0);
        h.DamageDelta = me.Damage - me.ReceivedShieldDmg;

        // First blood / first death — sort kill feed by elapsed time.
        if (m.KillFeed != null && m.KillFeed.Count > 0)
        {
            var ordered = m.KillFeed
                .OrderBy(k => ParseTime(k.TimeInMatch))
                .ToList();
            var first = ordered[0];
            // KillerName might have "(suicide)" suffix — match by raw substring
            h.FirstBlood = !string.IsNullOrEmpty(first.KillerName)
                            && first.KillerName.StartsWith(me.Name ?? "", StringComparison.OrdinalIgnoreCase)
                            && !first.KillerName.Contains("(suicide)", StringComparison.OrdinalIgnoreCase);
            h.FirstDeath = first.VictimName == me.Name;
        }

        // MVP — score-based ranking. Score is "PersonalScore" in the schema.
        if (m.Players != null && m.Players.Count > 0)
        {
            // Overall ranking
            var overall = m.Players.OrderByDescending(p => p.Score).ToList();
            h.OverallRank = overall.FindIndex(p => p == me) + 1;
            h.MatchMvp = h.OverallRank == 1;

            // Team ranking
            var team = m.Players.Where(p => p.Team == me.Team)
                                .OrderByDescending(p => p.Score).ToList();
            h.TeamRank = team.FindIndex(p => p == me) + 1;
            h.TeamMvp = h.TeamRank == 1;
        }

        return h;
    }

    static double ParseTime(string? t)
    {
        if (string.IsNullOrEmpty(t)) return double.MaxValue;
        var parts = t.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int min) && int.TryParse(parts[1], out int sec))
            return min * 60 + sec;
        return double.MaxValue;
    }

    /// <summary>Returns the longest run of values equal to <paramref name="target"/> in the sequence.</summary>
    static int LongestRun(IList<bool?> seq, bool target)
    {
        int best = 0, cur = 0;
        foreach (bool? v in seq)
        {
            if (v == target) { cur++; if (cur > best) best = cur; }
            else cur = 0;
        }
        return best;
    }

    /// <summary>Returns the trailing run length of values equal to target.
    /// (Streak ending at the most recent match.)</summary>
    static int TrailingRun(IList<bool?> seq, bool target)
    {
        int n = 0;
        for (int i = seq.Count - 1; i >= 0; i--)
        {
            if (seq[i] == target) n++;
            else break;
        }
        return n;
    }

    /// <summary>
    /// Group matches by a key (e.g. map name, weapon name) and produce a
    /// per-group summary. Used by the Maps and Weapons tables.
    /// </summary>
    public static List<BucketStats> ByBucket(
        IEnumerable<MatchRecord> matches,
        Func<MatchRecord, PlayerRecord, string?> keyOf,
        string? knownAccountId = null)
    {
        var matchList = matches as IList<MatchRecord> ?? matches.ToList();
        knownAccountId ??= FindKnownAccountId(matchList);

        var byKey = new Dictionary<string, List<(MatchRecord m, PlayerRecord me)>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (MatchRecord m in matchList)
        {
            PlayerRecord? me = Local(m, knownAccountId);
            if (me is null) continue;
            string? key = keyOf(m, me);
            if (string.IsNullOrEmpty(key)) continue;
            if (!byKey.TryGetValue(key, out var bucket))
                byKey[key] = bucket = new();
            bucket.Add((m, me));
        }

        var results = new List<BucketStats>();
        foreach (var kv in byKey)
        {
            int count  = kv.Value.Count;
            int wins   = kv.Value.Count(x => DidLocalWin(x.m, knownAccountId) == true);
            int kills  = kv.Value.Sum(x => x.me.Kills);
            int deaths = kv.Value.Sum(x => x.me.Deaths);
            double dmg = kv.Value.Average(x => x.me.Damage);
            double acc = kv.Value
                .Where(x => x.me.NbHitsCaused > 0 || x.me.Accuracy > 0)
                .Select(x => (double)x.me.Accuracy)
                .DefaultIfEmpty(0)
                .Average();

            results.Add(new BucketStats
            {
                Key     = kv.Key,
                Matches = count,
                Wins    = wins,
                WinPct  = count > 0 ? 100.0 * wins / count : 0,
                Kills   = kills,
                Deaths  = deaths,
                KD      = deaths > 0 ? (double)kills / deaths : kills,
                AvgDmg  = dmg,
                AvgAcc  = acc,
            });
        }

        results.Sort((a, b) => b.Matches.CompareTo(a.Matches));
        return results;
    }

    static void Bump<TKey>(Dictionary<TKey, int> dict, TKey k) where TKey : notnull
    {
        dict[k] = dict.TryGetValue(k, out int v) ? v + 1 : 1;
    }

    static string? TopKey(Dictionary<string, int> dict)
        => dict.Count == 0 ? null : dict.OrderByDescending(kv => kv.Value).First().Key;
}
