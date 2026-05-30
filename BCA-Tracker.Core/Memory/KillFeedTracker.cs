using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BCATracker.Core
{
    /// <summary>
    /// Detects player deaths and emits kill-feed entries.
    ///
    /// This is a direct port of the alpha v0.11 DetectKills algorithm. We
    /// previously over-engineered this with deferred queues, counter
    /// overrides, and backwards scans. Each iteration introduced new bugs.
    /// The alpha version is the one that's known to work — copy it.
    ///
    /// Behaviour summary:
    /// • Watches every player's HitHistory + Deaths counter.
    /// • When Deaths increments AND HitHistory is non-empty: read the last
    ///   HitInfo entry, resolve its InstigatorID to a player, that's the
    ///   killer. Then inspect Weapon/Ability bytes to classify the kill:
    ///     - Ability != 0  -> ability kill
    ///     - Weapon  != 0  -> weapon kill
    ///     - Both    == 0  -> environment kill (knocked into kill grid)
    /// • When Deaths increments AND HitHistory is empty: tag the death as
    ///   environment / suicide (player jumped off; no hit recorded).
    /// • Bot InstigatorIDs are negative (e.g. -101). The mapping from
    ///   InstigatorID to PlayerState is learned passively by reading the
    ///   TargetID field of every hit entry across the session - within a
    ///   few hits the mapping is complete.
    /// • State (prevStats, prevHitCount, instigatorMap) is NOT cleared
    ///   between matches. It accumulates across the session - alpha
    ///   relies on this and we shouldn't break it.
    /// </summary>
    public class KillFeedTracker
    {
        public const int MAX_ENTRIES = 50;

        public List<KillFeedEntry> Entries { get; } = new List<KillFeedEntry>();

        // Per-player state between ticks. NOT cleared between matches —
        // we just remove entries for departed players (handled at end of
        // each Update call).
        Dictionary<long, (int kills, int deaths)> _prevStats   = new Dictionary<long, (int, int)>();
        Dictionary<long, int>                     _prevHitCount = new Dictionary<long, int>();

        // Learned mapping: bot InstigatorID (negative ID space) → player
        // state pointer. Built passively from TargetID fields in HitHistory.
        Dictionary<int, long> _instigatorMap = new Dictionary<int, long>();

        // (Replaced — see _pendingSuicides below for the new time-aware
        // fixup-pending list.)

        /// <summary>
        /// For each player (key = state ptr), the most recent attacker we've
        /// observed in their hit history while we had non-empty hist data.
        /// Used as a fallback when the game clears the victim's hit history
        /// before recording the env-death (Revoker knockoffs do this).
        ///
        /// Without this, knockoff kills get labeled "Knockoff" because the
        /// hit history is empty by the time we see the death — we have no
        /// idea what weapon was actually used to push them off. With this,
        /// we remember "Puppetino. hit Hegemone with Revoker 0.5s ago" and
        /// use that label when the death registers.
        /// </summary>
        struct RecentDamage
        {
            public int       InstigatorId;
            public byte      Weapon;
            public byte      Ability;
            public DateTime  WhenUtc;
        }
        Dictionary<long, RecentDamage> _recentDamage = new Dictionary<long, RecentDamage>();
        // We also remember the last hit-count we observed per player so we
        // can detect new hits arriving (count grew since last tick).
        Dictionary<long, int> _lastObservedHitCount = new Dictionary<long, int>();

        /// <summary>
        /// Per-killer: the wallclock time at which we last saw their
        /// NbWeaponHit / NbAbilitiesHit counters increment. These counters
        /// are server-authoritative outgoing-hit tallies and don't get
        /// cleared on death — making them a far more reliable signal than
        /// the victim's HitHistory (which times out / clears).
        ///
        /// When a credited environmental kill needs attribution, we look
        /// at the killer's own recent counter activity:
        ///   - ability hit within last ~2s → ability kill (use loadout ability name)
        ///   - weapon hit within last ~2s  → weapon kill (use loadout weapon name)
        ///   - neither                      → genuine "Knockoff"
        /// </summary>
        struct KillerHitActivity
        {
            public int      LastWeaponHit;
            public int      LastAbilityHit;
            public int      LastAbilityUsed;
            public DateTime LastWeaponHitWhenUtc;
            public DateTime LastAbilityHitWhenUtc;
            public DateTime LastAbilityUsedWhenUtc;
        }
        Dictionary<long, KillerHitActivity> _killerActivity = new Dictionary<long, KillerHitActivity>();

        /// <summary>
        /// Pending suicide-entry fixup record. We track the index of the
        /// entry, the wallclock time we created it, and the killer's
        /// counters at that moment. This lets us:
        ///   - drop the entry by ABSOLUTE age, not by tick count (the old
        ///     count-based trim let entries linger 50s+ and grab unrelated
        ///     kill credits)
        ///   - when fixing up, only match the killer's hit-counter delta
        ///     that occurred BEFORE OR AT the death time, not after it
        /// </summary>
        struct PendingSuicide
        {
            public int       EntryIndex;
            public DateTime  CreatedUtc;
            // Snapshot of (NbWeaponHit, NbAbilitiesHit) for every player
            // at the moment we created the suicide entry. Used during
            // fixup to figure out whose counters incremented BETWEEN
            // a couple ticks before the death and the death itself.
            public Dictionary<long, (int weaponHit, int abilityHit)> KillerSnapshot;
        }
        readonly List<PendingSuicide> _pendingSuicides = new List<PendingSuicide>();

        // Hard age cap for pending suicide entries. If a credit delta
        // doesn't arrive within this window, we accept it as a real
        // environment death and stop trying to re-attribute. Long enough
        // to cover delayed counter ticks but short enough that a death
        // 30+ seconds later can't grab the credit.
        static readonly TimeSpan PendingSuicideMaxAge = TimeSpan.FromMilliseconds(2000);

        // How recent a killer's hit-counter increment has to be (relative
        // to the death) to be considered the cause of the kill.
        static readonly TimeSpan KillerActivityMaxAge = TimeSpan.FromMilliseconds(2500);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr addr, byte[] buf, int size, out int read);

        // ── Public ────────────────────────────────────────────────────────

        /// <summary>
        /// Hard reset, intended only for fresh attach (game process changed).
        /// We deliberately do NOT call this between matches — alpha didn't,
        /// and clearing _prevHitCount mid-session causes hits during the
        /// next match to look like they all happened at once.
        /// </summary>
        public void Reset()
        {
            Entries.Clear();
            _prevStats.Clear();
            _prevHitCount.Clear();
            _instigatorMap.Clear();
            _pendingSuicides.Clear();
            _recentDamage.Clear();
            _lastObservedHitCount.Clear();
            _killerActivity.Clear();
        }

        /// <summary>
        /// Clears only the visible kill feed, leaves tracking dictionaries
        /// intact. Use this between matches so a fresh round starts with an
        /// empty visual feed but doesn't corrupt the per-player baselines.
        /// </summary>
        public void ClearVisibleFeed()
        {
            Entries.Clear();
            _pendingSuicides.Clear();
        }

        /// <summary>
        /// Called once per tick while the game process is attached.
        /// Reads HitHistory from each player and emits kill events.
        /// </summary>
        public void Update(IntPtr handle, List<PlayerInfo> players, long myStatePtr, double elapsedSecs)
        {
            // Pass 1: scan ALL hits in everyone's history to learn
            // InstigatorID → player mappings. Each FSHitInfo's TargetID is
            // the victim's instigator-id, which we map to their state ptr.
            foreach (var p in players)
            {
                if (p.HitHistoryPtr == 0) continue;
                long historyData  = ReadLong(handle, p.HitHistoryPtr + Offsets.HH_History_Data);
                int  historyCount = ReadInt (handle, p.HitHistoryPtr + Offsets.HH_History_Count);
                if (historyData == 0 || historyCount <= 0 || historyCount > 128) continue;

                int targetId = ReadInt(handle, historyData + Offsets.HitInfo_TargetID);
                if (targetId != 0)
                    _instigatorMap[targetId] = p.StatePtr;
            }

            // Pass 1.5: snapshot the most recent attacker for each player that
            // has hit data right now. We use this later as a fallback when the
            // game clears a victim's hit history before their env-death lands.
            //
            // IMPORTANT — we used to gate this capture on "historyCount grew
            // since last tick", which seemed sensible (only refresh on new
            // hits). It was wrong. Tick rate is 500ms; the engine can post a
            // hit AND clear the history between two of our ticks, which means
            // we never see growth and never capture. We now snapshot the last
            // entry on EVERY tick where the history is non-empty. The entry
            // itself is timestamped, so stale entries get ignored downstream
            // by the 3-second freshness check.
            DateTime nowUtc = DateTime.UtcNow;

            // Pass 1.6: track each killer's outgoing-hit activity. The
            // game maintains server-authoritative counters per player:
            //   - NbWeaponHit: weapon shots that connected
            //   - NbAbilitiesHit: damaging ability hits that connected
            //   - AbilitiesUsed: ability button presses (regardless of hit)
            //
            // Unlike the victim's HitHistory (which times out / clears on
            // death), these counters are monotonic and never reset, so we
            // can reliably tell whether a credited environmental kill was
            // preceded by an actual hit from the credited killer.
            //
            // Why we ALSO track AbilitiesUsed (button presses): some
            // abilities don't register as a "hit" because they don't
            // deal damage. BlackHole is the canonical example — it's a
            // gravity well that pulls cores toward a center point but
            // does no direct damage, so NbAbilitiesHit never increments
            // even when BlackHole is what knocks a bot into the void.
            // For these cases AbilitiesUsed is the only signal we have:
            // if the killer's loadout ability is BlackHole and they
            // pressed the ability button shortly before the kill, that's
            // what did it.
            foreach (var p in players)
            {
                _killerActivity.TryGetValue(p.StatePtr, out var prev);
                var next = prev;

                // First time we see this player: just snapshot. We can't
                // infer activity from the first observation, only from
                // increments thereafter. Use DateTime.MinValue so
                // freshness checks treat it as ancient.
                if (prev.LastWeaponHit == 0 && prev.LastAbilityHit == 0
                    && prev.LastAbilityUsed == 0
                    && prev.LastWeaponHitWhenUtc == default
                    && prev.LastAbilityHitWhenUtc == default
                    && prev.LastAbilityUsedWhenUtc == default)
                {
                    next.LastWeaponHit          = p.NbWeaponHit;
                    next.LastAbilityHit         = p.NbAbilitiesHit;
                    next.LastAbilityUsed        = p.AbilitiesUsed;
                    next.LastWeaponHitWhenUtc   = DateTime.MinValue;
                    next.LastAbilityHitWhenUtc  = DateTime.MinValue;
                    next.LastAbilityUsedWhenUtc = DateTime.MinValue;
                }
                else
                {
                    if (p.NbWeaponHit > prev.LastWeaponHit)
                    {
                        next.LastWeaponHit        = p.NbWeaponHit;
                        next.LastWeaponHitWhenUtc = nowUtc;
                    }
                    if (p.NbAbilitiesHit > prev.LastAbilityHit)
                    {
                        next.LastAbilityHit        = p.NbAbilitiesHit;
                        next.LastAbilityHitWhenUtc = nowUtc;
                    }
                    if (p.AbilitiesUsed > prev.LastAbilityUsed)
                    {
                        next.LastAbilityUsed        = p.AbilitiesUsed;
                        next.LastAbilityUsedWhenUtc = nowUtc;
                    }
                    // Counter went DOWN (player swap / match reset) — re-baseline.
                    if (p.NbWeaponHit    < prev.LastWeaponHit)    next.LastWeaponHit    = p.NbWeaponHit;
                    if (p.NbAbilitiesHit < prev.LastAbilityHit)   next.LastAbilityHit   = p.NbAbilitiesHit;
                    if (p.AbilitiesUsed  < prev.LastAbilityUsed)  next.LastAbilityUsed  = p.AbilitiesUsed;
                }
                _killerActivity[p.StatePtr] = next;
            }


            foreach (var p in players)
            {
                if (p.HitHistoryPtr == 0) continue;
                long historyData  = ReadLong(handle, p.HitHistoryPtr + Offsets.HH_History_Data);
                int  historyCount = ReadInt (handle, p.HitHistoryPtr + Offsets.HH_History_Count);
                if (historyData == 0 || historyCount <= 0 || historyCount > 128)
                {
                    // No hits visible right now — leave any existing
                    // _recentDamage entry alone (it'll age out via the 3s
                    // freshness check). Just track count for diagnostics.
                    _lastObservedHitCount[p.StatePtr] = 0;
                    continue;
                }

                // Walk back from newest to oldest to find the most recent
                // entry that's actually an attacking hit by someone other
                // than the victim. Pure environment entries (weapon==0 &&
                // ability==0) get skipped — those are the "void claims you"
                // events that overwrite the real attacker if we just took
                // the last entry blindly.
                int  scanLimit = Math.Min(historyCount, 8);
                int  capturedIId = 0;
                byte capturedWp  = 0;
                byte capturedAb  = 0;
                for (int i = 0; i < scanLimit; i++)
                {
                    int  idx = historyCount - 1 - i;
                    long ad  = historyData + idx * Offsets.HitInfo_Size;
                    byte wp  = ReadByte(handle, ad + Offsets.HitInfo_Weapon);
                    byte ab  = ReadByte(handle, ad + Offsets.HitInfo_Ability);
                    if (wp == 0 && ab == 0) continue;  // env damage, skip
                    int iId = ReadInt(handle, ad + Offsets.HitInfo_InstigatorID);
                    capturedIId = iId;
                    capturedWp  = wp;
                    capturedAb  = ab;
                    break;
                }

                _lastObservedHitCount[p.StatePtr] = historyCount;
                if (capturedWp == 0 && capturedAb == 0) continue;  // nothing usable

                _recentDamage[p.StatePtr] = new RecentDamage
                {
                    InstigatorId = capturedIId,
                    Weapon       = capturedWp,
                    Ability      = capturedAb,
                    WhenUtc      = nowUtc,
                };
            }

            // Pass 2: detect kills.
            //
            // The Deaths counter on the player state is monotonic. Most
            // ticks see Deaths increment by 0 or 1, but with a 500ms tick
            // rate it's possible for a bot to die TWICE between two of our
            // ticks (Backup mode bots respawn fast and you can chain
            // Sparkler them down). We emit one kill-feed entry per integer
            // increment so we don't lose deaths.
            //
            // Per-entry attribution is best-effort for the multi-death
            // case: HitHistory only reflects the most recent state, so
            // the earlier death will pick up whatever weapon hits are
            // there now (likely the same as the current one anyway, since
            // it's typically you Sparkler-spamming the same bot).
            foreach (var victim in players)
            {
                int deathDelta = 0;
                if (_prevStats.TryGetValue(victim.StatePtr, out var pstats))
                    deathDelta = victim.Deaths - pstats.deaths;
                if (deathDelta < 0) deathDelta = 0;            // counter rolled back
                if (deathDelta > 5) deathDelta = 1;            // sanity-clamp; large jumps are state-resync, not real chains
                bool victimDied = deathDelta > 0;

                long historyData  = 0;
                int  historyCount = 0;
                if (victim.HitHistoryPtr != 0)
                {
                    historyData  = ReadLong(handle, victim.HitHistoryPtr + Offsets.HH_History_Data);
                    historyCount = ReadInt (handle, victim.HitHistoryPtr + Offsets.HH_History_Count);
                    if (historyCount < 0 || historyCount > 128) historyCount = 0;
                }

                _prevHitCount.TryGetValue(victim.StatePtr, out int prev);
                if (historyCount < prev) prev = 0; // new life started — reset our prev

                if (victimDied)
                {
                    KillFeedEntry entry;

                    if (historyCount > 0 && historyData != 0)
                    {
                        // Walk back through hit history to find the killing
                        // blow. The LAST entry isn't always the right one:
                        // someone shoots you for big damage, you keep moving
                        // for a couple seconds, then fall and die — your hist
                        // tail is [Revoker hit, ..., (env)]. The env entry has
                        // no instigator; the Revoker hit is the actual credit.
                        //
                        // Strategy: scan from newest to oldest, take the first
                        // entry that meets ALL of:
                        //   - instigator resolves to a non-victim player
                        //   - has a weapon or ability set (i.e. came from a
                        //     player action, not pure environment damage)
                        // If we find none, fall back to the very last entry
                        // (which mirrors alpha's behavior).
                        int  chosenIdx        = historyCount - 1;
                        int  chosenInstigator = 0;
                        byte chosenWeapon     = 0;
                        byte chosenAbility    = 0;
                        PlayerInfo chosenKiller = null;
                        bool foundGoodEntry    = false;

                        // Cap the walk so we don't spend forever reading
                        // 128 entries. 8 is plenty — typical "delayed death"
                        // is 1-3 hits ago.
                        int scanLimit = Math.Min(historyCount, 8);
                        for (int i = 0; i < scanLimit; i++)
                        {
                            int  idx = historyCount - 1 - i;
                            long ad  = historyData + idx * Offsets.HitInfo_Size;
                            int  iId = ReadInt (handle, ad + Offsets.HitInfo_InstigatorID);
                            byte wp  = ReadByte(handle, ad + Offsets.HitInfo_Weapon);
                            byte ab  = ReadByte(handle, ad + Offsets.HitInfo_Ability);

                            // First iteration captures the raw last entry as
                            // the fallback if nothing better is found.
                            if (i == 0)
                            {
                                chosenIdx        = idx;
                                chosenInstigator = iId;
                                chosenWeapon     = wp;
                                chosenAbility    = ab;
                                chosenKiller     = ResolveKiller(iId, players, myStatePtr);
                            }

                            bool hasMethod = (wp != 0 || ab != 0);
                            if (!hasMethod) continue;

                            PlayerInfo k = ResolveKiller(iId, players, myStatePtr);
                            if (k == null || k == victim) continue;

                            // This is a real attacking hit. Take it.
                            chosenIdx        = idx;
                            chosenInstigator = iId;
                            chosenWeapon     = wp;
                            chosenAbility    = ab;
                            chosenKiller     = k;
                            foundGoodEntry   = true;
                            break;
                        }

                        // Always log the full hit history for every death so we
                        // have proper data to diagnose attribution issues. The
                        // FSHitInfo struct is 0x50 bytes; we only know offsets
                        // 0x00-0x09 for sure, the rest may contain damage,
                        // timestamps, or a cause enum we haven't decoded yet.
                        DumpFullHitHistory(handle, victim.Name, historyData, historyCount);

                        DiagLog.Write($"[KillFeed] victim={victim.Name} histCount={historyCount} " +
                                      $"chose idx={chosenIdx} (walkback={(foundGoodEntry ? "yes" : "no")}) " +
                                      $"instigatorId={chosenInstigator} weapon={chosenWeapon} ability={chosenAbility} " +
                                      $"resolved={(chosenKiller == null ? "null" : chosenKiller.Name + (chosenKiller == victim ? " [SELF]" : ""))}");

                        string killerName;
                        int    killerTeam;

                        if (chosenKiller == victim)
                        {
                            killerName = victim.Name + " (suicide)";
                            killerTeam = victim.Team;
                        }
                        else if (chosenKiller != null)
                        {
                            killerName = chosenKiller.Name;
                            killerTeam = chosenKiller.Team;
                        }
                        else
                        {
                            killerName = $"Unknown(ID={chosenInstigator})";
                            killerTeam = -1;
                        }

                        string cause      = chosenAbility != 0 ? BCAEnums.AbilityName(chosenAbility) : BCAEnums.WeaponName(chosenWeapon);
                        bool   isAbility  = chosenAbility != 0;
                        if (chosenWeapon == 0 && chosenAbility == 0)
                        {
                            // Knock-into-environment: keep the killer name we
                            // resolved (might still be valid), tag the cause
                            // as Environment.
                            cause = "Environment";
                            isAbility = false;
                        }

                        entry = new KillFeedEntry
                        {
                            Time          = DateTime.Now,
                            KillerName    = killerName,
                            VictimName    = victim.Name,
                            Cause         = cause,
                            IsAbilityKill = isAbility,
                            KillerTeam    = killerTeam,
                            ElapsedSecs   = elapsedSecs,
                            VictimStatePtr = victim.StatePtr,
                        };
                    }
                    else
                    {
                        // Hit history empty — pure environment death.
                        DiagLog.Write($"[KillFeed] victim={victim.Name} histCount=0 - environment");
                        entry = new KillFeedEntry
                        {
                            Time          = DateTime.Now,
                            KillerName    = victim.Name + " (suicide)",
                            VictimName    = victim.Name,
                            Cause         = "Environment",
                            IsAbilityKill = false,
                            KillerTeam    = victim.Team,
                            ElapsedSecs   = elapsedSecs,
                            VictimStatePtr = victim.StatePtr,
                        };
                    }

                    // Emit one entry per integer death increment. For a
                    // single death this is the normal path; for >1 we add
                    // duplicates so the kill-feed length matches the
                    // server-authoritative Deaths counter.
                    for (int dup = 0; dup < deathDelta; dup++)
                    {
                        Entries.Add(entry);
                        if (Entries.Count > MAX_ENTRIES) Entries.RemoveAt(0);
                    }
                }

                _prevHitCount[victim.StatePtr] = historyCount;
            }

            // ── Pass 3: counter-fallback fixup ─────────────────────────────────
            // The hit-history attribution above is correct when histCount > 0,
            // but fails for "knockoff" kills (Revoker pushes a bot off the map,
            // engine kills them via environment damage with no instigator). The
            // game's score system DOES credit the correct player though — we
            // can see this via the Kills counter on each PlayerInfo.
            //
            // Strategy: AFTER processing all deaths this tick, look at the
            // "(suicide) Environment" entries we just produced. For each, find
            // out which non-victim player's Kills counter went up by exactly 1
            // since the previous tick (a "credit delta"). If exactly one
            // player has a +1 delta and isn't the victim, rewrite the entry to
            // credit them with cause="Knockoff".
            //
            // _prevStats has NOT been updated yet — it still points at the
            // PREVIOUS tick's (kills, deaths). We update it at the end of this
            // method.
            FixupSuicideEntriesByKillCounter(players);

            // Update stats snapshot for the next tick's comparison.
            foreach (var p in players)
                _prevStats[p.StatePtr] = (p.Kills, p.Deaths);

            // Clean up stale pointers (player left match).
            var current = new HashSet<long>();
            foreach (var p in players) current.Add(p.StatePtr);
            var stale = new List<long>();
            foreach (var kvp in _prevStats)
                if (!current.Contains(kvp.Key)) stale.Add(kvp.Key);
            foreach (var k in stale)
            {
                _prevStats.Remove(k);
                _prevHitCount.Remove(k);
            }
        }

        /// <summary>
        /// Walks pending "(suicide) Environment" entries and tries to
        /// re-attribute them by:
        ///   1. Finding which non-victim player got +1 Kills this tick
        ///      (the "credit delta" from the score system).
        ///   2. Looking at that killer's NbWeaponHit / NbAbilitiesHit
        ///      counter activity in the seconds leading up to the death,
        ///      to decide between weapon / ability / pure-knockoff.
        ///
        /// Why this works where the old "victim's HitHistory recent
        /// damage" approach failed: HitHistory is the victim's incoming
        /// hits, which time out / clear unpredictably. NbWeaponHit and
        /// NbAbilitiesHit are the killer's outgoing tally, monotonic
        /// and authoritative.
        ///
        /// Pending entries get an absolute wallclock age cap (not a
        /// tick-count cap) so a death that's been sitting in the
        /// pending list for 30+ seconds can't grab credit from an
        /// unrelated kill that happens later.
        /// </summary>
        void FixupSuicideEntriesByKillCounter(List<PlayerInfo> players)
        {
            // Drop pending suicide entries that have aged out. A real
            // environmental suicide (no killer credited) eventually
            // hits this cap and stays labeled as "Environment".
            DateTime nowUtc = DateTime.UtcNow;
            for (int idx = _pendingSuicides.Count - 1; idx >= 0; idx--)
            {
                if (nowUtc - _pendingSuicides[idx].CreatedUtc > PendingSuicideMaxAge)
                    _pendingSuicides.RemoveAt(idx);
            }

            // Collect this tick's credit deltas. _prevStats is still the
            // PREVIOUS tick's snapshot at this point — the caller updates
            // it after this method returns.
            var creditDeltas = new List<PlayerInfo>();
            foreach (var p in players)
            {
                if (!_prevStats.TryGetValue(p.StatePtr, out var prev)) continue;
                int delta = p.Kills - prev.kills;
                if (delta > 0) creditDeltas.Add(p);
            }

            // If there's nothing pending and no fresh suicide entries to
            // pick up, we're done.
            // First, scan recent Entries for newly-added "(suicide) Environment"
            // entries that aren't yet in _pendingSuicides (they were just
            // created by Pass 2 in this same tick).
            int scanStart = Math.Max(0, Entries.Count - players.Count);
            for (int i = scanStart; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (e.KillerName == null) continue;
                if (!e.KillerName.EndsWith(" (suicide)")) continue;
                if (e.Cause != "Environment") continue;
                bool alreadyTracked = false;
                foreach (var ps in _pendingSuicides)
                    if (ps.EntryIndex == i) { alreadyTracked = true; break; }
                if (alreadyTracked) continue;

                _pendingSuicides.Add(new PendingSuicide
                {
                    EntryIndex     = i,
                    CreatedUtc     = nowUtc,
                    KillerSnapshot = null, // not currently used; reserved
                });
            }

            // Walk pending fixups newest-first. Process each independently.
            var resolved = new List<int>();   // indices INTO _pendingSuicides to remove
            for (int pi = 0; pi < _pendingSuicides.Count; pi++)
            {
                var ps = _pendingSuicides[pi];
                int i = ps.EntryIndex;
                if (i < 0 || i >= Entries.Count) { resolved.Add(pi); continue; }
                var e = Entries[i];

                // Defensive: if the entry got rewritten away from suicide
                // somehow (shouldn't happen), drop the pending record.
                if (e.Cause != "Environment" || e.KillerName == null
                    || !e.KillerName.EndsWith(" (suicide)"))
                {
                    resolved.Add(pi);
                    continue;
                }

                // Find a non-victim player whose Kills counter just
                // incremented. We require exactly one such candidate
                // to avoid ambiguity in 3+ player matches.
                PlayerInfo match = null;
                int matchCount = 0;
                foreach (var p in creditDeltas)
                {
                    if (p.Name == e.VictimName) continue;
                    match = p;
                    matchCount++;
                }

                if (matchCount == 0)
                {
                    // No credit delta this tick — keep waiting until the
                    // age cap kicks in.
                    continue;
                }
                if (matchCount > 1)
                {
                    DiagLog.Write($"[KillFeed] Counter override skipped for '{e.VictimName}': " +
                                  $"{matchCount} candidates, ambiguous — leaving as suicide");
                    resolved.Add(pi);
                    continue;
                }

                // We have a single credited killer. Now pick the cause:
                // look at THEIR recent ability/weapon hit activity.
                string causeLabel = "Knockoff";
                bool   isAbility  = false;
                string activityNote = "no recent hit activity";

                if (_killerActivity.TryGetValue(match.StatePtr, out var act))
                {
                    double abilityHitAgeMs = act.LastAbilityHitWhenUtc == DateTime.MinValue
                        ? double.PositiveInfinity
                        : (nowUtc - act.LastAbilityHitWhenUtc).TotalMilliseconds;
                    double weaponHitAgeMs = act.LastWeaponHitWhenUtc == DateTime.MinValue
                        ? double.PositiveInfinity
                        : (nowUtc - act.LastWeaponHitWhenUtc).TotalMilliseconds;
                    double abilityUsedAgeMs = act.LastAbilityUsedWhenUtc == DateTime.MinValue
                        ? double.PositiveInfinity
                        : (nowUtc - act.LastAbilityUsedWhenUtc).TotalMilliseconds;

                    // Most-recent-action-wins. The credited killer's score
                    // counter went +1, so SOMETHING they did caused this
                    // death. We pick the most recent thing they did and
                    // attribute the kill to it. No hard freshness window —
                    // BlackHole-into-void kills can have arbitrarily long
                    // delays between the press and the death (bot drifts
                    // off the edge over many seconds), and during that time
                    // the killer typically isn't doing anything else to the
                    // bot. The "most recent" check naturally handles this:
                    // a BlackHole press 20s ago beats nothing-else-since.
                    //
                    // For impulse-only abilities (BlackHole), AbilitiesUsed
                    // (button presses) is the only signal — they don't
                    // damage on hit so NbAbilitiesHit never ticks. For
                    // damaging abilities, we ignore AbilitiesUsed and use
                    // NbAbilitiesHit instead, because a missed Blast press
                    // shouldn't claim a kill.
                    bool killerHasImpulseOnly = IsImpulseOnlyAbility(match.Ability);

                    double bestAgeMs = double.PositiveInfinity;
                    string bestKind  = null;

                    if (abilityHitAgeMs < bestAgeMs)
                    {
                        bestAgeMs = abilityHitAgeMs;
                        bestKind  = "ability-hit";
                    }
                    if (weaponHitAgeMs < bestAgeMs)
                    {
                        bestAgeMs = weaponHitAgeMs;
                        bestKind  = "weapon-hit";
                    }
                    if (killerHasImpulseOnly && abilityUsedAgeMs < bestAgeMs)
                    {
                        bestAgeMs = abilityUsedAgeMs;
                        bestKind  = "ability-used";
                    }

                    // Outer sanity cap. If literally nothing the killer has
                    // done in this match was recorded (e.g. they joined
                    // mid-match and the counters haven't moved yet), keep
                    // it as Knockoff. The cap is generous (60s) so genuine
                    // long-delay BlackHole drops still get attributed.
                    const double SanityMaxAgeMs = 60_000;

                    if (bestKind != null && bestAgeMs <= SanityMaxAgeMs)
                    {
                        if (bestKind == "ability-hit" || bestKind == "ability-used")
                        {
                            causeLabel = BCAEnums.AbilityName(match.Ability);
                            isAbility  = true;
                        }
                        else // weapon-hit
                        {
                            causeLabel = BCAEnums.WeaponName(match.Weapon);
                            isAbility  = false;
                        }
                        activityNote = $"{bestKind} {bestAgeMs:0}ms ago, killer loadout={match.WeaponName}/{match.AbilityName}";
                    }
                    else
                    {
                        activityNote = $"no recorded activity (ability-hit={abilityHitAgeMs:0}ms, weapon-hit={weaponHitAgeMs:0}ms, ability-used={abilityUsedAgeMs:0}ms)";
                    }
                }

                int pendingAgeMs = (int)(nowUtc - ps.CreatedUtc).TotalMilliseconds;
                DiagLog.Write($"[KillFeed] Counter override: '{e.VictimName}' → '{match.Name}', " +
                              $"cause={causeLabel} ({activityNote}, pending-age={pendingAgeMs}ms)");

                Entries[i] = new KillFeedEntry
                {
                    Time           = e.Time,
                    KillerName     = match.Name,
                    VictimName     = e.VictimName,
                    Cause          = causeLabel,
                    IsAbilityKill  = isAbility,
                    KillerTeam     = match.Team,
                    ElapsedSecs    = e.ElapsedSecs,
                    VictimStatePtr = e.VictimStatePtr,
                };
                resolved.Add(pi);
            }

            // Remove resolved fixups, highest-index first to preserve indices.
            for (int k = resolved.Count - 1; k >= 0; k--)
                _pendingSuicides.RemoveAt(resolved[k]);
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Dumps every entry in a victim's hit history to diag.log as raw hex.
        /// Each entry is 0x50 bytes — we know offsets 0x00-0x09 (instigatorId,
        /// targetId, weapon, ability) but the remaining 70 bytes may contain
        /// timestamps, damage values, or a cause/source enum we haven't
        /// decoded. Logging the full bytes lets us see patterns and decode
        /// the rest by inspection.
        /// </summary>
        void DumpFullHitHistory(IntPtr handle, string victimName, long histData, int histCount)
        {
            if (histData == 0 || histCount <= 0)
            {
                DiagLog.Write($"[KillFeed] HistDump victim={victimName} (empty)");
                return;
            }
            int entries = Math.Min(histCount, 16); // cap so logs don't explode
            DiagLog.Write($"[KillFeed] HistDump victim={victimName} count={histCount}");
            for (int i = 0; i < entries; i++)
            {
                long addr = histData + i * Offsets.HitInfo_Size;
                byte[] raw = new byte[Offsets.HitInfo_Size];
                ReadProcessMemory(handle, (IntPtr)addr, raw, Offsets.HitInfo_Size, out _);
                var sb = new StringBuilder();
                sb.Append($"  [{i:D2}] ");
                for (int bi = 0; bi < raw.Length; bi++)
                {
                    sb.Append(raw[bi].ToString("X2"));
                    if (bi == 3 || bi == 7 || bi == 9 || bi == 15 || bi == 23
                        || bi == 31 || bi == 39 || bi == 47 || bi == 55 || bi == 63 || bi == 71)
                        sb.Append(' ');
                }
                DiagLog.Write(sb.ToString());
            }
        }

        /// <summary>
        /// Abilities that don't deal damage on their own — they only apply
        /// impulse / movement effects. These never increment NbAbilitiesHit
        /// because the game's "hit" definition requires damage. For these
        /// we have to fall back to AbilitiesUsed (button-press counter) as
        /// the activity signal during fixup.
        ///
        /// Currently the only known case is BlackHole (id=4), a gravity
        /// well that pulls cores toward a center point but does no direct
        /// damage. If other abilities turn out to behave the same way they
        /// can be added here.
        /// </summary>
        static bool IsImpulseOnlyAbility(byte ability)
        {
            // BlackHole — id 4 in EAbilities. See BCAEnums.Abilities.
            return ability == 4;
        }

        PlayerInfo ResolveKiller(int instigatorId, List<PlayerInfo> players, long myStatePtr)
        {
            // Local player special case: kills attributed to them often have
            // InstigatorID==0 (or matches their LocalID, which is sometimes
            // -1). Match by state pointer instead, which is always correct.
            if (myStatePtr != 0)
            {
                foreach (var p in players)
                    if (p.StatePtr == myStatePtr && (instigatorId == p.LocalID || instigatorId == 0))
                        return p;
            }

            // Other humans: match by LocalID directly.
            foreach (var p in players)
                if (!p.IsLocal && p.LocalID == instigatorId) return p;

            // Bots: use the learned mapping.
            if (_instigatorMap.TryGetValue(instigatorId, out long ptr))
                foreach (var p in players)
                    if (p.StatePtr == ptr) return p;

            return null;
        }

        // Set DEBUG_HITS=true and rebuild to log raw 16 bytes of every hit
        // record processed. Useful when offsets change or kills attribute
        // wrongly — the bytes show what the game actually wrote.
        const bool DEBUG_HITS = false;

        // ── Low-level reads ───────────────────────────────────────────────
        static long ReadLong(IntPtr h, long addr)
        {
            var buf = new byte[8];
            ReadProcessMemory(h, (IntPtr)addr, buf, 8, out _);
            return BitConverter.ToInt64(buf, 0);
        }
        static int ReadInt(IntPtr h, long addr)
        {
            var buf = new byte[4];
            ReadProcessMemory(h, (IntPtr)addr, buf, 4, out _);
            return BitConverter.ToInt32(buf, 0);
        }
        static byte ReadByte(IntPtr h, long addr)
        {
            var buf = new byte[1];
            ReadProcessMemory(h, (IntPtr)addr, buf, 1, out _);
            return buf[0];
        }
    }
}
