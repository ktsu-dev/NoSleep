// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep;

using System;

/// <summary>
/// Carries the state of a <see cref="KeepAwakeController"/> after it changed.
/// </summary>
/// <param name="isActive">Whether an inhibitor is now held.</param>
/// <param name="request">The request the controller is holding, or would hold when enabled.</param>
public sealed class KeepAwakeStateChangedEventArgs(bool isActive, SleepBlockRequest request) : EventArgs
{
	/// <summary>
	/// Gets a value indicating whether an inhibitor is currently held.
	/// </summary>
	public bool IsActive { get; } = isActive;

	/// <summary>
	/// Gets the request in force.
	/// </summary>
	public SleepBlockRequest Request { get; } = request;
}
