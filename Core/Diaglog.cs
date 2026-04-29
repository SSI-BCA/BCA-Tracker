using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace BCATracker
{
    /// <summary>
    /// Centralised diagnostic logger. One file per session, written to BCA-Hub\diag.log.
    /// Covers: process attach/detach, GS class detection, lobby caching, FName
    /// resolution, match state transitions, save triggers, and any exceptions.
    /// </summary>
    public static class DiagLog
    {
        static string _path;
        static object _lock = new object();
        static byte _lastState = 255;
        static bool _lastIsLobby;
        static bool _lastIsMatch;
        static bool _lastIsPost;
        static bool _lastIsMenu;
        static string _lastGsClass = "";
        static int _tickCount;
        static DateTime _sessionStart;

        public static void Init()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BCA-Hub");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "diag.log");
            _sessionStart = DateTime.Now;

            // Keep last 3 logs
            RotateLogs(dir);

            Write($"═══ BCA-Hub diagnostic session {_sessionStart:yyyy-MM-dd HH:mm:ss} ═══");
        }

        // ── Process ───────────────────────────────────────────────────────────

        public static void ProcessFound(int pid) =>
            Write($"[Process] Found BattleCoreArena PID={pid}");

        public static void ProcessAttached(long moduleBase) =>
            Write($"[Process] Attached — moduleBase=0x{moduleBase:X}");

        public static void ProcessAttachFailed() =>
            Write("[Process] OpenProcess failed — not running as Administrator?");

        public static void ProcessLost() =>
            Write("[Process] Process exited — detaching");

        // ── GS classification ─────────────────────────────────────────────────

        public static void GsClass(string className)
        {
            if (className == _lastGsClass) return;
            _lastGsClass = className ?? "<null>";
            Write($"[GS] Class changed → \"{_lastGsClass}\"");
        }

        public static void GsClassUnresolved(long gsPtr, long classPtr, int nameIdx) =>
            Write($"[GS] Could not resolve class name — gsPtr=0x{gsPtr:X} classPtr=0x{classPtr:X} nameIdx=0x{nameIdx:X}");

        // ── Lobby ─────────────────────────────────────────────────────────────

        public static void LobbyRead(string map, string mode, int botsT1, int botsT2, int arenaFNameIdx, int modeFNameIdx) =>
            Write($"[Lobby] map=\"{map}\" mode=\"{mode}\" botsT1={botsT1} botsT2={botsT2} arenaIdx=0x{arenaFNameIdx:X} modeIdx=0x{modeFNameIdx:X}");

        public static void LobbyCached(string map, string mode) =>
            Write($"[Lobby] Cached — map=\"{map}\" mode=\"{mode}\"");

        public static void LobbyMapInvalid(string map) =>
            Write($"[Lobby] Map name not cached — value was \"{map}\"");

        // ── State machine ─────────────────────────────────────────────────────

        public static void SnapState(MatchSnapshot snap)
        {
            _tickCount++;

            // Only log when something meaningful changes
            bool lobby = snap?.IsLobby ?? false;
            bool match = snap?.InMatch ?? false;
            bool post = snap?.IsPostMatch ?? false;
            bool menu = snap?.IsMainMenu ?? false;
            byte state = snap?.GameStateEnum ?? 255;

            bool changed = lobby != _lastIsLobby
                        || match != _lastIsMatch
                        || post != _lastIsPost
                        || menu != _lastIsMenu
                        || state != _lastState;

            if (!changed) return;

            _lastIsLobby = lobby;
            _lastIsMatch = match;
            _lastIsPost = post;
            _lastIsMenu = menu;
            _lastState = state;

            if (snap == null)
            {
                Write("[Snap] null — waiting for process");
                return;
            }

            string phase = lobby ? "LOBBY"
                         : match ? "IN_MATCH"
                         : post ? "POST_MATCH"
                         : menu ? $"MENU(state={snap.MainMenuState})"
                         : snap.IsWaiting ? "WAITING"
                         : "UNKNOWN";

            Write($"[Snap] Phase={phase} GsState={state}({snap.StateName}) Mode={snap.GameModeEnum}({snap.ModeName}) " +
                  $"Players={snap.Players?.Count ?? 0} Map=\"{snap.CurrentMap}\" Timer={snap.Timer}");
        }

        // ── Save ──────────────────────────────────────────────────────────────

        public static void SaveTrigger(string map, string mode, int playerCount) =>
            Write($"[Save] Triggered — map=\"{map}\" mode=\"{mode}\" players={playerCount}");

        public static void SaveOk(string path) =>
            Write($"[Save] OK → {path}");

        public static void SaveFailed(Exception ex) =>
            Write($"[Save] FAILED — {ex.GetType().Name}: {ex.Message}");

        public static void SaveSkipped(string reason) =>
            Write($"[Save] Skipped — {reason}");

        // ── FName ─────────────────────────────────────────────────────────────

        public static void FNameResolved(string kind, int idx, string raw, string display) =>
            Write($"[FName] {kind} idx=0x{idx:X} raw=\"{raw}\" → \"{display}\"");

        public static void FNameUnmatched(string kind, int idx, string raw) =>
            Write($"[FName] {kind} idx=0x{idx:X} raw=\"{raw}\" — no display name match");

        // ── Exceptions ────────────────────────────────────────────────────────

        public static void Exception(string context, Exception ex) =>
            Write($"[EXCEPTION] {context} — {ex.GetType().Name}: {ex.Message}\n  {ex.StackTrace}");

        // ── Core ──────────────────────────────────────────────────────────────

        public static void Write(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            lock (_lock)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine); }
                catch { }
            }
        }

        static void RotateLogs(string dir)
        {
            // Rename diag.log → diag.1.log → diag.2.log, drop diag.3.log
            try
            {
                string d2 = Path.Combine(dir, "diag.2.log");
                string d1 = Path.Combine(dir, "diag.1.log");
                string d0 = Path.Combine(dir, "diag.log");
                if (File.Exists(d2)) File.Delete(d2);
                if (File.Exists(d1)) File.Move(d1, d2);
                if (File.Exists(d0)) File.Move(d0, d1);
            }
            catch { }
        }
    }
}