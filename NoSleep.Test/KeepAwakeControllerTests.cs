// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Test;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
	/// <summary>
	/// Gets or sets the test context, whose cancellation token bounds the concurrency test.
	/// </summary>
	public TestContext TestContext { get; set; } = null!;

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

	/// <summary>
	/// Toggles racing on several threads must each flip the state once and report the state their own flip
	/// left. Reading the state and flipping it under separate locks let two toggles both turn the controller
	/// on (ktsu-dev/NoSleep#30).
	/// </summary>
	[TestMethod]
	[DataRow(3)]
	[DataRow(4)]
	public void Toggle_ConcurrentCallsEachFlipOnceAndReportTheirOwnResult(int threads)
	{
		const int Trials = 200;

		for (int trial = 0; trial < Trials; trial++)
		{
			using FakeSleepBlocker blocker = new() { AcquireDelay = TimeSpan.FromMilliseconds(1) };
			using KeepAwakeController controller = new(blocker);
			using Barrier start = new(threads);

			Task<bool>[] toggles = [.. Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(
				() =>
				{
					start.SignalAndWait();
					return controller.Toggle();
				},
				TestContext.CancellationToken,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default))];
			Task.WaitAll(toggles, TestContext.CancellationToken);

			bool[] results = [.. toggles.Select(t => t.Result)];
			Assert.AreEqual(threads % 2 == 1, controller.IsActive, $"trial {trial}: final state");
			Assert.AreEqual((threads + 1) / 2, results.Count(r => r), $"trial {trial}: toggles that reported on");
			Assert.AreEqual(threads / 2, results.Count(r => !r), $"trial {trial}: toggles that reported off");
		}
	}

	/// <summary>
	/// A display change and a reason change racing on two threads must both survive. Reading the request and
	/// replacing it under separate locks let the later write drop the earlier one (ktsu-dev/NoSleep#39).
	/// </summary>
	[TestMethod]
	public void SetKeepDisplayAwake_RacingAReasonUpdate_KeepsBothChanges()
	{
		const int Trials = 3000;
		const string Reason = "Rendering";
		TimeSpan stallTimeout = TimeSpan.FromSeconds(10);
		int lost = 0;

		// Two threads that stay alive for every trial and spin on shared counters to start each one together.
		// Starting a thread or task per trial staggers the two by more than the gap a stale read needs, so
		// the race almost never shows.
		using KeepAwakeControllerPool pool = new(Trials);
		int arrived = 0;
		int finished = 0;
		int released = -1;

		void RunSide(Action<KeepAwakeController> change)
		{
			for (int trial = 0; trial < Trials; trial++)
			{
				Interlocked.Increment(ref arrived);
				SpinWait.SpinUntil(() => Volatile.Read(ref released) >= trial);
				change(pool[trial]);
				Interlocked.Increment(ref finished);
			}
		}

		// Background threads, so a side left spinning by a failed assertion cannot keep the test host alive.
		Thread display = new(() => RunSide(controller => controller.SetKeepDisplayAwake(true))) { IsBackground = true };
		Thread reason = new(() => RunSide(controller => controller.SetRequest(current => current with { Reason = Reason }))) { IsBackground = true };
		display.Start();
		reason.Start();

		for (int trial = 0; trial < Trials; trial++)
		{
			int bothSides = (trial + 1) * 2;
			Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref arrived) == bothSides, stallTimeout), $"trial {trial}: a side never arrived");
			Volatile.Write(ref released, trial);
			Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref finished) == bothSides, stallTimeout), $"trial {trial}: a side never finished");

			SleepBlockRequest final = pool[trial].Request;
			if (!final.KeepDisplayAwake || final.Reason != Reason)
			{
				lost++;
			}
		}

		display.Join();
		reason.Join();

		Assert.AreEqual(0, lost, $"{lost} of {Trials} trials lost one of the two changes");
	}

	[TestMethod]
	public void SetRequest_WithAnUpdate_AppliesItToTheRequestInForce()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);
		controller.SetKeepDisplayAwake(true);

		controller.SetRequest(current => current with { Reason = "Rendering" });

		Assert.IsTrue(controller.Request.KeepDisplayAwake);
		Assert.AreEqual("Rendering", controller.Request.Reason);
	}

	[TestMethod]
	public void SetRequest_WithAnUpdateThatReturnsNull_ThrowsAndKeepsTheRequest()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);
		SleepBlockRequest before = controller.Request;

		Assert.ThrowsExactly<ArgumentNullException>(() => controller.SetRequest(_ => null!));
		Assert.AreEqual(before, controller.Request);
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
	public void SetKeepDisplayAwake_WhenThePlatformRefusesWhileActive_GoesInactiveAndKeepsTheHeldRequest()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);
		controller.Enable();

		List<KeepAwakeStateChangedEventArgs> changes = [];
		controller.StateChanged += (_, args) => changes.Add(args);
		blocker.AcquireFailure = new SleepBlockException("refused");

		Assert.ThrowsExactly<SleepBlockException>(() => controller.SetKeepDisplayAwake(true));

		Assert.IsFalse(blocker.IsBlocking);
		Assert.AreEqual(blocker.IsBlocking, controller.IsActive, "The controller must not claim an inhibitor the platform dropped");
		Assert.IsFalse(controller.KeepDisplayAwake, "The request should stay the one last held successfully");
		Assert.AreEqual(1, changes.Count, "The tray needs an event to re-read the state");
		Assert.IsFalse(changes[0].IsActive);
		Assert.IsFalse(changes[0].Request.KeepDisplayAwake);
	}

	[TestMethod]
	public void SetRequest_WhenThePlatformRefusesWhileActive_CanBeEnabledAgain()
	{
		using FakeSleepBlocker blocker = new();
		using KeepAwakeController controller = new(blocker);
		controller.Enable();
		blocker.AcquireFailure = new SleepBlockException("refused");

		Assert.ThrowsExactly<SleepBlockException>(() => controller.SetRequest(current => current with { Reason = "Rendering" }));
		blocker.AcquireFailure = null;
		controller.Enable();

		Assert.IsTrue(controller.IsActive);
		Assert.IsTrue(blocker.IsBlocking, "Enable must not be a no-op after a refused replacement");
	}

	[TestMethod]
	public void Constructor_WithNullBlocker_Throws() =>
		Assert.ThrowsExactly<ArgumentNullException>(() => new KeepAwakeController(null!));

	/// <summary>
	/// One controller per trial, created up front so the racing threads do no allocation between trials.
	/// </summary>
	private sealed class KeepAwakeControllerPool : IDisposable
	{
		private readonly FakeSleepBlocker[] blockers;
		private readonly KeepAwakeController[] controllers;

		public KeepAwakeControllerPool(int count)
		{
			blockers = [.. Enumerable.Range(0, count).Select(_ => new FakeSleepBlocker())];
			controllers = [.. blockers.Select(blocker => new KeepAwakeController(blocker))];
		}

		public KeepAwakeController this[int index] => controllers[index];

		public void Dispose()
		{
			foreach (KeepAwakeController controller in controllers)
			{
				controller.Dispose();
			}

			foreach (FakeSleepBlocker blocker in blockers)
			{
				blocker.Dispose();
			}
		}
	}
}
