using System;
using System.IO;
using System.Text;

namespace BCATracker
{
    /// <summary>
    /// Resolves FName ComparisonIndex → string by reading UE5's FNamePool
    /// directly from the target process via ReadProcessMemory.
    ///
    /// ── UE5.1 FNamePool memory layout (build 5.1.0-20828) ────────────────────
    ///
    ///  FNamePool (global in .data):
    ///    +0x00  FRWLock lock           (8 bytes)
    ///    +0x08  uint32  CurrentBlock
    ///    +0x0C  uint32  CurrentByteCursor
    ///    +0x10  void*   Blocks[8192]   each block is Stride*65536 = 128 KB
    ///
    ///  FNameEntryHeader (uint16 bitfield, !WITH_CASE_PRESERVING_NAME):
    ///    bit  0     bIsWide               (0 = ANSI, 1 = wchar_t)
    ///    bits 1..5  LowercaseProbeHash    (5 bits, internal hash)
    ///    bits 6..15 Len                   (10 bits, char count)
    ///
    ///  FNameEntry layout in a block:
    ///    +0   FNameEntryHeader  (2 bytes)
    ///    +2   chars[Len]        (ANSI = Len bytes, Wide = Len*2 bytes)
    ///    Entries are packed tight with Stride (= 2-byte) alignment.
    ///
    ///  ComparisonIndex encoding:
    ///    blockIndex   = comparisonIndex >> 16
    ///    strideOffset = comparisonIndex & 0xFFFF      (units of Stride bytes)
    ///    byteAddress  = Blocks[blockIndex] + Stride * strideOffset
    ///
    /// ── Location strategy ─────────────────────────────────────────────────────
    ///  Strategy 1: scan FName::AppendString prologue (RVA 0x025C92D0) for
    ///              RIP-relative LEAs (direct &FNamePool refs) and the
    ///              `cmp byte ptr [rip+disp], 0` init-guard pattern (whose
    ///              target lives at FNamePool + sizeof(FNamePool)).
    ///  Strategy 2: scan the .data section for a region whose CurrentBlock,
    ///              CurrentByteCursor and Blocks[0..1] form a self-consistent
    ///              FNamePool, then validate by resolving "None" at index 0.
    /// </summary>
    public class FNameResolver
    {
        // ── Layout constants ──────────────────────────────────────────────────
        const int  BLOCK_OFFSET_BITS = 16;
        const int  BLOCK_SIZE        = 1 << BLOCK_OFFSET_BITS; // 65536 stride units
        const int  STRIDE            = 2;                       // alignof(FNameEntry) on Win64
        const int  MAX_BLOCKS        = 8192;
        const int  MAX_NAME_LEN      = 1024;
        const int  POOL_LOCK_SIZE    = 8;   // FRWLock (SRWLOCK wrapper)
        const int  POOL_BLOCKS_START = 0x10; // offset of Blocks[0] in FNamePool

        // AppendString RVA from SDK Basic.hpp Offsets::AppendString
        const long APPEND_STRING_RVA = 0x025C92D0;

        // How many bytes of AppendString prologue to scan for LEA
        const int SCAN_BYTES = 256;

        // ── State ─────────────────────────────────────────────────────────────
        readonly MemoryReader _mem;
        readonly string       _logPath;
        long _poolBase    = 0;
        bool _initialized = false;
        bool _initFailed  = false;

        public FNameResolver(MemoryReader mem)
        {
            _mem = mem;
            _logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BCA-Hub", "fname_resolver.log");
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void EnsureInitialized()
        {
            if (_initialized || _initFailed) return;
            TryInitialize();
        }

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
                if (moduleBase == 0)
                {
                    Log("Init failed: moduleBase=0");
                    _initFailed = true;
                    return;
                }

                // ── Strategy 1: scan AppendString prologue for LEA ────────────
                if (TryFindViaAppendString(moduleBase))
                    return;

                // ── Strategy 2: scan PE .data section for FNamePool signature ─
                if (TryFindViaDataScan(moduleBase))
                    return;

                Log("Init failed: all strategies exhausted");
                _initFailed = true;
            }
            catch (Exception ex)
            {
                Log($"Init exception: {ex.GetType().Name}: {ex.Message}");
                _initFailed = true;
            }
        }

        // ── Strategy 1: AppendString-region scan ─────────────────────────────
        //
        // FName::AppendString starts with a one-time-init guard like:
        //
        //     cmp byte ptr [rip+disp], 0   ; opcode 80 3D xx xx xx xx 00
        //     jne already_initialised
        //     ; ...lazy init code...
        //   already_initialised:
        //     lea rax, [rip+disp_pool]     ; opcode 48 8D xx xx xx xx xx
        //     ; ...uses rax as &FNamePool...
        //
        // We scan the function body for two kinds of RIP-relative references:
        //   (A) `cmp byte ptr [rip+disp], 0` — points to the init guard byte,
        //       which lives at the END of the FNamePool struct (right after the
        //       Blocks[] array). poolBase ≈ cmpTarget - sizeof(FNamePool).
        //   (B) `lea reg, [rip+disp]`        — typically points at FNamePool
        //       directly when the function is about to dereference it.
        //
        // For (A) we don't trust the size estimate; we just use the cmp target
        // as a hint and scan ±0x10010 backwards from it.
        // For (B) we validate the candidate directly.
        // All candidates also have to land inside .data (defence in depth).

        bool TryFindViaAppendString(long moduleBase)
        {
            long appendAddr = moduleBase + APPEND_STRING_RVA;

            byte[] code = new byte[SCAN_BYTES];
            if (!_mem.ReadRaw(appendAddr, code, code.Length))
            {
                Log($"AppendString scan: cannot read {SCAN_BYTES} bytes at RVA 0x{APPEND_STRING_RVA:X} (addr 0x{appendAddr:X})");
                return false;
            }

            // Sanity: if the read returned all zeros, the RVA is wrong for this
            // build / the page wasn't paged in. Bail to Strategy 2.
            bool allZero = true;
            for (int k = 0; k < 32 && allZero; k++) if (code[k] != 0) allZero = false;
            if (allZero)
            {
                Log("AppendString scan: bytes are all zero — RVA likely wrong, skipping to Strategy 2");
                return false;
            }

            Log($"AppendString bytes[0..31]: {BitConverter.ToString(code, 0, 32)}");

            // Get .data bounds so we can sanity-check candidates.
            long dataBase = 0, dataSize = 0;
            GetDataSection(moduleBase, out dataBase, out dataSize);
            bool hasData = dataBase != 0 && dataSize != 0;

            int leaCount = 0, cmpCount = 0;
            for (int i = 0; i < code.Length - 6; i++)
            {
                // ── (B) lea reg, [rip+disp] ──────────────────────────────
                //    REX (0x40..0x4F)   8D   modrm(mod=00 rm=101)   disp32
                if (code[i] >= 0x40 && code[i] <= 0x4F
                    && code[i + 1] == 0x8D
                    && (code[i + 2] & 0xC7) == 0x05)
                {
                    int  disp      = BitConverter.ToInt32(code, i + 3);
                    long rip       = appendAddr + i + 7;
                    long candidate = rip + disp;
                    leaCount++;

                    if (hasData && (candidate < dataBase || candidate >= dataBase + dataSize))
                    {
                        Log($"Strategy 1: LEA at +0x{i:X} → 0x{candidate:X} rejected: not in .data");
                        continue;
                    }
                    if (TryAcceptCandidate(candidate, $"LEA at +0x{i:X}, disp=0x{disp:X}"))
                        return true;
                }

                // ── (A) cmp byte ptr [rip+disp], 0 ────────────────────────
                //    80   3D   disp32   imm8(00)
                if (code[i] == 0x80 && code[i + 1] == 0x3D && i + 7 < code.Length)
                {
                    int  disp   = BitConverter.ToInt32(code, i + 2);
                    long rip    = appendAddr + i + 7;
                    long target = rip + disp;
                    cmpCount++;

                    if (hasData && (target < dataBase || target >= dataBase + dataSize))
                    {
                        Log($"Strategy 1: CMP at +0x{i:X} → 0x{target:X} rejected: not in .data");
                        continue;
                    }

                    // The init-guard byte typically sits at the END of FNamePool
                    // (right after the Blocks[] array). Try the exact predicted
                    // size first — that's almost always right.
                    long guess = target - (POOL_BLOCKS_START + 8L * MAX_BLOCKS);
                    if (TryAcceptCandidate(guess, $"CMP at +0x{i:X} (poolEnd-sizeof)"))
                        return true;
                }
            }

            Log($"Strategy 1 failed: scanned {SCAN_BYTES} bytes, {leaCount} LEA + {cmpCount} CMP candidate(s), none validated");
            return false;
        }

        // Validates a candidate address as the FNamePool base; on success, sets
        // _poolBase, logs, and returns true. Otherwise logs the rejection
        // (only if reason is "interesting") and returns false.
        bool TryAcceptCandidate(long candidate, string source)
        {
            string reason;
            if (ValidatePoolBase(candidate, out reason))
            {
                _poolBase = candidate;
                Log($"Strategy 1 OK: FNamePool at 0x{_poolBase:X} ({source})");
                return true;
            }
            // Suppress the spam when the candidate is just garbage; only log
            // when it got *close* to passing (e.g. Blocks[0] looked like a
            // pointer but Resolve(0) didn't return "None").
            if (!reason.StartsWith("Blocks[0]"))
                Log($"Strategy 1: {source} → 0x{candidate:X} rejected: {reason}");
            return false;
        }

        // ── Strategy 2: .data section scan ───────────────────────────────────
        //
        // FNamePool in a running game:
        //   [+0x00] FRWLock  (8 bytes; SRWLOCK — usually 0 when uncontended)
        //   [+0x08] CurrentBlock      (uint32, in [0, 8191])
        //   [+0x0C] CurrentByteCursor (uint32, in [0, 65535] stride units)
        //   [+0x10] Blocks[0..8191]   (8 bytes each; 128KB blocks of FNameEntries)
        //
        // We scan .data in 8-byte strides. Cheap structural filter:
        //   CurrentBlock < 8192,
        //   CurrentByteCursor < 65536,
        //   Blocks[0] is a plausible aligned heap pointer,
        //   Blocks[1] consistent with CurrentBlock (set iff CurrentBlock >= 1).
        // Then validate by resolving "None" at index 0.

        bool TryFindViaDataScan(long moduleBase)
        {
            // Read the PE header to find .data section bounds
            long dataBase, dataSize;
            if (!GetDataSection(moduleBase, out dataBase, out dataSize))
            {
                Log("Strategy 2: could not locate .data section");
                return false;
            }

            Log($"Strategy 2: scanning .data section at 0x{dataBase:X}, size=0x{dataSize:X}");

            if (dataSize < POOL_BLOCKS_START + 16)
            {
                Log("Strategy 2: .data section too small, skipping");
                return false;
            }

            // Read the whole .data section in one shot (cap at 64 MB to be safe).
            int readSize = (int)Math.Min(dataSize, 64L * 1024 * 1024);
            byte[] data  = new byte[readSize];
            if (!_mem.ReadRaw(dataBase, data, readSize))
            {
                Log("Strategy 2: failed to read .data section");
                return false;
            }

            int candidates = 0, rejects = 0;
            // Stride by 8 (FNamePool is at least 8-byte aligned in .data).
            // We need to read 24 bytes from each candidate offset
            // (0..8 lock, 8..12 CurrentBlock, 12..16 CurrentByteCursor,
            //  16..24 Blocks[0]). Add another 8 for the optional Blocks[1] sanity check.
            int maxOff = readSize - (POOL_BLOCKS_START + 16);
            for (int off = 0; off <= maxOff; off += 8)
            {
                // ── Cheap structural filter (no syscalls) ────────────────────

                // CurrentBlock ∈ [0, MAX_BLOCKS-1]
                uint curBlock = BitConverter.ToUInt32(data, off + 8);
                if (curBlock >= MAX_BLOCKS) continue;

                // CurrentByteCursor ∈ [0, BLOCK_SIZE-1].
                // Almost always non-zero in a running game with names allocated.
                uint curCursor = BitConverter.ToUInt32(data, off + 12);
                if (curCursor >= BLOCK_SIZE) continue;
                if (curCursor == 0 && curBlock == 0) continue; // empty pool, not what we want

                // Blocks[0] must be a plausible heap pointer.
                long block0 = BitConverter.ToInt64(data, off + POOL_BLOCKS_START);
                if (!IsLikelyHeapPointer(block0)) continue;

                // If CurrentBlock >= 1, Blocks[1] must also be a plausible
                // heap pointer; otherwise it must be 0 (blocks are filled in order).
                long block1 = BitConverter.ToInt64(data, off + POOL_BLOCKS_START + 8);
                if (curBlock >= 1)
                {
                    if (!IsLikelyHeapPointer(block1)) continue;
                }
                else
                {
                    if (block1 != 0) continue;
                }

                // ── Validate by reading "None" at index 0 ────────────────────
                candidates++;
                long candidate = dataBase + off;
                string reason;
                if (ValidatePoolBase(candidate, out reason))
                {
                    _poolBase = candidate;
                    Log($"Strategy 2 OK: FNamePool at 0x{_poolBase:X} (.data+0x{off:X}, " +
                        $"curBlock={curBlock}, curCursor=0x{curCursor:X})");
                    return true;
                }
                rejects++;
            }

            Log($"Strategy 2 failed: {candidates} structural candidate(s) examined, {rejects} validation rejects");
            return false;
        }

        static bool IsLikelyHeapPointer(long p)
            => p > 0x10000 && p < 0x7FFF_FFFF_FFFFL && (p & 0x7) == 0;

        bool GetDataSection(long moduleBase, out long sectionBase, out long sectionSize)
        {
            sectionBase = 0;
            sectionSize = 0;

            try
            {
                // DOS header → e_lfanew
                byte[] dosHdr = new byte[0x40];
                if (!_mem.ReadRaw(moduleBase, dosHdr, dosHdr.Length)) return false;
                if (dosHdr[0] != 'M' || dosHdr[1] != 'Z') return false;

                int peOffset = BitConverter.ToInt32(dosHdr, 0x3C);

                // PE header
                byte[] peHdr = new byte[0x18 + 0x70]; // COFF + first part of optional
                if (!_mem.ReadRaw(moduleBase + peOffset, peHdr, peHdr.Length)) return false;
                if (peHdr[0] != 'P' || peHdr[1] != 'E') return false;

                int numSections      = BitConverter.ToUInt16(peHdr, 6);
                int optHdrSize       = BitConverter.ToUInt16(peHdr, 20);
                long sectionTableAddr = moduleBase + peOffset + 24 + optHdrSize;

                // Each section header = 40 bytes
                byte[] secTable = new byte[numSections * 40];
                if (!_mem.ReadRaw(sectionTableAddr, secTable, secTable.Length)) return false;

                for (int i = 0; i < numSections; i++)
                {
                    int    o    = i * 40;
                    string name = Encoding.ASCII.GetString(secTable, o, 8).TrimEnd('\0');
                    // IMAGE_SECTION_HEADER layout:
                    //   +0   Name[8]
                    //   +8   VirtualSize       ← in-memory size, what we want
                    //   +12  VirtualAddress    (RVA)
                    //   +16  SizeOfRawData     (file-aligned, may be 0 or smaller)
                    //   +20  PointerToRawData
                    //   ...
                    // When the section has uninitialised tail bytes,
                    // SizeOfRawData < VirtualSize, so reading SizeOfRawData
                    // truncates the scan. When the section has BSS-only
                    // content, SizeOfRawData can be 0. Always prefer
                    // VirtualSize, fall back to SizeOfRawData if VS is 0.
                    uint   vSize = BitConverter.ToUInt32(secTable, o + 8);
                    uint   va    = BitConverter.ToUInt32(secTable, o + 12);
                    uint   rSize = BitConverter.ToUInt32(secTable, o + 16);
                    uint   sz    = vSize > 0 ? vSize : rSize;

                    if (name == ".data")
                    {
                        sectionBase = moduleBase + va;
                        sectionSize = sz;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"GetDataSection exception: {ex.Message}");
            }
            return false;
        }

        // ── Validation ────────────────────────────────────────────────────────

        bool ValidatePoolBase(long addr, out string rejectReason)
        {
            rejectReason = null;
            try
            {
                long block0 = _mem.ReadLong(addr + POOL_BLOCKS_START);
                if (!IsLikelyHeapPointer(block0))
                {
                    rejectReason = $"Blocks[0]=0x{block0:X} not a valid user-mode pointer";
                    return false;
                }

                // Resolve index 0 and require it to be exactly "None" (ANSI, 4 chars).
                // This is the canonical first FNameEntry in every UE 4.23+ pool.
                string test = ReadEntryFromPool(addr, 0);
                if (test != "None")
                {
                    rejectReason = $"index 0 = \"{test ?? "<null>"}\" (expected \"None\")";
                    return false;
                }

                // Walk forward one entry and check it also looks valid. This
                // rules out a region that happens to contain the bytes "None"
                // at byte offset 0 in some other allocation by coincidence:
                // the next entry should have header.length > 0 and characters
                // in printable ASCII range. UE engine names are always ASCII
                // identifiers.
                //
                // "None" is a 4-char ANSI entry: header(2) + chars(4) = 6 raw
                // bytes. With STRIDE = 2, that's 3 stride units. So the next
                // entry is at ComparisonIndex = (block 0, stride offset 3) = 3.
                int secondEntryStride = (2 + 4 + STRIDE - 1) / STRIDE;
                string second = ReadEntryFromPool(addr, secondEntryStride);
                if (second != null && second.Length > 0 && !IsAsciiIdentifierLike(second))
                {
                    // Not a hard fail — some pools have unusual second entries —
                    // but log it so we can spot a false positive later.
                    Log($"ValidatePoolBase: index {secondEntryStride} = \"{second}\" " +
                        $"(unexpected, but accepting because index 0 = \"None\")");
                }
                return true;
            }
            catch (Exception ex)
            {
                rejectReason = ex.Message;
                return false;
            }
        }

        static bool IsAsciiIdentifierLike(string s)
        {
            foreach (char c in s)
                if (c < 0x20 || c > 0x7E) return false;
            return true;
        }

        // ── Entry reading ─────────────────────────────────────────────────────

        string ReadEntry(int comparisonIndex)
            => ReadEntryFromPool(_poolBase, comparisonIndex);

        string ReadEntryFromPool(long pool, int comparisonIndex)
        {
            // ComparisonIndex layout (UE 5.1, FNamePool):
            //   bits [16..31]  blockIndex   — index into FNamePool.Blocks[]
            //   bits [0..15]   nameOffset   — 16-bit count of FNameEntry STRIDES,
            //                                 NOT raw bytes. Stride = alignof(FNameEntry) = 2,
            //                                 so the byte offset within a block is
            //                                 nameOffset * 2.
            //
            // Reference: UnrealEngine/Engine/Source/Runtime/Core/Private/UObject/UnrealNames.cpp
            //            FNameEntryAllocator::Resolve, line ~3375 in 5.1:
            //   return reinterpret_cast<FNameEntry*>(Blocks[Block] + Stride * Offset);
            int blockIndex   = (int)((uint)comparisonIndex >> BLOCK_OFFSET_BITS);
            int strideOffset = comparisonIndex & (BLOCK_SIZE - 1);
            int byteOffset   = strideOffset * STRIDE;

            if ((uint)blockIndex >= MAX_BLOCKS) return null;
            // Block size = STRIDE * BLOCK_SIZE = 128KB; need byteOffset + header
            // to fit. Header is 2 bytes; longest legal name is MAX_NAME_LEN*2
            // bytes (wide). Add a safety margin so a corrupt index can't make
            // us read past the block.
            if (byteOffset > STRIDE * BLOCK_SIZE - (2 + MAX_NAME_LEN * 2)) return null;

            long blockPtr = _mem.ReadLong(pool + POOL_BLOCKS_START + (long)blockIndex * 8);
            if (blockPtr == 0) return null;

            byte[] header = new byte[2];
            if (!_mem.ReadRaw(blockPtr + byteOffset, header, 2)) return null;

            ushort hdr    = BitConverter.ToUInt16(header, 0);
            // FNameEntryHeader bitfield (UE 5.1, !WITH_CASE_PRESERVING_NAME):
            //   bit  0     bIsWide
            //   bits 1..5  LowercaseProbeHash (5 bits, internal hash)
            //   bits 6..15 Len (10 bits)
            int    length = hdr >> 6;
            bool   isWide = (hdr & 0x1) != 0;

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

        // ── Logging ───────────────────────────────────────────────────────────

        readonly System.Collections.Generic.HashSet<int> _logged = new();

        public void LogResolution(string kind, int index, string raw, string display)
        {
            if (display == null) return;
            if (!_logged.Add(index)) return;
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
