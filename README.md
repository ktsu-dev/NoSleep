# ktsu.NoSleep

> Keep a machine awake, from the system tray or from a script, on Windows, macOS, and Linux

[![License](https://img.shields.io/github/license/ktsu-dev/NoSleep.svg?label=License&logo=nuget)](LICENSE.md)
[![NuGet Version](https://img.shields.io/nuget/v/ktsu.NoSleep?label=Stable&logo=nuget)](https://nuget.org/packages/ktsu.NoSleep)
[![NuGet Version](https://img.shields.io/nuget/vpre/ktsu.NoSleep?label=Latest&logo=nuget)](https://nuget.org/packages/ktsu.NoSleep)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ktsu.NoSleep?label=Downloads&logo=nuget)](https://nuget.org/packages/ktsu.NoSleep)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/ktsu-dev/NoSleep?label=Commits&logo=github)](https://github.com/ktsu-dev/NoSleep/commits/main)
[![GitHub contributors](https://img.shields.io/github/contributors/ktsu-dev/NoSleep?label=Contributors&logo=github)](https://github.com/ktsu-dev/NoSleep/graphs/contributors)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/ktsu-dev/NoSleep/ci.yml?label=Build&logo=github)](https://github.com/ktsu-dev/NoSleep/actions)

## Introduction

`ktsu.NoSleep` stops a machine from suspending, dimming, or locking its screen for as long as it is
running. It installs as the `nosleep` .NET tool and puts an icon in the system tray on all three desktop
platforms, so keep-awake is a click away during a long build, a presentation, or an overnight transfer -
and a click away again when it is done.

Every platform gets its own native inhibitor rather than a mouse-jiggler or a fake keystroke, so the
operating system knows what is happening and its own tooling can report it. On a machine with no desktop
session, the same command falls back to a headless console mode that a script, a CI agent, or a systemd
unit can use.

The library here is the inhibitor layer. The tray icon, the tray-versus-terminal decision, the standard
flags and the signal handling around them come from [`ktsu.TrayApp`](https://github.com/ktsu-dev/TrayApp),
which was extracted from this tool.

## Features

- **Real inhibitors, not input faking**: `SetThreadExecutionState` on Windows, IOKit power assertions on
  macOS, and logind or GNOME session inhibitors on Linux. Held assertions show up in `pmset -g assertions`
  and `systemd-inhibit --list` under a reason you choose.
- **A tray icon on every platform**: one icon and one menu across `Shell_NotifyIcon`, `NSStatusItem`, and
  the Linux StatusNotifierItem protocol, with an open eye while keep-awake is on and a closed eye while it
  is off.
- **System and display, separately**: stop the machine suspending, and optionally keep the screen lit too.
- **Headless mode**: `--no-tray` holds the inhibitor from the terminal and releases it on Ctrl+C or
  `SIGTERM`, with no dependency on a windowing system.
- **Timed runs**: `--for 90m` releases and exits on its own, so an unattended job cannot leave a machine
  awake forever.
- **Nothing left behind**: the inhibitor is released on exit, and the Linux helper process is tied to
  NoSleep's own lifetime so a kill -9 releases the lock rather than orphaning it.
- **Remembered state**: the tray comes back the way it was left - stored under the user's config home as
  `nosleep/tray.json` - while an explicit command line argument always wins.
- **Honest about what it can do**: `nosleep --status` says which mechanism is available on this machine,
  whether the display can be kept lit, and whether a tray icon is possible.

## Installation

### As a .NET tool

NoSleep ships as a .NET tool, which installs the `nosleep` command:

```bash
dotnet tool install -g ktsu.NoSleep.Tool
```

The package is framework-dependent, so it needs the .NET 10 runtime. Update it with
`dotnet tool update -g ktsu.NoSleep.Tool`.

### As a library

To hold an inhibitor from your own application, reference the library instead:

```bash
dotnet add package ktsu.NoSleep
```

### Package Reference

```xml
<PackageReference Include="ktsu.NoSleep" Version="x.y.z" />
```

## Usage

### The tray icon

```bash
nosleep
```

With no arguments, NoSleep starts keeping the machine awake and shows its tray icon. The menu carries the
current state, a **Keep awake** toggle, a **Keep display awake too** toggle, and **Quit NoSleep**; on
Windows a left click on the icon toggles keep-awake directly.

The icon is an open amber eye while an inhibitor is held and a closed grey eye while the machine is free to
sleep, so the state is readable without opening the menu.

### From a terminal or a script

```bash
# Keep the machine awake until Ctrl+C, with no tray icon
nosleep --no-tray

# Keep the machine and the screen awake for two hours, then release and exit
nosleep --no-tray --display --for 2h

# Label the inhibitor so it is identifiable in the platform's own tooling
nosleep --no-tray --reason "nightly backup" --for 6h
```

NoSleep releases the inhibitor on Ctrl+C or `SIGTERM` and exits 0. Exit code 1 means the platform refused,
and 2 means the command line was wrong.

### Checking what a machine supports

```bash
nosleep --status
```

```
nosleep 1.0.0
Platform:      Ubuntu 24.04.4 LTS (ubuntu.24.04-x64)
Tray icon:     available (DISPLAY or WAYLAND_DISPLAY is set)
State:         NoSleep is off - this machine may sleep
Mechanism:     systemd-inhibit + gnome-session-inhibit
Keep awake:    supported
Keep display:  supported
```

### Options

| Option | Description |
|--------|-------------|
| `-t`, `--tray` | Show the tray icon even if no desktop session was detected. |
| `--no-tray` | Stay in the terminal; never show a tray icon. |
| `-d`, `--display` | Keep the display lit as well, not just the system awake. |
| `-o`, `--off` | Start with keep-awake switched off. |
| `-f`, `--for <duration>` | Stop and exit after a duration: `45s`, `90m`, `2h`, `1h30m`. A bare number means minutes. |
| `-r`, `--reason <text>` | Reason recorded with the inhibitor, shown by the platform's own tooling. |
| `-s`, `--status` | Print what NoSleep can do on this machine, then exit. |
| `-h`, `--help` | Show usage, then exit. |
| `-v`, `--version` | Print the version, then exit. |

With neither `--tray` nor `--no-tray`, NoSleep shows a tray icon when the session has somewhere to put one
and stays in the terminal when it does not. If the windowing stack refuses after that check passes, it says
so and carries on in the terminal rather than exiting with the machine left awake.

`--status` describes the machine and exits 0 whether or not an inhibitor is available; read the
`Keep awake:` row rather than the exit code to tell those apart.

Set `TRAYAPP_DEBUG=1` to print the full exception behind a tray failure.

## Usage Examples

### Basic Example

```csharp
using ktsu.NoSleep;

using KeepAwakeController controller = new();

controller.Enable();
// ... do the long-running work ...
controller.Disable();
```

`KeepAwakeController` picks the inhibitor for the current platform, ignores a second `Enable` while already
active, and releases whatever it holds when disposed.

### Keeping the display awake, and following the state

```csharp
using ktsu.NoSleep;

using KeepAwakeController controller = new();

controller.StateChanged += (_, args) =>
	Console.WriteLine(args.IsActive ? "awake" : "free to sleep");

controller.SetRequest(new SleepBlockRequest(KeepDisplayAwake: true, "rendering a preview"));
controller.Enable();

// Changing the request while active re-takes the inhibitor, so this needs no off/on cycle.
controller.SetKeepDisplayAwake(false);
```

### Checking support before promising anything

```csharp
using ktsu.NoSleep;
using ktsu.NoSleep.Contracts;

using ISleepBlocker blocker = SleepBlockerFactory.Create();

if (!blocker.IsSupported)
{
	Console.WriteLine($"No inhibitor here: {blocker.Mechanism}");
	return;
}

blocker.Acquire(SleepBlockRequest.SystemAndDisplay);
```

## Advanced Usage

### Driving a blocker directly

`ISleepBlocker` is the thin platform layer: it acquires and releases, and carries no state of its own.
`KeepAwakeController` is the on/off switch built on top. Use the blocker directly when you already have
somewhere to keep that state, and the controller when you do not.

Acquiring twice replaces the inhibitor rather than stacking a second one, so changing what is being kept
awake is a single call.

### Substituting a blocker in tests

`KeepAwakeController` takes an `ISleepBlocker`, and a controller given one does not dispose it - the caller
that supplied it keeps that job. A test can hand in its own implementation and assert on what was asked for
without touching the machine's real power state.

```csharp
using KeepAwakeController controller = new(myFakeBlocker);
```

## API Reference

### `KeepAwakeController`

The on/off switch behind both the tray icon and the console mode. Every mutation is serialised, and
`StateChanged` is raised outside the lock so a handler can call back in.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `IsActive` | `bool` | Whether an inhibitor is currently held. |
| `IsSupported` | `bool` | Whether the platform can inhibit sleep at all. |
| `Mechanism` | `string` | Short description of the mechanism in use. |
| `Request` | `SleepBlockRequest` | The request currently in force. |
| `KeepDisplayAwake` | `bool` | Whether the display is being kept awake as well as the system. |

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Enable()` | `void` | Starts inhibiting sleep. Does nothing if already active. |
| `Disable()` | `void` | Stops inhibiting sleep. Does nothing if already inactive. |
| `Toggle()` | `bool` | Flips the state and returns the state after the flip. |
| `SetKeepDisplayAwake(bool)` | `void` | Sets whether to keep the display lit, re-taking the inhibitor when active. |
| `SetRequest(SleepBlockRequest)` | `void` | Replaces the request, re-taking the inhibitor when active. |
| `Dispose()` | `void` | Releases any inhibitor held, and the blocker when the controller created it. |

#### Events

| Name | Type | Description |
|------|------|-------------|
| `StateChanged` | `EventHandler<KeepAwakeStateChangedEventArgs>` | Raised when the controller turns on or off, or its request changes. |

### `SleepBlockRequest`

A record describing what to keep awake and why.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `KeepDisplayAwake` | `bool` | Whether to keep the display lit as well as the system awake. |
| `Reason` | `string` | Justification shown by the platform's own tooling. |

#### Static Properties

| Name | Type | Description |
|------|------|-------------|
| `DefaultReason` | `string` | The reason used when a caller supplies none. |
| `SystemOnly` | `SleepBlockRequest` | Stops system sleep and leaves the display alone. |
| `SystemAndDisplay` | `SleepBlockRequest` | Stops system sleep and keeps the display lit. |

### `ISleepBlocker`

The platform layer. Implemented by `WindowsSleepBlocker`, `MacOsSleepBlocker`, `LinuxSleepBlocker`, and
`UnsupportedSleepBlocker`.

#### Properties

| Name | Type | Description |
|------|------|-------------|
| `Mechanism` | `string` | Short description of the mechanism behind this blocker. |
| `IsSupported` | `bool` | Whether this blocker can inhibit sleep on the current machine. |
| `IsBlocking` | `bool` | Whether an inhibitor is currently held. |

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Acquire(SleepBlockRequest)` | `void` | Acquires an inhibitor, replacing any already held. |
| `Release()` | `void` | Releases the inhibitor. Does nothing if none is held. |

### `SleepBlockerFactory`

#### Methods

| Name | Return Type | Description |
|------|-------------|-------------|
| `Create()` | `ISleepBlocker` | Returns the blocker for the current platform, or `UnsupportedSleepBlocker` where there is none. |

## How each platform is kept awake

| Platform | Mechanism | Notes |
|----------|-----------|-------|
| Windows | `SetThreadExecutionState` with `ES_CONTINUOUS \| ES_SYSTEM_REQUIRED`, plus `ES_DISPLAY_REQUIRED` for the display | The execution state belongs to the thread that set it, so NoSleep parks a dedicated thread for the duration rather than trusting a thread pool thread to stay alive. |
| macOS | IOKit `IOPMAssertionCreateWithName` with `PreventUserIdleSystemSleep`, plus `PreventUserIdleDisplaySleep` for the display | The same mechanism `caffeinate` uses. Assertions belong to the process, so the kernel drops them if NoSleep is killed. |
| Linux | `systemd-inhibit --what=idle:sleep`, with `gnome-session-inhibit --inhibit idle` for the display | Held as a child process whose standard input NoSleep keeps open, so the lock dies with NoSleep instead of being orphaned. A machine with neither helper reports keep-awake as unsupported. |

Keeping the display lit on Linux needs `gnome-session-inhibit`, because screen blanking belongs to the
desktop session rather than to logind. `nosleep --status` says whether this machine has it.

## Contributing

Contributions are welcome! Feel free to open issues or submit pull requests.

## License

This project is licensed under the MIT License. See the [LICENSE.md](LICENSE.md) file for details.
