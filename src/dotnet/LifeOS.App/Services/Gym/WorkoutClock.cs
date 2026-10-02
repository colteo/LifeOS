using LifeOS.Contracts.Gym.Sessions;

namespace LifeOS.App.Services.Gym;

// Elapsed workout time and remaining rest, derived from the server's timestamps rather than from a
// counter, so they stay right after the app was backgrounded or restarted. Offset is the server
// clock minus the device clock, measured when a session response arrived (ServerTimeUtc), so a
// wrong device clock does not distort the timers. Presentation only. Plain .NET.
public sealed record WorkoutClock(TimeSpan Offset)
{
	public static readonly WorkoutClock Device = new(TimeSpan.Zero);

	public static WorkoutClock Align(DateTimeOffset serverTimeUtc, DateTimeOffset deviceNowUtc) =>
		new(serverTimeUtc - deviceNowUtc);

	public DateTimeOffset ServerNow(DateTimeOffset deviceNowUtc) => deviceNowUtc + Offset;

	// Started → now while in progress; started → completed once finished. Never negative.
	public TimeSpan Elapsed(DateTimeOffset startedAtUtc, DateTimeOffset? completedAtUtc, DateTimeOffset deviceNowUtc)
	{
		var elapsed = (completedAtUtc ?? ServerNow(deviceNowUtc)) - startedAtUtc;

		return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
	}

	// The rest still to go, or null when there is no rest or it is over.
	public TimeSpan? RestRemaining(WorkoutRestResponse? rest, DateTimeOffset deviceNowUtc)
	{
		if (rest is null)
		{
			return null;
		}

		var remaining = rest.EndsAtUtc - ServerNow(deviceNowUtc);

		return remaining > TimeSpan.Zero ? remaining : null;
	}

	// "4:05", "12:30", "1:02:05".
	public static string Format(TimeSpan duration)
	{
		var seconds = (long)Math.Floor(duration.TotalSeconds);

		return seconds >= 3600
			? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
			: $"{seconds / 60}:{seconds % 60:00}";
	}

	// A countdown rounds up, so it shows 0:01 until the rest is really over.
	public static string FormatCountdown(TimeSpan remaining) =>
		Format(TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds)));
}
