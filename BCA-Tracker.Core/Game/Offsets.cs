namespace BCATracker.Core
{
    /// <summary>
    /// Memory offsets for BattleCore Arena (UE5.1, build 5.1.0-20828).
    ///
    /// The game was permanently shut down by Ubisoft in Feb 2025, so no
    /// further patches will land - these offsets are valid forever for
    /// any copy of the final client build.
    ///
    /// Primary chain (everything important goes through GWorld):
    ///   BattleCoreArena.exe + GWorld          - GWorld pointer
    ///   [GWorld] + World_GameState (+0x158)   - GameState object
    ///   - In a custom lobby this is CustomGameGS_C  (size 0x430)
    ///   - In a live match this is ArenaTeamGS_C    (size 0x708+)
    ///
    /// Distinguishing the two at runtime:
    ///   - Tier 1 (preferred): resolve the GS UObject's UClass.NamePrivate
    ///     FName via FNameResolver to get the exact class name as a string.
    ///   - Tier 2 (fallback):  read the byte at GameState+0x369.
    ///     ArenaTeamGS_C uses it as the CurrentGameState enum (0..15).
    ///     CustomGameGS_C has a delegate there - reads garbage / >15.
    ///     MainMenuGS_C is only 0x308 bytes long so reads beyond that
    ///     return heap junk; check the class name first.
    ///
    /// Local player's own PlayerState (always YOUR state, regardless of
    /// PlayerArray order):
    ///   [BattleCoreArena.exe + LocalPCSlot (+0x07C09D80)] + 0x30 + 0x298
    /// </summary>
    internal static class Offsets
    {
        // ── Module-relative bases ────────────────────────────────────────
        public const long GWorld      = 0x07C28880;
        public const long LocalPCSlot = 0x07C09D80;

        // Lives pointer chains (legacy, pre-GWorld; still usable).
        public static readonly long    BASE_ENEMY_LIVES = 0x07C09D80;
        public static readonly int[]   OFF_ENEMY_LIVES  = { 0x30, 0x340, 0x690, 0x18 };
        public static readonly long    BASE_MY_LIVES    = 0x07C28880;
        public static readonly int[]   OFF_MY_LIVES     = { 0x158, 0x348, 0x0 };

        // ── UWorld ───────────────────────────────────────────────────────
        public const int World_GameState = 0x158;

        // ── Engine AGameState (base of every GS class) ───────────────────
        // Both CustomGameGS and ArenaTeamGS derivatives extend AGameState,
        // so they share these PlayerArray fields.
        public const int GS_PlayerArray_Data  = 0x2A8;  // TArray<APlayerState*>.Data
        public const int GS_PlayerArray_Count = 0x2B0;  // TArray count (int32)

        // ── CustomGameGS_C (lobby) ───────────────────────────────────────
        public const int CGS_GameName            = 0x310;  // FText (0x18) - display name typed by host
        public const int CGS_ArenaRowName        = 0x328;  // FName (low 4 bytes = index)
        public const int CGS_PreviousGameMode    = 0x330;  // FName - last mode used (informational)
        public const int CGS_GameModeRowName     = 0x338;  // FName
        public const int CGS_BotCountT1          = 0x390;  // int32 (doc: AmountOfBotsT1)
        public const int CGS_BotCountT2          = 0x394;  // int32 (doc: AmountOfBotsT2)
        public const int CGS_LobbyPassword       = 0x3D0;  // FGuid (16 bytes) - 0 if no password
        public const int CGS_ConnectedPlayersArr_Data  = 0x3F0;  // TArray<PS*>.Data
        public const int CGS_ConnectedPlayersArr_Count = 0x3F8;  // TArray count
        public const int CGS_MaxTeamSize         = 0x418;  // int32
        public const int CGS_MaxSpectatorSize    = 0x41C;  // int32

        // ── ArenaTeamGS_C (in-match) ─────────────────────────────────────
        // Use +0x369 (the EGameState byte) to verify this is really an
        // ArenaTeamGS - values 0..15 mean yes, >15 means it's something
        // else (CustomGameGS, transition state, garbage).
        public const int AGS_GameState  = 0x369;  // EGameState byte (see Enums.cs)
        public const int AGS_GameMode   = 0x3B0;  // EGameMode byte
        public const int AGS_WinnerTeam = 0x3C8;  // int32
        // NOTE: AGS_CurrentMap is only set server-side; it is 0 on the client.
        // MatchSaver._lobbyMapName is the reliable source. We still read it
        // for potential future dedicated-server use or display when non-zero.
        public const int AGS_CurrentMap = 0x3E8;  // FName
        public const int AGS_TeamScore  = 0x348;  // TArray<int32>
        public const int AGS_MatchID    = 0x538;  // FString - server-assigned match GUID
        public const int AGS_IsRanked   = 0x568;  // bool
        public const int AGS_BotDeaths  = 0x6B0;  // int32
        public const int AGS_GameTime   = 0x340;  // double

        // ── GoldenCoreGS_C (Q-Ball, extends ArenaTeamGS_C at 0x0708) ──────
        public const int GCGS_GoldenCoreTeam = 0x770;  // int32 - team holding the ball (-1 = nobody)
        public const int GCGS_EGCGameState   = 0x718;  // EGoldenCoreGameState uint8

        // ── BackupFFAGS_C (FFA, extends BackupGS_C->ArenaTeamGS_C at 0x0718)
        public const int FFAGS_CurrentDeathRanking = 0x718; // int32

        // ── APlayerController ────────────────────────────────────────────
        public const int PC_PlayerState = 0x298;
        public const int PC_MyHUD       = 0x340;

        // ── MainMenuHUD_C ────────────────────────────────────────────────
        public const int MMHUD_MainMenuState = 0x428;

        // ── CustomGamePS_C (lobby player state, base = 0x03C0) ───────────
        // Different layout from in-match ArenaTeamPS_C. Only used while
        // the GameState class is CustomGameGS_C.
        public const int CPS_Team             = 0x3D8;  // int32  - same as PS_Team but at a different offset
        public const int CPS_IsGameLeader     = 0x3F4;  // bool   - IS THE HOST of the lobby
        public const int CPS_CustomProfileID  = 0x3F8;  // FGuid  - stable per-player ID; replaces our random GUID

        // ── Engine-level APlayerState fields (parent of every PS variant)
        // These work for both lobby and in-match player states because
        // the engine-level fields live before the BCA-specific overlays.
        public const int APS_PlayerNamePrivate = 0x388;  // FString - display name
        public const int APS_bIsABot_Byte      = 0x29A;  // bIsABot is bit 3 of this byte

        // ── ArenaTeamPS_C (universal player state, base = 0x03C0) ────────
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
        public const int PS_LobbyID                = 0x408;  // FString - lobby session id this player belongs to
        public const int PS_Weapon                 = 0x430;  // EWeapons uint8
        public const int PS_Module                 = 0x431;  // EModules uint8
        public const int PS_NbAssists              = 0x450;  // int32
        public const int PS_LocalID                = 0x558;  // int32 (only valid for local player; remotes get 0/garbage)
        public const int PS_PersonalScore          = 0x55C;  // int32 (Q-Ball: score = time holding ball)
        public const int PS_IsWinner               = 0x580;  // bool
        public const int PS_IsHost                 = 0x581;  // bool
        public const int PS_KDAScore               = 0x588;  // double
        public const int PS_NbEmpowermentSoFar     = 0x5A0;  // int32  empowerment pickups
        public const int PS_ShieldPickups          = 0x5A4;  // int32
        public const int PS_ReceivedShieldDmg      = 0x5A8;  // double - NbReceivedShieldDamageSoFar
        public const int PS_ReceivedEffectiveShieldDmg = 0x5B0;  // double - NbReceivedEffectiveShieldDamageSoFar
        public const int PS_DashMode               = 0x5B8;  // int32
        public const int PS_ImpulseReceived        = 0x5C0;  // double
        public const int PS_Dashes                 = 0x5C8;  // int32
        public const int PS_Jumps                  = 0x5CC;  // int32
        public const int PS_AbilitiesUsed          = 0x5D0;  // int32
        public const int PS_Item_Ability           = 0x638;  // UItemObject_Ability_C*  active ability item
        public const int PS_Item_Weapon            = 0x648;  // UItemObject_Weapon_C*   active weapon item
        public const int PS_Item_Module            = 0x650;  // UItemObject_Module_C*   active module item
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
        public const int PS_AbilityShieldDmgDealt  = 0x760;  // double  AbilityShieldDamageDealtTotal
        public const int PS_WeaponShieldDmgDealt   = 0x768;  // double  WeaponShieldDamageDealtTotal
        public const int PS_NbAbilitiesHit         = 0x770;  // int32  ability shots that connected
        public const int PS_Damage                 = 0x790;  // double  NbDamages - matches post-game DMG column
        public const int PS_Heal                   = 0x798;  // double  NbHeals  - matches post-game HEAL column

        // ── BackupFFAPS_C extensions (base = 0x07A0) ─────────────────────
        // Class: BackupFFAPS_C -> BackupPS_C -> ArenaTeamPS_C
        public const int FFAPS_NbBackUp      = 0x7A0;  // int32 backup/respawn lives consumed
        public const int FFAPS_DeathRanking  = 0x7A4;  // int32 elimination order (1 = first eliminated)

        // ── GoldenCorePS_C extensions ────────────────────────────────────
        // Class: GoldenCorePS_C -> ArenaTeamPS_C - no new PS fields.
        // Q-Ball score is PS_PersonalScore. Ball-holder team is GCGS_GoldenCoreTeam on GS.

        // ── UHitHistory_C component ──────────────────────────────────────
        // Each PlayerState's HitHistory pointer at PS_HitHistory (+0x3C8).
        // Stores hits RECEIVED by this player. The killing blow is the
        // last entry at the moment NbDeaths increments. History is cleared
        // at the start of each life (count resets to a low value).
        public const int HH_History_Data  = 0xA0;  // TArray<FSHitInfo>.Data
        public const int HH_History_Count = 0xA8;  // int32

        // ── FSHitInfo struct (0x50 bytes per entry) ──────────────────────
        // The remaining 0x47 bytes contain damage values, location, and
        // hit type flags - not fully decoded yet.
        public const int HitInfo_Size         = 0x50;
        public const int HitInfo_InstigatorID = 0x00;  // int32 - killer's LocalID for humans, negative for bots
        public const int HitInfo_TargetID     = 0x04;  // int32 - victim's InstigatorID (used to learn bot mapping)
        public const int HitInfo_Weapon       = 0x08;  // byte  EWeapons (0 if ability kill or environment)
        public const int HitInfo_Ability      = 0x09;  // byte  EAbilities (0 if weapon kill or environment)

        // ── UObject layout (for class-name resolution) ───────────────────
        // +0x00 VTable  +0x08 ObjectFlags  +0x0C InternalIndex
        // +0x10 ClassPrivate (UClass*)  +0x18 NamePrivate (FName)  +0x20 OuterPrivate
        public const int UObj_ClassPrivate = 0x10;
        public const int UObj_NamePrivate  = 0x18;
    }
}
