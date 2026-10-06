// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Contracts;

using System;

/// <summary>
/// Holds a platform sleep inhibitor open for as long as it is acquired.
/// </summary>
/// <remarks>
/// Implementations are the thin platform layer and carry no policy: they acquire what they are asked for and
/// release it again. <see cref="KeepAwakeController"/> owns the on/off state on top of one of these.
/// </remarks>
public interface ISleepBlocker : IDisposable
{
	/// <summary>
	/// Gets a short description of the mechanism behind this blocker, for diagnostics and the status output.
	/// </summary>
	/// <example><c>SetThreadExecutionState</c>, <c>IOKit power assertions</c>, <c>systemd-inhibit</c>.</example>
	public string Mechanism { get; }

	/// <summary>
	/// Gets a value indicating whether this blocker can actually inhibit sleep on the current machine.
	/// </summary>
	/// <remarks>
	/// A blocker can be the right one for the platform and still be unsupported - a Linux box with neither
	/// <c>systemd-inhibit</c> nor <c>gnome-session-inhibit</c> installed, for instance.
	/// </remarks>
	public bool IsSupported { get; }

	/// <summary>
	/// Gets a value indicating whether an inhibitor is currently held.
	/// </summary>
	public bool IsBlocking { get; }

	/// <summary>
	/// Acquires an inhibitor matching <paramref name="request"/>, replacing any inhibitor already held.
	/// </summary>
	/// <param name="request">What to keep awake, and why.</param>
	/// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
	/// <exception cref="PlatformNotSupportedException"><see cref="IsSupported"/> is <see langword="false"/>.</exception>
	/// <exception cref="SleepBlockException">The platform refused to grant the inhibitor.</exception>
	public void Acquire(SleepBlockRequest request);

	/// <summary>
	/// Releases the inhibitor, letting the machine sleep again. Does nothing if none is held.
	/// </summary>
	public void Release();
}
