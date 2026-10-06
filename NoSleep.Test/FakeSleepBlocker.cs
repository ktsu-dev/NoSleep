// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Test;

using System;
using System.Collections.Generic;
using ktsu.NoSleep.Contracts;

/// <summary>
/// A blocker that records what it was asked to do instead of touching the machine.
/// </summary>
/// <remarks>
/// The real blockers can only be exercised on their own platform, and only where the platform is willing -
/// a container with no systemd bus cannot take a logind lock. The controller's own behaviour is
/// platform-independent, so it is tested against this.
/// </remarks>
internal sealed class FakeSleepBlocker : ISleepBlocker
{
	/// <summary>
	/// Gets every request passed to <see cref="Acquire"/>, in order.
	/// </summary>
	public List<SleepBlockRequest> AcquiredRequests { get; } = [];

	/// <summary>
	/// Gets the number of times <see cref="Release"/> was called.
	/// </summary>
	public int ReleaseCount { get; private set; }

	/// <summary>
	/// Gets a value indicating whether this blocker was disposed.
	/// </summary>
	public bool IsDisposed { get; private set; }

	/// <summary>
	/// Gets or sets the exception <see cref="Acquire"/> should throw instead of succeeding.
	/// </summary>
	public Exception? AcquireFailure { get; set; }

	/// <inheritdoc/>
	public string Mechanism => "fake";

	/// <inheritdoc/>
	public bool IsSupported { get; set; } = true;

	/// <inheritdoc/>
	public bool IsBlocking { get; private set; }

	/// <inheritdoc/>
	public void Acquire(SleepBlockRequest request)
	{
		if (AcquireFailure is not null)
		{
			throw AcquireFailure;
		}

		AcquiredRequests.Add(request);
		IsBlocking = true;
	}

	/// <inheritdoc/>
	public void Release()
	{
		ReleaseCount++;
		IsBlocking = false;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		IsDisposed = true;
		IsBlocking = false;
	}
}
