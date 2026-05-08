using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BCATracker.Core
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

        const int PROCESS_VM_READ = 0x0010;
        const int PROCESS_QUERY_INFO = 0x0400;
        const uint LIST_MODULES_ALL = 0x03;

        // Standard UE5 UObject layout
        // +0x00 VTable  +0x08 ObjectFlags  +0x0C InternalIndex
        // +0x10 ClassPrivate (UClass*)  +0x18 NamePrivate (FName)  +0x20 OuterPrivate
        const int UObj_ClassPrivate = 0x10;
        const int UObj_NamePrivate = 0x18;

        IntPtr _handle = IntPtr.Zero;
        long _modBase = 0;

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

        // ── Snapshot ──────────────────────────────────────────────────────────

        public MatchSnapshot ReadSnapshot(KillFeedTracker killFeed,
                                          System.Diagnostics.Stopwatch matchTimer,
                                          ref byte lastState,
                                          FNameResolver nameResolver)
        {
            if (!IsAttached) return null;
            nameResolver?.EnsureInitialized();

            var snap = new MatchSnapshot();

            long gworld = ReadLong(_modBase + Offsets.GWorld);
            long gameState = gworld != 0 ? ReadLong(gworld + Offsets.World_GameState) : 0;
            long myStatePtr = ResolveLocalPlayerStatePtr();

            DiagLog.Write($"[Mem] gworld=0x{gworld:X} gameState=0x{gameState:X} myPS=0x{myStatePtr:X}");

            if (gameState == 0)
            {
                snap.IsWaiting = true;
                snap.UpdatedAt = DateTime.Now;
                snap.KillFeed = new List<KillFeedEntry>(killFeed.Entries);
                return snap;
            }

            // ── GS class detection ───────────────────────────────────────────
            // We use a two-tier approach:
            //
            //  Tier 1 (preferred): resolve the GS UObject's UClass.NamePrivate
            //                      FName via FNameResolver. This gives us the
            //                      exact class name as a string, e.g.
            //                      "CustomGameGS_C", "ArenaTeamGS_C",
            //                      "BackupGS_C", "GoldenCoreGS_C", "BackupFFAGS_C",
            //                      "MainMenuGS_C", "BCATutorialGS_C".
            //                      This is unambiguous and robust against the
            //                      memory-layout collisions that bit the
            //                      structural-only approach (e.g. reading
            //                      offset 0x328 from a MainMenuGS_C, which is
            //                      only 0x308 bytes long, returns whatever
            //                      heap junk lives next door).
            //
            //  Tier 2 (fallback):  structural fingerprint, used only if class
            //                      resolution fails (e.g. FNameResolver hasn't
            //                      bootstrapped yet, or the UClass FName isn't
            //                      one we recognise).
            //
            // Both CustomGameGS_C and ArenaTeamGS_C derivatives extend
            // AGameState, so they share the engine PlayerArray at 0x2A8/0x2B0.
            //
            //  CustomGameGS_C (lobby):
            //   +0x328  ArenaRowName     FName  (>=0x100 once a map is picked)
            //   +0x338  GameModeRowName  FName  (>=0x100 once a mode is picked)
            //   total size: 0x430 bytes
            //
            //  ArenaTeamGS_C and derivatives (in-match):
            //   +0x368  HasToCountDownBeforeStart (bool, 0 or 1)
            //   +0x369  CurrentGameState (EGameState, 0..13)
            //   +0x3B0  CurrentGameMode  (EGameMode,  0..12)
            //   +0x328  NbPlayersPerTeam (int32, typically 1..3) — NOT FName-shaped
            //   total size: 0x708+ bytes
            //
            //  MainMenuGS_C: only 0x308 bytes — reads beyond that return heap junk.

            int playerCount   = ReadInt (gameState + Offsets.GS_PlayerArray_Count);
            int arenaFNameIdx = ReadInt (gameState + Offsets.CGS_ArenaRowName);
            int arenaFNameNum = ReadInt (gameState + Offsets.CGS_ArenaRowName + 4);
            int modeFNameIdx  = ReadInt (gameState + Offsets.CGS_GameModeRowName);
            int modeFNameNum  = ReadInt (gameState + Offsets.CGS_GameModeRowName + 4);

            byte gsStateByte  = ReadByte(gameState + Offsets.AGS_GameState);
            byte gsCountdown  = ReadByte(gameState + 0x368);
            byte gsModeByte   = ReadByte(gameState + Offsets.AGS_GameMode);

            // ── Tier 1: ask the UObject what class it is ────────────────────
            string gsClass = ReadUObjectClassName(gameState, nameResolver);
            bool isCustomGS = false;
            bool isArenaGS  = false;
            bool isMainMenuGS = false;

            if (gsClass != null)
            {
                if (gsClass == "CustomGameGS_C")
                    isCustomGS = true;
                else if (gsClass == "MainMenuGS_C")
                    isMainMenuGS = true;
                else if (IsArenaGSClassName(gsClass))
                    isArenaGS = true;
                // else: unknown class — leave all flags false, falls through to "menu"
            }
            else
            {
                // ── Tier 2: structural fingerprint fallback ──────────────────
                // Lobby fingerprint: two FName-shaped reads in a row.
                // ComparisonIndex >= 0x100 (engine-reserved range ends below this)
                // ComparisonIndex < 0x40_000_000 (sanity cap; real indices are << this)
                // Number < 0x10 (almost always 0; counter for duplicate names)
                static bool LooksLikeFName(int idx, int num)
                    => idx >= 0x100 && idx < 0x4000_0000 && (uint)num < 0x10;

                isCustomGS = LooksLikeFName(arenaFNameIdx, arenaFNameNum)
                          && LooksLikeFName(modeFNameIdx,  modeFNameNum);

                // Arena fingerprint: three correlated enum/bool reads + populated
                // PlayerArray, plus a "not all zero" guard.
                isArenaGS = !isCustomGS
                         && playerCount > 0
                         && playerCount <= 32
                         && gsStateByte <= 13
                         && gsCountdown <= 1
                         && gsModeByte  <= 12
                         && (gsStateByte != 0 || gsModeByte != 0 || playerCount > 1);
            }

            string classification = isCustomGS  ? "CustomGS"
                                  : isArenaGS   ? "ArenaGS"
                                  : isMainMenuGS ? "MainMenuGS"
                                  : "Unknown";
            string gsClassDesc = $"class={gsClass ?? "<unresolved>"} " +
                                 $"arenaFName=0x{arenaFNameIdx:X}.{arenaFNameNum} " +
                                 $"modeFName=0x{modeFNameIdx:X}.{modeFNameNum} " +
                                 $"players={playerCount} gsState={gsStateByte} " +
                                 $"countdown={gsCountdown} gsMode={gsModeByte}";
            DiagLog.GsClass($"{classification} [{gsClassDesc}]");

            snap.KillFeed = new List<KillFeedEntry>(killFeed.Entries);

            // ── Lobby ────────────────────────────────────────────────────────
            if (isCustomGS)
            {
                snap.IsLobby = true;
                snap.Lobby = ReadLobbyData(gameState, nameResolver);
                snap.UpdatedAt = DateTime.Now;
                return snap;
            }

            // ── Menu / loading ───────────────────────────────────────────────
            if (!isArenaGS)
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
                return snap;
            }

            // ── Arena match ──────────────────────────────────────────────────
            byte rawState = ReadByte(gameState + Offsets.AGS_GameState);
            byte rawMode = ReadByte(gameState + Offsets.AGS_GameMode);

            snap.GameStateEnum = rawState;
            snap.GameModeEnum = rawMode;
            // InMatch covers loadout selection through gameplay (states 3-9):
            //   3 LoadoutSelect, 4 LoadoutReady, 5 PreMatchStart, 6 MatchStart,
            //   7 MapLoaded, 8 Countdown, 9 Playing.
            snap.InMatch = rawState >= 3 && rawState <= 9;
            snap.IsPostMatch = rawState >= 10 && rawState <= 13;

            snap.Players = ReadAllPlayers(gameState, rawMode);
            foreach (var p in snap.Players)
                p.IsLocal = (p.StatePtr == myStatePtr);

            // AGS_CurrentMap is server-only, always 0 on the client.
            // MatchSaver._lobbyMapName is the reliable source.
            int mapFNameIdx = ReadInt(gameState + Offsets.AGS_CurrentMap);
            DiagLog.Write($"[Mem] AGS_CurrentMap FName idx=0x{mapFNameIdx:X}");
            snap.CurrentMap = mapFNameIdx != 0
                ? ResolveMapName(mapFNameIdx, nameResolver)
                : "Unknown";

            // Q-Ball (GoldenCore3v3) is identified by game mode byte, not class name
            if (rawMode == 5)
                snap.QBallHolderTeam = ReadInt(gameState + Offsets.GCGS_GoldenCoreTeam);

            if (rawState != lastState)
            {
                DiagLog.Write($"[Mem] GS state transition {lastState} → {rawState} ({snap.StateName})");
                if (rawState == 9) matchTimer.Restart();
                else if (rawState >= 10) matchTimer.Stop();
                lastState = rawState;
            }
            snap.MatchTime = matchTimer.Elapsed.TotalSeconds;

            // Run KillFeed update during Playing AND post-match. The
            // Counter override for the very last death in a match often
            // lands a tick or two AFTER the death itself — and on a tight
            // game ending, that tick is already in post-match (state >= 10).
            // If we stopped polling at state 9, the final suicide entry
            // would never get re-attributed and saved as such.
            if (snap.InMatch && rawState == 9)
                killFeed.Update(_handle, snap.Players, myStatePtr, snap.MatchTime);
            else if (snap.IsPostMatch)
                killFeed.Update(_handle, snap.Players, myStatePtr, snap.MatchTime);
            snap.KillFeed = new List<KillFeedEntry>(killFeed.Entries);

            snap.MyLives = ReadChain4(_modBase, Offsets.BASE_MY_LIVES, Offsets.OFF_MY_LIVES);
            snap.EnemyLives = ReadChain4(_modBase, Offsets.BASE_ENEMY_LIVES, Offsets.OFF_ENEMY_LIVES);

            snap.UpdatedAt = DateTime.Now;
            return snap;
        }

        // ── UObject class identification ──────────────────────────────────────

        // Cache the last (gsPtr, classFName, resolved name) so we don't spam
        // the diag log on every tick. The vast majority of ticks see no change.
        long _lastClassGsPtr   = 0;
        int  _lastClassFNameIdx = 0;
        string _lastClassName  = null;

        string ReadUObjectClassName(long objPtr, FNameResolver resolver)
        {
            if (objPtr == 0 || resolver == null) return null;

            long classPtr = ReadLong(objPtr + UObj_ClassPrivate);
            if (classPtr == 0) return null;

            int nameIndex = ReadInt(classPtr + UObj_NamePrivate);
            if (nameIndex == 0) return null;

            // Cache hit: same GS object, same class FName as last tick.
            if (objPtr == _lastClassGsPtr && nameIndex == _lastClassFNameIdx)
                return _lastClassName;

            string name = resolver.Resolve(nameIndex);

            _lastClassGsPtr    = objPtr;
            _lastClassFNameIdx = nameIndex;
            _lastClassName     = name;

            DiagLog.Write($"[Mem] GS UClass resolved: gsPtr=0x{objPtr:X} " +
                          $"classPtr=0x{classPtr:X} nameIdx=0x{nameIndex:X} → \"{name ?? "<null>"}\"");
            return name;
        }

        // Class names that subclass AArenaTeamGS_C and therefore use the same
        // memory layout for state/mode/player-array reads. Listed in the SDK
        // under BattleCoreArena_classes.hpp:
        //   ABackupGS_C : AArenaTeamGS_C
        //   ABackupFFAGS_C : ABackupGS_C
        //   AGoldenCoreGS_C : AArenaTeamGS_C
        //   ABCATutorialGS_C : AArenaTeamGS_C
        //   ATestMapGS_C : AArenaTeamGS_C
        static bool IsArenaGSClassName(string name)
            => name == "ArenaTeamGS_C"
            || name == "BackupGS_C"
            || name == "BackupFFAGS_C"
            || name == "GoldenCoreGS_C"
            || name == "BCATutorialGS_C"
            || name == "TestMapGS_C";

        // ── Name resolution ───────────────────────────────────────────────────

        string ResolveMapName(int fnameIndex, FNameResolver resolver)
        {
            if (fnameIndex == 0) return "Unknown";
            string raw = resolver?.Resolve(fnameIndex);
            if (raw == null)
            {
                DiagLog.FNameUnmatched("Map", fnameIndex, "<unresolved>");
                // Don't fall back to a fnameIndex lookup — those indexes
                // change per-session, so a static dictionary can't reliably
                // map them. Better to return an honest "unknown" so the UI
                // shows it as such; the next tick will probably resolve once
                // FNamePool is fully ready.
                return $"Unknown (0x{fnameIndex:X})";
            }

            string leaf = raw.Contains('/') ? raw[(raw.LastIndexOf('/') + 1)..] : raw;
            if (leaf.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
                leaf = leaf[..^2];

            string display = BCAEnums.MapRowNameToDisplayName(leaf);
            if (display != null) DiagLog.FNameResolved("Map", fnameIndex, leaf, display);
            else DiagLog.FNameUnmatched("Map", fnameIndex, leaf);
            resolver.LogResolution("Map", fnameIndex, leaf, display);
            return display ?? $"Unknown ({leaf})";
        }

        string ResolveModeName(int fnameIndex, FNameResolver resolver)
        {
            if (fnameIndex == 0) return "Unknown";
            string raw = resolver?.Resolve(fnameIndex);
            if (raw == null)
            {
                DiagLog.FNameUnmatched("Mode", fnameIndex, "<unresolved>");
                return BCAEnums.LobbyModeName(fnameIndex);
            }

            string display = BCAEnums.ModeRowNameToDisplayName(raw);
            if (display != null) DiagLog.FNameResolved("Mode", fnameIndex, raw, display);
            else DiagLog.FNameUnmatched("Mode", fnameIndex, raw);
            resolver.LogResolution("Mode", fnameIndex, raw, display);
            return display ?? $"Unknown ({raw})";
        }

        // ── Pointer helpers ───────────────────────────────────────────────────

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

        // ── Player reading ────────────────────────────────────────────────────

        List<PlayerInfo> ReadAllPlayers(long gameState, byte gameMode)
        {
            var list = new List<PlayerInfo>();
            long dataPtr = ReadLong(gameState + Offsets.GS_PlayerArray_Data);
            int count = ReadInt(gameState + Offsets.GS_PlayerArray_Count);

            DiagLog.Write($"[Mem] PlayerArray dataPtr=0x{dataPtr:X} count={count}");

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
            p.StatePtr = ps;
            p.HitHistoryPtr = ReadLong(ps + Offsets.PS_HitHistory);
            p.LocalID = ReadInt(ps + Offsets.PS_LocalID);
            p.Name = ReadFString(ps + Offsets.PS_Name, 64);
            p.Team = ReadInt(ps + Offsets.PS_Team);
            p.IsBot = ReadByte(ps + Offsets.PS_IsBot) != 0;
            p.BotLevel = ReadInt(ps + Offsets.PS_BotLevel);
            p.IsHost = ReadByte(ps + Offsets.PS_IsHost) != 0;
            p.IsWinner = ReadByte(ps + Offsets.PS_IsWinner) != 0;

            p.Kills = ReadInt(ps + Offsets.PS_NbKills);
            p.Deaths = ReadInt(ps + Offsets.PS_NbDeaths);
            p.Assists = ReadInt(ps + Offsets.PS_NbAssists);
            p.Score = ReadInt(ps + Offsets.PS_PersonalScore);
            p.Shots = ReadInt(ps + Offsets.PS_NbShots);
            p.SuccessfulShots = ReadInt(ps + Offsets.PS_NbSuccessfulShots);
            p.NbWeaponUsed = ReadInt(ps + Offsets.PS_NbWeaponUsed);
            p.NbWeaponHit = ReadInt(ps + Offsets.PS_NbWeaponHit);
            p.NbAbilitiesHit = ReadInt(ps + Offsets.PS_NbAbilitiesHit);
            p.NbHitsCaused = ReadInt(ps + Offsets.PS_NbHitsCaused);
            p.NbHitsReceived = ReadInt(ps + Offsets.PS_NbHitsReceived);
            p.Damage = ReadDouble(ps + Offsets.PS_Damage);
            p.Heal = ReadDouble(ps + Offsets.PS_Heal);

            p.ReceivedShieldDmg = ReadDouble(ps + Offsets.PS_ReceivedShieldDmg);
            p.ReceivedEffectiveShieldDmg = ReadDouble(ps + Offsets.PS_ReceivedEffectiveShieldDmg);
            p.WeaponShieldDmgDealt = ReadDouble(ps + Offsets.PS_WeaponShieldDmgDealt);
            p.AbilityShieldDmgDealt = ReadDouble(ps + Offsets.PS_AbilityShieldDmgDealt);

            p.ImpulseReceived = ReadDouble(ps + Offsets.PS_ImpulseReceived);
            p.WeaponImpulseDealt = ReadDouble(ps + Offsets.PS_WeaponImpulseDealt);
            p.AbilityImpulseDealt = ReadDouble(ps + Offsets.PS_AbilityImpulseDealt);

            p.ShieldPickups = ReadInt(ps + Offsets.PS_ShieldPickups);
            p.Empowerments = ReadInt(ps + Offsets.PS_NbEmpowermentSoFar);
            p.Dashes = ReadInt(ps + Offsets.PS_Dashes);
            p.Jumps = ReadInt(ps + Offsets.PS_Jumps);
            p.AbilitiesUsed = ReadInt(ps + Offsets.PS_AbilitiesUsed);
            p.TimeAlive = ReadDouble(ps + Offsets.PS_TimeAlive);
            p.DashMode = ReadInt(ps + Offsets.PS_DashMode);

            p.GravityDuration = ReadDouble(ps + Offsets.PS_GravityDuration);
            p.GravityOnGround = ReadDouble(ps + Offsets.PS_GravityOnGround);
            p.GravityInAir = ReadDouble(ps + Offsets.PS_GravityInAir);
            p.GravityUseCount = ReadInt(ps + Offsets.PS_GravityUseCount);

            p.NbTotalPings = ReadInt(ps + Offsets.PS_NbTotalPings);
            p.NbEnemyPings = ReadInt(ps + Offsets.PS_NbEnemyPings);

            p.Weapon = ReadByte(ps + Offsets.PS_Weapon);
            p.Ability = ReadByte(ps + Offsets.PS_Ability);
            p.Module = ReadByte(ps + Offsets.PS_Module);

            if (gameMode == 4)
            {
                p.FfaNbBackUp = ReadInt(ps + Offsets.FFAPS_NbBackUp);
                p.FfaDeathRanking = ReadInt(ps + Offsets.FFAPS_DeathRanking);
            }

            return p;
        }

        LobbyData ReadLobbyData(long gs, FNameResolver resolver)
        {
            var d = new LobbyData();
            int arenaIdx = ReadInt(gs + Offsets.CGS_ArenaRowName);
            int modeIdx = ReadInt(gs + Offsets.CGS_GameModeRowName);
            d.BotCountT1 = ReadInt(gs + Offsets.CGS_BotCountT1);
            d.BotCountT2 = ReadInt(gs + Offsets.CGS_BotCountT2);
            d.MapName = ResolveMapName(arenaIdx, resolver);
            d.ModeName = ResolveModeName(modeIdx, resolver);

            DiagLog.LobbyRead(d.MapName, d.ModeName, d.BotCountT1, d.BotCountT2, arenaIdx, modeIdx);
            return d;
        }

        // ── Primitives ────────────────────────────────────────────────────────

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
            int count = ReadInt(addr + 8);
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
                        GetModuleInformation(_handle, mods[i], out MODULEINFO info,
                            (uint)Marshal.SizeOf<MODULEINFO>());
                        return (long)info.Base;
                    }
                }
            }
            catch { }
            return 0;
        }
    }
}