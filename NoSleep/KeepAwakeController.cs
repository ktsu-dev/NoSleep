// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep;

using System;
using System.Threading;
using ktsu.NoSleep.Contracts;

/// <summary>
/// The on/off switch behind both the tray icon and the console mode.
/// </summary>
/// <remarks>
/// An <see cref="ISleepBlocker"/> knows how to take and drop an inhibitor; this knows whether one <em>should</em>
/// be held, survives being asked twice for the same thing, and announces changes so a menu can follow along.
/// Every mutation is serialised, but <see cref="StateChanged"/> is raised outside the lock so a handler is free
/// to call back in without deadlocking.
/// </remarks>
public sealed class KeepAwakeController : IDisposable
{
	private readonly ISleepBlocker blocker;
	private readonly bool ownsBlocker;
	private readonly Lock gate = new();
	private SleepBlockRequest request;
	private bool isActive;
	private bool disposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="KeepAwakeController"/> class over the blocker for the
	/// current platform, which it disposes along with itself.
	/// </summary>
	public KeepAwakeController()
		: this(SleepBlockerFactory.Create(), ownsBlocker: true)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="KeepAwakeController"/> class over a supplied blocker,
	/// whose lifetime stays with the caller.
	/// </summary>
	/// <param name="blocker">The blocker to drive.</param>
	/// <exception cref="ArgumentNullException"><paramref name="blocker"/> is <see langword="null"/>.</exception>
	public KeepAwakeController(ISleepBlocker blocker)
		: this(blocker, ownsBlocker: false)
	{
	}

	private KeepAwakeController(ISleepBlocker blocker, bool ownsBlocker)
	{
		Ensure.NotNull(blocker);

		this.blocker = blocker;
		this.ownsBlocker = ownsBlocker;
		request = SleepBlockRequest.SystemOnly;
	}

	/// <summary>
	/// Raised whenever the controller turns on or off, or the request it holds changes.
	/// </summary>
	public event EventHandler<KeepAwakeStateChangedEventArgs>? StateChanged;

	/// <summary>
	/// Gets a value indicating whether an inhibitor is currently held.
	/// </summary>
	public bool IsActive
	{
		get
		{
			lock (gate)
			{
				return isActive;
			}
		}
	}

	/// <summary>
	/// Gets a value indicating whether the underlying platform can inhibit sleep at all.
	/// </summary>
	public bool IsSupported => blocker.IsSupported;

	/// <summary>
	/// Gets a short description of the mechanism in use, for the status output.
	/// </summary>
	public string Mechanism => blocker.Mechanism;

	/// <summary>
	/// Gets the request currently in force.
	/// </summary>
	public SleepBlockRequest Request
	{
		get
		{
			lock (gate)
			{
				return request;
			}
		}
	}

	/// <summary>
	/// Gets a value indicating whether the display is being kept awake as well as the system.
	/// </summary>
	public bool KeepDisplayAwake => Request.KeepDisplayAwake;

	/// <summary>
	/// Starts inhibiting sleep. Does nothing if already active.
	/// </summary>
	/// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
	/// <exception cref="PlatformNotSupportedException">The platform has no inhibitor.</exception>
	/// <exception cref="SleepBlockException">The platform refused the inhibitor.</exception>
	public void Enable() => SetActive(active: true);

	/// <summary>
	/// Stops inhibiting sleep. Does nothing if already inactive.
	/// </summary>
	public void Disable() => SetActive(active: false);

	/// <summary>
	/// Flips between enabled and disabled.
	/// </summary>
	/// <returns>The state after the flip: <see langword="true"/> when now inhibiting sleep.</returns>
	/// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
	/// <exception cref="PlatformNotSupportedException">The platform has no inhibitor.</exception>
	/// <exception cref="SleepBlockException">The platform refused the inhibitor.</exception>
	public bool Toggle()
	{
		SetActive(!IsActive);
		return IsActive;
	}

	/// <summary>
	/// Sets whether the display should be kept awake too, re-taking the inhibitor when active.
	/// </summary>
	/// <param name="keepDisplayAwake"><see langword="true"/> to keep the screen lit as well.</param>
	/// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
	/// <exception cref="SleepBlockException">The platform refused the replacement inhibitor.</exception>
	public void SetKeepDisplayAwake(bool keepDisplayAwake) =>
		SetRequest(Request with { KeepDisplayAwake = keepDisplayAwake });

	/// <summary>
	/// Replaces the request, re-taking the inhibitor when active so the change takes effect immediately.
	/// </summary>
	/// <param name="newRequest">The request to hold from now on.</param>
	/// <exception cref="ArgumentNullException"><paramref name="newRequest"/> is <see langword="null"/>.</exception>
	/// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
	/// <exception cref="SleepBlockException">The platform refused the replacement inhibitor.</exception>
	public void SetRequest(SleepBlockRequest newRequest)
	{
		Ensure.NotNull(newRequest);

		KeepAwakeStateChangedEventArgs change;

		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);

			if (request == newRequest)
			{
				return;
			}

			request = newRequest;

			if (isActive)
			{
				blocker.Acquire(newRequest);
			}

			change = new KeepAwakeStateChangedEventArgs(isActive, newRequest);
		}

		StateChanged?.Invoke(this, change);
	}

	/// <summary>
	/// Releases any inhibitor held, and the blocker itself when this controller created it.
	/// </summary>
	public void Dispose()
	{
		lock (gate)
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			isActive = false;
			blocker.Release();

			if (ownsBlocker)
			{
				blocker.Dispose();
			}
		}

		GC.SuppressFinalize(this);
	}

	private void SetActive(bool active)
	{
		KeepAwakeStateChangedEventArgs change;

		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);

			if (isActive == active)
			{
				return;
			}

			if (active)
			{
				blocker.Acquire(request);
			}
			else
			{
				blocker.Release();
			}

			isActive = active;
			change = new KeepAwakeStateChangedEventArgs(active, request);
		}

		StateChanged?.Invoke(this, change);
	}
}
