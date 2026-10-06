// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Test;

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="KeepAwakeController"/>.
/// </summary>
/// <remarks>
/// These pin the properties the tray relies on: that a repeated click does not stack inhibitors, that the
/// display setting reaches the platform without a manual off/on, and that a controller never leaves an
/// inhibitor behind. Getting any of them wrong leaves a machine awake with nothing on screen saying so.
/// </remarks>
[TestClass]
public class KeepAwakeControllerTests
{
	[TestMethod]
	public void Enable_AcquiresTheCurrentRequest()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);

		controller.Enable();

		Assert.IsTrue(controller.IsActive);
		Assert.AreEqual(1, blocker.AcquiredRequests.Count);
		Assert.AreEqual(SleepBlockRequest.SystemOnly, blocker.AcquiredRequests[0]);
	}

	[TestMethod]
	public void Enable_WhenAlreadyActive_DoesNotAcquireAgain()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);

		controller.Enable();
		controller.Enable();

		Assert.AreEqual(1, blocker.AcquiredRequests.Count, "A second enable should be a no-op, not a second inhibitor");
	}

	[TestMethod]
	public void Disable_WhenNotActive_DoesNotRelease()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);

		controller.Disable();

		Assert.AreEqual(0, blocker.ReleaseCount);
	}

	[TestMethod]
	public void Toggle_FlipsAndReportsTheNewState()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);

		Assert.IsTrue(controller.Toggle());
		Assert.IsTrue(controller.IsActive);

		Assert.IsFalse(controller.Toggle());
		Assert.IsFalse(controller.IsActive);
		Assert.AreEqual(1, blocker.ReleaseCount);
	}

	[TestMethod]
	public void SetKeepDisplayAwake_WhileActive_RetakesTheInhibitor()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);

		controller.Enable();
		controller.SetKeepDisplayAwake(true);

		Assert.AreEqual(2, blocker.AcquiredRequests.Count);
		Assert.IsFalse(blocker.AcquiredRequests[0].KeepDisplayAwake);
		Assert.IsTrue(blocker.AcquiredRequests[1].KeepDisplayAwake);
	}

	[TestMethod]
	public void SetKeepDisplayAwake_WhileInactive_DoesNotAcquire()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);

		controller.SetKeepDisplayAwake(true);

		Assert.AreEqual(0, blocker.AcquiredRequests.Count);
		Assert.IsTrue(controller.KeepDisplayAwake, "The setting should still be remembered for the next enable");
	}

	[TestMethod]
	public void SetRequest_WithAnUnchangedRequest_DoesNothing()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);
		controller.Enable();

		controller.SetRequest(SleepBlockRequest.SystemOnly);

		Assert.AreEqual(1, blocker.AcquiredRequests.Count);
	}

	[TestMethod]
	public void StateChanged_ReportsEveryTransition()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);

		List<bool> states = [];
		controller.StateChanged += (_, args) => states.Add(args.IsActive);

		controller.Enable();
		controller.Enable();
		controller.Disable();

		CollectionAssert.AreEqual(new[] { true, false }, states, "The repeated enable should not raise a second event");
	}

	[TestMethod]
	public void Dispose_ReleasesButLeavesASuppliedBlockerAlone()
	{
		using FakeSleepBlocker blocker = new();
		KeepAwakeController controller = new(blocker);
		controller.Enable();

		controller.Dispose();

		Assert.AreEqual(1, blocker.ReleaseCount);
		Assert.IsFalse(blocker.IsDisposed, "A blocker handed in from outside belongs to the caller");
	}

	[TestMethod]
	public void Dispose_IsIdempotent()
	{
		using FakeSleepBlocker blocker = new();
		KeepAwakeController controller = new(blocker);

		controller.Dispose();
		controller.Dispose();

		Assert.AreEqual(1, blocker.ReleaseCount);
	}

	[TestMethod]
	public void Enable_AfterDispose_Throws()
	{
		using FakeSleepBlocker blocker = new();
		KeepAwakeController controller = new(blocker);
		controller.Dispose();

		Assert.ThrowsExactly<ObjectDisposedException>(controller.Enable);
	}

	[TestMethod]
	public void Enable_WhenThePlatformRefuses_LeavesTheControllerInactive()
	{
		using FakeSleepBlocker blocker = new() { AcquireFailure = new SleepBlockException("refused") };
		using KeepAwakeController controller = new(blocker);

		Assert.ThrowsExactly<SleepBlockException>(controller.Enable);
		Assert.IsFalse(controller.IsActive, "A failed acquire must not leave the controller claiming to be on");
	}

	[TestMethod]
	public void Constructor_WithNullBlocker_Throws() =>
		Assert.ThrowsExactly<ArgumentNullException>(() => new KeepAwakeController(null!));
}
