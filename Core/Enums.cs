using System.Collections.Generic;

namespace BCATracker
{
    public static class BCAEnums
    {
        // ── Weapon ───────────────────────────────────────────────────────────────
        static readonly string[] Weapons =
        {
            "None", "Sparkler", "Shafter", "Revoker",
            "Pounder", "Striker", "Spreader", "Ransacker"
        };
        public static string WeaponName(byte id)
            => id < Weapons.Length ? Weapons[id] : $"W?{id}";

        // ── Ability ──────────────────────────────────────────────────────────────
        static readonly string[] Abilities =
        {
            "None", "Blast", "ShieldHealing", "ProtectiveWall",
            "BlackHole", "Invisibility", "Flashbang", "TeleportationProj"
        };
        public static string AbilityName(byte id)
            => id < Abilities.Length ? Abilities[id] : $"A?{id}";

        // ── Module ───────────────────────────────────────────────────────────────
        static readonly string[] Modules =
        {
            "None", "Regeneration", "Mobility", "HealBooster",
            "Vampirism", "Camouflage", "Synchronisation", "Berserker", "Anchoring"
        };
        public static string ModuleName(byte id)
            => id < Modules.Length ? Modules[id] : $"M?{id}";

        // ── GameMode ─────────────────────────────────────────────────────────────
        static readonly string[] GameModes =
        {
            "Default", "Tuto", "Training", "Backup3v3", "BackupFFA",
            "GoldenCore3v3", "TestMap", "Checkpoint", "Autotest", "CoopVSAI",
            "CustomGamemode", "Quickplay", "First_Match_VS_AI"
        };
        public static string GameModeName(byte id)
            => id < GameModes.Length ? GameModes[id] : "?";

        // ── GameState ─────────────────────────────────────────────────────────────
        static readonly string[] GameStates =
        {
            "MainMenu",         // 0
            "Loading",          // 1
            "Loading2",         // 2
            "Loading3",         // 3
            "LoadoutSelection", // 4
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
        public static string GameStateName(byte id)
            => id < GameStates.Length ? GameStates[id] : "Transition";

        // ── Map row-name → display name ──────────────────────────────────────────
        //
        // These are keyed by the DataTable row name string (what you see in the
        // FNamePool entry), NOT by the FName ComparisonIndex integer.
        // FName indices are session-assigned and change every launch; row name
        // strings are stable across all builds of this game.
        //
        // Source: CustomGameGS_C.ArenaRowName values observed in-game +
        //         ArenaTeamGS_C.CurrentMap values observed in-game.
        //
        // The "CurrentMap" FName used during a match contains the row name directly
        // (e.g. "Trinity_Island", "Vertigo", "Circular", "Shroomworld",
        //  "Intersection", "Nest") — these match the DataTable keys.
        static readonly Dictionary<string, string> _mapRowToDisplay = new()
        {
            { "Trinity_Island", "Trinity Island" },
            { "Vertigo",        "Lost Complex"   },  // CGS uses "Vertigo", AGS uses same
            { "Circular",       "Singularity"    },
            { "Shroomworld",    "Shroomworld"    },
            { "Intersection",   "Twilight Path"  },
            { "Nest",           "Enkidu Nest"    },
        };

        /// <summary>
        /// Maps a raw row-name string (from FNamePool) to a display name.
        /// Returns null if the row name is unknown (caller can display the raw name).
        /// </summary>
        public static string MapRowNameToDisplayName(string rowName)
        {
            if (rowName == null) return null;
            return _mapRowToDisplay.TryGetValue(rowName, out string n) ? n : null;
        }

        // ── Mode row-name → display name ─────────────────────────────────────────
        static readonly Dictionary<string, string> _modeRowToDisplay = new()
        {
            { "Backup3v3",     "Backup 3v3" },
            { "GoldenCore3v3", "Q-Ball"     },
            { "BackupFFA",     "FFA"        },
        };

        public static string ModeRowNameToDisplayName(string rowName)
        {
            if (rowName == null) return null;
            return _modeRowToDisplay.TryGetValue(rowName, out string n) ? n : null;
        }

        // ── Legacy static-index fallback ─────────────────────────────────────────
        //
        // These are only used when FNameResolver fails to initialize (e.g. pool
        // scan found nothing). They were valid for build 5.1.0-20828 session where
        // they were originally captured. Do NOT update these; fix FNameResolver instead.
        //
        static readonly Dictionary<int, string> _mapNames = new()
        {
            { 0x5BE09, "Trinity Island" },
            { 0x2DEAF, "Lost Complex"   },
            { 0x59D6C, "Singularity"    },
            { 0x2DEA8, "Shroomworld"    },
            { 0x404EF, "Twilight Path"  },
        };

        static readonly Dictionary<int, string> _modeNames = new()
        {
            { 0x16BE22, "Backup 3v3" },
            { 0x16DACC, "Q-Ball"     },
        };

        public static string LobbyMapName(int fnameIndex)
        {
            if (_mapNames.TryGetValue(fnameIndex, out string n)) return n;
            return fnameIndex != 0 ? $"Unknown (0x{fnameIndex:X})" : "Unknown";
        }

        public static string LobbyModeName(int fnameIndex)
        {
            if (_modeNames.TryGetValue(fnameIndex, out string n)) return n;
            return fnameIndex != 0 ? $"Unknown (0x{fnameIndex:X})" : "Unknown";
        }
    }
}
