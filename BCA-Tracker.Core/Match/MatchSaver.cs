using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BCATracker.Core
{
    public class MatchSaver
    {
        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        readonly string _saveDir;
        readonly MatchUploadService? _uploader;

        bool _wasPostMatch = false;
        MatchSnapshot _lastPostMatchSnap = null;

        // Map/mode cached from lobby phase.
        // AGS_CurrentMap (0x3E8) is server-only and always 0 on the client.
        // CustomGameGS_C.ArenaRowName (0x328) is reliably replicated in lobby.
        string _lobbyMapName = null;
        string _lobbyModeName = null;

        public MatchSaver() : this(uploader: null) { }

        /// <summary>
        /// Construct with an optional uploader. When non-null, every match
        /// successfully saved to disk also gets enqueued for upload (the
        /// uploader itself decides whether to actually send anything based
        /// on its own enabled/endpoint configuration).
        /// </summary>
        public MatchSaver(MatchUploadService? uploader)
        {
            _uploader = uploader;
            string hub = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BCA-Tracker");
            _saveDir = Path.Combine(hub, "matches");
            Directory.CreateDirectory(_saveDir);
            DiagLog.Write($"[Saver] Started - saveDir={_saveDir}" +
                          (_uploader != null ? " (uploader attached)" : ""));
        }

        public void Tick(MatchSnapshot snap)
        {
            if (snap == null)
            {
                DiagLog.Write("[Saver] Tick(null) - process gone");
                if (_lastPostMatchSnap != null)
                {
                    DiagLog.Write("[Saver] Saving pending post-match snap on process exit");
                    TrySave(_lastPostMatchSnap);
                    _lastPostMatchSnap = null;
                    _wasPostMatch = false;
                }
                return;
            }

            // Cache map/mode from lobby — only overwrite with a valid name
            if (snap.IsLobby && snap.Lobby != null)
            {
                if (IsValidName(snap.Lobby.MapName))
                {
                    if (_lobbyMapName != snap.Lobby.MapName)
                    {
                        _lobbyMapName = snap.Lobby.MapName;
                        DiagLog.LobbyCached(_lobbyMapName, _lobbyModeName ?? "<pending>");
                    }
                }
                else
                {
                    DiagLog.LobbyMapInvalid(snap.Lobby.MapName);
                }

                if (IsValidName(snap.Lobby.ModeName) && _lobbyModeName != snap.Lobby.ModeName)
                {
                    _lobbyModeName = snap.Lobby.ModeName;
                    DiagLog.LobbyCached(_lobbyMapName ?? "<pending>", _lobbyModeName);
                }
            }

            if (snap.IsPostMatch)
            {
                if (!_wasPostMatch)
                    DiagLog.Write($"[Saver] Entered post-match - holding snap for save " +
                                  $"(map={_lobbyMapName ?? "null"} mode={_lobbyModeName ?? "null"} " +
                                  $"players={snap.Players?.Count ?? 0})");
                // Keep the snap that has the most players. Human players
                // (not bots) sometimes leave during the end-of-match
                // screens (state 10..13); their APlayerState gets removed
                // from GameState.PlayerArray and a naive "always replace"
                // policy ends up saving an incomplete scoreboard.
                // Tie-break on count goes to the newer snap so post-match
                // stat updates (final K/D, time-alive, etc.) are
                // preserved.
                int newCount = snap.Players?.Count ?? 0;
                int oldCount = _lastPostMatchSnap?.Players?.Count ?? -1;
                if (newCount >= oldCount)
                {
                    _lastPostMatchSnap = snap;
                }
                else
                {
                    DiagLog.Write($"[Saver] Ignoring post-match tick with {newCount} players " +
                                  $"(cached has {oldCount}) - a player likely left the lobby");
                }
                _wasPostMatch = true;
                return;
            }

            if (_wasPostMatch && _lastPostMatchSnap != null)
            {
                DiagLog.Write($"[Saver] Left post-match (now state={snap.StateName}) - saving");
                TrySave(_lastPostMatchSnap);
                _lastPostMatchSnap = null;
                _wasPostMatch = false;
                return;
            }

            if (snap.IsLobby || snap.IsWaiting || snap.IsMainMenu)
            {
                _wasPostMatch = false;
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
                catch (Exception ex)
                {
                    DiagLog.Write($"[Saver] LoadAll failed for {file}: {ex.Message}");
                }
            }
            records.Sort((a, b) => b.PlayedAt.CompareTo(a.PlayedAt));
            return records;
        }

        // ── Private ──────────────────────────────────────────────────────────

        static bool IsValidName(string name)
            => !string.IsNullOrEmpty(name)
            && !name.StartsWith("Unknown", StringComparison.Ordinal);

        void TrySave(MatchSnapshot snap)
        {
            try
            {
                string mapName = IsValidName(_lobbyMapName) ? _lobbyMapName
                                : IsValidName(snap.CurrentMap) ? snap.CurrentMap
                                : "Unknown";
                string modeName = IsValidName(_lobbyModeName) ? _lobbyModeName
                                : snap.ModeName;

                DiagLog.SaveTrigger(mapName, modeName, snap.Players?.Count ?? 0);

                var record = BuildRecord(snap, mapName, modeName);
                var date = snap.UpdatedAt;
                string dir = Path.Combine(_saveDir,
                                    date.ToString("yyyy-MM"),
                                    date.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dir);

                string file = $"match_{date:HH-mm-ss}_{Truncate(Sanitize(mapName), 30)}" +
                              $"_{Truncate(Sanitize(modeName), 20)}.json";
                string path = Path.Combine(dir, file);

                File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOpts));

                DiagLog.SaveOk(path);
                Console.WriteLine($"  [Saved] {path}");

                // Hand the saved match to the uploader. This is a no-op
                // when uploads aren't configured/enabled, so it's safe to
                // call unconditionally.
                _uploader?.Enqueue(path);

                // Reset cache so the next match doesn't inherit this map
                _lobbyMapName = null;
                _lobbyModeName = null;
            }
            catch (Exception ex)
            {
                DiagLog.SaveFailed(ex);
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

        MatchRecord BuildRecord(MatchSnapshot snap, string mapName, string modeName)
        {
            var record = new MatchRecord
            {
                PlayedAt = snap.UpdatedAt.ToUniversalTime(),
                Map = mapName,
                GameMode = modeName,
                DurationSecs = snap.MatchTime,
            };

            var winners = snap.Players?.FindAll(p => p.IsWinner && !p.IsBot);
            record.WinningTeam = winners?.Count > 0 ? $"Team{winners[0].Team}" : "Unknown";

            foreach (var p in snap.Players ?? new List<PlayerInfo>())
            {
                record.Players.Add(new PlayerRecord
                {
                    Name = p.Name,
                    Team = p.Team,
                    IsBot = p.IsBot,
                    BotLevel = p.BotLevel,
                    IsLocalPlayer = p.IsLocal,
                    IsWinner = p.IsWinner,
                    Weapon = p.WeaponName,
                    Ability = p.AbilityName,
                    Module = p.ModuleName,

                    Kills = p.Kills,
                    Deaths = p.Deaths,
                    Assists = p.Assists,
                    KDRatio = p.KDRatio,
                    Accuracy = p.Accuracy,
                    AbilityAccuracy = p.AbilityAccuracy,
                    Damage = Math.Round(p.Damage, 1),
                    Heal = Math.Round(p.Heal, 1),
                    NbHitsCaused = p.NbHitsCaused,
                    NbHitsReceived = p.NbHitsReceived,
                    Score = p.Score,

                    ReceivedShieldDmg = Math.Round(p.ReceivedShieldDmg, 1),
                    ReceivedEffectiveShieldDmg = Math.Round(p.ReceivedEffectiveShieldDmg, 1),
                    WeaponShieldDmgDealt = Math.Round(p.WeaponShieldDmgDealt, 1),
                    AbilityShieldDmgDealt = Math.Round(p.AbilityShieldDmgDealt, 1),

                    ImpulseReceived = Math.Round(p.ImpulseReceived, 1),
                    WeaponImpulseDealt = Math.Round(p.WeaponImpulseDealt, 1),
                    AbilityImpulseDealt = Math.Round(p.AbilityImpulseDealt, 1),

                    Dashes = p.Dashes,
                    Jumps = p.Jumps,
                    AbilitiesUsed = p.AbilitiesUsed,
                    NbAbilitiesHit = p.NbAbilitiesHit,
                    ShieldPickups = p.ShieldPickups,
                    Empowerments = p.Empowerments,
                    TimeAliveSecs = Math.Round(p.TimeAlive, 1),

                    GravityDurationSecs = Math.Round(p.GravityDuration, 1),
                    GravityOnGround = Math.Round(p.GravityOnGround, 1),
                    GravityInAir = Math.Round(p.GravityInAir, 1),
                    GravityUseCount = p.GravityUseCount,

                    NbTotalPings = p.NbTotalPings,
                    NbEnemyPings = p.NbEnemyPings,

                    FfaNbBackUp = p.FfaNbBackUp,
                    FfaDeathRanking = p.FfaDeathRanking,
                });
            }

            foreach (var k in snap.KillFeed)
            {
                int t = (int)k.ElapsedSecs;
                record.KillFeed.Add(new KillRecord
                {
                    KillerName = k.KillerName,
                    VictimName = k.VictimName,
                    Cause = k.Cause,
                    IsAbilityKill = k.IsAbilityKill,
                    KillerTeam = k.KillerTeam,
                    TimeInMatch = $"{t / 60}:{t % 60:D2}",
                });
            }

            return record;
        }
    }
}