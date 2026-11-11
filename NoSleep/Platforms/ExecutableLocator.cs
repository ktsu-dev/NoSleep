// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Resolves helper executables against the <c>PATH</c> environment variable.
/// </summary>
/// <remarks>
/// The Linux blocker has to know whether <c>systemd-inhibit</c> exists <em>before</em> it reports itself
/// supported, and "just try to start it and see if it throws" makes that answer cost a process launch.
/// </remarks>
public static partial class ExecutableLocator
{
	/// <summary>
	/// Finds an executable on <c>PATH</c>.
	/// </summary>
	/// <param name="fileName">The command name to look for, without a directory.</param>
	/// <returns>The full path to the first match, or <see langword="null"/> when nothing on <c>PATH</c> matches.</returns>
	/// <exception cref="ArgumentException"><paramref name="fileName"/> is <see langword="null"/> or whitespace.</exception>
	/// <remarks>
	/// A file that cannot be executed is skipped, as the shell, <c>command -v</c> and <c>execvp</c> skip it, so a
	/// stray non-executable copy earlier on <c>PATH</c> does not hide a working helper later on it.
	/// </remarks>
	public static string? Find(string fileName) => Find(fileName, Environment.GetEnvironmentVariable("PATH"));

	/// <summary>
	/// Finds an executable on the given search path.
	/// </summary>
	/// <param name="fileName">The command name to look for, without a directory.</param>
	/// <param name="path">The search path, in the same form as the <c>PATH</c> environment variable.</param>
	/// <returns>The full path to the first match, or <see langword="null"/> when nothing on <paramref name="path"/> matches.</returns>
	/// <exception cref="ArgumentException"><paramref name="fileName"/> is <see langword="null"/> or whitespace.</exception>
	internal static string? Find(string fileName, string? path) => Find(fileName, path, IsExecutableFile);

	/// <summary>
	/// Finds an executable on the given search path, deciding what counts as executable with <paramref name="isExecutableFile"/>.
	/// </summary>
	/// <param name="fileName">The command name to look for, without a directory.</param>
	/// <param name="path">The search path, in the same form as the <c>PATH</c> environment variable.</param>
	/// <param name="isExecutableFile">Reports whether a candidate path is a file this process may execute.</param>
	/// <returns>The full path to the first match, or <see langword="null"/> when nothing on <paramref name="path"/> matches.</returns>
	/// <exception cref="ArgumentException"><paramref name="fileName"/> is <see langword="null"/> or whitespace.</exception>
	/// <remarks>
	/// The check is a parameter so the search order can be tested for a match this process may not execute,
	/// which a test running as root cannot set up with a real file.
	/// </remarks>
	internal static string? Find(string fileName, string? path, Func<string, bool> isExecutableFile)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		Ensure.NotNull(isExecutableFile);

		if (string.IsNullOrEmpty(path))
		{
			return null;
		}

		foreach (string directory in path.Split(Path.PathSeparator))
		{
			if (string.IsNullOrWhiteSpace(directory))
			{
				continue;
			}

			string candidate;

			try
			{
				candidate = Path.Combine(directory, fileName);
			}
			catch (ArgumentException)
			{
				// A PATH entry containing invalid path characters is a broken environment, not a reason to
				// stop looking at the entries after it.
				continue;
			}

			if (isExecutableFile(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Reports whether a path names a regular file that this process could execute.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <see cref="File.Exists(string)"/> is false for a directory already. Windows has no execute bit, so any file
	/// counts there.
	/// </para>
	/// <para>
	/// Off Windows the kernel decides, through <c>access(path, X_OK)</c>, as it does for the shell and
	/// <c>execvp</c>. Reading the mode bits is not enough: a root-owned <c>0700</c> file has an execute bit set,
	/// yet nobody but root may run it. If libc's <c>access</c> cannot be called, the mode bits are the fallback.
	/// </para>
	/// </remarks>
	private static bool IsExecutableFile(string candidate)
	{
		if (!File.Exists(candidate))
		{
			return false;
		}

		if (OperatingSystem.IsWindows())
		{
			return true;
		}

		try
		{
			return NativeMethods.Access(candidate, NativeMethods.ExecuteOk) == 0;
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return HasExecuteBit(candidate, File.GetUnixFileMode);
		}
	}

	/// <summary>
	/// Reports whether the file's mode, as <paramref name="readMode"/> reads it, grants execute to anyone.
	/// </summary>
	/// <param name="candidate">The file to ask about.</param>
	/// <param name="readMode">Reads the file's Unix mode; <see cref="File.GetUnixFileMode(string)"/> in production.</param>
	/// <returns><see langword="true"/> when any execute bit is set; <see langword="false"/> when none is, or the mode cannot be read.</returns>
	/// <remarks>
	/// <para>
	/// This is the fallback for a platform whose libc does not export <c>access</c>. It cannot tell whether this
	/// process in particular may execute the file.
	/// </para>
	/// <para>
	/// The mode reader is a parameter so the decision, including what a file that vanishes or cannot be read
	/// means, is covered on every platform rather than only where the bit exists.
	/// </para>
	/// </remarks>
	internal static bool HasExecuteBit(string candidate, Func<string, UnixFileMode> readMode)
	{
		try
		{
			const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
			return (readMode(candidate) & anyExecute) != 0;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// Reports whether an executable is available on <c>PATH</c>.
	/// </summary>
	/// <param name="fileName">The command name to look for, without a directory.</param>
	/// <returns><see langword="true"/> when the command was found.</returns>
	public static bool Exists(string fileName) => Find(fileName) is not null;

	private static partial class NativeMethods
	{
		/// <summary>
		/// The <c>X_OK</c> mode for <c>access</c>: test for execute permission.
		/// </summary>
		internal const int ExecuteOk = 1;

		/// <summary>
		/// Checks whether the calling process may access a file, using its real user and group ids.
		/// </summary>
		/// <param name="path">The file to check.</param>
		/// <param name="mode">The access to check for, such as <see cref="ExecuteOk"/>.</param>
		/// <returns>0 when access is allowed; -1 otherwise.</returns>
		[LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
		[DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
		internal static partial int Access(string path, int mode);
	}
}
