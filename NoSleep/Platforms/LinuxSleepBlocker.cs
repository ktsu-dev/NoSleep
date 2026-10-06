// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using ktsu.NoSleep.Contracts;

/// <summary>
/// Keeps Linux awake through whichever inhibitor the machine actually has.
/// </summary>
/// <remarks>
/// <para>
/// There is no single Linux answer. <c>systemd-inhibit</c> takes a logind lock, which is what stops the
/// system suspending and is present on anything running systemd - including headless servers with no desktop
/// at all. Screen blanking, on the other hand, belongs to the desktop session, so keeping the display lit
/// needs <c>gnome-session-inhibit</c> where it exists.
/// </para>
/// <para>
/// Both are held as child processes; see <see cref="ChildProcessHold"/> for why that is the safe shape.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxSleepBlocker : ISleepBlocker
{
	private const string SystemdInhibit = "systemd-inhibit";
	private const string GnomeSessionInhibit = "gnome-session-inhibit";

	/// <summary>The name reported to <c>systemd-inhibit --list</c>.</summary>
	private const string InhibitorIdentity = "NoSleep";

	private readonly Lock gate = new();
	private readonly List<ChildProcessHold> holds = [];
	private readonly string? systemdInhibitPath;
	private readonly string? gnomeSessionInhibitPath;
	private bool disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="LinuxSleepBlocker"/> class, resolving the inhibitor
	/// helpers available on this machine.
	/// </summary>
	public LinuxSleepBlocker()
	{
		systemdInhibitPath = ExecutableLocator.Find(SystemdInhibit);
		gnomeSessionInhibitPath = ExecutableLocator.Find(GnomeSessionInhibit);
	}

	/// <inheritdoc/>
	public string Mechanism
	{
		get
		{
			List<string> available = [];

			if (systemdInhibitPath is not null)
			{
				available.Add(SystemdInhibit);
			}

			if (gnomeSessionInhibitPath is not null)
			{
				available.Add(GnomeSessionInhibit);
			}

			return available.Count == 0 ? "no inhibitor available" : string.Join(" + ", available);
		}
	}

	/// <inheritdoc/>
	public bool IsSupported => systemdInhibitPath is not null || gnomeSessionInhibitPath is not null;

	/// <summary>
	/// Gets a value indicating whether this machine can keep the display lit as well as awake.
	/// </summary>
	/// <remarks>
	/// A logind lock stops the machine suspending but says nothing about the screen, so a box with
	/// <c>systemd-inhibit</c> and no session inhibitor can honour a system-sleep request and not a display one.
	/// </remarks>
	public bool SupportsDisplay => gnomeSessionInhibitPath is not null;

	/// <inheritdoc/>
	public bool IsBlocking
	{
		get
		{
			lock (gate)
			{
				return holds.Count > 0;
			}
		}
	}

	/// <inheritdoc/>
	public void Acquire(SleepBlockRequest request)
	{
		Ensure.NotNull(request);
		ObjectDisposedException.ThrowIf(disposed, this);

		if (!IsSupported)
		{
			throw new PlatformNotSupportedException(
				$"Neither {SystemdInhibit} nor {GnomeSessionInhibit} is installed, so there is no way to take an inhibitor lock on this machine.");
		}

		lock (gate)
		{
			ReleaseCore();

			try
			{
				// gnome-session-inhibit's "idle" covers the screensaver and the session's idle action, so when
				// it is carrying system sleep too there is nothing extra to start for the display.
				List<string> sessionInhibits = [];

				if (systemdInhibitPath is not null)
				{
					holds.Add(ChildProcessHold.Start(systemdInhibitPath,
					[
						"--what=idle:sleep",
						$"--who={InhibitorIdentity}",
						$"--why={request.Reason}",
						"--mode=block",

						// A logind lock lives for as long as the process holding it, so the helper needs a
						// command that never returns on its own. cat blocks on the standard input pipe that
						// ChildProcessHold keeps open, which also makes the lock die with NoSleep.
						"cat",
					]));
				}
				else
				{
					sessionInhibits.Add("suspend");
					sessionInhibits.Add("idle");
				}

				if (request.KeepDisplayAwake && gnomeSessionInhibitPath is not null && !sessionInhibits.Contains("idle"))
				{
					sessionInhibits.Add("idle");
				}

				if (sessionInhibits.Count > 0 && gnomeSessionInhibitPath is not null)
				{
					holds.Add(ChildProcessHold.Start(gnomeSessionInhibitPath,
					[
						"--inhibit",
						string.Join(":", sessionInhibits),
						"--inhibit-only",
						"--reason",
						request.Reason,
					]));
				}
			}
			catch (SleepBlockException)
			{
				// A half-started set of holds would report as blocking while covering only part of what was
				// asked for, so drop everything and let the caller see the failure.
				ReleaseCore();
				throw;
			}
		}
	}

	/// <inheritdoc/>
	public void Release()
	{
		lock (gate)
		{
			ReleaseCore();
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		lock (gate)
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			ReleaseCore();
		}

		GC.SuppressFinalize(this);
	}

	private void ReleaseCore()
	{
		foreach (ChildProcessHold hold in holds)
		{
			hold.Dispose();
		}

		holds.Clear();
	}
}
