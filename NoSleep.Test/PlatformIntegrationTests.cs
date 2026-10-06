// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Test;

using System;
using System.Linq;
using System.Reflection;
using ktsu.NoSleep.Contracts;
using ktsu.NoSleep.Platforms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tool = ktsu.NoSleep.Tool;

/// <summary>
/// Tests that the pieces which can only be checked on a real machine line up with the platform they run on.
/// </summary>
[TestClass]
public class PlatformIntegrationTests
{
	[TestMethod]
	public void Create_ReturnsTheBlockerForThisPlatform()
	{
		using ISleepBlocker blocker = SleepBlockerFactory.Create();

		if (OperatingSystem.IsWindows())
		{
			Assert.IsInstanceOfType<WindowsSleepBlocker>(blocker);
		}
		else if (OperatingSystem.IsMacOS())
		{
			Assert.IsInstanceOfType<MacOsSleepBlocker>(blocker);
		}
		else if (OperatingSystem.IsLinux())
		{
			Assert.IsInstanceOfType<LinuxSleepBlocker>(blocker);
		}
		else
		{
			Assert.IsInstanceOfType<UnsupportedSleepBlocker>(blocker);
		}
	}

	[TestMethod]
	public void Create_ReturnsABlockerThatIsNotYetHoldingAnything()
	{
		using ISleepBlocker blocker = SleepBlockerFactory.Create();

		Assert.IsFalse(blocker.IsBlocking);
		Assert.IsFalse(string.IsNullOrWhiteSpace(blocker.Mechanism));
	}

	[TestMethod]
	public void UnsupportedBlocker_RefusesToAcquire()
	{
		using UnsupportedSleepBlocker blocker = new();

		Assert.IsFalse(blocker.IsSupported);
		Assert.ThrowsExactly<PlatformNotSupportedException>(() => blocker.Acquire(SleepBlockRequest.SystemOnly));

		// Release on a blocker that never acquired anything has to be a no-op: the controller calls it on
		// every disable, supported or not.
		blocker.Release();
	}

	[TestMethod]
	public void Find_LocatesACommandThatIsOnThePath()
	{
		string command = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";

		Assert.IsNotNull(ExecutableLocator.Find(command), $"'{command}' should be on PATH on this platform");
		Assert.IsTrue(ExecutableLocator.Exists(command));
	}

	[TestMethod]
	public void Find_ReturnsNullForACommandThatIsNotOnThePath() =>
		Assert.IsNull(ExecutableLocator.Find("definitely-not-a-real-command-9f3a1"));

	[TestMethod]
	[DataRow("")]
	[DataRow("   ")]
	public void Find_WithABlankName_Throws(string name) =>
		Assert.ThrowsExactly<ArgumentException>(() => ExecutableLocator.Find(name));

	[TestMethod]
	public void TrayIcons_AreEmbeddedUnderTheNamesTheLoaderExpects()
	{
		// The loader resolves these by string, so a moved or renamed asset only shows up as a tray icon that
		// fails to draw at run time. Checking the resource names keeps that a build-time failure instead, and
		// does not need a windowing system the way constructing a WindowIcon would.
		Assembly toolAssembly = typeof(Tool.Program).Assembly;
		string[] resources = toolAssembly.GetManifestResourceNames();

		CollectionAssert.Contains(resources, "ktsu.NoSleep.Tool.Assets.tray-active.png");
		CollectionAssert.Contains(resources, "ktsu.NoSleep.Tool.Assets.tray-idle.png");
	}

	[TestMethod]
	public void LinuxBlocker_ReportsWhatTheMachineHasInstalled()
	{
		if (!OperatingSystem.IsLinux())
		{
			Assert.Inconclusive("The Linux blocker only reports meaningfully on Linux.");
			return;
		}

		using LinuxSleepBlocker blocker = new();

		bool hasSystemd = ExecutableLocator.Exists("systemd-inhibit");
		bool hasGnome = ExecutableLocator.Exists("gnome-session-inhibit");

		Assert.AreEqual(hasSystemd || hasGnome, blocker.IsSupported);
		Assert.AreEqual(hasGnome, blocker.SupportsDisplay);

		string[] mentioned = blocker.Mechanism.Split(" + ");
		Assert.AreEqual(hasSystemd, mentioned.Contains("systemd-inhibit"));
	}
}
