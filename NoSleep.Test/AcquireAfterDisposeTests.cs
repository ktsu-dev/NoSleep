// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Test;

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using ktsu.NoSleep.Contracts;
using ktsu.NoSleep.Platforms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Checks that every platform blocker refuses an <see cref="ISleepBlocker.Acquire"/> that queued on its lock
/// behind <see cref="IDisposable.Dispose"/>.
/// </summary>
/// <remarks>
/// These run on every operating system, not only the blocker's own. The check under the lock throws before any
/// platform API is reached, and releasing a blocker that holds nothing makes no platform call either, so the
/// path under test is the same managed code everywhere. Each method still names its blocker's platform so the
/// platform analyzer sees the call is deliberate.
/// </remarks>
[TestClass]
public class AcquireAfterDisposeTests
{
	[TestMethod]
	[SupportedOSPlatform("linux")]
	public void Linux_AcquireQueuedBehindDispose_Throws()
	{
		// A path that is never started: the disposed check under the lock throws first.
		using LinuxSleepBlocker blocker = new("systemd-inhibit-never-started", null);
		AssertAcquireQueuedBehindDisposeThrows(blocker, GateOf(blocker));
	}

	[TestMethod]
	[SupportedOSPlatform("macos")]
	public void MacOs_AcquireQueuedBehindDispose_Throws()
	{
		using MacOsSleepBlocker blocker = new();
		AssertAcquireQueuedBehindDisposeThrows(blocker, GateOf(blocker));
	}

	[TestMethod]
	[SupportedOSPlatform("windows")]
	public void Windows_AcquireQueuedBehindDispose_Throws()
	{
		using WindowsSleepBlocker blocker = new();
		AssertAcquireQueuedBehindDisposeThrows(blocker, GateOf(blocker));
	}

	private static Lock GateOf(ISleepBlocker blocker) =>
		(Lock)blocker.GetType()
			.GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(blocker)!;

	private static void AssertAcquireQueuedBehindDisposeThrows(ISleepBlocker blocker, Lock gate)
	{
		Exception? acquireFailure = null;
		Thread acquirer = new(() =>
		{
			try
			{
				blocker.Acquire(SleepBlockRequest.SystemOnly);
			}
			catch (ObjectDisposedException ex)
			{
				acquireFailure = ex;
			}
		});

		// Hold the lock so the Acquire passes its up-front disposed check and then queues on the lock, which is
		// where an Acquire racing a Dispose on another thread ends up.
		gate.Enter();

		try
		{
			acquirer.Start();
			WaitUntilBlocked(acquirer);

			// The lock is re-entrant, so Dispose runs to completion while the Acquire is still queued.
			blocker.Dispose();
		}
		finally
		{
			gate.Exit();
		}

		Assert.IsTrue(acquirer.Join(TimeSpan.FromSeconds(10)), "The queued Acquire should finish once the lock is free");
		Assert.IsInstanceOfType<ObjectDisposedException>(acquireFailure, "An Acquire queued behind Dispose should see the blocker as disposed");
		Assert.IsFalse(blocker.IsBlocking, "A disposed blocker should not report that it is blocking");
	}

	private static void WaitUntilBlocked(Thread thread)
	{
		Stopwatch elapsed = Stopwatch.StartNew();

		while ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0)
		{
			if (elapsed.Elapsed > TimeSpan.FromSeconds(10))
			{
				Assert.Fail("The Acquire never queued on the blocker's lock");
			}

			Thread.Sleep(10);
		}
	}
}
