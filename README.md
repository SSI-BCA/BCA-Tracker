# BCA-Tracker

External memory reader for Battle Core Arena (UE5.1, Ubisoft, P2P, EAC disabled).
Reads live match stats, kill feed, lobby info and post-match summaries via
ReadProcessMemory; saves matches as JSON under `%AppData%\BCA-Hub\matches\`.

## Build

```
dotnet build -c Release
```

Targets `net10.0`, x64. Run as Administrator (OpenProcess requires it).

## Layout

| File | Role |
|---|---|
| `Program.cs` | Entry point, attach loop, main tick. |
| `Memory/MemoryReader.cs` | Pointer chains, snapshot read, GS class detection. |
| `Memory/FNameResolver.cs` | Locates `FNamePool` and resolves comparison indices to strings. |
| `Memory/KillFeedTracker.cs` | Diff-based kill detection from `HitHistory` arrays. |
| `Core/Offsets.cs` | All static offsets, captured from the Dumper-7 SDK. |
| `Core/Enums.cs` | Weapon/Ability/Module/GameMode/GameState lookup tables and the row-name to display-name maps. |
| `Core/MatchSaver.cs` | Caches lobby map/mode and writes JSON on post-match exit. |
| `Core/MatchRecord.cs` | JSON DTOs. |
| `Core/Models.cs` | Runtime snapshot model used by the UI and saver. |
| `Core/Diaglog.cs` | Diagnostic log writer (`%AppData%\BCA-Hub\diag.log`). |
| `UI/ConsoleUI.cs` | Terminal renderer. |

## Diagnostics

Two log files are written under `%AppData%\BCA-Hub\`:

- `diag.log` — process attach, GS classification, lobby caching, save triggers, exceptions.
- `fname_resolver.log` — FNamePool location attempts and resolutions.

## Changes since alpha v0.13

- **Fixed lobby detection (round 2: UClass-based).** The first round used a
  structural fingerprint at offsets 0x328/0x338. That worked while the user
  was actually in a `CustomGameGS_C`, but after a match the GameState
  pointer transitions through `MainMenuGS_C` (only 0x308 bytes long), and
  reads at 0x328/0x338 returned heap junk that happened to look like
  FNames. The classifier now reads `UObject.ClassPrivate` and resolves the
  UClass FName directly, comparing against the known names
  (`CustomGameGS_C`, `ArenaTeamGS_C`, `BackupGS_C`, `BackupFFAGS_C`,
  `GoldenCoreGS_C`, `BCATutorialGS_C`, `TestMapGS_C`, `MainMenuGS_C`). The
  structural fingerprint is kept as a fallback for the rare case where
  FNameResolver hasn't bootstrapped yet.

- **Added `Backup_Casual` to the mode row-name lookup.** The previous table
  had `Backup3v3`, `GoldenCore3v3`, `BackupFFA` — those were guesses based
  on the EGameMode enum. The actual data table key for casual 3v3 is
  `Backup_Casual`. Other modes will appear as `Unknown (<row_name>)` in
  the UI; `diag.log` will show the raw row name in
  `[FName] Mode idx=... raw="..." — no display name match`. Add new entries
  to `Enums.cs::_modeRowToDisplay` as you encounter them.

- **`ReadUObjectClassName` now caches its last resolution.** The diag log
  used to print one `[Mem] GS UObject: ...` line per tick (~2/s). Now it
  only prints when the class FName actually changes, keeping the log
  readable.

## Changes in alpha v0.13 → v0.14 (round 1)

- **Fixed lobby detection (initial round).** Replaced the broken
  `gsStateByte > 13` check with a 6-byte FName-shape signature on
  `ArenaRowName` and `GameModeRowName`.

- **Fixed FName resolution.** Three bugs in `FNameResolver`:
  1. PE section size was read from `SizeOfRawData` (offset +16) instead of
     `VirtualSize` (offset +8). On `BattleCoreArena.exe` the on-disk size
     for `.data` reads as 0, so Strategy 2's section scan never ran.
  2. The byte-offset within an FNamePool block was treated as raw bytes,
     but UE 5.1 stores it as a count of `FNameEntryAllocator::Stride`-byte
     units (Stride = 2 on Windows). Names other than `"None"` (which is at
     offset 0 either way) were misread.
  3. The `bIsWide` flag was extracted from bit 5 of the entry header
     instead of bit 0 (it sits next to a 5-bit `LowercaseProbeHash`,
     not at the top of the byte). Wide-character names would be misread
     even when the pool was correctly located.

- **Strategy 1 now also recognises the `cmp byte ptr [rip+disp], 0` pattern
  used by MSVC for static one-time-init guards**, which sits at the end of
  the `FNamePool` struct, and uses it to back-compute the pool base. This
  is a hint, not a primary mechanism — Strategy 2's structural scan is the
  reliable fallback.

- **Removed `Memory/DllInjector.cs`.** The plan to inject a C++ helper that
  calls `Conv_StringToName` is no longer needed.
