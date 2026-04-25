using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class BCATracker
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EnumProcessModulesEx(IntPtr hProcess, [Out] IntPtr[] lphModule, int cb, out int lpcbNeeded, uint dwFilterFlag);

    [DllImport("psapi.dll")]
    static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, [Out] char[] lpBaseName, int nSize);

    [DllImport("psapi.dll")]
    static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

    [StructLayout(LayoutKind.Sequential)]
    struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    const int PROCESS_VM_READ = 0x0010;
    const int PROCESS_QUERY_INFORMATION = 0x0400;
    const uint LIST_MODULES_ALL = 0x03;

    // ============================================================
    // MEMORY LAYOUT CONSTANTS
    // ============================================================

    // Static offsets in BattleCoreArena.exe
    static readonly long GWORLD_OFFSET = 0x07C28880;

    // GameState chain: [GWorld + 0x158] = GameState object
    const int OFF_WORLD_TO_GAMESTATE = 0x158;

    // GameState -> CustomGameGS offsets (lobby)
    const int OFF_CUSTOMGS_ARENA_ROWNAME = 0x328;       // FName (8 bytes, but low 4 are index)
    const int OFF_CUSTOMGS_GAMEMODE_ROWNAME = 0x338;    // FName

    // GameState -> AGameStateBase.PlayerArray (works in-match)
    const int OFF_GAMESTATE_PLAYERARRAY_DATA = 0x2A8;   // TArray<APlayerState*>.Data
    const int OFF_GAMESTATE_PLAYERARRAY_COUNT = 0x2B0;  // TArray<APlayerState*>.Count (int32)

    // ArenaTeamPS_C (per-player in match) field offsets
    const int OFF_PS_HIT_HISTORY = 0x3C8;    // UHitHistory_C*
    const int OFF_PS_NB_SHOTS = 0x3D8;
    const int OFF_PS_NB_SUCCESSFUL_SHOTS = 0x3DC;
    const int OFF_PS_NB_KILLS = 0x3E0;
    const int OFF_PS_NB_DEATHS = 0x3E4;
    const int OFF_PS_NAME = 0x3E8;        // FString (16 bytes: data ptr + count + max)
    const int OFF_PS_TEAM = 0x3F8;        // int32
    const int OFF_PS_ABILITY = 0x3FC;     // EAbilities (byte)
    const int OFF_PS_IS_SPECTATOR = 0x3FD;
    const int OFF_PS_IS_BOT = 0x3FE;
    const int OFF_PS_BOT_LEVEL = 0x400;   // int32
    const int OFF_PS_WEAPON = 0x430;      // EWeapons (byte)
    const int OFF_PS_MODULE = 0x431;      // EModules (byte)
    const int OFF_PS_NB_ASSISTS = 0x450;
    const int OFF_PS_LOCAL_ID = 0x558;    // int32 — used by SHitInfo.InstigatorID
    const int OFF_PS_PERSONAL_SCORE = 0x55C;
    const int OFF_PS_IS_HOST = 0x581;
    const int OFF_PS_DAMAGE = 0x790;      // double, replicated
    const int OFF_PS_HEAL = 0x798;        // double, replicated
    const int OFF_PS_SHIELD_PICKUPS = 0x5A4;   // int32
    const int OFF_PS_DASHES = 0x5C8;           // int32
    const int OFF_PS_JUMPS = 0x5CC;            // int32
    const int OFF_PS_ABILITIES_USED = 0x5D0;   // int32
    const int OFF_PS_IS_WINNER = 0x580;        // bool
    const int OFF_PS_KDA_SCORE = 0x588;        // double
    const int OFF_PS_TIME_ALIVE = 0x660;       // double
    const int OFF_PS_DASH_MODE = 0x5B8;        // int32
    const int OFF_PS_IMPULSE_RECEIVED = 0x5C0; // double
    const int OFF_PS_GRAVITY_DURATION = 0x670; // double

    // HitHistory_C component offsets (inherits UPlayerStateComponent size 0xA0)
    const int OFF_HH_HISTORY_DATA = 0xA0;    // TArray<FSHitInfo>.Data
    const int OFF_HH_HISTORY_COUNT = 0xA8;   // int32

    // FSHitInfo struct offsets (0x50 bytes total)
    const int SIZEOF_HITINFO = 0x50;
    const int OFF_HIT_INSTIGATOR_ID = 0x0;   // int32
    const int OFF_HIT_TARGET_ID = 0x4;       // int32
    const int OFF_HIT_WEAPON = 0x8;          // byte
    const int OFF_HIT_ABILITY = 0x9;         // byte

    // ArenaTeamGS_C match-specific offsets (inherits from ABCAGameState -> AGameState -> AGameStateBase)
    const int OFF_ARENAGS_TEAMSCORE = 0x348;            // TArray<int32>
    const int OFF_ARENAGS_CURRENT_GAMESTATE = 0x369;    // EGameState byte
    const int OFF_ARENAGS_CURRENT_GAMEMODE = 0x3B0;     // EGameMode byte
    const int OFF_ARENAGS_CURRENT_MAP = 0x3E8;          // FName
    const int OFF_ARENAGS_GAMETIME = 0x340;             // double (seconds elapsed)

    // Working pointer chains for local player from previous session
    static readonly long BASE_ENEMY_LIVES = 0x07C09D80;
    static readonly int[] OFF_ENEMY_LIVES = { 0x30, 0x340, 0x690, 0x18 };

    static readonly long BASE_MY_LIVES = 0x07C28880;
    static readonly int[] OFF_MY_LIVES = { 0x158, 0x348, 0x0 };

    // Static slot that points to the local PlayerController.
    // [BASE_LOCAL_PC]+30 = local PC, then +0x298 = APlayerController.PlayerState which is OUR state.
    static readonly long BASE_LOCAL_PC = 0x07C09D80;

    // Enum tables
    static readonly string[] WEAPONS = { "None", "Sparkler", "Shafter", "Revoker", "Pounder", "Striker", "Spreader", "Ransacker" };
    static readonly string[] ABILITIES = { "None", "Blast", "ShieldHealing", "ProtectiveWall", "BlackHole", "Invisibility", "Flashbang", "TeleportationProj" };
    static readonly string[] MODULES = { "None", "Regeneration", "Mobility", "HealBooster", "Vampirism", "Camouflage", "Synchronisation", "Berserker", "Anchoring" };
    static readonly string[] GAMEMODES = { "Default", "Tuto", "Training", "Backup3v3", "BackupFFA", "GoldenCore3v3", "TestMap", "Checkpoint", "Autotest", "CoopVSAI", "CustomGamemode", "Quickplay", "First_Match_VS_AI" };

    // ============================================================
    // LOBBY DATA
    // ============================================================

    class LobbyData
    {
        public string MapName = "Unknown";
        public string ModeName = "Unknown";
        public int BotCountT1;
        public int BotCountT2;
    }

    // FName index → map display name (assigned at runtime, stable within session)
    static readonly Dictionary<int, string> MAP_FNAMES = new Dictionary<int, string>
    {
        { 0x5BE09, "Trinity Island" },
        { 0x2DEAF, "Lost Complex" },
        { 0x59D6C, "Singularity" },
        { 0x2DEA8, "Shroomworld" },
        { 0x404EF, "Twilight Path" },
    };
    // Nest's FName index is assigned at runtime by the DLL — we discover it dynamically
    static int nestFNameIndex = 0;

    static readonly Dictionary<int, string> MODE_FNAMES = new Dictionary<int, string>
    {
        { 0x16BE22, "Backup 3v3" },
        { 0x16DACC, "Q-Ball" },
    };

    static LobbyData ReadLobbyData(IntPtr handle, long gameState)
    {
        var data = new LobbyData();

        int arenaFName = ReadInt(handle, gameState + OFF_CUSTOMGS_ARENA_ROWNAME);
        int modeFName = ReadInt(handle, gameState + OFF_CUSTOMGS_GAMEMODE_ROWNAME);
        data.BotCountT1 = ReadInt(handle, gameState + 0x390);
        data.BotCountT2 = ReadInt(handle, gameState + 0x394);

        if (MAP_FNAMES.TryGetValue(arenaFName, out string mapName))
            data.MapName = mapName;
        else if (arenaFName != 0)
            data.MapName = nestFNameIndex != 0 && arenaFName == nestFNameIndex
                ? "Enkidu Nest" : $"Unknown (0x{arenaFName:X})";

        if (MODE_FNAMES.TryGetValue(modeFName, out string modeName))
            data.ModeName = modeName;
        else if (modeFName != 0)
            data.ModeName = $"Unknown (0x{modeFName:X})";

        return data;
    }

    class KillFeedEntry
    {
        public DateTime Time;
        public string KillerName = "";
        public string VictimName = "";
        public string Cause = "";   // Weapon name OR Ability name (whichever was the killing blow)
        public bool IsAbilityKill;
        public int KillerTeam;
    }

    // Previous-tick snapshot of each player's K/D so we can detect changes
    static Dictionary<long, (int kills, int deaths)> prevPlayerStats = new Dictionary<long, (int, int)>();
    static List<KillFeedEntry> killFeed = new List<KillFeedEntry>();
    const int KILL_FEED_MAX = 10;
    static readonly string[] GAME_STATES = {
        "MainMenu",         // 0
        "Loading",          // 1
        "Loading2",         // 2 - loading before loadout
        "Loading3",         // 3 - loading before loadout
        "LoadoutRead",      // 4
        "PreMatchStart",    // 5
        "MatchStart",       // 6
        "MapLoaded",        // 7
        "Countdown",        // 8
        "Playing",          // 9
        "EndGame1",         // 10
        "EndGame2",         // 11
        "Podium",           // 12
        "Leaderboard",      // 13
        "State14",          // 14
        "State15"           // 15
    };

    // ============================================================
    // PLAYER DATA STRUCT
    // ============================================================

    class PlayerInfo
    {
        public long StatePtr;
        public long HitHistoryPtr;
        public int LocalID;
        public bool IsLocal;
        public string Name = "";
        public int Team;
        public bool IsBot;
        public int BotLevel;
        public bool IsHost;
        public int Kills;
        public int Deaths;
        public int Assists;
        public int Score;
        public int Shots;
        public int SuccessfulShots;
        public double Damage;
        public double Heal;
        public int ShieldPickups;
        public int Dashes;
        public int Jumps;
        public int AbilitiesUsed;
        public bool IsWinner;
        public double KDAScore;
        public double TimeAlive;
        public int DashMode;
        public double ImpulseReceived;
        public double GravityDuration;
        public byte Weapon;
        public byte Ability;
        public byte Module;

        public string WeaponName => WEAPONS[Math.Min(Weapon, (byte)(WEAPONS.Length - 1))];
        public string AbilityName => ABILITIES[Math.Min(Ability, (byte)(ABILITIES.Length - 1))];
        public string ModuleName => MODULES[Math.Min(Module, (byte)(MODULES.Length - 1))];
        public float Accuracy => Shots > 0 ? Math.Min(100f, (float)SuccessfulShots / Shots * 100f) : 0f;
        public float KDRatio => Deaths > 0 ? (float)Kills / Deaths : Kills;
    }

    // ============================================================
    // MAIN
    // ============================================================

    static void Main()
    {
        Console.Title = "BCA Tracker — Alpha V0.11";
        Console.CursorVisible = false;

        while (true)
        {
            try { Run(); }
            catch (Exception ex)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Error: {ex.Message}");
                Console.WriteLine($"  {ex.StackTrace}");
                Console.ResetColor();
                Thread.Sleep(3000);
            }
        }
    }

    static void Run()
    {
        while (true)
        {
            Process[] procs = Process.GetProcessesByName("BattleCoreArena");
            if (procs.Length == 0)
            {
                Console.Clear();
                Console.WriteLine("  Waiting for BattleCoreArena.exe ...");
                Thread.Sleep(2000);
                continue;
            }

            Process process = procs[0];
            IntPtr handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, process.Id);
            if (handle == IntPtr.Zero)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  OpenProcess failed. Run as Administrator.");
                Console.ResetColor();
                Thread.Sleep(3000);
                continue;
            }

            long moduleBase = GetModuleBaseEx(handle, "BattleCoreArena.exe");
            if (moduleBase == 0)
            {
                CloseHandle(handle);
                Thread.Sleep(2000);
                continue;
            }

            while (true)
            {
                if (Process.GetProcessesByName("BattleCoreArena").Length == 0)
                {
                    CloseHandle(handle);
                    break;
                }

                // Resolve GameState object
                long gameState = ResolveGameState(handle, moduleBase);

                // Resolve our own player state pointer
                long myStatePtr = ResolveLocalPlayerStatePtr(handle, moduleBase);

                // Read player array
                var players = gameState != 0 ? ReadAllPlayers(handle, gameState) : new List<PlayerInfo>();

                // Mark which player is the local one
                foreach (var p in players)
                    p.IsLocal = (p.StatePtr == myStatePtr);

                // Detect whether GameState is lobby (CustomGameGS) or in-match (ArenaTeamGS)
                byte gameStateEnum = 0;
                byte gameModeEnum = 0;
                bool inMatch = false;
                bool isPostMatch = false;
                bool isLobby = false;
                double gameTime = 0;
                LobbyData lobbyData = null;

                if (gameState != 0)
                {
                    byte rawState = ReadByte(handle, gameState + OFF_ARENAGS_CURRENT_GAMESTATE);
                    byte rawMode = ReadByte(handle, gameState + OFF_ARENAGS_CURRENT_GAMEMODE);

                    if (rawState <= 15 && players.Count > 0)
                    {
                        gameStateEnum = rawState;
                        gameModeEnum = rawMode;
                        gameTime = ReadDouble(handle, gameState + OFF_ARENAGS_GAMETIME);

                        if (rawState == 9) inMatch = true;                        // Playing
                        else if (rawState >= 4 && rawState <= 8) inMatch = true;  // Loading → Countdown also show live data
                        else if (rawState >= 10 && rawState <= 13) isPostMatch = true; // EndGame → Leaderboard
                    }
                    else
                    {
                        // CustomGameGS — lobby
                        isLobby = true;
                        lobbyData = ReadLobbyData(handle, gameState);
                    }
                }

                // Kill feed only during active play
                if (inMatch && gameStateEnum == 9) DetectKills(handle, players, myStatePtr);

                // Read match lives
                int myLives = ReadChain4(handle, moduleBase, BASE_MY_LIVES, OFF_MY_LIVES);
                int enemyLives = ReadChain4(handle, moduleBase, BASE_ENEMY_LIVES, OFF_ENEMY_LIVES);

                Draw(players, myLives, enemyLives, gameStateEnum, gameModeEnum,
                     inMatch, isPostMatch, isLobby, lobbyData, gameTime);

                Thread.Sleep(500);
            }
        }
    }

    // ============================================================
    // MEMORY HELPERS
    // ============================================================

    static long ResolveGameState(IntPtr handle, long moduleBase)
    {
        long gworld = ReadLong(handle, moduleBase + GWORLD_OFFSET);
        if (gworld == 0) return 0;
        long gs = ReadLong(handle, gworld + OFF_WORLD_TO_GAMESTATE);
        return gs;
    }

    // Resolves the LOCAL player's PlayerState pointer via the local PlayerController.
    // This is reliable across game modes (TestMap, Backup, etc.) where LocalID is unreliable.
    static long ResolveLocalPlayerStatePtr(IntPtr handle, long moduleBase)
    {
        long pcSlot = ReadLong(handle, moduleBase + BASE_LOCAL_PC);
        if (pcSlot == 0) return 0;
        long pc = ReadLong(handle, pcSlot + 0x30);
        if (pc == 0) return 0;
        return ReadLong(handle, pc + 0x298);
    }

    static List<PlayerInfo> ReadAllPlayers(IntPtr handle, long gameState)
    {
        var list = new List<PlayerInfo>();

        long arrayDataPtr = ReadLong(handle, gameState + OFF_GAMESTATE_PLAYERARRAY_DATA);
        int count = ReadInt(handle, gameState + OFF_GAMESTATE_PLAYERARRAY_COUNT);

        if (arrayDataPtr == 0 || count <= 0 || count > 32) return list;

        for (int i = 0; i < count; i++)
        {
            long psPtr = ReadLong(handle, arrayDataPtr + i * 8);
            if (psPtr == 0) continue;

            var p = new PlayerInfo { StatePtr = psPtr };
            p.HitHistoryPtr = ReadLong(handle, psPtr + OFF_PS_HIT_HISTORY);
            p.LocalID = ReadInt(handle, psPtr + OFF_PS_LOCAL_ID);
            p.Name = ReadFString(handle, psPtr + OFF_PS_NAME, 64);
            p.Team = ReadInt(handle, psPtr + OFF_PS_TEAM);
            p.IsBot = ReadByte(handle, psPtr + OFF_PS_IS_BOT) != 0;
            p.BotLevel = ReadInt(handle, psPtr + OFF_PS_BOT_LEVEL);
            p.IsHost = ReadByte(handle, psPtr + OFF_PS_IS_HOST) != 0;
            p.Kills = ReadInt(handle, psPtr + OFF_PS_NB_KILLS);
            p.Deaths = ReadInt(handle, psPtr + OFF_PS_NB_DEATHS);
            p.Assists = ReadInt(handle, psPtr + OFF_PS_NB_ASSISTS);
            p.Score = ReadInt(handle, psPtr + OFF_PS_PERSONAL_SCORE);
            p.Shots = ReadInt(handle, psPtr + OFF_PS_NB_SHOTS);
            p.SuccessfulShots = ReadInt(handle, psPtr + OFF_PS_NB_SUCCESSFUL_SHOTS);
            p.Damage = ReadDouble(handle, psPtr + OFF_PS_DAMAGE);
            p.Heal = ReadDouble(handle, psPtr + OFF_PS_HEAL);
            p.ShieldPickups = ReadInt(handle, psPtr + OFF_PS_SHIELD_PICKUPS);
            p.Dashes = ReadInt(handle, psPtr + OFF_PS_DASHES);
            p.Jumps = ReadInt(handle, psPtr + OFF_PS_JUMPS);
            p.AbilitiesUsed = ReadInt(handle, psPtr + OFF_PS_ABILITIES_USED);
            p.IsWinner = ReadByte(handle, psPtr + OFF_PS_IS_WINNER) != 0;
            p.KDAScore = ReadDouble(handle, psPtr + OFF_PS_KDA_SCORE);
            p.TimeAlive = ReadDouble(handle, psPtr + OFF_PS_TIME_ALIVE);
            p.DashMode = ReadInt(handle, psPtr + OFF_PS_DASH_MODE);
            p.ImpulseReceived = ReadDouble(handle, psPtr + OFF_PS_IMPULSE_RECEIVED);
            p.GravityDuration = ReadDouble(handle, psPtr + OFF_PS_GRAVITY_DURATION);
            p.Weapon = ReadByte(handle, psPtr + OFF_PS_WEAPON);
            p.Ability = ReadByte(handle, psPtr + OFF_PS_ABILITY);
            p.Module = ReadByte(handle, psPtr + OFF_PS_MODULE);

            list.Add(p);
        }

        return list;
    }

    // Tracks HitHistory count we've already processed, per player
    static Dictionary<long, int> prevHitCount = new Dictionary<long, int>();

    // Maps InstigatorID (the negative-ID system used in FSHitInfo for bots) to a player state pointer.
    // Bots use a different ID space than LocalID; we learn the mapping by watching deaths:
    // when a player dies, the TargetID in their death record IS that player's instigator-ID.
    static Dictionary<int, long> instigatorIdToStatePtr = new Dictionary<int, long>();

    static PlayerInfo FindPlayerByInstigatorId(int id, List<PlayerInfo> players, long myStatePtr)
    {
        // The local player has a special case: when they kill someone, the InstigatorID
        // appears as 0 (or whatever LocalID is on the local machine, which can be -1).
        // We check by pointer match instead, which is always correct.
        if (myStatePtr != 0)
        {
            foreach (var p in players)
            {
                if (p.StatePtr == myStatePtr && (id == p.LocalID || id == 0))
                    return p;
            }
        }

        // Other humans: match by LocalID
        foreach (var p in players)
            if (!p.IsLocal && p.LocalID == id) return p;

        // Bots: use the learned mapping
        if (instigatorIdToStatePtr.TryGetValue(id, out long ptr))
        {
            foreach (var p in players)
                if (p.StatePtr == ptr) return p;
        }

        return null;
    }

    static void DetectKills(IntPtr handle, List<PlayerInfo> players, long myStatePtr)
    {
        // First pass: scan ALL hits in everyone's history to learn InstigatorID->player mappings.
        // Each FSHitInfo has TargetID (victim's instigator-id) at +0x4. So we learn mappings
        // from any hit, even non-killing ones.
        foreach (var p in players)
        {
            if (p.HitHistoryPtr == 0) continue;
            long historyData = ReadLong(handle, p.HitHistoryPtr + OFF_HH_HISTORY_DATA);
            int historyCount = ReadInt(handle, p.HitHistoryPtr + OFF_HH_HISTORY_COUNT);
            if (historyData == 0 || historyCount <= 0 || historyCount > 128) continue;

            // Read TargetID from any one hit — they all target the same victim (this player)
            int targetId = ReadInt(handle, historyData + OFF_HIT_TARGET_ID);
            if (targetId != 0)
                instigatorIdToStatePtr[targetId] = p.StatePtr;
        }

        foreach (var victim in players)
        {
            // Did victim die since last tick?
            bool victimDied = false;
            if (prevPlayerStats.TryGetValue(victim.StatePtr, out var pstats))
                victimDied = victim.Deaths > pstats.deaths;

            // Read HitHistory (may be empty for environment kills)
            long historyData = 0;
            int historyCount = 0;
            if (victim.HitHistoryPtr != 0)
            {
                historyData = ReadLong(handle, victim.HitHistoryPtr + OFF_HH_HISTORY_DATA);
                historyCount = ReadInt(handle, victim.HitHistoryPtr + OFF_HH_HISTORY_COUNT);
                if (historyCount < 0 || historyCount > 128) historyCount = 0;
            }

            prevHitCount.TryGetValue(victim.StatePtr, out int prev);
            // History count goes back down when a new life starts - reset prev so we detect new entries correctly
            if (historyCount < prev) prev = 0;

            bool hitHistoryGrew = historyCount > prev;

            if (victimDied)
            {
                // Case 1: victim died AND there's a hit in history (normal kill from weapon/ability/projectile)
                if (historyCount > 0 && historyData != 0)
                {
                    long hitAddr = historyData + (historyCount - 1) * SIZEOF_HITINFO;

                    int instigatorId = ReadInt(handle, hitAddr + OFF_HIT_INSTIGATOR_ID);
                    byte weapon = ReadByte(handle, hitAddr + OFF_HIT_WEAPON);
                    byte ability = ReadByte(handle, hitAddr + OFF_HIT_ABILITY);

                    PlayerInfo killer = FindPlayerByInstigatorId(instigatorId, players, myStatePtr);

                    string killerName;
                    int killerTeam;

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

                    string weaponLabel = weapon < WEAPONS.Length ? WEAPONS[weapon] : $"W?{weapon}";
                    string abilityLabel = ability < ABILITIES.Length ? ABILITIES[ability] : $"A?{ability}";
                    string cause = ability != 0 ? abilityLabel : weaponLabel;

                    // If both weapon and ability are 0, it's likely a knock-into-environment kill.
                    // Tag it as Environment but keep the killer name (the person who pushed/knocked them).
                    if (weapon == 0 && ability == 0)
                    {
                        cause = "Environment";
                    }

                    var entry = new KillFeedEntry
                    {
                        Time = DateTime.Now,
                        KillerName = killerName,
                        VictimName = victim.Name,
                        Cause = cause,
                        IsAbilityKill = ability != 0,
                        KillerTeam = killerTeam
                    };

                    killFeed.Add(entry);
                    if (killFeed.Count > KILL_FEED_MAX) killFeed.RemoveAt(0);
                }
                // Case 2: victim died but HitHistory is empty / had no growth = pure environment death (fall, kill grid, no attacker)
                else
                {
                    var entry = new KillFeedEntry
                    {
                        Time = DateTime.Now,
                        KillerName = victim.Name + " (suicide)",
                        VictimName = victim.Name,
                        Cause = "Environment",
                        IsAbilityKill = false,
                        KillerTeam = victim.Team
                    };

                    killFeed.Add(entry);
                    if (killFeed.Count > KILL_FEED_MAX) killFeed.RemoveAt(0);
                }
            }

            prevHitCount[victim.StatePtr] = historyCount;
        }

        foreach (var p in players)
            prevPlayerStats[p.StatePtr] = (p.Kills, p.Deaths);

        var currentPtrs = new HashSet<long>();
        foreach (var p in players) currentPtrs.Add(p.StatePtr);
        var toRemove = new List<long>();
        foreach (var kvp in prevPlayerStats)
            if (!currentPtrs.Contains(kvp.Key)) toRemove.Add(kvp.Key);
        foreach (var k in toRemove)
        {
            prevPlayerStats.Remove(k);
            prevHitCount.Remove(k);
        }
    }

    static long ReadLong(IntPtr handle, long addr)
    {
        byte[] buf = new byte[8];
        if (!ReadProcessMemory(handle, (IntPtr)addr, buf, 8, out _)) return 0;
        return BitConverter.ToInt64(buf, 0);
    }

    static int ReadInt(IntPtr handle, long addr)
    {
        byte[] buf = new byte[4];
        if (!ReadProcessMemory(handle, (IntPtr)addr, buf, 4, out _)) return 0;
        return BitConverter.ToInt32(buf, 0);
    }

    static byte ReadByte(IntPtr handle, long addr)
    {
        byte[] buf = new byte[1];
        if (!ReadProcessMemory(handle, (IntPtr)addr, buf, 1, out _)) return 0;
        return buf[0];
    }

    static double ReadDouble(IntPtr handle, long addr)
    {
        byte[] buf = new byte[8];
        if (!ReadProcessMemory(handle, (IntPtr)addr, buf, 8, out _)) return 0;
        return BitConverter.ToDouble(buf, 0);
    }

    /// <summary>
    /// Reads a UE5 FString. Layout: [data ptr: 8][count: 4][max: 4]
    /// Strings are UTF-16 with null terminator included in count.
    /// </summary>
    static string ReadFString(IntPtr handle, long addr, int maxChars)
    {
        long dataPtr = ReadLong(handle, addr);
        int count = ReadInt(handle, addr + 8);
        if (dataPtr == 0 || count <= 0) return "";
        if (count > maxChars) count = maxChars;

        byte[] buf = new byte[count * 2];
        if (!ReadProcessMemory(handle, (IntPtr)dataPtr, buf, buf.Length, out _)) return "";

        string s = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        return s;
    }

    static int ReadChain4(IntPtr handle, long moduleBase, long baseOffset, int[] offsets)
    {
        try
        {
            long addr = ReadLong(handle, moduleBase + baseOffset);
            if (addr == 0) return -1;

            for (int i = 0; i < offsets.Length - 1; i++)
            {
                addr = ReadLong(handle, addr + offsets[i]);
                if (addr == 0) return -1;
            }

            return ReadInt(handle, addr + offsets[offsets.Length - 1]);
        }
        catch { return -1; }
    }

    static long GetModuleBaseEx(IntPtr handle, string targetModule)
    {
        try
        {
            IntPtr[] modules = new IntPtr[1024];
            if (!EnumProcessModulesEx(handle, modules, modules.Length * IntPtr.Size, out int needed, LIST_MODULES_ALL))
                return 0;

            int count = needed / IntPtr.Size;
            for (int i = 0; i < count; i++)
            {
                char[] name = new char[256];
                GetModuleFileNameEx(handle, modules[i], name, name.Length);
                string moduleName = new string(name).TrimEnd('\0');

                if (moduleName.EndsWith(targetModule, StringComparison.OrdinalIgnoreCase))
                {
                    GetModuleInformation(handle, modules[i], out MODULEINFO info, (uint)Marshal.SizeOf(typeof(MODULEINFO)));
                    return (long)info.lpBaseOfDll;
                }
            }
        }
        catch { }
        return 0;
    }

    // ============================================================
    // RENDERING
    // ============================================================

    static void Draw(List<PlayerInfo> players, int myLives, int enemyLives,
                     byte gameStateEnum, byte gameModeEnum,
                     bool inMatch, bool isPostMatch, bool isLobby,
                     LobbyData lobbyData, double gameTime)
    {
        Console.Clear();

        // Header
        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.WriteLine("  ╔════════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("  ║                      BCA TRACKER — ALPHA V0.12                                 ║");
        Console.WriteLine("  ╚════════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();

        // ── LOBBY VIEW ─────────────────────────────────────────────────────────────────
        if (isLobby && lobbyData != null)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  CUSTOM GAME LOBBY");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  " + new string('─', 40));
            Console.ResetColor();
            Console.Write("  Map:  "); Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(lobbyData.MapName); Console.ResetColor();
            Console.Write("  Mode: "); Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(lobbyData.ModeName); Console.ResetColor();
            Console.Write("  Bots: "); Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"Team 1: {lobbyData.BotCountT1}   Team 2: {lobbyData.BotCountT2}");
            Console.ResetColor();
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Updated: {DateTime.Now:HH:mm:ss}");
            Console.ResetColor();
            return;
        }

        // ── WAITING / MAIN MENU ────────────────────────────────────────────────────────
        if (!inMatch && !isPostMatch && !isLobby)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Waiting for match...");
            Console.WriteLine($"  Updated: {DateTime.Now:HH:mm:ss}");
            Console.ResetColor();
            return;
        }

        // ── POST-MATCH SUMMARY (states 10-13) ─────────────────────────────────────────
        if (isPostMatch)
        {
            string stateName = gameStateEnum < GAME_STATES.Length ? GAME_STATES[gameStateEnum] : "?";
            string modeName = gameModeEnum < GAMEMODES.Length ? GAMEMODES[gameModeEnum] : "?";

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  MATCH SUMMARY — {modeName}   [{stateName}]");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  " + new string('─', 110));
            Console.ResetColor();

            // Sort all players by kills desc
            var sorted = new List<PlayerInfo>(players);
            sorted.Sort((a, b) => b.Kills != a.Kills ? b.Kills.CompareTo(a.Kills) : a.Deaths.CompareTo(b.Deaths));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  " + "Name".PadRight(20) + "Team".PadRight(6) + "K/D/A".PadRight(12) +
                              "Acc".PadRight(7) + "DMG".PadRight(8) + "HEAL".PadRight(8) + "Dashes".PadRight(8) +
                              "AbilUses".PadRight(10) + "ShldPick".PadRight(10) +
                              "Alive".PadRight(10) + "Result");
            Console.WriteLine("  " + new string('─', 107));
            Console.ResetColor();

            foreach (var p in sorted)
            {
                string nameTag = p.IsBot ? $"[BOT]{p.Name}" : p.Name;
                if (p.IsLocal) nameTag = "*" + nameTag;
                if (nameTag.Length > 19) nameTag = nameTag.Substring(0, 19);

                string kda = $"{p.Kills}/{p.Deaths}/{p.Assists}";
                string acc = $"{p.Accuracy:F0}%";
                string dmg = p.Damage > 0 ? p.Damage.ToString("F0") : "-";
                string heal = p.Heal > 0 ? p.Heal.ToString("F0") : "-";
                string alive = p.TimeAlive > 0 ? $"{(int)p.TimeAlive / 60}m{(int)p.TimeAlive % 60}s" : "-";
                string result = p.IsWinner ? "WIN" : "LOSS";

                Console.ForegroundColor = p.IsBot ? ConsoleColor.DarkGray
                                        : p.IsWinner ? ConsoleColor.Green : ConsoleColor.White;
                Console.Write("  " + nameTag.PadRight(20));
                Console.ResetColor();
                Console.ForegroundColor = p.Team == 0 ? ConsoleColor.Cyan : ConsoleColor.Red;
                Console.Write($"T{p.Team}".PadRight(6));
                Console.ResetColor();
                Console.Write(kda.PadRight(12));
                Console.Write(acc.PadRight(7));
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write(dmg.PadRight(8));
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(heal.PadRight(8));
                Console.ResetColor();
                Console.Write($"{p.Dashes}".PadRight(8));
                Console.Write($"{p.AbilitiesUsed}".PadRight(10));
                Console.Write($"{p.ShieldPickups}".PadRight(10));
                Console.Write(alive.PadRight(10));
                Console.ForegroundColor = p.IsWinner ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write(result);
                Console.ResetColor();
                Console.WriteLine();
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Updated: {DateTime.Now:HH:mm:ss}");
            Console.ResetColor();
            return;
        }

        // ── IN-MATCH VIEW ──────────────────────────────────────────────────────────────
        {
            string stateName = gameStateEnum < GAME_STATES.Length ? GAME_STATES[gameStateEnum] : "Transition";
            string modeName = gameModeEnum < GAMEMODES.Length ? GAMEMODES[gameModeEnum] : "?";
            string timer = gameTime > 0 ? $"{(int)gameTime / 60}:{(int)gameTime % 60:D2}" : "--:--";

            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  {modeName}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  [{stateName}]");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  ⏱ {timer}");
            Console.ResetColor();

            // Lives
            string myLivesStr = myLives == -1 ? "???" : myLives.ToString();
            string enemyLivesStr = enemyLives == -1 ? "???" : enemyLives.ToString();
            Console.Write("  My Team Lives: ");
            Console.ForegroundColor = ConsoleColor.Cyan; Console.Write(myLivesStr.PadRight(8)); Console.ResetColor();
            Console.Write("Enemy Lives: ");
            Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(enemyLivesStr); Console.ResetColor();
            Console.WriteLine();

            if (players.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  No players detected.");
                Console.ResetColor();
            }
            else
            {
                var teams = new Dictionary<int, List<PlayerInfo>>();
                foreach (var p in players)
                {
                    if (!teams.ContainsKey(p.Team)) teams[p.Team] = new List<PlayerInfo>();
                    teams[p.Team].Add(p);
                }
                var teamIds = new List<int>(teams.Keys);
                teamIds.Sort();

                foreach (var teamId in teamIds)
                {
                    Console.ForegroundColor = teamId == 0 ? ConsoleColor.Cyan : ConsoleColor.Red;
                    Console.WriteLine($"  TEAM {teamId}");
                    Console.ResetColor();

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("  " + "Name".PadRight(20) + "K/D/A".PadRight(12) + "KD".PadRight(7) +
                                      "Acc".PadRight(7) + "Weapon".PadRight(12) + "Ability".PadRight(18) +
                                      "Module".PadRight(14) + "DMG".PadRight(8) + "HEAL");
                    Console.WriteLine("  " + new string('─', 103));
                    Console.ResetColor();

                    foreach (var p in teams[teamId])
                    {
                        string nameTag = p.IsBot ? $"[BOT{p.BotLevel}]{p.Name}" : p.Name;
                        if (p.IsLocal) nameTag = "*" + nameTag;
                        if (p.IsHost && !p.IsLocal) nameTag = "H:" + nameTag;
                        if (nameTag.Length > 19) nameTag = nameTag.Substring(0, 19);

                        string kda = $"{p.Kills}/{p.Deaths}/{p.Assists}";
                        string kd = p.KDRatio.ToString("F2");
                        string acc = $"{p.Accuracy:F0}%";
                        string dmg = p.Damage > 0 ? p.Damage.ToString("F0") : "-";
                        string heal = p.Heal > 0 ? p.Heal.ToString("F0") : "-";

                        // Row 1: main stats
                        Console.ForegroundColor = p.IsBot ? ConsoleColor.DarkGray : ConsoleColor.White;
                        Console.Write("  " + nameTag.PadRight(20)); Console.ResetColor();
                        Console.Write(kda.PadRight(12));
                        Console.ForegroundColor = p.KDRatio >= 1 ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.Write(kd.PadRight(7)); Console.ResetColor();
                        Console.Write(acc.PadRight(7));
                        Console.ForegroundColor = ConsoleColor.Yellow; Console.Write(p.WeaponName.PadRight(12)); Console.ResetColor();
                        Console.ForegroundColor = ConsoleColor.Magenta; Console.Write(p.AbilityName.PadRight(18)); Console.ResetColor();
                        Console.ForegroundColor = ConsoleColor.Cyan; Console.Write(p.ModuleName.PadRight(14)); Console.ResetColor();
                        Console.ForegroundColor = ConsoleColor.Red; Console.Write(dmg.PadRight(8)); Console.ResetColor();
                        Console.ForegroundColor = ConsoleColor.Green; Console.Write(heal); Console.ResetColor();
                        Console.WriteLine();

                        // Row 2: extended stats
                        string timeAlive = p.TimeAlive > 0 ? $"{(int)p.TimeAlive / 60}m{(int)p.TimeAlive % 60}s" : "-";
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine(
                            $"  {"".PadRight(20)}" +
                            $"Dashes:{p.Dashes,-5} " +
                            $"Jumps:{p.Jumps,-6} " +
                            $"AbilUses:{p.AbilitiesUsed,-5} " +
                            $"ShldPick:{p.ShieldPickups,-5} " +
                            $"Impulse:{p.ImpulseReceived.ToString("F0"),-8} " +
                            $"GravCtrl:{p.GravityDuration.ToString("F0"),0}s " +
                            $"Alive:{timeAlive}"
                        );
                        Console.ResetColor();
                    }
                    Console.WriteLine();
                }
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Updated: {DateTime.Now:HH:mm:ss}    Players: {players.Count}");
            Console.ResetColor();

            // Kill feed
            if (killFeed.Count > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("  KILL FEED");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  " + new string('─', 60));
                Console.ResetColor();

                for (int i = killFeed.Count - 1; i >= 0; i--)
                {
                    var k = killFeed[i];
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($"  [{k.Time:HH:mm:ss}] ");
                    Console.ForegroundColor = k.KillerTeam == 0 ? ConsoleColor.Cyan
                                             : k.KillerTeam == 1 ? ConsoleColor.Red
                                             : ConsoleColor.DarkGray;
                    Console.Write(k.KillerName);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("  [");
                    Console.ForegroundColor = k.IsAbilityKill ? ConsoleColor.Magenta : ConsoleColor.Yellow;
                    Console.Write(k.Cause);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("]  ");
                    Console.ForegroundColor = k.KillerTeam == 0 ? ConsoleColor.Red
                                             : k.KillerTeam == 1 ? ConsoleColor.Cyan
                                             : ConsoleColor.White;
                    Console.WriteLine(k.VictimName);
                    Console.ResetColor();
                }
            }
        }
    }
}