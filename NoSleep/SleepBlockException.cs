// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep;

using System;

/// <summary>
/// Thrown when a platform refuses to grant or release a sleep inhibitor.
/// </summary>
public class SleepBlockException : Exception
{
	/// <summary>
	/// Initializes a new instance of the <see cref="SleepBlockException"/> class.
	/// </summary>
	public SleepBlockException()
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="SleepBlockException"/> class with a message.
	/// </summary>
	/// <param name="message">A description of what the platform refused.</param>
	public SleepBlockException(string message)
		: base(message)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="SleepBlockException"/> class with a message and a cause.
	/// </summary>
	/// <param name="message">A description of what the platform refused.</param>
	/// <param name="innerException">The underlying failure.</param>
	public SleepBlockException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
