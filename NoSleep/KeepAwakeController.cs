// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep;

using System;
using System.Runtime.ExceptionServices;
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
	public void Enable() => SetActive(_ => true);

	/// <summary>
	/// Stops inhibiting sleep. Does nothing if already inactive.
	/// </summary>
	public void Disable() => SetActive(_ => false);

	/// <summary>
	/// Flips between enabled and disabled.
	/// </summary>
	/// <returns>The state after the flip: <see langword="true"/> when now inhibiting sleep.</returns>
	/// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
	/// <exception cref="PlatformNotSupportedException">The platform has no inhibitor.</exception>
	/// <exception cref="SleepBlockException">The platform refused the inhibitor.</exception>
	public bool Toggle() => SetActive(current => !current);

	/// <summary>
	/// Sets whether the display should be kept awake too, re-taking the inhibitor when active.
	/// </summary>
	/// <param name="keepDisplayAwake"><see langword="true"/> to keep the screen lit as well.</param>
	/// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
	/// <exception cref="SleepBlockException">The platform refused the replacement inhibitor.</exception>
	public void SetKeepDisplayAwake(bool keepDisplayAwake) =>
		SetRequest(current => current with { KeepDisplayAwake = keepDisplayAwake });

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

		SetRequest(_ => newRequest);
	}

	/// <summary>
	/// Changes the request by computing a new one from the request in force, re-taking the inhibitor when
	/// active so the change takes effect immediately.
	/// </summary>
	/// <param name="update">
	/// Maps the request in force to the request to hold from now on. It runs under the controller's lock, so
	/// no other change can land between reading the current request and replacing it. Keep it short, and do
	/// not call back into the controller from it.
	/// </param>
	/// <example>
	/// Change only the reason, keeping whatever display setting another thread may have just made:
	/// <code>controller.SetRequest(current =&gt; current with { Reason = "Rendering" });</code>
	/// </example>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="update"/> is <see langword="null"/>, or returned <see langword="null"/>.
	/// </exception>
	/// <exception cref="ObjectDisposedException">The controller has been disposed.</exception>
	/// <exception cref="SleepBlockException">
	/// The platform refused the replacement inhibitor. The controller is then inactive, since the inhibitor it
	/// held was dropped to make way for the replacement, and it keeps the request in force before the call.
	/// <see cref="StateChanged"/> is raised before the exception is thrown.
	/// </exception>
	public void SetRequest(Func<SleepBlockRequest, SleepBlockRequest> update)
	{
		Ensure.NotNull(update);

		KeepAwakeStateChangedEventArgs change;
		ExceptionDispatchInfo? failure = null;

		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);

			SleepBlockRequest newRequest = update(request);
			Ensure.NotNull(newRequest);

			if (request == newRequest)
			{
				return;
			}

			if (isActive)
			{
				try
				{
					blocker.Acquire(newRequest);
				}
				catch (SleepBlockException ex)
				{
					// Every blocker drops the inhibitor it held before taking the replacement, so a refusal
					// leaves nothing held. Say so, rather than claim to be on while the machine is free to
					// sleep, and keep the request that was last held successfully.
					isActive = false;
					blocker.Release();
					failure = ExceptionDispatchInfo.Capture(ex);
				}
			}

			if (failure is null)
			{
				request = newRequest;
			}

			change = new KeepAwakeStateChangedEventArgs(isActive, request);
		}

		StateChanged?.Invoke(this, change);
		failure?.Throw();
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

	/// <summary>
	/// Turns the controller on or off, deciding the new state from the current one under the same lock that
	/// applies it, so a concurrent <see cref="Toggle"/> cannot act on a state another call has since changed.
	/// </summary>
	/// <param name="target">Maps the current state to the state wanted.</param>
	/// <returns>The state this call left.</returns>
	private bool SetActive(Func<bool, bool> target)
	{
		KeepAwakeStateChangedEventArgs change;

		lock (gate)
		{
			ObjectDisposedException.ThrowIf(disposed, this);

			bool active = target(isActive);
			if (isActive == active)
			{
				return active;
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
		return change.IsActive;
	}
}
