using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BCATracker
{
    public class MemoryReader
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr addr, byte[] buf, int size, out int read);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EnumProcessModulesEx(IntPtr h, [Out] IntPtr[] mods, int cb, out int needed, uint filter);
        [DllImport("psapi.dll")]
        static extern uint GetModuleFileNameEx(IntPtr h, IntPtr mod, [Out] char[] name, int size);
        [DllImport("psapi.dll")]
        static extern bool GetModuleInformation(IntPtr h, IntPtr mod, out MODULEINFO info, uint cb);

        [StructLayout(LayoutKind.Sequential)]
        struct MODULEINFO { public IntPtr Base; public uint Size; public IntPtr Entry; }

        const int PROCESS_VM_READ  = 0x0010;
        const int PROCESS_QUERY_INFO = 0x0400;
        const uint LIST_MODULES_ALL = 0x03;

        IntPtr _handle  = IntPtr.Zero;
        long   _modBase = 0;

        public long ModuleBase => _modBase;
        public bool IsAttached => _handle != IntPtr.Zero && _modBase != 0;

        public bool TryAttach(int pid)
        {
            Detach();
            _handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFO, false, pid);
            if (_handle == IntPtr.Zero) return false;
            _modBase = GetModuleBase("BattleCoreArena.exe");
            if (_modBase == 0) { Detach(); return false; }
            return true;
        }

        public void Detach()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
            _modBase = 0;
        }

        // ── Main snapshot ────────────────────────────────────────────────────────

        public MatchSnapshot ReadSnapshot(KillFeedTracker killFeed,
                                          System.Diagnostics.Stopwatch matchTimer,
                                          ref byte lastState,
                                          FNameResolver nameResolver)
        {
            if (!IsAttached) return null;

            nameResolver?.EnsureInitialized();

            var snap = new MatchSnapshot();

            long gworld    = ReadLong(_modBase + Offsets.GWorld);
            long gameState = gworld != 0 ? ReadLong(gworld + Offsets.World_GameState) : 0;
            long myStatePtr = ResolveLocalPlayerStatePtr();

            // ── Classify GameState ──────────────────────────────────────────────
            byte rawState   = gameState != 0 ? ReadByte(gameState + Offsets.AGS_GameState) : (byte)0xFF;
            byte rawMode    = gameState != 0 ? ReadByte(gameState + Offsets.AGS_GameMode)  : (byte)0;
            int  playerCount = gameState != 0 ? ReadInt(gameState + Offsets.GS_PlayerArray_Count) : 0;

            bool isArenaGS = rawState <= 13 && playerCount > 0 && playerCount <= 32;

            int  arenaFNameIdx = (!isArenaGS && gameState != 0)
                                     ? ReadInt(gameState + Offsets.CGS_ArenaRowName) : 0;
            bool isLobbyGS = arenaFNameIdx != 0;
            bool isMenuGS  = !isArenaGS && !isLobbyGS && gameState != 0;

            snap.WorldName = "";

            // ── Lobby ────────────────────────────────────────────────────────────
            if (isLobbyGS)
            {
                snap.IsLobby  = true;
                snap.Lobby    = ReadLobbyData(gameState, nameResolver);
                snap.UpdatedAt = DateTime.Now;
                snap.KillFeed  = new List<KillFeedEntry>(killFeed.Entries);
                return snap;
            }

            // ── Main menu ────────────────────────────────────────────────────────
            if (isMenuGS)
            {
                snap.IsMainMenu = true;
                long localPC = ResolveLocalPC();
                if (localPC != 0)
                {
                    long hud = ReadLong(localPC + Offsets.PC_MyHUD);
                    if (hud != 0)
                        snap.MainMenuState = ReadByte(hud + Offsets.MMHUD_MainMenuState);
                }
                snap.UpdatedAt = DateTime.Now;
                snap.KillFeed  = new List<KillFeedEntry>(killFeed.Entries);
                return snap;
            }

            // ── Arena world ──────────────────────────────────────────────────────
            snap.Players = gameState != 0 ? ReadAllPlayers(gameState, rawMode) : new List<PlayerInfo>();
            foreach (var p in snap.Players)
                p.IsLocal = (p.StatePtr == myStatePtr);

            if (isArenaGS)
            {
                snap.GameStateEnum = rawState;
                snap.GameModeEnum  = rawMode;
                snap.InMatch       = rawState >= 4 && rawState <= 9;
                snap.IsPostMatch   = rawState >= 10 && rawState <= 13;

                // Map name: AGS_CurrentMap is not replicated to the client — will
                // almost always be 0. We still try to resolve it; MatchSaver has the
                // lobby cache as the reliable fallback.
                int mapFNameIdx = ReadInt(gameState + Offsets.AGS_CurrentMap);
                snap.CurrentMap = ResolveMapName(mapFNameIdx, nameResolver);

                // Q-Ball: read ball-holder team from GoldenCoreGS
                // GameMode 5 = GoldenCore3v3 (Q-Ball)
                if (rawMode == 5)
                    snap.QBallHolderTeam = ReadInt(gameState + Offsets.GCGS_GoldenCoreTeam);
            }
            else
            {
                snap.IsWaiting = true;
            }

            // Match timer
            if (snap.GameStateEnum != lastState)
            {
                if (snap.GameStateEnum == 9)  matchTimer.Restart();
                else if (snap.GameStateEnum >= 10) matchTimer.Stop();
                lastState = snap.GameStateEnum;
            }
            snap.MatchTime = matchTimer.Elapsed.TotalSeconds;

            if (snap.InMatch && snap.GameStateEnum == 9)
                killFeed.Update(_handle, snap.Players, myStatePtr, snap.MatchTime);
            snap.KillFeed = new List<KillFeedEntry>(killFeed.Entries);

            snap.MyLives    = ReadChain4(_modBase, Offsets.BASE_MY_LIVES,    Offsets.OFF_MY_LIVES);
            snap.EnemyLives = ReadChain4(_modBase, Offsets.BASE_ENEMY_LIVES, Offsets.OFF_ENEMY_LIVES);

            snap.UpdatedAt = DateTime.Now;
            return snap;
        }

        // ── FName resolution ─────────────────────────────────────────────────────

        string ResolveMapName(int fnameIndex, FNameResolver resolver)
        {
            if (fnameIndex == 0) return "Unknown";

            string raw = resolver?.Resolve(fnameIndex);
            if (raw != null)
            {
                string leaf = raw.Contains('/') ? raw[(raw.LastIndexOf('/') + 1)..] : raw;
                if (leaf.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
                    leaf = leaf[..^2];
                string display = BCAEnums.MapRowNameToDisplayName(leaf);
                resolver.LogResolution("Map", fnameIndex, leaf, display);
                return display ?? $"Unknown ({leaf})";
            }

            return BCAEnums.LobbyMapName(fnameIndex);
        }

        string ResolveModeName(int fnameIndex, FNameResolver resolver)
        {
            if (fnameIndex == 0) return "Unknown";

            string raw = resolver?.Resolve(fnameIndex);
            if (raw != null)
            {
                string display = BCAEnums.ModeRowNameToDisplayName(raw);
                resolver.LogResolution("Mode", fnameIndex, raw, display);
                return display ?? $"Unknown ({raw})";
            }

            return BCAEnums.LobbyModeName(fnameIndex);
        }

        // ── Pointer helpers ──────────────────────────────────────────────────────

        long ResolveLocalPC()
        {
            long slot = ReadLong(_modBase + Offsets.LocalPCSlot);
            if (slot == 0) return 0;
            return ReadLong(slot + 0x30);
        }

        long ResolveLocalPlayerStatePtr()
        {
            long pc = ResolveLocalPC();
            if (pc == 0) return 0;
            return ReadLong(pc + Offsets.PC_PlayerState);
        }

        // ── Player reading ───────────────────────────────────────────────────────

        List<PlayerInfo> ReadAllPlayers(long gameState, byte gameMode)
        {
            var list = new List<PlayerInfo>();

            long dataPtr = ReadLong(gameState + Offsets.GS_PlayerArray_Data);
            int  count   = ReadInt(gameState + Offsets.GS_PlayerArray_Count);

            if (dataPtr == 0 || count <= 0 || count > 32) return list;

            for (int i = 0; i < count; i++)
            {
                long ps = ReadLong(dataPtr + i * 8);
                if (ps == 0) continue;
                list.Add(ReadPlayerInfo(ps, gameMode));
            }

            return list;
        }

        PlayerInfo ReadPlayerInfo(long ps, byte gameMode)
        {
            var p = new PlayerInfo();
            p.StatePtr       = ps;
            p.HitHistoryPtr  = ReadLong(ps + Offsets.PS_HitHistory);
            p.LocalID        = ReadInt(ps + Offsets.PS_LocalID);
            p.Name           = ReadFString(ps + Offsets.PS_Name, 64);
            p.Team           = ReadInt(ps + Offsets.PS_Team);
            p.IsBot          = ReadByte(ps + Offsets.PS_IsBot) != 0;
            p.BotLevel       = ReadInt(ps + Offsets.PS_BotLevel);
            p.IsHost         = ReadByte(ps + Offsets.PS_IsHost) != 0;
            p.IsWinner       = ReadByte(ps + Offsets.PS_IsWinner) != 0;

            // Combat
            p.Kills              = ReadInt(ps + Offsets.PS_NbKills);
            p.Deaths             = ReadInt(ps + Offsets.PS_NbDeaths);
            p.Assists            = ReadInt(ps + Offsets.PS_NbAssists);
            p.Score              = ReadInt(ps + Offsets.PS_PersonalScore);
            p.Shots              = ReadInt(ps + Offsets.PS_NbShots);
            p.SuccessfulShots    = ReadInt(ps + Offsets.PS_NbSuccessfulShots);
            p.NbWeaponUsed       = ReadInt(ps + Offsets.PS_NbWeaponUsed);
            p.NbWeaponHit        = ReadInt(ps + Offsets.PS_NbWeaponHit);
            p.NbAbilitiesHit     = ReadInt(ps + Offsets.PS_NbAbilitiesHit);
            p.NbHitsCaused       = ReadInt(ps + Offsets.PS_NbHitsCaused);
            p.NbHitsReceived     = ReadInt(ps + Offsets.PS_NbHitsReceived);
            p.Damage             = ReadDouble(ps + Offsets.PS_Damage);
            p.Heal               = ReadDouble(ps + Offsets.PS_Heal);

            // Shield
            p.ReceivedShieldDmg          = ReadDouble(ps + Offsets.PS_ReceivedShieldDmg);
            p.ReceivedEffectiveShieldDmg = ReadDouble(ps + Offsets.PS_ReceivedEffectiveShieldDmg);
            p.WeaponShieldDmgDealt       = ReadDouble(ps + Offsets.PS_WeaponShieldDmgDealt);
            p.AbilityShieldDmgDealt      = ReadDouble(ps + Offsets.PS_AbilityShieldDmgDealt);

            // Impulse
            p.ImpulseReceived      = ReadDouble(ps + Offsets.PS_ImpulseReceived);
            p.WeaponImpulseDealt   = ReadDouble(ps + Offsets.PS_WeaponImpulseDealt);
            p.AbilityImpulseDealt  = ReadDouble(ps + Offsets.PS_AbilityImpulseDealt);

            // Movement
            p.ShieldPickups = ReadInt(ps + Offsets.PS_ShieldPickups);
            p.Empowerments  = ReadInt(ps + Offsets.PS_NbEmpowermentSoFar);
            p.Dashes        = ReadInt(ps + Offsets.PS_Dashes);
            p.Jumps         = ReadInt(ps + Offsets.PS_Jumps);
            p.AbilitiesUsed = ReadInt(ps + Offsets.PS_AbilitiesUsed);
            p.TimeAlive     = ReadDouble(ps + Offsets.PS_TimeAlive);
            p.DashMode      = ReadInt(ps + Offsets.PS_DashMode);

            // Gravity
            p.GravityDuration = ReadDouble(ps + Offsets.PS_GravityDuration);
            p.GravityOnGround = ReadDouble(ps + Offsets.PS_GravityOnGround);
            p.GravityInAir    = ReadDouble(ps + Offsets.PS_GravityInAir);
            p.GravityUseCount = ReadInt(ps + Offsets.PS_GravityUseCount);

            // Pings
            p.NbTotalPings = ReadInt(ps + Offsets.PS_NbTotalPings);
            p.NbEnemyPings = ReadInt(ps + Offsets.PS_NbEnemyPings);

            // Loadout
            p.Weapon  = ReadByte(ps + Offsets.PS_Weapon);
            p.Ability = ReadByte(ps + Offsets.PS_Ability);
            p.Module  = ReadByte(ps + Offsets.PS_Module);

            // Mode-specific extensions
            // FFA (BackupFFA = gameMode 4): BackupFFAPS_C adds NbBackUp + DeathRanking
            if (gameMode == 4)
            {
                p.FfaNbBackUp     = ReadInt(ps + Offsets.FFAPS_NbBackUp);
                p.FfaDeathRanking = ReadInt(ps + Offsets.FFAPS_DeathRanking);
            }

            return p;
        }

        LobbyData ReadLobbyData(long gs, FNameResolver resolver)
        {
            var d = new LobbyData();
            int arenaIdx = ReadInt(gs + Offsets.CGS_ArenaRowName);
            int modeIdx  = ReadInt(gs + Offsets.CGS_GameModeRowName);
            d.MapName   = ResolveMapName(arenaIdx, resolver);
            d.ModeName  = ResolveModeName(modeIdx, resolver);
            d.BotCountT1 = ReadInt(gs + Offsets.CGS_BotCountT1);
            d.BotCountT2 = ReadInt(gs + Offsets.CGS_BotCountT2);
            return d;
        }

        // ── Low-level primitives ─────────────────────────────────────────────────

        public long ReadLong(long addr)
        {
            var buf = new byte[8];
            ReadProcessMemory(_handle, (IntPtr)addr, buf, 8, out _);
            return BitConverter.ToInt64(buf, 0);
        }

        public int ReadInt(long addr)
        {
            var buf = new byte[4];
            ReadProcessMemory(_handle, (IntPtr)addr, buf, 4, out _);
            return BitConverter.ToInt32(buf, 0);
        }

        public byte ReadByte(long addr)
        {
            var buf = new byte[1];
            ReadProcessMemory(_handle, (IntPtr)addr, buf, 1, out _);
            return buf[0];
        }

        public double ReadDouble(long addr)
        {
            var buf = new byte[8];
            ReadProcessMemory(_handle, (IntPtr)addr, buf, 8, out _);
            return BitConverter.ToDouble(buf, 0);
        }

        public bool ReadRaw(long addr, byte[] buf, int size)
            => ReadProcessMemory(_handle, (IntPtr)addr, buf, size, out _);

        public bool ReadProcessMemoryRaw(long addr, byte[] buf, int size)
            => ReadProcessMemory(_handle, (IntPtr)addr, buf, size, out _);

        public string ReadFString(long addr, int maxChars)
        {
            long dataPtr = ReadLong(addr);
            int  count   = ReadInt(addr + 8);
            if (dataPtr == 0 || count <= 0) return "";
            if (count > maxChars) count = maxChars;
            var buf = new byte[count * 2];
            if (!ReadProcessMemory(_handle, (IntPtr)dataPtr, buf, buf.Length, out _)) return "";
            return Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }

        int ReadChain4(long moduleBase, long baseOffset, int[] offsets)
        {
            try
            {
                long addr = ReadLong(moduleBase + baseOffset);
                if (addr == 0) return -1;
                for (int i = 0; i < offsets.Length - 1; i++)
                {
                    addr = ReadLong(addr + offsets[i]);
                    if (addr == 0) return -1;
                }
                return ReadInt(addr + offsets[offsets.Length - 1]);
            }
            catch { return -1; }
        }

        long GetModuleBase(string targetName)
        {
            try
            {
                var mods = new IntPtr[1024];
                if (!EnumProcessModulesEx(_handle, mods, mods.Length * IntPtr.Size, out int needed, LIST_MODULES_ALL))
                    return 0;

                int count = needed / IntPtr.Size;
                for (int i = 0; i < count; i++)
                {
                    var name = new char[256];
                    GetModuleFileNameEx(_handle, mods[i], name, name.Length);
                    string modName = new string(name).TrimEnd('\0');
                    if (modName.EndsWith(targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        GetModuleInformation(_handle, mods[i], out MODULEINFO info, (uint)Marshal.SizeOf<MODULEINFO>());
                        return (long)info.Base;
                    }
                }
            }
            catch { }
            return 0;
        }
    }
}
