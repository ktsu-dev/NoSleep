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
	public static string? Find(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

		string? path = Environment.GetEnvironmentVariable("PATH");
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

			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Reports whether an executable is available on <c>PATH</c>.
	/// </summary>
	/// <param name="fileName">The command name to look for, without a directory.</param>
	/// <returns><see langword="true"/> when the command was found.</returns>
	public static bool Exists(string fileName) => Find(fileName) is not null;
}
