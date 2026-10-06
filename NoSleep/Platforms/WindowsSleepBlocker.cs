// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using ktsu.NoSleep.Contracts;

/// <summary>
/// Keeps Windows awake through <c>SetThreadExecutionState</c>.
/// </summary>
/// <remarks>
/// The execution state is a property of the thread that sets it and is dropped the moment that thread exits,
/// so the flags are set on a dedicated thread that then parks until <see cref="Release"/> is called. Setting
/// them on a thread pool thread would work for as long as the pool happened to keep that thread around, which
/// is exactly the kind of bug that only shows up on an idle machine at 3am.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSleepBlocker : ISleepBlocker
{
	private const uint EsContinuous = 0x80000000;
	private const uint EsSystemRequired = 0x00000001;
	private const uint EsDisplayRequired = 0x00000002;

	private readonly Lock gate = new();
	private ManualResetEventSlim? releaseSignal;
	private Thread? holderThread;
	private bool disposed;

	/// <inheritdoc/>
	public string Mechanism => "SetThreadExecutionState";

	/// <inheritdoc/>
	public bool IsSupported => OperatingSystem.IsWindows();

	/// <inheritdoc/>
	public bool IsBlocking
	{
		get
		{
			lock (gate)
			{
				return releaseSignal is not null;
			}
		}
	}

	/// <inheritdoc/>
	public void Acquire(SleepBlockRequest request)
	{
		Ensure.NotNull(request);
		ObjectDisposedException.ThrowIf(disposed, this);

		uint flags = EsContinuous | EsSystemRequired;
		if (request.KeepDisplayAwake)
		{
			flags |= EsDisplayRequired;
		}

		lock (gate)
		{
			ReleaseCore();

			ManualResetEventSlim signal = new(initialState: false);
			Exception? startupFailure = null;
			using ManualResetEventSlim ready = new(initialState: false);

			Thread thread = new(() =>
			{
				try
				{
					if (NativeMethods.SetThreadExecutionState(flags) == 0)
					{
						startupFailure = new SleepBlockException(string.Create(
							CultureInfo.InvariantCulture,
							$"SetThreadExecutionState(0x{flags:X8}) failed with Win32 error {Marshal.GetLastWin32Error()}."));
						return;
					}
				}
				finally
				{
					ready.Set();
				}

				signal.Wait();
				_ = NativeMethods.SetThreadExecutionState(EsContinuous);
			})
			{
				IsBackground = true,
				Name = "NoSleep execution state holder",
			};

			thread.Start();
			ready.Wait();

			if (startupFailure is not null)
			{
				signal.Dispose();
				throw startupFailure;
			}

			releaseSignal = signal;
			holderThread = thread;
		}
	}

	/// <inheritdoc/>
	public void Release()
	{
		lock (gate)
		{
			ReleaseCore();
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		lock (gate)
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			ReleaseCore();
		}

		GC.SuppressFinalize(this);
	}

	private void ReleaseCore()
	{
		ManualResetEventSlim? signal = releaseSignal;
		Thread? thread = holderThread;
		releaseSignal = null;
		holderThread = null;

		if (signal is null)
		{
			return;
		}

		signal.Set();

		// The holder thread only has to clear the flags and fall off the end of its delegate, so it always
		// finishes promptly. The timeout exists so a wedged thread cannot hang a tray menu click.
		thread?.Join(TimeSpan.FromSeconds(5));
		signal.Dispose();
	}

	/// <summary>
	/// The kernel32 entry point behind this blocker. CA1060 wants P/Invokes in a type with this name.
	/// </summary>
	private static partial class NativeMethods
	{
		/// <summary>
		/// Tells Windows that the calling thread is in use, so the idle timers do not run down.
		/// </summary>
		/// <param name="esFlags">The <c>ES_*</c> flags to apply to the calling thread.</param>
		/// <returns>The previous execution state, or zero on failure.</returns>
		[LibraryImport("kernel32.dll", SetLastError = true)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
		internal static partial uint SetThreadExecutionState(uint esFlags);
	}
}
