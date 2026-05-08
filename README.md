# BCA-Tracker

External memory reader and match tracker for Battle Core Arena
(UE5.1, P2P, EAC disabled).

## Solution layout

```
BCA-Tracker.slnx
├── BCA-Tracker.Core/        Class library — memory reader, match logic.
│   ├── Memory/              FNameResolver, MemoryReader, KillFeedTracker
│   ├── Game/                Offsets, Enums, runtime models
│   ├── Match/               MatchSaver, MatchRecord (now incl. AccountId)
│   └── Diagnostics/         DiagLog
│
├── BCA-Tracker.Legacy/      Console exe (the original alpha tracker).
│
└── BCA-Tracker/             Avalonia desktop app (user-facing).
    ├── Program.cs           Avalonia bootstrap
    ├── App.axaml            Application + theme dictionaries
    ├── LauncherWindow.axaml "New UI" / "Legacy" picker
    ├── MainWindow.axaml     TitleBar + TopNav + page host + status bar
    ├── Themes/              Palette, Typography, Controls, Cards
    ├── Controls/            TitleBar, TopNav, MatchCardView, NavItem
    ├── Views/               Pages (Home + 6 placeholders)
    └── Services/            AppPaths, AppSettings, AppServices, MatchStore, Stats
```

## What's new in v4

- **Switched WPF → Avalonia 12.** Same C#, similar XAML, much better defaults.
  The custom theme is a fresh build on top of `SimpleTheme`.
- **AccountId field on PlayerRecord.** Forward-compat for the future
  reader update that captures the local player's stable Ubisoft account ID
  from `APlayerState.UniqueId`. UI already uses it when present, falls
  back to `IsLocalPlayer` when not. All existing matches deserialise fine
  (the field is nullable).
- **Home page is the only fully-built page.** Match History, Lifetime
  Stats, Trends, Maps, Weapons are placeholders pending design feedback.

## Build

```
dotnet build BCA-Tracker.slnx -c Release
```

Targets `net10.0-windows`, x64. Avalonia 12.0.2 + Avalonia.Themes.Simple +
Avalonia.Diagnostics (debug only — F12 opens DevTools at runtime).

Open in Visual Studio 2026 / Rider / VS Code with the Avalonia for VS Code
extension. Set `BCA-Tracker` as startup, F5.

## Theming

`Themes/Palette.axaml` is the only file you edit to recolour the entire
app. Every brush in every other file references its keys via
`DynamicResource`, so changes propagate immediately.

| File              | What's in it                                       |
|-------------------|----------------------------------------------------|
| `Palette.axaml`   | Colours + brushes + font families. ResourceDict.   |
| `Typography.axaml`| Text class selectors (`page-title`, `label`, etc.) |
| `Controls.axaml`  | Button, TextBox, CheckBox, ComboBox restyles.      |
| `Cards.axaml`     | `card`, `stat-tile`, `card-button`, `match-card`.  |

## Diagnostics

Two log files under `%AppData%\BCA-Hub\` (legacy folder):

- `diag.log` — process attach, GS classification, save triggers.
- `fname_resolver.log` — FNamePool location attempts.

UI crashes write to `%AppData%\BCA-Tracker\crash.log`.
