using System;
using System.Collections.Generic;

namespace BCATracker.Core
{
    public class PlayerInfo
    {
        public long StatePtr;
        public long HitHistoryPtr;
        public int  LocalID;
        public bool IsLocal;

        public string Name = "";
        public int    Team;
        public bool   IsBot;
        public int    BotLevel;
        public bool   IsHost;
        public bool   IsWinner;

        // Core combat
        public int    Kills;
        public int    Deaths;
        public int    Assists;
        public int    Score;           // PersonalScore (Q-Ball: time holding ball in some unit)
        public int    Shots;           // NbShots (weapon fire presses)
        public int    SuccessfulShots; // NbSuccessfulShots (weapon projectile hits)
        public int    NbWeaponUsed;    // NbTotalWeaponUsedSoFar (same as Shots but client-local)
        public int    NbWeaponHit;     // NbTotalWeaponHitSoFar
        public int    NbAbilitiesHit;  // ability shots that actually connected
        public int    NbHitsCaused;    // all hits dealt (weapon + ability combined)
        public int    NbHitsReceived;  // all hits taken
        public double Damage;
        public double Heal;

        // Shield
        public double ReceivedShieldDmg;           // raw shield dmg taken
        public double ReceivedEffectiveShieldDmg;  // effective (post-shield reduction) dmg taken
        public double WeaponShieldDmgDealt;
        public double AbilityShieldDmgDealt;

        // Impulse
        public double ImpulseReceived;
        public double WeaponImpulseDealt;
        public double AbilityImpulseDealt;

        // Movement / ability usage
        public int    ShieldPickups;
        public int    Empowerments;    // NbEmpowermentSoFar
        public int    Dashes;
        public int    Jumps;
        public int    AbilitiesUsed;
        public double TimeAlive;
        public int    DashMode;

        // Gravity control
        public double GravityDuration;   // total seconds
        public double GravityOnGround;
        public double GravityInAir;
        public int    GravityUseCount;   // number of activations

        // Pings
        public int    NbTotalPings;
        public int    NbEnemyPings;

        // Loadout
        public byte   Weapon;
        public byte   Ability;
        public byte   Module;

        // FFA-specific (BackupFFAPS_C) — 0 in other modes
        public int    FfaNbBackUp;     // backup/respawn lives consumed
        public int    FfaDeathRanking; // elimination order (1 = first out)

        // Computed
        public string WeaponName  => BCAEnums.WeaponName(Weapon);
        public string AbilityName => BCAEnums.AbilityName(Ability);
        public string ModuleName  => BCAEnums.ModuleName(Module);

        public float Accuracy => Shots > 0
            ? Math.Min(100f, (float)SuccessfulShots / Shots * 100f) : 0f;

        // Ability accuracy: how many ability uses actually hit
        public float AbilityAccuracy => AbilitiesUsed > 0
            ? Math.Min(100f, (float)NbAbilitiesHit / AbilitiesUsed * 100f) : 0f;

        public float KDRatio => Deaths > 0 ? (float)Kills / Deaths : Kills;

        public string DisplayName
        {
            get
            {
                string tag = IsBot ? $"[BOT{BotLevel}]{Name}" : Name;
                if (IsLocal)  tag = "*"  + tag;
                else if (IsHost) tag = "H:" + tag;
                return tag.Length > 19 ? tag[..19] : tag;
            }
        }

        public string FormatAlive()
            => TimeAlive > 0 ? $"{(int)TimeAlive / 60}m{(int)TimeAlive % 60}s" : "-";
    }

    public class LobbyData
    {
        public string MapName  = "Unknown";
        public string ModeName = "Unknown";
        public int    BotCountT1;
        public int    BotCountT2;
    }

    public class KillFeedEntry
    {
        public DateTime Time;
        public string   KillerName  = "";
        public string   VictimName  = "";
        public string   Cause       = "";
        public bool     IsAbilityKill;
        public int      KillerTeam;
        public double   ElapsedSecs;
        /// <summary>Memory state-ptr of the victim. Used by KillFeedTracker
        /// to look up recent-damage info during the suicide-fixup pass.
        /// Not persisted to JSON.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public long     VictimStatePtr;
    }

    public class MatchSnapshot
    {
        public List<PlayerInfo>    Players  = new List<PlayerInfo>();
        public List<KillFeedEntry> KillFeed = new List<KillFeedEntry>();
        public int    MyLives;
        public int    EnemyLives;
        public byte   GameStateEnum;
        public byte   GameModeEnum;
        public bool   InMatch;
        public bool   IsPostMatch;
        public bool   IsLobby;
        public bool   IsWaiting;
        public bool   IsMainMenu;
        public LobbyData Lobby;
        public double MatchTime;
        public string CurrentMap  = "";
        public string WorldName   = "";
        public byte   MainMenuState;
        public DateTime UpdatedAt = DateTime.Now;

        // Q-Ball specific GS data (only valid when GameModeEnum == GoldenCore3v3)
        public int  QBallHolderTeam = -1;  // -1 = nobody; 0 or 1 = team index

        public string StateName => BCAEnums.GameStateName(GameStateEnum);
        public string ModeName  => BCAEnums.GameModeName(GameModeEnum);
        public string Timer
        {
            get { int t = (int)MatchTime; return $"{t / 60}:{t % 60:D2}"; }
        }
    }
}
