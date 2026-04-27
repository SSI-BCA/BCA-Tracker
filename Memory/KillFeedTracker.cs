using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BCATracker
{
    /// <summary>
    /// Detects kills by watching HitHistory on each player state.
    /// Maintains a rolling list of KillFeedEntry.
    /// Separated from MemoryReader so it can be unit-tested or swapped.
    /// </summary>
    public class KillFeedTracker
    {
        public const int MAX_ENTRIES = 10;

        public List<KillFeedEntry> Entries { get; } = new List<KillFeedEntry>();

        // Per-player state between ticks
        Dictionary<long, (int kills, int deaths)> _prevStats  = new Dictionary<long, (int, int)>();
        Dictionary<long, int>                     _prevHitCount = new Dictionary<long, int>();

        // Learned mapping: InstigatorID (bot negative-ID system) → PlayerState pointer
        // Built passively from TargetID fields in HitHistory entries.
        Dictionary<int, long> _instigatorMap = new Dictionary<int, long>();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr addr, byte[] buf, int size, out int read);

        // ── Public ───────────────────────────────────────────────────────────────
        public void Reset()
        {
            Entries.Clear();
            _prevStats.Clear();
            _prevHitCount.Clear();
            _instigatorMap.Clear();
        }

        /// <summary>
        /// Called once per tick while in active play.
        /// Reads HitHistory from each player and emits kill events.
        /// </summary>
        public void Update(IntPtr handle, List<PlayerInfo> players, long myStatePtr, double elapsedSecs)
        {
            // Pass 1: learn InstigatorID mapping from TargetID fields
            foreach (var p in players)
            {
                if (p.HitHistoryPtr == 0) continue;
                long data  = ReadLong(handle, p.HitHistoryPtr + Offsets.HH_History_Data);
                int  count = ReadInt(handle,  p.HitHistoryPtr + Offsets.HH_History_Count);
                if (data == 0 || count <= 0 || count > 128) continue;

                int targetId = ReadInt(handle, data + Offsets.HitInfo_TargetID);
                if (targetId != 0)
                    _instigatorMap[targetId] = p.StatePtr;
            }

            // Pass 2: detect kills
            foreach (var victim in players)
            {
                bool died = _prevStats.TryGetValue(victim.StatePtr, out var prev)
                            && victim.Deaths > prev.deaths;

                long histData  = 0;
                int  histCount = 0;
                if (victim.HitHistoryPtr != 0)
                {
                    histData  = ReadLong(handle, victim.HitHistoryPtr + Offsets.HH_History_Data);
                    histCount = ReadInt(handle,  victim.HitHistoryPtr + Offsets.HH_History_Count);
                    if (histCount < 0 || histCount > 128) histCount = 0;
                }

                _prevHitCount.TryGetValue(victim.StatePtr, out int prevCount);
                if (histCount < prevCount) prevCount = 0; // new life started

                if (died)
                {
                    KillFeedEntry entry;
                    if (histCount > 0 && histData != 0)
                        entry = BuildKillEntry(handle, histData, histCount, victim, players, myStatePtr, elapsedSecs);
                    else
                        entry = BuildEnvironmentEntry(victim, elapsedSecs);

                    Entries.Add(entry);
                    if (Entries.Count > MAX_ENTRIES) Entries.RemoveAt(0);
                }

                _prevHitCount[victim.StatePtr] = histCount;
            }

            // Update stats snapshot
            foreach (var p in players)
                _prevStats[p.StatePtr] = (p.Kills, p.Deaths);

            // Clean up stale pointers
            var current = new HashSet<long>();
            foreach (var p in players) current.Add(p.StatePtr);
            var stale = new List<long>();
            foreach (var k in _prevStats.Keys) if (!current.Contains(k)) stale.Add(k);
            foreach (var k in stale) { _prevStats.Remove(k); _prevHitCount.Remove(k); }
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        KillFeedEntry BuildKillEntry(IntPtr handle, long histData, int histCount,
                                     PlayerInfo victim, List<PlayerInfo> players, long myStatePtr,
                                     double elapsedSecs)
        {
            long hitAddr = histData + (histCount - 1) * Offsets.HitInfo_Size;

            int  instigatorId = ReadInt(handle,  hitAddr + Offsets.HitInfo_InstigatorID);
            byte weapon       = ReadByte(handle, hitAddr + Offsets.HitInfo_Weapon);
            byte ability      = ReadByte(handle, hitAddr + Offsets.HitInfo_Ability);

            PlayerInfo killer = ResolveKiller(instigatorId, players, myStatePtr);

            string killerName;
            int    killerTeam;

            if (killer == victim)
            {
                killerName = victim.Name + " (suicide)";
                killerTeam = victim.Team;
            }
            else if (killer != null)
            {
                killerName = killer.Name;
                killerTeam = killer.Team;
            }
            else
            {
                killerName = $"Unknown(ID={instigatorId})";
                killerTeam = -1;
            }

            string cause        = ability != 0 ? BCAEnums.AbilityName(ability) : BCAEnums.WeaponName(weapon);
            bool   isAbility    = ability != 0;
            if (weapon == 0 && ability == 0) { cause = "Environment"; isAbility = false; }

            return new KillFeedEntry
            {
                Time         = DateTime.Now,
                KillerName   = killerName,
                VictimName   = victim.Name,
                Cause        = cause,
                IsAbilityKill = isAbility,
                KillerTeam   = killerTeam,
                ElapsedSecs  = elapsedSecs
            };
        }

        KillFeedEntry BuildEnvironmentEntry(PlayerInfo victim, double elapsedSecs)
            => new KillFeedEntry
            {
                Time         = DateTime.Now,
                KillerName   = victim.Name + " (suicide)",
                VictimName   = victim.Name,
                Cause        = "Environment",
                IsAbilityKill = false,
                KillerTeam   = victim.Team,
                ElapsedSecs  = elapsedSecs
            };

        PlayerInfo ResolveKiller(int instigatorId, List<PlayerInfo> players, long myStatePtr)
        {
            // Local player: InstigatorID matches their LocalID (or 0 in some modes)
            if (myStatePtr != 0)
                foreach (var p in players)
                    if (p.StatePtr == myStatePtr && (instigatorId == p.LocalID || instigatorId == 0))
                        return p;

            // Other humans: match by LocalID
            foreach (var p in players)
                if (!p.IsLocal && p.LocalID == instigatorId) return p;

            // Bots: use learned mapping
            if (_instigatorMap.TryGetValue(instigatorId, out long ptr))
                foreach (var p in players)
                    if (p.StatePtr == ptr) return p;

            return null;
        }

        // ── Low-level reads (duplicated here to keep KillFeedTracker self-contained) ──
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
