// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Platforms;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
/// <para>
/// On Linux the helper is started through <c>setsid</c>, in a session and process group of its own. A Ctrl+C
/// in the terminal sends SIGINT to the whole foreground process group, so a helper left in NoSleep's group
/// would die, and the lock with it, even when the host handles the Ctrl+C and keeps running. The pipe still
/// ends the helper when NoSleep itself exits. Where <c>setsid</c> is not installed, the helper is started
/// directly.
/// </para>
/// </remarks>
public sealed class ChildProcessHold : IDisposable
{
	private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(2);

	/// <summary>
	/// How long to wait, once a helper has exited, for the rest of its standard error to arrive.
	/// </summary>
	/// <remarks>
	/// The pipe reaches end of input only when every process holding its write end has closed it. A helper
	/// that started a background child before exiting leaves that child holding the pipe, so waiting for end
	/// of input without a deadline would wait for as long as the child runs.
	/// </remarks>
	private static readonly TimeSpan ExitDrainTimeout = TimeSpan.FromSeconds(1);

	/// <summary>
	/// The util-linux command that runs a program in a new session.
	/// </summary>
	/// <remarks>
	/// It is not passed <c>--fork</c> or <c>--wait</c>. A process that <see cref="Process"/> starts is never a
	/// process group leader, so <c>setsid</c> creates the session itself and then replaces itself with the
	/// helper. The helper therefore keeps the process id that <see cref="Process"/> reports, and that
	/// <see cref="Dispose"/> waits for and kills.
	/// </remarks>
	private const string SetSid = "setsid";

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

		// A missing helper is started directly, so the failure says it could not be started rather than
		// surfacing as setsid's "failed to execute" exit.
		string? setSidPath = OperatingSystem.IsLinux() && File.Exists(fileName) ? ExecutableLocator.Find(SetSid) : null;

		ProcessStartInfo startInfo = new(setSidPath ?? fileName)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		if (setSidPath is not null)
		{
			startInfo.ArgumentList.Add(fileName);
		}

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
			// error have been read. Wait a bounded time for the stream to reach end of input, rather than with
			// the untimed WaitForExit: a background child the helper left behind can hold the pipe open for as
			// long as it runs, and this wait happens inside the blocker's and the controller's locks.
			_ = startupErrors.WaitForEndOfInput(ExitDrainTimeout);
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
		private readonly TaskCompletionSource endOfInput = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int length;
		private bool stopped;

		/// <summary>
		/// Records a line of standard error, unless recording has stopped.
		/// </summary>
		/// <param name="line">The line, or <see langword="null"/> at end of input.</param>
		public void Add(string? line)
		{
			if (line is null)
			{
				_ = endOfInput.TrySetResult();
				return;
			}

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
		/// Waits for standard error to reach end of input, up to a deadline.
		/// </summary>
		/// <param name="timeout">The longest time to wait.</param>
		/// <returns><see langword="true"/> when end of input arrived; <see langword="false"/> when the deadline passed first.</returns>
		public bool WaitForEndOfInput(TimeSpan timeout) => endOfInput.Task.Wait(timeout);

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
