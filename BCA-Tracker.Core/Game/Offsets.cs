namespace BCATracker.Core
{
    internal static class Offsets
    {
        public const long GWorld      = 0x07C28880;
        public const long LocalPCSlot = 0x07C09D80;

        // Lives pointer chains
        public static readonly long    BASE_ENEMY_LIVES = 0x07C09D80;
        public static readonly int[]   OFF_ENEMY_LIVES  = { 0x30, 0x340, 0x690, 0x18 };
        public static readonly long    BASE_MY_LIVES    = 0x07C28880;
        public static readonly int[]   OFF_MY_LIVES     = { 0x158, 0x348, 0x0 };

        // UWorld
        public const int World_GameState = 0x158;

        // Engine AGameState (base of all GS classes)
        public const int GS_PlayerArray_Data  = 0x2A8;
        public const int GS_PlayerArray_Count = 0x2B0;

        // CustomGameGS_C  (lobby)
        public const int CGS_ArenaRowName    = 0x328;
        public const int CGS_GameModeRowName = 0x338;
        public const int CGS_BotCountT1      = 0x390;
        public const int CGS_BotCountT2      = 0x394;

        // ArenaTeamGS_C  (in-match)
        public const int AGS_GameState  = 0x369;
        public const int AGS_GameMode   = 0x3B0;
        public const int AGS_WinnerTeam = 0x3C8;
        // NOTE: AGS_CurrentMap is only set server-side; it is 0 on the client.
        // MatchSaver._lobbyMapName is the reliable source. We still read it for
        // potential future dedicated-server use or for display when non-zero.
        public const int AGS_CurrentMap = 0x3E8;
        public const int AGS_TeamScore  = 0x348;
        public const int AGS_MatchID    = 0x538;
        public const int AGS_IsRanked   = 0x568;
        public const int AGS_BotDeaths  = 0x6B0;
        public const int AGS_GameTime   = 0x340;

        // GoldenCoreGS_C  (Q-Ball, extends ArenaTeamGS_C at 0x0708)
        public const int GCGS_GoldenCoreTeam = 0x770;  // int32 — team holding the ball (-1 = nobody)
        public const int GCGS_EGCGameState   = 0x718;  // EGoldenCoreGameState uint8

        // BackupFFAGS_C  (FFA, extends BackupGS_C->ArenaTeamGS_C at 0x0718)
        public const int FFAGS_CurrentDeathRanking = 0x718; // int32

        // APlayerController
        public const int PC_PlayerState = 0x298;
        public const int PC_MyHUD       = 0x340;

        // MainMenuHUD_C
        public const int MMHUD_MainMenuState = 0x428;

        // ── ArenaTeamPS_C (universal player state, base = 0x03C0) ────────────────
        public const int PS_HitHistory             = 0x3C8;  // UHitHistory_C*
        public const int PS_NbShots                = 0x3D8;  // int32
        public const int PS_NbSuccessfulShots      = 0x3DC;  // int32
        public const int PS_NbKills                = 0x3E0;  // int32
        public const int PS_NbDeaths               = 0x3E4;  // int32
        public const int PS_Name                   = 0x3E8;  // FString
        public const int PS_Team                   = 0x3F8;  // int32
        public const int PS_Ability                = 0x3FC;  // EAbilities uint8
        public const int PS_IsSpectator            = 0x3FD;  // bool
        public const int PS_IsBot                  = 0x3FE;  // bool
        public const int PS_BotLevel               = 0x400;  // int32
        public const int PS_Weapon                 = 0x430;  // EWeapons uint8
        public const int PS_Module                 = 0x431;  // EModules uint8
        public const int PS_NbAssists              = 0x450;  // int32
        public const int PS_LocalID                = 0x558;  // int32
        public const int PS_PersonalScore          = 0x55C;  // int32  (Q-Ball: score = time holding ball)
        public const int PS_NbEmpowermentSoFar     = 0x5A0;  // int32  empowerment pickups
        public const int PS_ShieldPickups          = 0x5A4;  // int32
        public const int PS_ReceivedShieldDmg      = 0x5A8;  // double raw shield damage taken
        public const int PS_ReceivedEffectiveShieldDmg = 0x5B0; // double effective shield damage taken
        public const int PS_DashMode               = 0x5B8;  // int32
        public const int PS_ImpulseReceived        = 0x5C0;  // double
        public const int PS_Dashes                 = 0x5C8;  // int32
        public const int PS_Jumps                  = 0x5CC;  // int32
        public const int PS_AbilitiesUsed          = 0x5D0;  // int32
        public const int PS_IsWinner               = 0x580;  // bool
        public const int PS_IsHost                 = 0x581;  // bool
        public const int PS_KDAScore               = 0x588;  // double
        public const int PS_Item_Ability           = 0x638;  // ptr
        public const int PS_Item_Weapon            = 0x648;  // ptr
        public const int PS_Item_Module            = 0x650;  // ptr
        public const int PS_TimeAlive              = 0x660;  // double
        public const int PS_NbTotalPings           = 0x668;  // int32
        public const int PS_NbEnemyPings           = 0x66C;  // int32
        public const int PS_GravityDuration        = 0x670;  // double total gravity control time
        public const int PS_GravityOnGround        = 0x678;  // double on-ground portion
        public const int PS_GravityInAir           = 0x680;  // double in-air portion
        public const int PS_GravityUseCount        = 0x688;  // int32  number of times gravity used
        public const int PS_NbHitsCaused           = 0x6D4;  // int32  hits dealt (all sources)
        public const int PS_NbHitsReceived         = 0x6D8;  // int32  hits taken
        public const int PS_NbWeaponUsed           = 0x700;  // int32  weapon fire count
        public const int PS_NbWeaponHit            = 0x704;  // int32  weapon hits landed
        public const int PS_WeaponImpulseDealt     = 0x750;  // double
        public const int PS_AbilityImpulseDealt    = 0x758;  // double
        public const int PS_AbilityShieldDmgDealt  = 0x760;  // double
        public const int PS_WeaponShieldDmgDealt   = 0x768;  // double
        public const int PS_NbAbilitiesHit         = 0x770;  // int32  ability shots that connected
        public const int PS_Damage                 = 0x790;  // double
        public const int PS_Heal                   = 0x798;  // double

        // ── BackupFFAPS_C extensions (base = 0x07A0) ────────────────────────────
        // Class: BackupFFAPS_C -> BackupPS_C -> ArenaTeamPS_C
        public const int FFAPS_NbBackUp      = 0x7A0;  // int32 backup/respawn lives consumed
        public const int FFAPS_DeathRanking  = 0x7A4;  // int32 elimination order (1 = first eliminated)

        // ── GoldenCorePS_C extensions ────────────────────────────────────────────
        // Class: GoldenCorePS_C -> ArenaTeamPS_C — no new PS fields.
        // Q-Ball score is PS_PersonalScore. Ball-holder team is GCGS_GoldenCoreTeam on GS.

        // ── HitHistory ───────────────────────────────────────────────────────────
        public const int HH_History_Data  = 0xA0;
        public const int HH_History_Count = 0xA8;

        public const int HitInfo_Size         = 0x50;
        public const int HitInfo_InstigatorID = 0x00;
        public const int HitInfo_TargetID     = 0x04;
        public const int HitInfo_Weapon       = 0x08;
        public const int HitInfo_Ability      = 0x09;
    }
}
