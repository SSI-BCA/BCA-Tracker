using System;
using System.IO;
using System.Text;

namespace BCATracker
{
    /// <summary>
    /// Resolves FName ComparisonIndex → string by reading UE5's FNamePool
    /// directly from the target process via ReadProcessMemory. No DLL required.
    ///
    /// ── UE5.1 FNamePool memory layout (build 5.1.0-20828) ────────────────────
    ///
    ///  FNamePool (global in .data, located via LEA inside FName::AppendString):
    ///    +0x00  FRWLock lock           (8 bytes — Windows SRWLOCK wrapper)
    ///    +0x08  uint32  CurrentBlock   — index of the block being written
    ///    +0x0C  uint32  CurrentCursor  — byte offset within CurrentBlock
    ///    +0x10  void*   Blocks[8192]   — pointers to 64 KB data blocks
    ///
    ///  FNameEntry (stride = 1, byte-packed — NO padding between entries):
    ///    +0  uint16 Header:
    ///          bits [15:6]  = char count
    ///          bit  [5]     = isWide (1 = UTF-16, 0 = ANSI)
    ///          bits [4:0]   = unused
    ///    +2  char[length]  (ANSI) or wchar_t[length] (Wide)
    ///
    ///  ComparisonIndex encoding:
    ///    blockIndex = comparisonIndex >> 16
    ///    byteOffset = comparisonIndex & 0xFFFF  ← low 16 bits directly (stride=1)
    ///
    ///  Confirmed by reverse-engineering from live match data:
    ///    index 0x3F133 → block 3, offset 0xF133  (valid, fits in 64KB)
    ///    index 0x5894F → block 5, offset 0x894F  (valid, fits in 64KB)
    ///    Both in different sessions for the same game → same map name expected.
    ///
    /// ── Finding FNamePool ────────────────────────────────────────────────────
    ///  FName::AppendString (RVA 0x025C92D0) loads FNamePool via two RIP-relative
    ///  LEA instructions early in its prologue. We scan up to 128 bytes for any
    ///  REX (0x40–0x4F) + LEA (0x8D) + RIP-ModRM (0xC7 mask = 0x05), decode the
    ///  displacement, and validate the candidate by reading "None" at index 0.
    ///
    /// ── Validation design ────────────────────────────────────────────────────
    ///  We intentionally do NOT check CurrentBlock/CurrentCursor ranges or
    ///  block-pointer alignment. A full UE5 game has an unpredictable block count
    ///  and uses CRT-heap allocations (not VirtualAlloc), so alignment and count
    ///  assumptions break in practice. The only reliable check is: does reading
    ///  "None" at index 0 succeed? That check is strong enough to reject any
    ///  false-positive LEA target.
    /// </summary>
    public class FNameResolver
    {
        // ── Layout constants ──────────────────────────────────────────────────
        const int BLOCK_OFFSET_BITS = 16;
        const int BLOCK_SIZE = 1 << BLOCK_OFFSET_BITS;   // 65536 = 0x10000
        const int MAX_BLOCKS = 8192;
        const int MAX_NAME_LEN = 1024;
        const int POOL_BLOCKS_START = 0x10;   // offset of Blocks[0] in FNamePool

        const long APPEND_STRING_RVA = 0x025C92D0;

        // ── State ─────────────────────────────────────────────────────────────
        readonly MemoryReader _mem;
        readonly string _logPath;
        long _poolBase = 0;
        bool _initialized = false;
        bool _initFailed = false;

        public FNameResolver(MemoryReader mem)
        {
            _mem = mem;
            _logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BCA-Hub", "fname_resolver.log");
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Eagerly initialises the FNamePool. Call immediately after attaching to the
        /// process so that world-name reads on the very first tick succeed.
        /// Safe to call multiple times — no-ops once already initialised.
        /// </summary>
        public void EnsureInitialized()
        {
            if (_initialized || _initFailed) return;
            TryInitialize();
        }

        /// <summary>
        /// Resolves a ComparisonIndex to its raw name string (e.g. "Trinity_Island").
        /// Returns null on failure — caller should use its own fallback label.
        /// </summary>
        public string Resolve(int comparisonIndex)
        {
            if (comparisonIndex == 0) return null;
            if (_initFailed) return null;
            if (!_initialized) TryInitialize();
            if (_initFailed) return null;

            return ReadEntry(comparisonIndex);
        }

        // ── Initialization ────────────────────────────────────────────────────

        void TryInitialize()
        {
            _initialized = true;
            try
            {
                long moduleBase = _mem.ModuleBase;
                if (moduleBase == 0) { Log("Init failed: moduleBase=0"); _initFailed = true; return; }

                long appendAddr = moduleBase + APPEND_STRING_RVA;

                byte[] code = new byte[128];
                if (!_mem.ReadRaw(appendAddr, code, code.Length))
                {
                    Log($"Init failed: cannot read AppendString at 0x{appendAddr:X}");
                    _initFailed = true;
                    return;
                }

                // Scan for REX (0x40–0x4F) + LEA (0x8D) + RIP-relative ModRM
                // (ModRM & 0xC7) == 0x05 → mod=00, rm=101=RIP
                for (int i = 0; i < code.Length - 6; i++)
                {
                    byte rex = code[i];
                    if (rex < 0x40 || rex > 0x4F || code[i + 1] != 0x8D) continue;

                    byte modrm = code[i + 2];
                    if ((modrm & 0xC7) != 0x05) continue;

                    int disp = BitConverter.ToInt32(code, i + 3);
                    long rip = appendAddr + i + 7;
                    long candidate = rip + disp;

                    string reason = null;
                    if (ValidatePoolBase(candidate, out reason))
                    {
                        _poolBase = candidate;
                        Log($"FNamePool confirmed at 0x{_poolBase:X}");
                        return;
                    }
                }

                Log("Init failed: no LEA candidate passed validation");
                _initFailed = true;
            }
            catch (Exception ex)
            {
                Log($"Init exception: {ex.GetType().Name}: {ex.Message}");
                _initFailed = true;
            }
        }

        bool ValidatePoolBase(long addr, out string rejectReason)
        {
            rejectReason = null;
            try
            {
                long block0 = _mem.ReadLong(addr + POOL_BLOCKS_START);
                if (block0 == 0 || block0 < 0x10000 || block0 > 0x7FFFFFFFFFFFL)
                {
                    rejectReason = $"block[0]=0x{block0:X} not a valid user-mode pointer";
                    return false;
                }

                string test = ReadEntryFromPool(addr, 0);
                if (test != "None")
                {
                    rejectReason = $"index 0 = \"{test ?? "<null>"}\" (expected \"None\")";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                rejectReason = ex.Message;
                return false;
            }
        }

        // ── Entry reading ─────────────────────────────────────────────────────

        string ReadEntry(int comparisonIndex)
            => ReadEntryFromPool(_poolBase, comparisonIndex);

        string ReadEntryFromPool(long pool, int comparisonIndex)
        {
            int blockIndex = (int)((uint)comparisonIndex >> BLOCK_OFFSET_BITS);
            int byteOffset = comparisonIndex & (BLOCK_SIZE - 1);

            if ((uint)blockIndex >= MAX_BLOCKS) return null;
            if (byteOffset > BLOCK_SIZE - 3) return null;

            long blockPtr = _mem.ReadLong(pool + POOL_BLOCKS_START + (long)blockIndex * 8);
            if (blockPtr == 0) return null;

            byte[] header = new byte[2];
            if (!_mem.ReadRaw(blockPtr + byteOffset, header, 2)) return null;

            ushort hdr = BitConverter.ToUInt16(header, 0);
            int length = hdr >> 6;
            bool isWide = (hdr & 0x20) != 0;

            if (length <= 0 || length > MAX_NAME_LEN) return null;

            long charAddr = blockPtr + byteOffset + 2;
            if (isWide)
            {
                byte[] wb = new byte[length * 2];
                return _mem.ReadRaw(charAddr, wb, wb.Length)
                    ? Encoding.Unicode.GetString(wb).TrimEnd('\0') : null;
            }
            else
            {
                byte[] ab = new byte[length];
                return _mem.ReadRaw(charAddr, ab, ab.Length)
                    ? Encoding.ASCII.GetString(ab).TrimEnd('\0') : null;
            }
        }

        // ── Resolution logging ────────────────────────────────────────────────

        // Only log each index once, and only when it resolves to a known display name.
        // Raw/unmatched FName strings are intentionally not logged — they're garbage
        // from unset or server-only fields and just pollute the log.
        readonly System.Collections.Generic.HashSet<int> _logged = new();
        public void LogResolution(string kind, int index, string raw, string display)
        {
            if (display == null) return;           // no match → don't log
            if (!_logged.Add(index)) return;       // already logged → skip
            Log($"{kind}: \"{display}\" (raw={raw}, idx=0x{index:X})");
        }

        void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
            }
            catch { }
        }
    }
}