using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCATracker
{
    public class MatchSaver
    {
        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        readonly string _saveDir;
        readonly string _logPath;

        bool          _wasPostMatch      = false;
        MatchSnapshot _lastPostMatchSnap = null;

        // Map/mode are carried from the lobby phase because:
        //   • ArenaTeamGS.CurrentMap (0x3E8) is server-side only — always 0 on the client.
        //   • CustomGameGS.ArenaRowName (0x328) is reliably replicated in lobby.
        // We keep the last known values and only overwrite them when we get a valid name.
        string _lobbyMapName  = null;
        string _lobbyModeName = null;

        public MatchSaver()
        {
            string hub = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BCA-Hub");

            _saveDir = Path.Combine(hub, "matches");
            _logPath = Path.Combine(hub, "saver.log");

            Directory.CreateDirectory(_saveDir);
            Log($"MatchSaver started. saveDir={_saveDir}");
        }

        public void Tick(MatchSnapshot snap)
        {
            if (snap == null)
            {
                Log("Tick(null): process exited");
                if (_lastPostMatchSnap != null)
                {
                    Log("  → saving pending post-match snap");
                    TrySave(_lastPostMatchSnap);
                    _lastPostMatchSnap = null;
                    _wasPostMatch      = false;
                }
                return;
            }

            // Cache map/mode from lobby. Only update when the name is valid so a
            // transient "Unknown" reading doesn't clobber a good cached value.
            if (snap.IsLobby && snap.Lobby != null)
            {
                if (IsValidName(snap.Lobby.MapName) && _lobbyMapName != snap.Lobby.MapName)
                {
                    _lobbyMapName = snap.Lobby.MapName;
                    Log($"Lobby map cached: \"{_lobbyMapName}\"");
                }
                if (IsValidName(snap.Lobby.ModeName) && _lobbyModeName != snap.Lobby.ModeName)
                {
                    _lobbyModeName = snap.Lobby.ModeName;
                    Log($"Lobby mode cached: \"{_lobbyModeName}\"");
                }
            }

            if (snap.IsPostMatch)
            {
                _lastPostMatchSnap = snap;
                _wasPostMatch      = true;
                return;
            }

            if (_wasPostMatch && _lastPostMatchSnap != null)
            {
                Log($"Tick: left post-match (state={snap.StateName}), saving…");
                TrySave(_lastPostMatchSnap);
                _lastPostMatchSnap = null;
                _wasPostMatch      = false;
                return;
            }

            if (snap.IsLobby || snap.IsWaiting)
            {
                _wasPostMatch      = false;
                _lastPostMatchSnap = null;
            }
        }

        public List<MatchRecord> LoadAll()
        {
            var records = new List<MatchRecord>();
            foreach (var file in Directory.GetFiles(_saveDir, "match_*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var rec = JsonSerializer.Deserialize<MatchRecord>(File.ReadAllText(file), JsonOpts);
                    if (rec != null) records.Add(rec);
                }
                catch { }
            }
            records.Sort((a, b) => b.PlayedAt.CompareTo(a.PlayedAt));
            return records;
        }

        // ── Private ─────────────────────────────────────────────────────────────

        static bool IsValidName(string name)
            => !string.IsNullOrEmpty(name) && !name.StartsWith("Unknown", StringComparison.Ordinal);

        void TrySave(MatchSnapshot snap)
        {
            try
            {
                // Prefer lobby-cached name; snap.CurrentMap is almost always "Unknown"
                // on the client (server-only field). Only use it as a last resort.
                string mapName = IsValidName(_lobbyMapName)  ? _lobbyMapName
                               : IsValidName(snap.CurrentMap) ? snap.CurrentMap
                               : "Unknown";

                string modeName = IsValidName(_lobbyModeName) ? _lobbyModeName : snap.ModeName;

                var    record = BuildRecord(snap, mapName, modeName);
                var    date   = snap.UpdatedAt;
                string dir    = Path.Combine(_saveDir, date.ToString("yyyy-MM"), date.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dir);

                string mapSlug  = Truncate(Sanitize(mapName),  30);
                string modeSlug = Truncate(Sanitize(modeName), 20);
                string file     = $"match_{date:HH-mm-ss}_{mapSlug}_{modeSlug}.json";
                string path     = Path.Combine(dir, file);

                File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOpts));
                Log($"Saved → {path}");
                Console.WriteLine($"  [Saved] {path}");

                // Reset cache so back-to-back matches don't inherit the old map.
                _lobbyMapName  = null;
                _lobbyModeName = null;
            }
            catch (Exception ex)
            {
                Log($"TrySave FAILED: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  [Save failed] {ex.Message}");
            }
        }

        static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '-');
            return s.Replace(' ', '-');
        }

        static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..max];

        void Log(string msg)
        {
            try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); }
            catch { }
        }

        MatchRecord BuildRecord(MatchSnapshot snap, string mapName, string modeName)
        {
            var record = new MatchRecord
            {
                PlayedAt     = snap.UpdatedAt.ToUniversalTime(),
                Map          = mapName,
                GameMode     = modeName,
                DurationSecs = snap.MatchTime,
            };

            var winners = snap.Players.FindAll(p => p.IsWinner && !p.IsBot);
            record.WinningTeam = winners.Count > 0 ? $"Team{winners[0].Team}" : "Unknown";

            foreach (var p in snap.Players)
            {
                record.Players.Add(new PlayerRecord
                {
                    Name          = p.Name,
                    Team          = p.Team,
                    IsBot         = p.IsBot,
                    BotLevel      = p.BotLevel,
                    IsLocalPlayer = p.IsLocal,
                    IsWinner      = p.IsWinner,
                    Weapon        = p.WeaponName,
                    Ability       = p.AbilityName,
                    Module        = p.ModuleName,

                    Kills           = p.Kills,
                    Deaths          = p.Deaths,
                    Assists         = p.Assists,
                    KDRatio         = p.KDRatio,
                    Accuracy        = p.Accuracy,
                    AbilityAccuracy = p.AbilityAccuracy,
                    Damage          = Math.Round(p.Damage, 1),
                    Heal            = Math.Round(p.Heal, 1),
                    NbHitsCaused    = p.NbHitsCaused,
                    NbHitsReceived  = p.NbHitsReceived,
                    Score           = p.Score,

                    ReceivedShieldDmg         = Math.Round(p.ReceivedShieldDmg, 1),
                    ReceivedEffectiveShieldDmg = Math.Round(p.ReceivedEffectiveShieldDmg, 1),
                    WeaponShieldDmgDealt       = Math.Round(p.WeaponShieldDmgDealt, 1),
                    AbilityShieldDmgDealt      = Math.Round(p.AbilityShieldDmgDealt, 1),

                    ImpulseReceived    = Math.Round(p.ImpulseReceived, 1),
                    WeaponImpulseDealt = Math.Round(p.WeaponImpulseDealt, 1),
                    AbilityImpulseDealt = Math.Round(p.AbilityImpulseDealt, 1),

                    Dashes         = p.Dashes,
                    Jumps          = p.Jumps,
                    AbilitiesUsed  = p.AbilitiesUsed,
                    NbAbilitiesHit = p.NbAbilitiesHit,
                    ShieldPickups  = p.ShieldPickups,
                    Empowerments   = p.Empowerments,
                    TimeAliveSecs  = Math.Round(p.TimeAlive, 1),

                    GravityDurationSecs = Math.Round(p.GravityDuration, 1),
                    GravityOnGround     = Math.Round(p.GravityOnGround, 1),
                    GravityInAir        = Math.Round(p.GravityInAir, 1),
                    GravityUseCount     = p.GravityUseCount,

                    NbTotalPings = p.NbTotalPings,
                    NbEnemyPings = p.NbEnemyPings,

                    FfaNbBackUp     = p.FfaNbBackUp,
                    FfaDeathRanking = p.FfaDeathRanking,
                });
            }

            foreach (var k in snap.KillFeed)
            {
                int t = (int)k.ElapsedSecs;
                record.KillFeed.Add(new KillRecord
                {
                    KillerName    = k.KillerName,
                    VictimName    = k.VictimName,
                    Cause         = k.Cause,
                    IsAbilityKill = k.IsAbilityKill,
                    KillerTeam    = k.KillerTeam,
                    TimeInMatch   = $"{t / 60}:{t % 60:D2}",
                });
            }

            return record;
        }
    }
}
