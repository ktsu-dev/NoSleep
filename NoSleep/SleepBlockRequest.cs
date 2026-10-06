// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.NoSleep;

/// <summary>
/// Describes what a caller wants kept awake and why.
/// </summary>
/// <param name="KeepDisplayAwake">
/// <see langword="true"/> to also keep the display lit and the screensaver away; <see langword="false"/> to
/// stop only system sleep and let the screen blank normally.
/// </param>
/// <param name="Reason">
/// Human readable justification. Windows ignores it, macOS shows it in <c>pmset -g assertions</c>, and Linux
/// passes it to the inhibitor so it shows up in <c>systemd-inhibit --list</c>.
/// </param>
public sealed record SleepBlockRequest(bool KeepDisplayAwake, string Reason)
{
	/// <summary>
	/// The reason used when a caller does not supply one.
	/// </summary>
	public const string DefaultReason = "NoSleep is keeping this machine awake";

	/// <summary>
	/// A request that stops system sleep and leaves the display alone.
	/// </summary>
	public static SleepBlockRequest SystemOnly { get; } = new(KeepDisplayAwake: false, DefaultReason);

	/// <summary>
	/// A request that stops system sleep and keeps the display lit.
	/// </summary>
	public static SleepBlockRequest SystemAndDisplay { get; } = new(KeepDisplayAwake: true, DefaultReason);
}
