using System;
using System.Collections.Generic;

namespace BCATracker.Core
{
    public static class BCAEnums
    {
        // ── Weapon ───────────────────────────────────────────────────────────────
        // Order in this array maps to the in-game weapon ID (index = byte
        // value read from memory). "None" stays at index 0; "Shafter" is
        // a development weapon not in the public game so we map it to a
        // placeholder so its ID slot stays correct, and AllWeaponNames()
        // filters it out for the Weapons page grid.
        static readonly string[] Weapons =
        {
            "None", "Sparkler", "Shafter", "Revoker",
            "Pounder", "Striker", "Spreader", "Ransacker"
        };
        public static string WeaponName(byte id)
            => id < Weapons.Length ? Weapons[id] : $"W?{id}";

        /// <summary>Weapons that are hidden in the public-facing Weapons
        /// page because they aren't part of normal play (devkit-only,
        /// removed, etc.). Lookups via WeaponName still resolve them.</summary>
        static readonly HashSet<string> _hiddenWeapons = new(StringComparer.OrdinalIgnoreCase)
        {
            "Shafter",
        };

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

        // ── GameState (in-match) ─────────────────────────────────────────────
        // Read from ArenaTeamGS_C.CurrentGameState (uint8 EGameState at +0x369).
        // Names corrected from observation in-game.
        static readonly string[] GameStates =
        {
            "MainMenu",          // 0
            "Loading",           // 1
            "Loading2",          // 2
            "LoadoutSelect",     // 3  — players picking weapon/ability/module
            "LoadoutReady",      // 4  — everyone has confirmed their loadout
            "PreMatchStart",     // 5
            "MatchStart",        // 6
            "MapLoaded",         // 7
            "Countdown",         // 8
            "Playing",           // 9
            "EndGame1",          // 10
            "EndGame2",          // 11
            "Podium",            // 12
            "Leaderboard",       // 13
            "State14",           // 14
            "State15"            // 15
        };
        public static string GameStateName(byte id)
            => id < GameStates.Length ? GameStates[id] : "Transition";

        // ── MainMenuHUD screen state ─────────────────────────────────────────
        // Read from MainMenuHUD_C.MainMenuState (uint8 at +0x428).
        // Names from observation in-game; gaps are states not yet identified.
        static readonly Dictionary<byte, string> MainMenuStates = new()
        {
            { 0,  "Main Menu" },
            { 1,  "Gamemode Selection" },
            { 3,  "Shop" },
            { 4,  "Item Detail" },
            { 6,  "Customization" },
            { 9,  "Options" },
            { 11, "Social" },
            { 12, "Customization Transition" },
            { 13, "Credits" },
        };
        public static string MainMenuStateName(byte id)
            => MainMenuStates.TryGetValue(id, out string? n) ? n! : $"State {id}";

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
        public static string? MapRowNameToDisplayName(string? rowName)
        {
            if (rowName == null) return null;
            return _mapRowToDisplay.TryGetValue(rowName, out string? n) ? n : null;
        }

        /// <summary>
        /// All known map display names. The Maps page uses this to render
        /// every map on first load — even ones the player hasn't played
        /// yet — so the page has presence on day one and isn't blank for
        /// new users.
        /// </summary>
        /// <summary>Maps that are hidden in the Maps page grid because
        /// they aren't part of normal public play. Lookups via
        /// MapRowNameToDisplayName still resolve them so old saved
        /// matches still display correctly.</summary>
        static readonly HashSet<string> _hiddenMaps = new(StringComparer.OrdinalIgnoreCase)
        {
            "Enkidu Nest",
        };

        public static IReadOnlyList<string> AllMapDisplayNames()
        {
            // Project the row→display dictionary; preserve insertion order;
            // filter out maps that shouldn't appear on the public grid.
            var list = new List<string>(_mapRowToDisplay.Count);
            foreach (var kv in _mapRowToDisplay)
            {
                if (_hiddenMaps.Contains(kv.Value)) continue;
                if (!list.Contains(kv.Value))
                    list.Add(kv.Value);
            }
            return list;
        }

        /// <summary>
        /// All real weapon names (excludes the "None" sentinel). Used by
        /// the Weapons page to render a full grid even when the player
        /// hasn't tried every loadout yet.
        /// </summary>
        public static IReadOnlyList<string> AllWeaponNames()
        {
            var list = new List<string>(Weapons.Length);
            for (int i = 1; i < Weapons.Length; i++)  // skip "None" at index 0
            {
                string name = Weapons[i];
                if (_hiddenWeapons.Contains(name)) continue;
                list.Add(name);
            }
            return list;
        }

        // ── Mode row-name → display name ─────────────────────────────────────────
        //
        // Keys are the row names found in CustomGameGS_C.GameModeRowName at
        // runtime. To extend: pick a new mode in-game. The diag log will print
        //   [FName] Mode idx=... raw="<actual_row_name>" — no display name match
        // Add the row name to this dictionary. The legacy guesses
        // ("Backup3v3", "GoldenCore3v3", "BackupFFA") were wrong but are kept
        // in case other game versions still use them.
        static readonly Dictionary<string, string> _modeRowToDisplay = new()
        {
            { "Backup_Casual", "Backup 3v3" },         // observed in custom-game lobby
            { "Backup3v3",     "Backup 3v3" },         // legacy guess, kept as fallback
            { "GoldenCore3v3", "Q-Ball"     },         // legacy guess, kept as fallback
            { "BackupFFA",     "FFA"        },         // legacy guess, kept as fallback
        };

        public static string? ModeRowNameToDisplayName(string? rowName)
        {
            if (rowName == null) return null;
            return _modeRowToDisplay.TryGetValue(rowName, out string? n) ? n : null;
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
            if (_mapNames.TryGetValue(fnameIndex, out string? n)) return n!;
            return fnameIndex != 0 ? $"Unknown (0x{fnameIndex:X})" : "Unknown";
        }

        public static string LobbyModeName(int fnameIndex)
        {
            if (_modeNames.TryGetValue(fnameIndex, out string? n)) return n!;
            return fnameIndex != 0 ? $"Unknown (0x{fnameIndex:X})" : "Unknown";
        }
    }
}
