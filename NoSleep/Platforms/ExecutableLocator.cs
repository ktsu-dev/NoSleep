// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using System.IO;

/// <summary>
/// Resolves helper executables against the <c>PATH</c> environment variable.
/// </summary>
/// <remarks>
/// The Linux blocker has to know whether <c>systemd-inhibit</c> exists <em>before</em> it reports itself
/// supported, and "just try to start it and see if it throws" makes that answer cost a process launch.
/// </remarks>
public static class ExecutableLocator
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
	internal static string? Find(string fileName, string? path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

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

			if (IsExecutableFile(candidate))
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
	/// <see cref="File.Exists(string)"/> is false for a directory already. Off Windows, a file with no execute bit
	/// for anyone is skipped; Windows has no such bit, so any file counts there.
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
			const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
			return (File.GetUnixFileMode(candidate) & anyExecute) != 0;
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
}
