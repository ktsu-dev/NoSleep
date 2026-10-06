# GitHub Copilot Custom Instructions for NoSleep

## Overview

NoSleep is a cross-platform .NET tool that stops a machine from sleeping, dimming, or locking its screen
while it runs. It installs as the `nosleep` command, shows a system tray icon on Windows, macOS, and Linux,
and falls back to a headless console mode on machines with no desktop session.

### Key Features

- Native inhibitors on every platform, not input faking: `SetThreadExecutionState`, IOKit power assertions,
  and logind or GNOME session inhibitors
- One tray icon and menu across `Shell_NotifyIcon`, `NSStatusItem`, and StatusNotifierItem, via `ktsu.TrayApp`
- Separate control over system sleep and display sleep
- Headless mode with Ctrl+C and `SIGTERM` handling, and an optional duration after which NoSleep exits
- `nosleep --status` reports what the current machine actually supports

## Tech Stack

- **Language**: C# on .NET 10 (the library also targets .NET 9)
- **SDK**: ktsu.Sdk family (`ktsu.Sdk`, `ktsu.Sdk.Tool`) plus `MSTest.Sdk`
- **UI**: Avalonia, used only for the tray icon and its native menu - there is no XAML and no window
- **Testing**: MSTest on Microsoft Testing Platform
- **Key dependencies**: Avalonia, Avalonia.Desktop, Avalonia.Native, ktsu.AppDataStorage, Polyfill

## Project Structure

```
NoSleep/
├── .github/               # Workflows and this file
├── NoSleep/               # Core library (ktsu.NoSleep), no UI dependencies
│   ├── Contracts/         # ISleepBlocker
│   └── Platforms/         # Per-platform inhibitors and their helpers
├── NoSleep.Tool/          # The nosleep command (ktsu.NoSleep.Tool): one Program.cs
│   └── Assets/            # Generated tray icons, embedded as resources
├── NoSleep.Test/          # MSTest suite
└── scripts/               # Icon generation
```

## Conventions

- Tabs for indentation in C#; the file header is `// Copyright (c) 2023-2026 ktsu-dev contributors` (it comes from `COPYRIGHT.md`, which CI regenerates)
- `using` directives go inside the namespace, after the file-scoped namespace declaration
- Warnings are errors and analysis runs at `10.0-all`, so public members need XML documentation and
  string comparisons need an explicit `StringComparison`
- Prefer `Ensure.NotNull(x)` from Polyfill over `ArgumentNullException.ThrowIfNull(x)` - the KTSU0003
  analyzer enforces this
- Never add global warning suppressions; use a targeted `SuppressMessage` with a justification instead

## Things to be careful about

- `WindowsSleepBlocker` must keep its own thread alive: the Windows execution state belongs to the thread
  that set it
- `ChildProcessHold` must keep the helper's standard input pipe open, so a killed NoSleep releases its
  Linux inhibitor rather than orphaning it
- `Program` holds the user's keep-awake intent in a local `bool` and only acts on it in `OnStart`: a
  toggle setter that enabled directly would be overridden by `OnStart` a moment later, so `--off` and a
  remembered "off" would both stop working
- The tool package stays at 50 MB rather than 190 MB because `ktsu.TrayApp`'s `build/` props trim the
  per-RID SkiaSharp natives and their debug symbols; `TrayAppTrimToolRuntimeAssets=false` turns that off
