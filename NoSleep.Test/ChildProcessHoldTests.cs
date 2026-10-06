// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Test;

using System;
using System.Diagnostics;
using ktsu.NoSleep.Platforms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="ChildProcessHold"/>, the mechanism behind the Linux inhibitor.
/// </summary>
/// <remarks>
/// These run against stand-in helpers rather than <c>systemd-inhibit</c>: the shape being tested is "a helper
/// process that stays alive until told otherwise", and a build agent has no logind bus to take a real lock on.
/// They are skipped off Unix, where those paths do not exist.
/// </remarks>
[TestClass]
public class ChildProcessHoldTests
{
	/// <summary>A helper that blocks on standard input until the pipe closes.</summary>
	private const string BlockingHelper = "/bin/cat";

	/// <summary>
	/// A helper that exits straight away.
	/// </summary>
	/// <remarks>
	/// Spelled as a shell invocation rather than <c>/bin/false</c>: POSIX puts <c>sh</c> at <c>/bin/sh</c> on
	/// every Unix, while <c>false</c> lives in <c>/usr/bin</c> on macOS and <c>/bin</c> on Linux. Hardcoding
	/// the Linux path made this test fail on macOS for the wrong reason - the helper could not be started at
	/// all, rather than starting and exiting, which is the case under test.
	/// </remarks>
	private const string ImmediateExitHelper = "/bin/sh";

	private static readonly string[] ImmediateExitArguments = ["-c", "exit 1"];

	private static bool IsUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

	[TestMethod]
	public void Start_KeepsTheHelperRunningUntilDisposed()
	{
		if (!IsUnix)
		{
			Assert.Inconclusive("The helper paths used by this test only exist on Unix.");
			return;
		}

		int helperId;

		using (ChildProcessHold hold = ChildProcessHold.Start(BlockingHelper, []))
		{
			Assert.AreEqual(BlockingHelper, hold.Command);
			helperId = hold.ProcessId;
			Assert.IsTrue(IsRunning(helperId), "The helper should still be running while the hold is held");
		}

		Assert.IsFalse(IsRunning(helperId), "Disposing the hold should end the helper");
	}

	[TestMethod]
	public void Start_WhenTheHelperExitsImmediately_Throws()
	{
		if (!IsUnix)
		{
			Assert.Inconclusive("The helper paths used by this test only exist on Unix.");
			return;
		}

		SleepBlockException exception = Assert.ThrowsExactly<SleepBlockException>(() => ChildProcessHold.Start(ImmediateExitHelper, ImmediateExitArguments));
		StringAssert.Contains(exception.Message, "exited immediately", StringComparison.Ordinal);
	}

	[TestMethod]
	public void Start_WithAMissingHelper_Throws()
	{
		SleepBlockException exception = Assert.ThrowsExactly<SleepBlockException>(
			() => ChildProcessHold.Start("/definitely/not/a/real/inhibitor", []));

		StringAssert.Contains(exception.Message, "Could not start", StringComparison.Ordinal);
	}

	[TestMethod]
	public void Dispose_IsIdempotent()
	{
		if (!IsUnix)
		{
			Assert.Inconclusive("The helper paths used by this test only exist on Unix.");
			return;
		}

		ChildProcessHold hold = ChildProcessHold.Start(BlockingHelper, []);

		hold.Dispose();
		hold.Dispose();
	}

	[TestMethod]
	public void Start_WithNullArguments_Throws() =>
		Assert.ThrowsExactly<ArgumentNullException>(() => ChildProcessHold.Start(BlockingHelper, null!));

	private static bool IsRunning(int processId)
	{
		try
		{
			using Process process = Process.GetProcessById(processId);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}
}
