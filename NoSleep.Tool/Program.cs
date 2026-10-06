// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep.Tool;

using System.Threading.Tasks;
using ktsu.Essentials;
using ktsu.Essentials.FileSystemProviders.Native;
using ktsu.Essentials.PersistenceProviders.ConfigHome;
using ktsu.Essentials.SerializationProviders.Json;
using ktsu.NoSleep.Contracts;
using ktsu.NoSleep.Platforms;
using ktsu.TrayApp;

/// <summary>
/// Entry point for the <c>nosleep</c> .NET tool.
/// </summary>
/// <remarks>
/// Everything that is not specific to keeping a machine awake - the tray host, the tray-versus-console
/// decision and its fallback, Ctrl+C and <c>SIGTERM</c>, the <c>--for</c> timer, the standard flags, the
/// <c>--status</c> report, and remembering what the tray was left set to - belongs to
/// <c>ktsu.TrayApp</c>. What is left here is the menu, the three switches NoSleep adds, and the wiring
/// between them and <see cref="KeepAwakeController"/>.
/// </remarks>
internal static class Program
{
	/// <summary>
	/// Runs the tool.
	/// </summary>
	/// <param name="args">Command line arguments.</param>
	/// <returns>0 on success, 1 when the platform refused, 2 when the command line was wrong.</returns>
	private static async Task<int> Main(string[] args)
	{
		// The whole persistence wiring: a serializer, a file system, and a directory convention.
		// ktsu.TrayApp takes the IPersistenceProvider<string> that falls out of it and nothing else, so
		// where NoSleep's settings live is NoSleep's decision rather than the library's.
		IPersistenceProvider<string> preferences = new ConfigHomePersistenceProvider<string>(
			new NativeFileSystemProvider(),
			new JsonSerializationProvider(),
			"nosleep",
			string.Empty);

		// The blocker is created here rather than left to the controller so that --status can ask it
		// whether this machine can keep the display lit, which is a Linux-only distinction the controller
		// has no reason to carry. Declared first, so the controller is disposed before it.
		using ISleepBlocker blocker = SleepBlockerFactory.Create();
		using KeepAwakeController controller = new(blocker);

		// What the user has asked for, which is not yet what the controller is doing: the toggle's setter,
		// --off, and the remembered tray state all land here before the run starts, and OnStart is what
		// turns the intent into a held inhibitor. Enabling straight from the setter instead would leave
		// OnStart to override a remembered "off" - or an explicit --off - a moment later.
		bool keepAwakeWanted = true;
		bool started = false;

		void SetKeepAwake(bool value)
		{
			keepAwakeWanted = value;

			if (started)
			{
				ApplyKeepAwake(controller, value);
			}
		}

		void Start()
		{
			started = true;

			if (keepAwakeWanted)
			{
				controller.Enable();
			}
		}

		return await TrayAppBuilder.Create("nosleep")
			.DisplayName("NoSleep")
			.Summary("keep this machine awake.")
			.Icons(typeof(Program).Assembly, "Assets.tray-active.png", "Assets.tray-idle.png")
			.Status(() => Describe(controller))
			.Status("Mechanism", () => controller.Mechanism)
			.Status("Keep awake", () => controller.IsSupported ? "supported" : "not supported on this machine")
			.Status("Keep display", () => DescribeDisplaySupport(blocker))
			.Toggle(
				"Keep awake",
				() => controller.IsActive,
				SetKeepAwake,
				() => controller.IsSupported,
				persistAs: "keep-awake")
			.Toggle(
				"Keep display awake too",
				() => controller.KeepDisplayAwake,
				controller.SetKeepDisplayAwake,
				() => controller.IsSupported,
				persistAs: "keep-display-awake")
			.Flag(["-d", "--display"], "Keep the display lit as well, not just the system awake.", () => controller.SetKeepDisplayAwake(true))
			.Flag(["-o", "--off"], "Start with keep-awake switched off.", () => keepAwakeWanted = false)
			.Option(
				["-r", "--reason"],
				"text",
				"Reason recorded with the inhibitor, shown by the platform's own\ntooling (pmset -g assertions, systemd-inhibit --list).",
				reason => controller.SetRequest(controller.Request with { Reason = reason }))
			.RefreshOn(refresh => controller.StateChanged += (_, _) => refresh())
			.OnStart(Start)
			.OnStop(controller.Disable)
			.Preferences(preferences)
			.RunAsync(args)
			.ConfigureAwait(false);
	}

	private static void ApplyKeepAwake(KeepAwakeController controller, bool keepAwake)
	{
		if (keepAwake)
		{
			controller.Enable();
		}
		else
		{
			controller.Disable();
		}
	}

	/// <summary>
	/// Says what NoSleep is doing, for the menu's status line, the tray tooltip, and the <c>State</c> row of
	/// <c>--status</c>.
	/// </summary>
	/// <param name="controller">The controller being driven.</param>
	/// <returns>The sentence to show.</returns>
	/// <remarks>
	/// A refusal is not described here: <c>ktsu.TrayApp</c> keeps the last failure's message and puts it in
	/// the status line itself, because the refresh that runs after every action would overwrite anything
	/// written straight into the menu.
	/// </remarks>
	private static string Describe(KeepAwakeController controller)
	{
		if (!controller.IsSupported)
		{
			return $"NoSleep cannot keep this machine awake ({controller.Mechanism})";
		}

		if (!controller.IsActive)
		{
			return "NoSleep is off - this machine may sleep";
		}

		return controller.KeepDisplayAwake
			? "Keeping system and display awake"
			: "Keeping system awake";
	}

	private static string DescribeDisplaySupport(ISleepBlocker blocker)
	{
		if (!blocker.IsSupported)
		{
			return "not supported on this machine";
		}

		// Windows and macOS keep the screen lit through the same call that keeps the system awake. Linux
		// splits the two, and a box with only a logind lock can do one and not the other.
		return OperatingSystem.IsLinux() && blocker is LinuxSleepBlocker linux && !linux.SupportsDisplay
			? "not supported (needs gnome-session-inhibit)"
			: "supported";
	}
}
