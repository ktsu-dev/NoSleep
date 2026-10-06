// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using ktsu.NoSleep.Contracts;

/// <summary>
/// Stands in for a platform NoSleep has no inhibitor for.
/// </summary>
/// <remarks>
/// Returning this rather than throwing from the factory keeps the failure where a caller can present it:
/// <c>nosleep --status</c> can report what is wrong, and the tray can start up and grey out its toggle,
/// instead of the process dying before it has drawn anything.
/// </remarks>
public sealed class UnsupportedSleepBlocker : ISleepBlocker
{
	/// <inheritdoc/>
	public string Mechanism => "none";

	/// <inheritdoc/>
	public bool IsSupported => false;

	/// <inheritdoc/>
	public bool IsBlocking => false;

	/// <inheritdoc/>
	/// <exception cref="PlatformNotSupportedException">Always.</exception>
	public void Acquire(SleepBlockRequest request)
	{
		Ensure.NotNull(request);

		throw new PlatformNotSupportedException(
			$"NoSleep has no sleep inhibitor for {Environment.OSVersion.Platform}. Supported platforms are Windows, macOS, and Linux.");
	}

	/// <inheritdoc/>
	public void Release()
	{
	}

	/// <inheritdoc/>
	public void Dispose() => GC.SuppressFinalize(this);
}
