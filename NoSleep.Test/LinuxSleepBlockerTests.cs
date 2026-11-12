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

	/// <summary>
	/// The display hold must end when its standard input closes, as the systemd hold does. With
	/// <c>--inhibit-only</c>, <c>gnome-session-inhibit</c> runs no command and never reads standard input, so it
	/// outlived a killed NoSleep and every release waited out the graceful-exit timeout (ktsu-dev/NoSleep#11).
	/// </summary>
	[TestMethod]
	[SupportedOSPlatform("linux")]
	public void Release_GnomeSessionHold_EndsOnEndOfInputWithoutWaitingOutTheTimeout()
	{
		if (!OperatingSystem.IsLinux())
		{
			Assert.Inconclusive("LinuxSleepBlocker only runs on Linux.");
			return;
		}

		string root = Directory.CreateTempSubdirectory("nosleep-linux-").FullName;

		try
		{
			string argsFile = Path.Join(root, "args");
			string helper = Path.Join(root, "gnome-session-inhibit");

			// Behaves as the real gnome-session-inhibit does: with --inhibit-only it waits forever and ignores
			// standard input; otherwise it runs COMMAND as a child that inherits standard input, and exits with it.
			File.WriteAllText(helper,
				$"""
				#!/bin/sh
				printf '%s\n' "$@" > '{argsFile}'
				while [ $# -gt 0 ]; do
					case "$1" in
						--inhibit|--reason|--app-id) shift 2 ;;
						--inhibit-only) exec sleep 3600 ;;
						--*) shift ;;
						*) break ;;
					esac
				done
				"$@"
				""".ReplaceLineEndings("\n"));
			File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

			using LinuxSleepBlocker blocker = new(null, helper);
			blocker.Acquire(SleepBlockRequest.SystemAndDisplay);
			Assert.IsTrue(blocker.IsBlocking, "The gnome-session hold should be running");

			Stopwatch release = Stopwatch.StartNew();
			blocker.Release();
			release.Stop();

			Assert.IsLessThan(TimeSpan.FromSeconds(1.5), release.Elapsed, "The hold should end on end of input, before the 2 s graceful-exit timeout");

			string[] arguments = File.ReadAllLines(argsFile);
			Assert.DoesNotContain("--inhibit-only", arguments, "--inhibit-only runs no command, so nothing reads standard input");
			Assert.AreEqual("cat", arguments[^1], "The inhibited command should be cat, which exits on end of input");
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
