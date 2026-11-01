// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Test;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using ktsu.NoSleep.Platforms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="LinuxSleepBlocker"/> run against a stand-in <c>systemd-inhibit</c>.
/// </summary>
/// <remarks>
/// The stand-in records its process id and then becomes <c>cat</c>, the same "stay alive until standard input
/// closes" shape as the real hold, so a test can tell whether any helper outlived the blocker. They are skipped
/// off Linux, where the blocker does not run.
/// </remarks>
[TestClass]
public class LinuxSleepBlockerTests
{
	[TestMethod]
	[SupportedOSPlatform("linux")]
	public void Acquire_QueuedBehindDispose_ThrowsAndStartsNoHelper()
	{
		if (!OperatingSystem.IsLinux())
		{
			Assert.Inconclusive("LinuxSleepBlocker only runs on Linux.");
			return;
		}

		string root = Directory.CreateTempSubdirectory("nosleep-linux-").FullName;

		try
		{
			string pidFile = Path.Join(root, "pids");
			string helper = Path.Join(root, "systemd-inhibit");
			File.WriteAllText(helper, $"#!/bin/sh\necho $$ >> '{pidFile}'\nexec cat\n");
			File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

			LinuxSleepBlocker blocker = new(helper, null);
			Lock gate = (Lock)typeof(LinuxSleepBlocker)
				.GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(blocker)!;

			Exception? acquireFailure = null;
			Thread acquirer = new(() => acquireFailure = TryAcquire(blocker));

			// Hold the blocker's lock so the Acquire passes its up-front disposed check and then queues on the
			// lock, which is where an Acquire racing a Dispose on another thread ends up.
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

			int[] helpers = File.Exists(pidFile)
				? [.. File.ReadAllLines(pidFile).Where(line => line.Length > 0).Select(int.Parse)]
				: [];

			// Clean up before asserting, so a failure here does not leave a helper running for the rest of the run.
			bool leftBlocking = blocker.IsBlocking;
			blocker.Release();

			Assert.IsInstanceOfType<ObjectDisposedException>(acquireFailure, "An Acquire queued behind Dispose should see the blocker as disposed");
			Assert.IsFalse(leftBlocking, "A disposed blocker should not report that it is blocking");
			Assert.IsEmpty(helpers, "No helper should have been started for a disposed blocker");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[SupportedOSPlatform("linux")]
	private static ObjectDisposedException? TryAcquire(LinuxSleepBlocker blocker)
	{
		try
		{
			blocker.Acquire(SleepBlockRequest.SystemOnly);
			return null;
		}
		catch (ObjectDisposedException ex)
		{
			return ex;
		}
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
