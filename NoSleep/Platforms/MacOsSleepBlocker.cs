// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using ktsu.NoSleep.Contracts;

/// <summary>
/// Keeps macOS awake through IOKit power assertions, the same mechanism <c>caffeinate</c> uses.
/// </summary>
/// <remarks>
/// Assertions live on the process rather than on a thread, and the kernel drops every assertion a process
/// holds when it exits, so nothing is leaked if NoSleep is killed rather than asked to stop. Held assertions
/// are visible in <c>pmset -g assertions</c> under the reason string the caller supplied.
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed partial class MacOsSleepBlocker : ISleepBlocker
{
	private const string IOKitLibrary = "/System/Library/Frameworks/IOKit.framework/IOKit";
	private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

	/// <summary>kCFStringEncodingUTF8.</summary>
	private const uint CFStringEncodingUtf8 = 0x08000100;

	/// <summary>kIOPMAssertionLevelOn.</summary>
	private const uint AssertionLevelOn = 255;

	/// <summary>kIOReturnSuccess.</summary>
	private const int IOReturnSuccess = 0;

	/// <summary>kIOPMAssertionTypePreventUserIdleSystemSleep.</summary>
	private const string PreventUserIdleSystemSleep = "PreventUserIdleSystemSleep";

	/// <summary>kIOPMAssertionTypePreventUserIdleDisplaySleep.</summary>
	private const string PreventUserIdleDisplaySleep = "PreventUserIdleDisplaySleep";

	private readonly Lock gate = new();
	private readonly List<uint> assertionIds = [];
	private bool disposed;

	/// <inheritdoc/>
	public string Mechanism => "IOKit power assertions";

	/// <inheritdoc/>
	public bool IsSupported => OperatingSystem.IsMacOS();

	/// <inheritdoc/>
	public bool IsBlocking
	{
		get
		{
			lock (gate)
			{
				return assertionIds.Count > 0;
			}
		}
	}

	/// <inheritdoc/>
	public void Acquire(SleepBlockRequest request)
	{
		Ensure.NotNull(request);
		ObjectDisposedException.ThrowIf(disposed, this);

		lock (gate)
		{
			ReleaseCore();

			try
			{
				assertionIds.Add(CreateAssertion(PreventUserIdleSystemSleep, request.Reason));

				if (request.KeepDisplayAwake)
				{
					assertionIds.Add(CreateAssertion(PreventUserIdleDisplaySleep, request.Reason));
				}
			}
			catch (SleepBlockException)
			{
				// Never leave half an inhibitor behind: a display assertion without the system one would
				// report as blocking while letting the machine suspend.
				ReleaseCore();
				throw;
			}
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
		foreach (uint assertionId in assertionIds)
		{
			_ = NativeMethods.IOPMAssertionRelease(assertionId);
		}

		assertionIds.Clear();
	}

	private static uint CreateAssertion(string assertionType, string reason)
	{
		nint type = CreateCFString(assertionType);
		nint name = CreateCFString(reason);

		try
		{
			int result = NativeMethods.IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out uint assertionId);
			return result == IOReturnSuccess
				? assertionId
				: throw new SleepBlockException(string.Create(
					CultureInfo.InvariantCulture,
					$"IOPMAssertionCreateWithName(\"{assertionType}\") failed with IOReturn 0x{result:X8}."));
		}
		finally
		{
			ReleaseCFString(name);
			ReleaseCFString(type);
		}
	}

	private static nint CreateCFString(string value)
	{
		nint utf8 = Marshal.StringToCoTaskMemUTF8(value);

		try
		{
			nint cfString = NativeMethods.CFStringCreateWithCString(alloc: 0, utf8, CFStringEncodingUtf8);
			return cfString == 0
				? throw new SleepBlockException($"CFStringCreateWithCString returned null for \"{value}\".")
				: cfString;
		}
		finally
		{
			Marshal.FreeCoTaskMem(utf8);
		}
	}

	private static void ReleaseCFString(nint cfString)
	{
		if (cfString != 0)
		{
			NativeMethods.CFRelease(cfString);
		}
	}

	/// <summary>
	/// The IOKit and CoreFoundation entry points behind this blocker. CA1060 wants P/Invokes in a type with
	/// this name.
	/// </summary>
	private static partial class NativeMethods
	{
		/// <summary>
		/// Creates a named power assertion of the given type.
		/// </summary>
		/// <param name="assertionType">A CFString naming the assertion type.</param>
		/// <param name="assertionLevel">The assertion level; 255 is on.</param>
		/// <param name="assertionName">A CFString describing why the assertion is held.</param>
		/// <param name="assertionId">Receives the identifier used to release the assertion.</param>
		/// <returns>An <c>IOReturn</c> code; zero on success.</returns>
		[LibraryImport(IOKitLibrary)]
		internal static partial int IOPMAssertionCreateWithName(nint assertionType, uint assertionLevel, nint assertionName, out uint assertionId);

		/// <summary>
		/// Releases a power assertion.
		/// </summary>
		/// <param name="assertionId">The identifier returned when the assertion was created.</param>
		/// <returns>An <c>IOReturn</c> code; zero on success.</returns>
		[LibraryImport(IOKitLibrary)]
		internal static partial int IOPMAssertionRelease(uint assertionId);

		/// <summary>
		/// Wraps a NUL-terminated C string in a CFString.
		/// </summary>
		/// <param name="alloc">The allocator to use; zero for the default.</param>
		/// <param name="cStr">Pointer to the NUL-terminated bytes.</param>
		/// <param name="encoding">The encoding of <paramref name="cStr"/>.</param>
		/// <returns>A CFString the caller owns, or zero on failure.</returns>
		[LibraryImport(CoreFoundationLibrary)]
		internal static partial nint CFStringCreateWithCString(nint alloc, nint cStr, uint encoding);

		/// <summary>
		/// Drops a reference to a CoreFoundation object.
		/// </summary>
		/// <param name="cf">The object to release.</param>
		[LibraryImport(CoreFoundationLibrary)]
		internal static partial void CFRelease(nint cf);
	}
}
