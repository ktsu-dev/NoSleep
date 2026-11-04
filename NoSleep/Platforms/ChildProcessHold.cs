// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

/// <summary>
/// A sleep inhibitor that exists for as long as a helper process stays alive.
/// </summary>
/// <remarks>
/// <para>
/// Linux hands out inhibitor locks to a process, not to a library call, so the only way to keep one is to keep
/// something running. The helper is started with its standard input redirected and the pipe deliberately left
/// open: if NoSleep is killed outright, the write end closes, the helper reaches end of input and exits, and
/// the lock goes with it. Without that, an orphaned <c>systemd-inhibit</c> would keep the machine awake with
/// nothing left to turn it off.
/// </para>
/// </remarks>
public sealed class ChildProcessHold : IDisposable
{
	private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(2);

	private readonly Process process;
	private bool disposed;

	private ChildProcessHold(Process process) => this.process = process;

	/// <summary>
	/// Gets the command that is holding the inhibitor open.
	/// </summary>
	public string Command { get; private init; } = string.Empty;

	/// <summary>
	/// Gets the process id of the helper, for diagnostics.
	/// </summary>
	/// <remarks>
	/// Reading it after the hold is disposed returns the id of a process that no longer exists.
	/// </remarks>
	public int ProcessId { get; private init; }

	/// <summary>
	/// Starts a helper process and keeps it running until the hold is disposed.
	/// </summary>
	/// <param name="fileName">Full path to the helper executable.</param>
	/// <param name="arguments">Arguments passed to the helper, one element per argument.</param>
	/// <returns>A hold that releases the inhibitor when disposed.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="arguments"/> is <see langword="null"/>.</exception>
	/// <exception cref="SleepBlockException">The helper could not be started, or exited immediately.</exception>
	public static ChildProcessHold Start(string fileName, IEnumerable<string> arguments)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		Ensure.NotNull(arguments);

		ProcessStartInfo startInfo = new(fileName)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		Process process = new() { StartInfo = startInfo };

		// An unread pipe eventually fills and blocks the writer, so both streams are drained. Standard output
		// is dropped. Standard error is kept while the helper starts up, because a helper that fails at once
		// usually says why there, and that reason belongs in the exception.
		StartupErrorOutput startupErrors = new();
		process.OutputDataReceived += static (_, _) => { };
		process.ErrorDataReceived += (_, e) => startupErrors.Add(e.Data);

		try
		{
			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
		{
			process.Dispose();
			throw new SleepBlockException($"Could not start the inhibitor helper \"{fileName}\".", ex);
		}

		if (process.WaitForExit(GetMilliseconds(StartupGrace)))
		{
			// The timed wait returns as soon as the process ends, possibly before its last lines of standard
			// error have been read. The untimed wait also waits for both streams to reach end of input, which
			// they do promptly now that the helper is gone.
			process.WaitForExit();
			int exitCode = process.ExitCode;
			string errorOutput = startupErrors.Stop();
			process.Dispose();

			string message = string.Create(
				CultureInfo.InvariantCulture,
				$"The inhibitor helper \"{fileName}\" exited immediately with code {exitCode}.");

			throw new SleepBlockException(errorOutput.Length == 0
				? message
				: $"{message} It reported: {errorOutput}");
		}

		_ = startupErrors.Stop();

		return new ChildProcessHold(process) { Command = fileName, ProcessId = process.Id };
	}

	/// <summary>
	/// Stops the helper process, releasing the inhibitor.
	/// </summary>
	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;

		try
		{
			if (!process.HasExited)
			{
				// Closing the pipe is the polite exit for a helper reading standard input; anything that
				// ignores its input gets killed once the grace period is up.
				try
				{
					process.StandardInput.Close();
				}
				catch (IOException)
				{
					// The helper already closed its end. Killing below covers it.
				}

				if (!process.WaitForExit(GetMilliseconds(GracefulExitTimeout)))
				{
					process.Kill(entireProcessTree: true);
					process.WaitForExit(GetMilliseconds(GracefulExitTimeout));
				}
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
		{
			// The helper is already gone, which is the outcome this method exists to produce.
		}
		finally
		{
			process.Dispose();
		}
	}

	private static int GetMilliseconds(TimeSpan timeout) => (int)timeout.TotalMilliseconds;

	/// <summary>
	/// Keeps the most recent standard error lines a helper writes while it starts up, up to a fixed size.
	/// </summary>
	/// <remarks>
	/// Lines arrive on a thread-pool thread while <see cref="Start"/> waits, so access is locked. Once stopped,
	/// later lines are dropped, so a long-running helper cannot grow the buffer.
	/// </remarks>
	private sealed class StartupErrorOutput
	{
		/// <summary>
		/// The most characters kept. When more arrive, the oldest are dropped, since the last lines a failing
		/// helper writes are the ones most likely to say why it failed.
		/// </summary>
		private const int MaxLength = 4096;

		private readonly Lock gate = new();
		private readonly LinkedList<string> lines = new();
		private int length;
		private bool stopped;

		/// <summary>
		/// Records a line of standard error, unless recording has stopped.
		/// </summary>
		/// <param name="line">The line, or <see langword="null"/> at end of input.</param>
		public void Add(string? line)
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				return;
			}

			string trimmed = line.Trim();

			lock (gate)
			{
				if (stopped)
				{
					return;
				}

				if (trimmed.Length > MaxLength)
				{
					trimmed = trimmed[^MaxLength..];
				}

				lines.AddLast(trimmed);
				// Each line after the first costs one more character for the space that joins it to the last.
				length += trimmed.Length + (lines.Count > 1 ? 1 : 0);

				while (length > MaxLength && lines.First is { } oldest)
				{
					length -= oldest.Value.Length + (lines.Count > 1 ? 1 : 0);
					lines.RemoveFirst();
				}
			}
		}

		/// <summary>
		/// Stops recording and returns what was recorded.
		/// </summary>
		/// <returns>The recorded lines joined by spaces, or an empty string when there were none.</returns>
		public string Stop()
		{
			lock (gate)
			{
				stopped = true;
				string text = string.Join(' ', lines);
				lines.Clear();
				length = 0;
				return text;
			}
		}
	}
}
