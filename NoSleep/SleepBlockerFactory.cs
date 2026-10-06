// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep;

using System;
using ktsu.NoSleep.Contracts;
using ktsu.NoSleep.Platforms;

/// <summary>
/// Picks the sleep inhibitor that matches the machine NoSleep is running on.
/// </summary>
public static class SleepBlockerFactory
{
	/// <summary>
	/// Creates the blocker for the current operating system.
	/// </summary>
	/// <returns>
	/// A platform blocker, or an <see cref="UnsupportedSleepBlocker"/> on a platform with no implementation.
	/// The result is never <see langword="null"/>, and callers should check <see cref="ISleepBlocker.IsSupported"/>
	/// before assuming it can do anything.
	/// </returns>
	public static ISleepBlocker Create()
	{
		if (OperatingSystem.IsWindows())
		{
			return new WindowsSleepBlocker();
		}

		if (OperatingSystem.IsMacOS())
		{
			return new MacOsSleepBlocker();
		}

		return OperatingSystem.IsLinux() ? new LinuxSleepBlocker() : new UnsupportedSleepBlocker();
	}
}
