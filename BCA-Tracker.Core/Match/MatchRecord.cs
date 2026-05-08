using System;
using System.Collections.Generic;

namespace BCATracker.Core
{
    public class MatchRecord
    {
        // Metadata
        public string   MatchId      { get; set; } = Guid.NewGuid().ToString();
        public DateTime PlayedAt     { get; set; } = DateTime.UtcNow;
        public string   Map          { get; set; } = "";
        public string   GameMode     { get; set; } = "";
        public double   DurationSecs { get; set; }
        public string   WinningTeam  { get; set; } = "";

        // Players
        public List<PlayerRecord> Players  { get; set; } = new List<PlayerRecord>();

        // Kill feed
        public List<KillRecord> KillFeed { get; set; } = new List<KillRecord>();
    }

    public class PlayerRecord
    {
        // Identity
        public string  Name          { get; set; } = "";
        public int     Team          { get; set; }
        public bool    IsBot         { get; set; }
        public int     BotLevel      { get; set; }
        public bool    IsLocalPlayer { get; set; }
        public bool    IsWinner      { get; set; }

        // Stable account identity. Populated by the reader from
        // APlayerState.UniqueId in a future build; for now this stays null on
        // newly-saved matches. Old matches have always been null. The UI
        // identifies "the local player across all matches" by matching this
        // value when present, falling back to IsLocalPlayer when not.
        public string? AccountId     { get; set; }

        // Loadout
        public string Weapon  { get; set; } = "";
        public string Ability { get; set; } = "";
        public string Module  { get; set; } = "";

        // Core combat
        public int    Kills           { get; set; }
        public int    Deaths          { get; set; }
        public int    Assists         { get; set; }
        public float  KDRatio         { get; set; }
        public float  Accuracy        { get; set; }   // weapon shot accuracy
        public float  AbilityAccuracy { get; set; }   // ability hit accuracy
        public double Damage          { get; set; }
        public double Heal            { get; set; }
        public int    NbHitsCaused    { get; set; }
        public int    NbHitsReceived  { get; set; }
        public int    Score           { get; set; }   // PersonalScore (Q-Ball: hold time)

        // Shield
        public double ReceivedShieldDmg          { get; set; }
        public double ReceivedEffectiveShieldDmg  { get; set; }
        public double WeaponShieldDmgDealt        { get; set; }
        public double AbilityShieldDmgDealt       { get; set; }

        // Impulse
        public double ImpulseReceived     { get; set; }
        public double WeaponImpulseDealt  { get; set; }
        public double AbilityImpulseDealt { get; set; }

        // Movement / ability
        public int    Dashes        { get; set; }
        public int    Jumps         { get; set; }
        public int    AbilitiesUsed { get; set; }
        public int    NbAbilitiesHit { get; set; }
        public int    ShieldPickups { get; set; }
        public int    Empowerments  { get; set; }
        public double TimeAliveSecs { get; set; }

        // Gravity
        public double GravityDurationSecs { get; set; }
        public double GravityOnGround     { get; set; }
        public double GravityInAir        { get; set; }
        public int    GravityUseCount     { get; set; }

        // Pings
        public int NbTotalPings { get; set; }
        public int NbEnemyPings { get; set; }

        // FFA only (0 in other modes)
        public int FfaNbBackUp     { get; set; }
        public int FfaDeathRanking { get; set; }
    }

    public class KillRecord
    {
        public string KillerName    { get; set; } = "";
        public string VictimName    { get; set; } = "";
        public string Cause         { get; set; } = "";
        public bool   IsAbilityKill { get; set; }
        public int    KillerTeam    { get; set; }
        public string TimeInMatch   { get; set; } = "";
    }
}
